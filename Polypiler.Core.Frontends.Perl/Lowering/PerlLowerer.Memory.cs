using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  private static readonly HashSet<string> _indexedOps = new(StringComparer.Ordinal) {
    "multideref", "aelem", "aelemfast", "helem"
  };

  private static readonly HashSet<string> _assignOps = new(StringComparer.Ordinal) {
    "padsv_store", "padsv", "sassign", "aassign"
  };

  private static bool IsIntConst(PerlOp op, string value)
    => op.Constant is { Kind: "integer" } c && c.Value == value;

  private bool TryLowerAssignmentStatement(PerlOp node, List<IrStmt> statements) {
    // 1. `my $x = EXPR;` (`padsv_store`) or `$x = EXPR;` (`sassign`)
    if (node is { Name: "padsv_store", Children: [var rhs, ..] }) {
      if (this._closureLocals.ContainsKey(node.Target))
        throw this.Fail(PerlFrontend.EscapingClosure, this.LexicalName(node.Target), cause: "OBJ-CLOSURES");
      if (rhs.Name == "anoncode") {
        var lambda = this.LowerClosureExpr(rhs.Code!);
        var local = this.DeclareLocal(this.LexicalName(node.Target), lambda.Type, node.Target);
        this._closureLocals[node.Target] = lambda;
        statements.Add(new IrDeclare(local, lambda));
        return true;
      }
      var val = this.LiftExpr(rhs);
      this.EmitScalarAssign(node.Target, val, statements);
      return true;
    }

    if (node is { Name: "aelemfastlex_store", Children: [var fastValOp, ..] }) {
      var arr = this.LowerPadAvRead(node.Target);
      var idx = new IrConst(IrType.I32, node.Private);
      var val = this.LiftExpr(fastValOp);
      if (arr.Type.IsList)
        statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [arr, idx, this.ToType(val, arr.Type.Element!)])));
      else
        statements.Add(new IrStore(arr, idx, this.ToType(val, arr.Type.Element!)));
      return true;
    }

    if (node is { Name: "sassign", Children: [var valOp, var rawLhs] }) {
      var lhsOp = rawLhs switch {
        { Name: "null", Children: [var innerLhs] } => innerLhs,
        { Name: "null", Children: [{ Name: "null", Children: [{ Name: "aelemfast" } fastLhs] }, { Name: "null", Children: [] }] } => fastLhs,
        _ => rawLhs
      };
      // Check write to `$_[N]` -> PLY1730 (#436)
      if (lhsOp.Name == "aelemfast" && lhsOp.Symbol == "_")
        throw this.Fail(PerlFrontend.SubUnsupported, "writing to @_/argument aliasing");

      // Check reassigning closure local -> PLY1732 (#436)
      if (lhsOp is { Name: "padsv", Target: var t } && this._closureLocals.ContainsKey(t))
        throw this.Fail(PerlFrontend.EscapingClosure, this.LexicalName(t), cause: "OBJ-CLOSURES");

      // Assign to lexical `$x`
      if (lhsOp is { Name: "padsv", Target: var slot }) {
        var val = this.LiftExpr(valOp);
        this.EmitScalarAssign(slot, val, statements);
        return true;
      }

      // Assign to `$_` (topic variable)
      if (lhsOp is { Name: "gvsv", Symbol: "_" } && this._topicVar is not null) {
        var val = this.LiftExpr(valOp);
        statements.Add(new IrAssign(this._topicVar, this.ToType(val, this._topicVar.Type)));
        return true;
      }

      // Assign to array/hash element (`multideref` / `aelem` / `helem`)
      if (_indexedOps.Contains(lhsOp.Name)) {
        this.LowerIndexedStore(lhsOp, this.LiftExpr(valOp), statements);
        return true;
      }
    }

    // 2. Compound assignment (`$x += $y` where `OPf_STACKED` is set) or Perl's `OPpTARGET_MY` optimization (`$r = int(...)`, `$s1 = ... % 65521`)
    if (IsNumericBinaryOp(node.Name) && (node.Flags & this._tree.Stacked) != 0 && node.Children.Length == 2) {
      var targetOp = node.Children[0];
      var val = this.LowerNumericBinary(node.Name, targetOp, node.Children[1]);
      if (targetOp is { Name: "padsv", Target: var slot }) {
        this.EmitScalarAssign(slot, val, statements);
        return true;
      }
      if (targetOp.Name == "gvsv") {
        statements.Add(new IrAssign(this._topicVar!, this.ToType(val, this._topicVar!.Type)));
        return true;
      }
      if (_indexedOps.Contains(targetOp.Name)) {
        this.LowerIndexedStore(targetOp, val, statements);
        return true;
      }
    }
    if ((node.Private & this._tree.TargetMy) != 0 && node.Target > 0 && this.LexicalSigil(node.Target) == '$' &&
        !_assignOps.Contains(node.Name)) {
      var val = this.LiftExpr(node);
      this.EmitScalarAssign(node.Target, val, statements);
      return true;
    }

    // 3. `my @a = (LIST);` / `my %h = (...);` (`aassign`)
    if (node is { Name: "aassign", Children: [var rhsList, var lhsList] }) {
      return this.LowerListAssign(rhsList, lhsList, statements);
    }

    // 4. Uninitialized `my $x;` / `my @a;` / `my %h;` (`padsv` / `padav` / `padhv` in void context)
    if (node is { Name: "padsv", Target: var uSlot } && !this._variables.ContainsKey(uSlot)) {
      var local = this.DeclareLocal(this.LexicalName(uSlot), IrType.Optional(IrType.I64), uSlot);
      statements.Add(new IrDeclare(local, new IrNone(local.Type)));
      return true;
    }
    if (node is { Name: "padav", Target: var aSlot } && !this._variables.ContainsKey(aSlot)) {
      var local = this.DeclareLocal(this.LexicalName(aSlot), IrType.List(IrType.I64), aSlot);
      statements.Add(new IrDeclare(local, new IrCollection(IrCollectionOp.ListNew, local.Type, [])));
      return true;
    }
    if (node is { Name: "padhv", Target: var hSlot } && !this._variables.ContainsKey(hSlot)) {
      var local = this.DeclareLocal(this.LexicalName(hSlot), IrType.Map(IrType.String, IrType.I64, MapOrder.Insertion), hSlot);
      statements.Add(new IrDeclare(local, new IrCollection(IrCollectionOp.MapNew, local.Type, [])));
      return true;
    }

    return false;
  }

  private void EmitScalarAssign(int slot, IrExpr val, List<IrStmt> statements) {
    if (this._variables.TryGetValue(slot, out var existing)) {
      statements.Add(new IrAssign(existing, this.ToType(val, existing.Type)));
      return;
    }
    var local = this.DeclareLocal(this.LexicalName(slot), val.Type, slot);
    statements.Add(new IrDeclare(local, val));
  }

  private bool LowerListAssign(PerlOp rhsList, PerlOp lhsList, List<IrStmt> statements) {
    var rhsItems = StripMarks(UnwrapList(rhsList.Children));
    var lhsItems = StripMarks(UnwrapList(lhsList.Children));

    // Single array target: `my @a = (1, 2, 3);`
    if (lhsItems is [{ Name: "padav", Target: var arrSlot }]) {
      var exprs = rhsItems.Select(this.LiftExpr).ToArray();
      var elemType = exprs.Length > 0 ? exprs[0].Type : IrType.I64;
      var listType = IrType.List(elemType);
      var init = new IrCollection(IrCollectionOp.ListOf, listType, exprs.Select(e => this.ToType(e, elemType)).ToArray());
      if (this._variables.TryGetValue(arrSlot, out var existing))
        statements.Add(new IrAssign(existing, init));
      else {
        var local = this.DeclareLocal(this.LexicalName(arrSlot), listType, arrSlot);
        statements.Add(new IrDeclare(local, init));
      }
      return true;
    }

    // Single hash target: `my %h = (a => 1, b => 2);`
    if (lhsItems is [{ Name: "padhv", Target: var hashSlot }]) {
      var pairs = rhsItems.Select(this.LiftExpr).ToArray();
      var valType = pairs.Length >= 2 ? pairs[1].Type : IrType.I64;
      var mapType = IrType.Map(IrType.String, valType, MapOrder.Insertion);
      var local = this._variables.TryGetValue(hashSlot, out var ex)
        ? ex
        : this.DeclareLocal(this.LexicalName(hashSlot), mapType, hashSlot);
      if (ex is null)
        statements.Add(new IrDeclare(local, new IrCollection(IrCollectionOp.MapNew, mapType, [])));
      for (var i = 0; i + 1 < pairs.Length; i += 2) {
        statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void,
          [new IrLocalRef(local), pairs[i], this.ToType(pairs[i + 1], valType)])));
      }
      return true;
    }

    // Parallel scalar assignment `my ($x, $y) = (E1, E2);`
    if (lhsItems.All(l => l.Name == "padsv")) {
      var temps = new List<IrLocal>();
      for (var i = 0; i < lhsItems.Length; ++i) {
        var val = i < rhsItems.Length ? this.LiftExpr(rhsItems[i]) : new IrNone(IrType.Optional(IrType.I64));
        var t = this.TempLocal(val.Type, "_la");
        statements.Add(new IrDeclare(t, val));
        temps.Add(t);
      }
      for (var i = 0; i < lhsItems.Length; ++i)
        this.EmitScalarAssign(lhsItems[i].Target, new IrLocalRef(temps[i]), statements);
      return true;
    }

    return false;
  }

  private IrExpr LowerScalarContext(PerlOp inner) {
    var expr = this.LiftExpr(inner);
    if (expr.Type.IsList)
      return new IrCollection(IrCollectionOp.ListCount, IrType.I32, [expr]);
    if (expr.Type.IsArray)
      return new IrArrayLength(expr);
    if (expr.Type.Kind == IrTypeKind.Map)
      return new IrCollection(IrCollectionOp.MapCount, IrType.I32, [expr]);
    return expr;
  }

  private IrExpr LowerPadAvRead(int slot) {
    if (!this._variables.TryGetValue(slot, out var local))
      throw this.Fail(PerlFrontend.ArrayUnsupported, $"unknown array lexical slot {slot}");
    return new IrLocalRef(local);
  }

  private IrExpr LowerAnonList(PerlOp op) {
    var items = StripMarks(op.Children).Select(this.LiftExpr).ToArray();
    var elemType = items.Length > 0 ? items[0].Type : IrType.I64;
    var arrType = IrType.Array(elemType);
    return new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrType, items.Select(e => this.ToType(e, elemType)).ToArray());
  }

  private IrExpr LowerRefGen(PerlOp op) {
    var kids = StripMarks(UnwrapList(op.Children));
    if (kids is [{ Name: "gv" } or { Name: "rv2cv" }])
      throw this.Fail(PerlFrontend.EscapingClosure, "\\&sub", cause: "OBJ-CLOSURES");
    if (kids is [{ Name: "padav", Target: var aSlot }])
      return this.LowerPadAvRead(aSlot);
    if (kids is [{ Name: "padhv", Target: var hSlot }])
      return this.LowerPadHvRead(hSlot);
    if (kids is [{ Name: "padsv", Target: var sSlot }])
      return this.LowerPadSvRead(sSlot);
    throw this.Fail(PerlFrontend.ReferenceUnsupported, op.Name);
  }

  private IrExpr LowerScalarDeref(PerlOp op) {
    var inner = op.Children.FirstOrDefault();
    if (inner is { Name: "padsv", Target: var slot } && this._variables.TryGetValue(slot, out var local))
      return new IrLocalRef(local);
    throw this.Fail(PerlFrontend.ReferenceUnsupported, "symbolic or indirect scalar dereference");
  }

  private IrExpr LowerArrayDeref(PerlOp op) {
    var inner = op.Children[0];
    if (inner.Name == "padsv" && this._variables.TryGetValue(inner.Target, out var local)) {
      if (op.Want == "scalar")
        return local.Type.IsList
          ? new IrCollection(IrCollectionOp.ListCount, IrType.I32, [new IrLocalRef(local)])
          : new IrArrayLength(new IrLocalRef(local));
      return new IrLocalRef(local);
    }
    if (inner.Name == "gv") {
      if (inner.Symbol == "_")
        throw this.Fail(PerlFrontend.SubUnsupported, "@_ outside parameter unpacking");
      if (inner.Symbol is { } sym && _magicGlobals.Contains(sym))
        throw this.Fail(PerlFrontend.MagicUnsupported, $"@{sym}");
    }
    throw this.Fail(PerlFrontend.ReferenceUnsupported, "array dereference");
  }

  private static PerlOp UnwrapArrayDeref(PerlOp op)
    => op.Name == "rv2av" && op.Children.Length == 1 && op.Children[0].Name == "padsv" ? op.Children[0] : op;

  private IrExpr LowerLastIndex(PerlOp op) {
    var arr = this.LiftExpr(UnwrapArrayDeref(op.Children[0]));
    var len = arr.Type.IsList
      ? (IrExpr)new IrCollection(IrCollectionOp.ListCount, IrType.I32, [arr])
      : new IrArrayLength(arr);
    return new IrBinary(IrBinaryOp.Subtract, len, new IrConst(IrType.I32, 1));
  }

  private IrExpr LowerArrayElemRead(PerlOp op) {
    if (op.Name == "aelemfast_lex")
      return this.BuildIndexedRead(this.LowerPadAvRead(op.Target), new IrConst(IrType.I32, op.Private));
    if (op.Name == "aelemfast" && op.Symbol == "_")
      return new IrLocalRef(this._argParams[op.Private]);
    var arr = this.LiftExpr(UnwrapArrayDeref(op.Children[0]));
    var idx = this.ToType(this.LiftExpr(op.Children[1]), IrType.I32);
    return this.BuildIndexedRead(arr, idx);
  }

  private IrExpr BuildIndexedRead(IrExpr arr, IrExpr idx) {
    if (arr.Type.IsList) {
      var len = new IrCollection(IrCollectionOp.ListCount, IrType.I32, [arr]);
      var norm = new IrConditional(new IrBinary(IrBinaryOp.Less, idx, new IrConst(IrType.I32, 0)),
        new IrBinary(IrBinaryOp.Add, len, idx), idx);
      return new IrCollection(IrCollectionOp.ListGet, arr.Type.Element!, [arr, norm]);
    }
    if (arr.Type.IsArray) {
      var len = new IrArrayLength(arr);
      var norm = new IrConditional(new IrBinary(IrBinaryOp.Less, idx, new IrConst(IrType.I32, 0)),
        new IrBinary(IrBinaryOp.Add, len, idx), idx);
      return new IrArrayLoad(arr, norm);
    }
    throw this.Fail(PerlFrontend.ArrayUnsupported, $"indexing {arr.Type}");
  }

  private IrExpr LowerMultiDerefRead(PerlOp op) {
    if (this.TryLowerInsideOutRead(op, out var insideOut))
      return insideOut;

    var exprIdx = 0;
    var exprKids = op.Children.Where(c => c.Name != "null" || c.Children.Length > 0).ToArray();
    IrExpr? current = null;

    foreach (var step in op.Deref!) {
      if (step.Source is "padsv" or "padav" or "padhv")
        current = this.LowerPadSvRead(step.Target);
      else if (step.Source == "pop" && exprIdx < exprKids.Length)
        current = this.LiftExpr(exprKids[exprIdx++]);
      else if (step.Source == "gv" && step.Symbol is { } gsym && _magicGlobals.Contains(gsym))
        throw this.Fail(PerlFrontend.MagicUnsupported, $"%{gsym}/@{gsym}");

      if (current is null)
        throw this.Fail(PerlFrontend.ReferenceUnsupported, "multideref target");

      if (step.Kind == "array") {
        var idx = this.ResolveDerefIndex(step, exprKids, ref exprIdx, wantInt: true);
        current = this.BuildIndexedRead(current, this.ToType(idx, IrType.I32));
      } else if (step.Kind == "hash") {
        current = this.BuildHashRead(current, step, exprKids, ref exprIdx);
      }
    }

    return current!;
  }

  private void LowerIndexedStore(PerlOp lhsOp, IrExpr value, List<IrStmt> statements) {
    if (this.TryLowerInsideOutStore(lhsOp, value, statements))
      return;

    if (lhsOp is { Name: "multideref", Deref: { Length: > 0 } steps }) {
      var exprIdx = 0;
      var exprKids = lhsOp.Children.Where(c => c.Name != "null").ToArray();
      IrExpr? current = null;
      for (var i = 0; i < steps.Length; ++i) {
        var step = steps[i];
        if (step.Source is "padsv" or "padav" or "padhv")
          current = this.LowerPadSvRead(step.Target);
        if (current is null)
          throw this.Fail(PerlFrontend.ReferenceUnsupported, "multideref store target");

        var isLast = i == steps.Length - 1;
        if (step.Kind == "array") {
          var idx = this.ToType(this.ResolveDerefIndex(step, exprKids, ref exprIdx, wantInt: true), IrType.I32);
          if (!isLast) {
            current = this.BuildIndexedRead(current, idx);
          } else if (current.Type.IsList) {
            statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [current, idx, this.ToType(value, current.Type.Element!)])));
          } else {
            statements.Add(new IrStore(current, idx, this.ToType(value, current.Type.Element!)));
          }
        } else {
          if (!isLast) {
            current = this.BuildHashRead(current, step, exprKids, ref exprIdx);
          } else {
            this.EmitHashStore(current, step, exprKids, ref exprIdx, value, statements);
          }
        }
      }
      return;
    }

    if (lhsOp.Name is "aelem" or "aelemfast") {
      var arr = this.LiftExpr(UnwrapArrayDeref(lhsOp.Children[0]));
      var idx = this.ToType(this.LiftExpr(lhsOp.Children[1]), IrType.I32);
      if (arr.Type.IsList)
        statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [arr, idx, this.ToType(value, arr.Type.Element!)])));
      else
        statements.Add(new IrStore(arr, idx, this.ToType(value, arr.Type.Element!)));
      return;
    }

    var hash = this.LiftExpr(lhsOp.Children[0]);
    var key = this.LiftExpr(lhsOp.Children[1]);
    this.EmitDirectHashStore(hash, key, value, statements);
  }

  private IrExpr ResolveDerefIndex(PerlDerefStep step, PerlOp[] exprKids, ref int exprIdx, bool wantInt) {
    if (step.IndexKind == "const") {
      if (wantInt && long.TryParse(step.IndexValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
        return new IrConst(IrType.I32, iv);
      return new IrStrConst(step.IndexValue ?? "");
    }
    if (step.IndexKind == "padsv")
      return this.LowerPadSvRead(step.IndexTarget);
    if (step.IndexKind == "gvsv")
      return this.LowerGlobalScalarRead(step.IndexSymbol);
    return this.LiftExpr(exprKids[exprIdx++]);
  }

  private IrExpr LowerListMutation(PerlOp op) {
    var kids = StripMarks(op.Children);
    if (kids.Length == 0)
      throw this.Fail(PerlFrontend.ArrayUnsupported, op.Name);
    var target = this.LiftExpr(kids[0]);
    if (!target.Type.IsList)
      throw this.Fail(PerlFrontend.ArrayUnsupported, $"{op.Name} on non-list {target.Type}");

    var elemType = target.Type.Element!;
    if (op.Name == "push" && kids.Length >= 2)
      return new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [target, this.ToType(this.LiftExpr(kids[1]), elemType)]);
    if (op.Name == "pop")
      return new IrCollection(IrCollectionOp.ListRemoveLast, IrCollections.Absent(elemType)!, [target]);
    if (op.Name == "unshift" && kids.Length >= 2)
      return new IrCollection(IrCollectionOp.ListInsert, IrType.Void, [target, new IrConst(IrType.I32, 0), this.ToType(this.LiftExpr(kids[1]), elemType)]);
    if (op.Name == "shift")
      return new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [target, new IrConst(IrType.I32, 0)]);

    throw this.Fail(PerlFrontend.ArrayUnsupported, op.Name);
  }

  private IrExpr LowerReverse(PerlOp op) {
    if (op.Want == "scalar")
      throw this.Fail(PerlFrontend.ArrayUnsupported, "reverse in scalar context");
    throw this.Fail(PerlFrontend.ArrayUnsupported, "reverse");
  }

  private IrExpr LowerSort(PerlOp op) {
    // Allow `sort keys %h` (#439), refuse plain string sort `sort @a` (#437 -> PLY1720).
    if (op.Children.Any(c => c.Name == "keys"))
      return this.LiftExpr(op.Children.First(c => c.Name == "keys"));
    throw this.Fail(PerlFrontend.ArrayUnsupported, "sort without supported comparator");
  }

  private IrExpr LowerJoin(PerlOp op) {
    var kids = StripMarks(op.Children);
    if (kids.Length >= 2) {
      var sep = this.LiftExpr(kids[0]);
      if (sep.Type == IrType.String) {
        if (kids.Length == 2) {
          var arr = this.LiftExpr(kids[1]);
          if (arr.Type == IrType.Array(IrType.String))
            return new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [sep, arr]);
          if (arr.Type == IrType.List(IrType.String))
            return new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [sep, new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.String), [arr])]);
        }
        var elems = kids[1..].Select(k => this.ToStringExpr(this.LiftExpr(k))).ToArray();
        var arrExpr = new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), elems);
        return new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [sep, arrExpr]);
      }
    }
    throw this.Fail(PerlFrontend.ArrayUnsupported, "join");
  }

  private IrExpr LowerLength(PerlOp op) {
    var arg = this.LiftExpr(op.Children[0]);
    if (arg.Type == IrType.String)
      return new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [arg]);
    if (arg.Type.IsArray)
      return new IrArrayLength(arg);
    throw this.Fail(PerlFrontend.StringUnsupported, $"length on {arg.Type}");
  }

  private IrExpr LowerConcat(PerlOp op) {
    if (op.Name == "concat")
      return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [this.ToStringExpr(this.LiftExpr(op.Children[0])), this.ToStringExpr(this.LiftExpr(op.Children[1]))]);

    var spec = op.Concat!;
    var dynArgs = op.Children.Where(c => c.Name != "null").Select(this.LiftExpr).ToArray();
    var parts = new List<IrExpr>();
    var pos = 0;
    var argIdx = 0;
    foreach (var len in spec.Lengths) {
      if (len > 0 && pos + len <= spec.Template.Length) {
        parts.Add(new IrStrConst(spec.Template.Substring(pos, len)));
        pos += len;
      }
      if (argIdx < dynArgs.Length)
        parts.Add(this.ToStringExpr(dynArgs[argIdx++]));
    }
    if (pos < spec.Template.Length)
      parts.Add(new IrStrConst(spec.Template[pos..]));
    return parts.Count == 1 ? parts[0] : new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, parts);
  }

  private IrExpr ToStringExpr(IrExpr expr)
    => expr.Type == IrType.String ? expr
      : expr.Type.IsNumeric ? new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [expr])
      : throw this.Fail(PerlFrontend.StringUnsupported, $"string conversion of {expr.Type}");

  private IrExpr LowerRepeat(PerlOp op) {
    var s = this.LiftExpr(op.Children[0]);
    var n = this.ToType(this.LiftExpr(op.Children[1]), IrType.I32);
    if (s.Type == IrType.String)
      return new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [s, n]);
    throw this.Fail(PerlFrontend.ArrayUnsupported, "repeat");
  }

  private IrExpr LowerSubstr(PerlOp op) {
    var kids = StripMarks(op.Children);
    if (op.Name == "substr_left" && kids.Length == 2) {
      var s = this.LiftExpr(kids[0]);
      if (s.Type == IrType.String)
        return new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [s, new IrConst(IrType.I32, 0), this.ToType(this.LiftExpr(kids[1]), IrType.I32)]);
    }
    if (kids.Length >= 4)
      throw this.Fail(PerlFrontend.StringUnsupported, "4-argument substr");
    var str = this.LiftExpr(kids[0]);
    var off = this.ToType(this.LiftExpr(kids[1]), IrType.I32);
    if (str.Type == IrType.String) {
      return kids.Length == 2
        ? new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [str, off])
        : new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [str, off, this.ToType(this.LiftExpr(kids[2]), IrType.I32)]);
    }
    throw this.Fail(PerlFrontend.StringUnsupported, "substr");
  }

  private IrExpr LowerOrd(PerlOp op) {
    // Fast path: `ord(substr($bytes, $i, 1))` on a `u8[]` byte buffer (#437).
    var argOp = op.Children[0];
    if (argOp.Name == "null")
      argOp = argOp.Children[0];
    if (argOp.Name is "substr" or "substr_left") {
      var skids = StripMarks(argOp.Children);
      if (argOp.Name == "substr_left" && skids.Length == 2 && IsIntConst(skids[1], "1")) {
        var buf = this.LiftExpr(skids[0]);
        if (buf.Type == IrType.Array(IrType.U8))
          return new IrConvert(IrType.I32, new IrArrayLoad(buf, new IrConst(IrType.I32, 0)));
      }
      if (skids.Length == 3 && IsIntConst(skids[2], "1")) {
        var buf = this.LiftExpr(skids[0]);
        if (buf.Type == IrType.Array(IrType.U8))
          return new IrConvert(IrType.I32, new IrArrayLoad(buf, this.ToType(this.LiftExpr(skids[1]), IrType.I32)));
      }
    }

    var s = this.LiftExpr(op.Children[0]);
    if (s.Type == IrType.String) {
      var len = new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [s]);
      return new IrConditional(new IrBinary(IrBinaryOp.Equal, len, new IrConst(IrType.I32, 0)),
        new IrConst(IrType.I32, 0),
        new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [s, new IrConst(IrType.I32, 0)])));
    }
    throw this.Fail(PerlFrontend.StringUnsupported, "ord");
  }

  private IrExpr LowerChr(PerlOp op) {
    var n = this.ToType(this.LiftExpr(op.Children[0]), IrType.U16);
    return new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [n]);
  }

  private IrExpr LowerVec(PerlOp op) {
    var kids = StripMarks(op.Children);
    if (kids.Length == 3 && IsIntConst(kids[2], "8")) {
      var buf = this.LiftExpr(kids[0]);
      var idx = this.ToType(this.LiftExpr(kids[1]), IrType.I32);
      if (buf.Type == IrType.Array(IrType.U8))
        return new IrArrayLoad(buf, idx);
      if (buf.Type == IrType.String)
        return new IrConvert(IrType.U8, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [buf, idx]));
    }
    throw this.Fail(PerlFrontend.StringUnsupported, "vec");
  }

  private IrExpr LowerUnpack(PerlOp op) {
    var kids = StripMarks(op.Children);
    if (kids.Length < 2 || kids[0] is not { Name: "const", Constant: { Kind: "string", Value: var tmpl } })
      throw this.Fail(PerlFrontend.PackTemplateUnsupported, "<dynamic>", "template must be a constant string");

    var items = PerlPackTemplate.Parse(tmpl, out var error);
    if (error is not null)
      throw this.Fail(PerlFrontend.PackTemplateUnsupported, tmpl, error);

    var buf = this.LiftExpr(kids[1]);
    var first = items[0];
    if (first.Type == IrType.U8 && first.Count == -1 && buf.Type == IrType.Array(IrType.U8))
      return buf;
    if (first.Type is { Kind: IrTypeKind.U8 or IrTypeKind.I8 } && first.Count == 1 && buf.Type == IrType.Array(IrType.U8)) {
      var ld = new IrArrayLoad(buf, new IrConst(IrType.I32, 0));
      return first.Type == IrType.U8 ? ld : new IrConvert(IrType.I8, ld);
    }
    if (first.Type.Size is 2 or 4 or 8 && buf.Type == IrType.Array(IrType.U8)) {
      var loadOp = first.LittleEndian ? IrIntrinsicOp.LoadLittle : IrIntrinsicOp.LoadBig;
      return new IrIntrinsic(loadOp, first.Type, [buf, new IrConst(IrType.I32, 0)]);
    }
    throw this.Fail(PerlFrontend.PackTemplateUnsupported, tmpl, "unsupported buffer type or multi-field unpack");
  }

  private IrExpr LowerPack(PerlOp op) {
    var kids = StripMarks(op.Children);
    if (kids.Length < 1 || kids[0] is not { Name: "const", Constant: { Kind: "string", Value: var tmpl } })
      throw this.Fail(PerlFrontend.PackTemplateUnsupported, "<dynamic>", "template must be a constant string");

    var items = PerlPackTemplate.Parse(tmpl, out var error);
    if (error is not null)
      throw this.Fail(PerlFrontend.PackTemplateUnsupported, tmpl, error);

    if (items is [{ Type.Kind: IrTypeKind.U8, Count: 1 }] && kids.Length == 2) {
      var b = this.ToType(this.LiftExpr(kids[1]), IrType.U8);
      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [b]);
    }
    throw this.Fail(PerlFrontend.PackTemplateUnsupported, tmpl, "unsupported pack form");
  }

  private IrExpr LowerStringCompare(string name, PerlOp leftOp, PerlOp rightOp) {
    var l = this.ToStringExpr(this.LiftExpr(leftOp));
    var r = this.ToStringExpr(this.LiftExpr(rightOp));
    if (name == "seq") return new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [l, r]);
    if (name == "sne") return new IrUnary(IrUnaryOp.LogicalNot, new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [l, r]));
    var cmp = new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [l, r]);
    return name switch {
      "scmp" => cmp,
      "slt" => new IrBinary(IrBinaryOp.Less, cmp, new IrConst(IrType.I32, 0)),
      "sle" => new IrBinary(IrBinaryOp.LessOrEqual, cmp, new IrConst(IrType.I32, 0)),
      "sgt" => new IrBinary(IrBinaryOp.Greater, cmp, new IrConst(IrType.I32, 0)),
      _ => new IrBinary(IrBinaryOp.GreaterOrEqual, cmp, new IrConst(IrType.I32, 0))
    };
  }

}
