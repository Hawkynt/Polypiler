using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  private static bool IsIntConst(PerlExpr op, long value)
    => op is PerlNumber { Value: { Kind: PerlNumericKind.Integer } n } && n.Integer == value;

  private static readonly Dictionary<string, string> _compoundOps = new(StringComparer.Ordinal) {
    ["+="] = "+", ["-="] = "-", ["*="] = "*", ["/="] = "/", ["%="] = "%", ["&="] = "&", ["|="] = "|", ["^="] = "^",
    ["<<="] = "<<", [">>="] = ">>"
  };

  private bool TryLowerAssignmentStatement(PerlExpr node, List<IrStmt> statements) {
    switch (node) {
      // `my $x;` / `my @a;` / `my %h;` / `my ($x, $y);`
      case PerlDeclaration { Kind: PerlDeclarator.My or PerlDeclarator.State } declaration:
        foreach (var variable in declaration.Variables.Where(v => v.Lexical is not null))
          this.DeclareUninitialized(variable, statements);
        return true;
      case PerlDeclaration { Kind: PerlDeclarator.Our }:
        return true;
      case PerlAssign { Operator: "=" } assign:
        return this.LowerAssign(assign, statements);
      case PerlAssign { Operator: ".=" } append:
        return this.LowerAppend(append, statements);
      case PerlAssign compound when _compoundOps.TryGetValue(compound.Operator, out var op):
        return this.LowerCompoundAssign(compound, op, statements);
    }
    return false;
  }

  private void DeclareUninitialized(PerlVariable variable, List<IrStmt> statements) {
    if (this._variables.ContainsKey(variable.Lexical!))
      return;
    switch (variable.Sigil) {
      case '$': {
        var local = this.DeclareLocal(variable.Name, IrType.Optional(IrType.I64), variable.Lexical);
        statements.Add(new IrDeclare(local, new IrNone(local.Type)));
        break;
      }
      case '@': {
        var local = this.DeclareLocal(variable.Name, IrType.List(IrType.I64), variable.Lexical);
        statements.Add(new IrDeclare(local, new IrCollection(IrCollectionOp.ListNew, local.Type, [])));
        break;
      }
      default: {
        var local = this.DeclareLocal(variable.Name, IrType.Map(IrType.String, IrType.I64, MapOrder.Insertion), variable.Lexical);
        statements.Add(new IrDeclare(local, new IrCollection(IrCollectionOp.MapNew, local.Type, [])));
        break;
      }
    }
  }

  private bool LowerAssign(PerlAssign assign, List<IrStmt> statements) {
    var target = assign.Target;
    var value = assign.Value;

    // `my $x = EXPR;`
    if (target is PerlDeclaration { Parenthesized: false, Variables: [{ Sigil: '$', Lexical: { } slot } variable] } declaration) {
      if (declaration.Kind == PerlDeclarator.Our)
        throw this.Fail(PerlFrontend.MagicUnsupported, $"global scalar ${variable.Name}");
      if (this._closureLocals.ContainsKey(slot))
        throw this.Fail(PerlFrontend.EscapingClosure, variable.Name, cause: "OBJ-CLOSURES");
      if (value is PerlAnonSub anon) {
        var lambda = this.LowerClosureExpr(anon.Sub);
        var local = this.DeclareLocal(variable.Name, lambda.Type, slot);
        this._closureLocals[slot] = lambda;
        statements.Add(new IrDeclare(local, lambda));
        return true;
      }
      this.EmitScalarAssign(slot, variable.Name, this.LiftScalarValue(value), statements);
      return true;
    }

    // `my @a = (...)`, `my %h = (...)`, `my ($x, $y) = (...)`, `($x, $y) = ...`, `@a = ...`, `%h = ...`
    if (target is PerlDeclaration { Kind: PerlDeclarator.My or PerlDeclarator.State } listDeclaration)
      return this.LowerListAssign(value, listDeclaration.Variables.Cast<PerlExpr>().ToList(), statements);
    if (target is PerlList { Parenthesized: true } targetList)
      return this.LowerListAssign(value, targetList.Items, statements);
    if (target is PerlVariable { Sigil: '@' or '%', Lexical: not null } aggregate)
      return this.LowerListAssign(value, [aggregate], statements);

    // Write to `$_[N]` -> PLY1730 (#436)
    if (target is PerlElement { Aggregate: PerlVariable { Sigil: '@', Name: "_" } })
      throw this.Fail(PerlFrontend.SubUnsupported, "writing to @_/argument aliasing");

    if (target is PerlVariable { Sigil: '$', Lexical: { Kind: not PerlDeclarator.Our } lexical } scalar) {
      if (this._closureLocals.ContainsKey(lexical))
        throw this.Fail(PerlFrontend.EscapingClosure, scalar.Name, cause: "OBJ-CLOSURES");
      this.EmitScalarAssign(lexical, scalar.Name, this.LiftScalarValue(value), statements);
      return true;
    }

    if (target is PerlVariable { Sigil: '$', Name: "_" } && this._topicVar is not null) {
      var val = this.LiftScalarValue(value);
      statements.Add(new IrAssign(this._topicVar, this.ToType(val, this._topicVar.Type)));
      return true;
    }

    if (target is PerlElement element) {
      this.LowerIndexedStore(element, this.LiftScalarValue(value), statements);
      return true;
    }
    return false;
  }

  /// <summary>The value of the right side of a scalar assignment; an empty <c>[]</c> or <c>{}</c> is perl's empty anonymous aggregate.</summary>
  private IrExpr LiftScalarValue(PerlExpr value) => this.LiftExpr(value, PerlWant.Scalar);

  private bool LowerCompoundAssign(PerlAssign assign, string op, List<IrStmt> statements) {
    var name = this.OpName(op);
    var target = assign.Target;
    var current = this.LiftExpr(target, PerlWant.Scalar);
    var val = this.LowerNumericBinary(name, current, this.LiftExpr(assign.Value, PerlWant.Scalar));
    return this.StoreBack(target, val, statements);
  }

  private bool LowerAppend(PerlAssign assign, List<IrStmt> statements) {
    var current = this.LiftExpr(assign.Target, PerlWant.Scalar);
    var parts = new List<IrExpr> { this.ToStringExpr(current) };
    parts.AddRange(this.ConcatParts(assign.Value).Select(p => p is PerlString s ? (IrExpr)new IrStrConst(s.Value) : this.ToStringExpr(this.LiftExpr(p, PerlWant.Scalar))));
    return this.StoreBack(assign.Target, new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, parts), statements);
  }

  private bool StoreBack(PerlExpr target, IrExpr val, List<IrStmt> statements) {
    switch (target) {
      case PerlVariable { Sigil: '$', Lexical: { Kind: not PerlDeclarator.Our } lexical } scalar:
        this.EmitScalarAssign(lexical, scalar.Name, val, statements);
        return true;
      case PerlVariable { Sigil: '$', Name: "_" } when this._topicVar is not null:
        statements.Add(new IrAssign(this._topicVar, this.ToType(val, this._topicVar.Type)));
        return true;
      case PerlElement element:
        this.LowerIndexedStore(element, val, statements);
        return true;
    }
    throw this.Fail(PerlFrontend.ScalarUnsupported, "compound assignment target");
  }

  private void EmitScalarAssign(PerlLexical slot, string name, IrExpr val, List<IrStmt> statements) {
    if (this.TryLocal(slot, out var existing)) {
      statements.Add(new IrAssign(existing, this.ToType(val, existing.Type)));
      return;
    }
    var local = this.DeclareLocal(name, val.Type, slot);
    statements.Add(new IrDeclare(local, val));
  }

  private static IReadOnlyList<PerlExpr> ListItems(PerlExpr value) => value switch {
    PerlList list => list.Items,
    PerlWords words => [.. words.Words.Select(w => new PerlString(w) { Line = value.Line })],
    _ => [value]
  };

  private bool LowerListAssign(PerlExpr rhsList, IReadOnlyList<PerlExpr> lhsItems, List<IrStmt> statements) {
    var rhsItems = ListItems(rhsList);

    // A single array target: `my @a = (1, 2, 3);`
    if (lhsItems is [PerlVariable { Sigil: '@', Lexical: { } arrSlot } array]) {
      var exprs = rhsItems.Select(e => this.LiftExpr(e, PerlWant.List)).ToArray();
      var elemType = exprs.Length > 0 ? exprs[0].Type : IrType.I64;
      var listType = IrType.List(elemType);
      var init = new IrCollection(IrCollectionOp.ListOf, listType, exprs.Select(e => this.ToType(e, elemType)).ToArray());
      if (this.TryLocal(arrSlot, out var existing))
        statements.Add(new IrAssign(existing, init));
      else {
        var local = this.DeclareLocal(array.Name, listType, arrSlot);
        statements.Add(new IrDeclare(local, init));
      }
      return true;
    }

    // A single hash target: `my %h = (a => 1, b => 2);`
    if (lhsItems is [PerlVariable { Sigil: '%', Lexical: { } hashSlot } hash]) {
      var pairs = rhsItems.Select(e => this.LiftExpr(e, PerlWant.List)).ToArray();
      var valType = pairs.Length >= 2 ? pairs[1].Type : IrType.I64;
      var mapType = IrType.Map(IrType.String, valType, MapOrder.Insertion);
      var existed = this.TryLocal(hashSlot, out var local);
      if (!existed) {
        local = this.DeclareLocal(hash.Name, mapType, hashSlot);
        statements.Add(new IrDeclare(local, new IrCollection(IrCollectionOp.MapNew, mapType, [])));
      }
      for (var i = 0; i + 1 < pairs.Length; i += 2)
        statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void,
          [new IrLocalRef(local), pairs[i], this.ToType(pairs[i + 1], valType)])));
      return true;
    }

    // Parallel scalar assignment `my ($x, $y) = (E1, E2);`
    if (lhsItems.All(l => l is PerlVariable { Sigil: '$', Lexical: not null })) {
      var temps = new List<IrLocal>();
      for (var i = 0; i < lhsItems.Count; ++i) {
        var val = i < rhsItems.Count ? this.LiftExpr(rhsItems[i], PerlWant.List) : new IrNone(IrType.Optional(IrType.I64));
        var t = this.TempLocal(val.Type, "_la");
        statements.Add(new IrDeclare(t, val));
        temps.Add(t);
      }
      for (var i = 0; i < lhsItems.Count; ++i) {
        var variable = (PerlVariable)lhsItems[i];
        this.EmitScalarAssign(variable.Lexical!, variable.Name, new IrLocalRef(temps[i]), statements);
      }
      return true;
    }

    return false;
  }

  private IrExpr LowerScalarContext(PerlExpr inner) {
    var expr = this.LiftExpr(inner, PerlWant.Scalar);
    if (expr.Type.IsList)
      return new IrCollection(IrCollectionOp.ListCount, IrType.I32, [expr]);
    if (expr.Type.IsArray)
      return new IrArrayLength(expr);
    if (expr.Type.Kind == IrTypeKind.Map)
      return new IrCollection(IrCollectionOp.MapCount, IrType.I32, [expr]);
    return expr;
  }

  private IrExpr LowerPadAvRead(PerlLexical lexical, string name) {
    if (!this.TryLocal(lexical, out var local))
      throw this.Fail(PerlFrontend.ArrayUnsupported, $"the array @{name} of an enclosing scope");
    return new IrLocalRef(local);
  }

  private IrExpr LowerAnonList(PerlAnonArray op) {
    var items = op.Items.Select(e => this.LiftExpr(e, PerlWant.List)).ToArray();
    var elemType = items.Length > 0 ? items[0].Type : IrType.I64;
    var arrType = IrType.Array(elemType);
    return new IrIntrinsic(IrIntrinsicOp.ArrayOf, arrType, items.Select(e => this.ToType(e, elemType)).ToArray());
  }

  private IrExpr LowerRefGen(PerlExpr operand) {
    var inner = operand is PerlList { Parenthesized: true, Items: [var only] } ? only : operand;
    switch (inner) {
      case PerlCall { Ampersand: true, Arguments: null }:
        throw this.Fail(PerlFrontend.EscapingClosure, "\\&sub", cause: "OBJ-CLOSURES");
      case PerlVariable { Sigil: '@', Lexical: { } a } array:
        return this.LowerPadAvRead(a, array.Name);
      case PerlVariable { Sigil: '%', Lexical: { } h } hash:
        return this.LowerPadHvRead(h, hash.Name);
      case PerlVariable { Sigil: '$', Lexical: { } s }:
        if (this.TryLocal(s, out var local))
          return new IrLocalRef(local);
        throw this.Fail(PerlFrontend.ScalarUnsupported, "reference to an undeclared scalar");
    }
    throw this.Fail(PerlFrontend.ReferenceUnsupported, inner is PerlList ? "refgen" : "srefgen");
  }

  private IrExpr LowerDeref(PerlDeref deref, PerlWant want) {
    switch (deref.Sigil) {
      case '$':
        if (deref.Reference is PerlVariable { Sigil: '$', Lexical: { } lexical } && this.TryLocal(lexical, out var scalar))
          return new IrLocalRef(scalar);
        throw this.Fail(PerlFrontend.ReferenceUnsupported, "symbolic or indirect scalar dereference");
      case '@':
        if (deref.Reference is PerlVariable { Sigil: '$', Lexical: { } arrayRef } && this.TryLocal(arrayRef, out var local)) {
          if (want == PerlWant.Scalar)
            return local.Type.IsList
              ? new IrCollection(IrCollectionOp.ListCount, IrType.I32, [new IrLocalRef(local)])
              : new IrArrayLength(new IrLocalRef(local));
          return new IrLocalRef(local);
        }
        throw this.Fail(PerlFrontend.ReferenceUnsupported, "array dereference");
      case '%':
        if (deref.Reference is PerlVariable { Sigil: '$', Lexical: { } hashRef } && this.TryLocal(hashRef, out var map))
          return new IrLocalRef(map);
        throw this.Fail(PerlFrontend.ReferenceUnsupported, "hash dereference");
    }
    throw this.Fail(PerlFrontend.ReferenceUnsupported, $"{deref.Sigil} dereference");
  }

  private IrExpr LowerLastIndex(PerlLastIndex op) {
    var arr = op.Array is PerlDeref { Sigil: '@', Reference: PerlVariable { Sigil: '$' } reference }
      ? this.LiftExpr(reference, PerlWant.Scalar)
      : this.LiftExpr(op.Array, PerlWant.List);
    var len = arr.Type.IsList
      ? (IrExpr)new IrCollection(IrCollectionOp.ListCount, IrType.I32, [arr])
      : new IrArrayLength(arr);
    return new IrBinary(IrBinaryOp.Subtract, len, new IrConst(IrType.I32, 1));
  }

  // ---- elements ----

  /// <summary>The aggregate an element reads: a lexical array or hash, or the reference a subscript chain or an expression gives.</summary>
  private IrExpr LowerAggregate(PerlExpr aggregate) {
    switch (aggregate) {
      case PerlVariable { Sigil: '@', Lexical: { Kind: not PerlDeclarator.Our } a } array:
        return this.LowerPadAvRead(a, array.Name);
      case PerlVariable { Sigil: '%', Lexical: { Kind: not PerlDeclarator.Our } h } hash:
        return this.LowerPadHvRead(h, hash.Name);
      case PerlVariable { Sigil: '%' or '@' } global:
        if (_magicGlobals.Contains(ShortName(global.Name)))
          throw this.Fail(PerlFrontend.MagicUnsupported, $"%{ShortName(global.Name)}/@{ShortName(global.Name)}");
        if (global is { Sigil: '@', Name: "_" })
          throw this.Fail(PerlFrontend.SubUnsupported, "@_ outside parameter unpacking");
        throw this.Fail(PerlFrontend.ReferenceUnsupported, "multideref target");
      case PerlDeref { Sigil: '@' or '%', Reference: var reference }:
        return this.LiftExpr(reference, PerlWant.Scalar);
    }
    throw this.Fail(PerlFrontend.ReferenceUnsupported, "multideref target");
  }

  private IrExpr LowerElementRead(PerlElement element) {
    if (this.TryLowerInsideOutRead(element, out var insideOut))
      return insideOut;
    if (element is { Hash: false, Aggregate: PerlVariable { Sigil: '@', Name: "_", Lexical: null }, Index: PerlNumber { Value.IsIntegral: true } argIndex }
        && argIndex.Value.Integer >= 0 && argIndex.Value.Integer < this._argParams.Count)
      return new IrLocalRef(this._argParams[(int)argIndex.Value.Integer]);
    var current = this.LowerAggregate(element.Aggregate);
    if (!element.Hash) {
      var idx = this.ToType(this.LiftExpr(element.Index, PerlWant.Scalar), IrType.I32);
      return this.BuildIndexedRead(current, idx);
    }
    return this.BuildHashRead(current, element.Index, IsSimpleKey(element.Index));
  }

  /// <summary>Whether perl reads a key without evaluating an expression (a constant or a plain scalar), which its multideref op does.</summary>
  private static bool IsSimpleKey(PerlExpr key) => key is PerlString or PerlNumber or PerlVariable { Sigil: '$' };

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

  private void LowerIndexedStore(PerlElement element, IrExpr value, List<IrStmt> statements) {
    if (this.TryLowerInsideOutStore(element, value, statements))
      return;
    var current = this.LowerAggregate(element.Aggregate);
    if (!element.Hash) {
      var idx = this.ToType(this.LiftExpr(element.Index, PerlWant.Scalar), IrType.I32);
      if (current.Type.IsList)
        statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [current, idx, this.ToType(value, current.Type.Element!)])));
      else if (current.Type.IsArray)
        statements.Add(new IrStore(current, idx, this.ToType(value, current.Type.Element!)));
      else
        throw this.Fail(PerlFrontend.ArrayUnsupported, $"indexing {current.Type}");
      return;
    }
    this.EmitHashStore(current, element.Index, value, statements);
  }

  private IrExpr LowerListMutation(PerlCall op) {
    var kids = op.Arguments ?? [];
    if (kids.Count == 0)
      throw this.Fail(PerlFrontend.ArrayUnsupported, op.Name);
    var target = this.LiftExpr(kids[0], PerlWant.List);
    if (!target.Type.IsList)
      throw this.Fail(PerlFrontend.ArrayUnsupported, $"{op.Name} on non-list {target.Type}");

    var elemType = target.Type.Element!;
    if (op.Name == "push" && kids.Count >= 2)
      return new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [target, this.ToType(this.LiftExpr(kids[1], PerlWant.List), elemType)]);
    if (op.Name == "pop")
      return new IrCollection(IrCollectionOp.ListRemoveLast, IrCollections.Absent(elemType)!, [target]);
    if (op.Name == "unshift" && kids.Count >= 2)
      return new IrCollection(IrCollectionOp.ListInsert, IrType.Void, [target, new IrConst(IrType.I32, 0), this.ToType(this.LiftExpr(kids[1], PerlWant.List), elemType)]);
    if (op.Name == "shift")
      return new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [target, new IrConst(IrType.I32, 0)]);

    throw this.Fail(PerlFrontend.ArrayUnsupported, op.Name);
  }

  private IrExpr LowerJoin(PerlCall op) {
    var kids = op.Arguments ?? [];
    if (kids.Count >= 2) {
      var sep = this.LiftExpr(kids[0], PerlWant.Scalar);
      if (sep.Type == IrType.String) {
        if (kids.Count == 2) {
          var arr = this.LiftExpr(kids[1], PerlWant.List);
          if (arr.Type == IrType.Array(IrType.String))
            return new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [sep, arr]);
          if (arr.Type == IrType.List(IrType.String))
            return new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [sep, new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.String), [arr])]);
        }
        var elems = kids.Skip(1).Select(k => this.ToStringExpr(this.LiftExpr(k, PerlWant.List))).ToArray();
        var arrExpr = new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), elems);
        return new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [sep, arrExpr]);
      }
    }
    throw this.Fail(PerlFrontend.ArrayUnsupported, "join");
  }

  private IrExpr LowerLength(PerlCall op) {
    var arg = this.LiftExpr(op.Arguments![0], PerlWant.Scalar);
    if (arg.Type == IrType.String)
      return new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [arg]);
    if (arg.Type.IsArray)
      return new IrArrayLength(arg);
    throw this.Fail(PerlFrontend.StringUnsupported, $"length on {arg.Type}");
  }

  // ---- strings ----

  /// <summary>The parts a chain of <c>.</c> and interpolations concatenates, as perl's multiconcat op takes them: constants merged.</summary>
  private List<PerlExpr> ConcatParts(PerlExpr expression) {
    var parts = new List<PerlExpr>();
    void Add(PerlExpr e) {
      switch (e) {
        case PerlBinary { Operator: "." } concat when this.Fold(concat) is null:
          Add(concat.Left);
          AddOperand(concat.Right);
          return;
        case PerlInterpolation interpolation:
          foreach (var part in interpolation.Parts)
            AddOperand(part);
          return;
      }
      AddOperand(e);
    }
    void AddOperand(PerlExpr e) {
      if (e is PerlInterpolation nested) {
        foreach (var part in nested.Parts)
          AddOperand(part);
        return;
      }
      var folded = this.Fold(e);
      if (folded is not null && (folded.Text is not null || folded.Number is not null)) {
        var text = folded.AsString();
        if (parts.Count > 0 && parts[^1] is PerlString previous)
          parts[^1] = new PerlString(previous.Value + text) { Line = previous.Line };
        else
          parts.Add(new PerlString(text) { Line = e.Line });
        return;
      }
      parts.Add(e);
    }
    Add(expression);
    return parts;
  }

  private IrExpr LowerConcatParts(List<PerlExpr> parts) {
    var lowered = parts.Select(p => p is PerlString s ? this.LowerString(s.Value) : this.ToStringExpr(this.LiftExpr(p, PerlWant.Scalar))).ToList();
    return lowered.Count == 1 ? lowered[0] : new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, lowered);
  }

  private IrExpr ToStringExpr(IrExpr expr)
    => expr.Type == IrType.String ? expr
      : expr.Type.IsNumeric ? new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [expr])
      : throw this.Fail(PerlFrontend.StringUnsupported, $"string conversion of {expr.Type}");

  private IrExpr LowerRepeat(PerlBinary op) {
    if (op.Left is PerlList { Parenthesized: true } || op.Left is PerlWords)
      throw this.Fail(PerlFrontend.ArrayUnsupported, "repeat");
    var s = this.LiftExpr(op.Left, PerlWant.Scalar);
    var n = this.ToType(this.LiftExpr(op.Right, PerlWant.Scalar), IrType.I32);
    if (s.Type == IrType.String)
      return new IrIntrinsic(IrIntrinsicOp.StrRepeat, IrType.String, [s, n]);
    throw this.Fail(PerlFrontend.ArrayUnsupported, "repeat");
  }

  private IrExpr LowerSubstr(PerlCall op) {
    var kids = op.Arguments ?? [];
    if (kids.Count >= 4)
      throw this.Fail(PerlFrontend.StringUnsupported, "4-argument substr");
    if (kids.Count == 3 && IsIntConst(kids[1], 0)) {
      var s = this.LiftExpr(kids[0], PerlWant.Scalar);
      if (s.Type == IrType.String)
        return new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [s, new IrConst(IrType.I32, 0), this.ToType(this.LiftExpr(kids[2], PerlWant.Scalar), IrType.I32)]);
    }
    var str = this.LiftExpr(kids[0], PerlWant.Scalar);
    var off = this.ToType(this.LiftExpr(kids[1], PerlWant.Scalar), IrType.I32);
    if (str.Type == IrType.String) {
      return kids.Count == 2
        ? new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [str, off])
        : new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [str, off, this.ToType(this.LiftExpr(kids[2], PerlWant.Scalar), IrType.I32)]);
    }
    throw this.Fail(PerlFrontend.StringUnsupported, "substr");
  }

  private IrExpr LowerOrd(PerlCall op) {
    // Fast path: `ord(substr($bytes, $i, 1))` on a `u8[]` byte buffer (#437).
    var argOp = op.Arguments![0];
    if (argOp is PerlCall { Builtin: true, Name: "substr", Arguments: var skids }) {
      if (skids is [var buffer, var start, var length] && IsIntConst(length, 1)) {
        var buf = this.LiftExpr(buffer, PerlWant.Scalar);
        if (buf.Type == IrType.Array(IrType.U8))
          return new IrConvert(IrType.I32, new IrArrayLoad(buf, IsIntConst(start, 0) ? new IrConst(IrType.I32, 0) : this.ToType(this.LiftExpr(start, PerlWant.Scalar), IrType.I32)));
      }
    }

    var s = this.LiftExpr(argOp, PerlWant.Scalar);
    if (s.Type == IrType.String) {
      var len = new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [s]);
      return new IrConditional(new IrBinary(IrBinaryOp.Equal, len, new IrConst(IrType.I32, 0)),
        new IrConst(IrType.I32, 0),
        new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [s, new IrConst(IrType.I32, 0)])));
    }
    throw this.Fail(PerlFrontend.StringUnsupported, "ord");
  }

  private IrExpr LowerChr(PerlCall op) {
    var n = this.ToType(this.LiftExpr(op.Arguments![0], PerlWant.Scalar), IrType.U16);
    return new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [n]);
  }

  private IrExpr LowerVec(PerlCall op) {
    var kids = op.Arguments ?? [];
    if (kids.Count == 3 && IsIntConst(kids[2], 8)) {
      var buf = this.LiftExpr(kids[0], PerlWant.Scalar);
      var idx = this.ToType(this.LiftExpr(kids[1], PerlWant.Scalar), IrType.I32);
      if (buf.Type == IrType.Array(IrType.U8))
        return new IrArrayLoad(buf, idx);
      if (buf.Type == IrType.String)
        return new IrConvert(IrType.U8, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [buf, idx]));
    }
    throw this.Fail(PerlFrontend.StringUnsupported, "vec");
  }

  private string? ConstantTemplate(PerlExpr expression) => this.Fold(expression)?.Text;

  private IrExpr LowerUnpack(PerlCall op) {
    var kids = op.Arguments ?? [];
    if (kids.Count < 2 || this.ConstantTemplate(kids[0]) is not { } tmpl)
      throw this.Fail(PerlFrontend.PackTemplateUnsupported, "<dynamic>", "template must be a constant string");

    var items = PerlPackTemplate.Parse(tmpl, out var error);
    if (error is not null)
      throw this.Fail(PerlFrontend.PackTemplateUnsupported, tmpl, error);

    var buf = this.LiftExpr(kids[1], PerlWant.Scalar);
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

  private IrExpr LowerPack(PerlCall op) {
    var kids = op.Arguments ?? [];
    if (kids.Count < 1 || this.ConstantTemplate(kids[0]) is not { } tmpl)
      throw this.Fail(PerlFrontend.PackTemplateUnsupported, "<dynamic>", "template must be a constant string");

    var items = PerlPackTemplate.Parse(tmpl, out var error);
    if (error is not null)
      throw this.Fail(PerlFrontend.PackTemplateUnsupported, tmpl, error);

    if (items is [{ Type.Kind: IrTypeKind.U8, Count: 1 }] && kids.Count == 2) {
      var b = this.ToType(this.LiftExpr(kids[1], PerlWant.List), IrType.U8);
      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [b]);
    }
    throw this.Fail(PerlFrontend.PackTemplateUnsupported, tmpl, "unsupported pack form");
  }

  private IrExpr LowerStringCompare(string name, PerlExpr leftOp, PerlExpr rightOp) {
    var l = this.ToStringExpr(this.LiftExpr(leftOp, PerlWant.Scalar));
    var r = this.ToStringExpr(this.LiftExpr(rightOp, PerlWant.Scalar));
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
