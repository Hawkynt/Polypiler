using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  /// <summary>A call: a builtin by its name, or a sub.</summary>
  private IrExpr LowerCallExpr(PerlCall call, PerlWant want) {
    if (!call.Builtin)
      return this.LowerCall(call);
    switch (call.Name) {
      case "scalar":
        return this.LowerScalarContext(call.Arguments![0]);
      case "undef" when call.Arguments is null or { Count: 0 }:
        return new IrNone(IrType.Optional(IrType.I64));
      case "defined":
        return this.LowerDefined(call);
      case "exists":
        return this.LowerHashExistsOrDelete(call, isExists: true);
      case "delete":
        return this.LowerHashExistsOrDelete(call, isExists: false);
      case "wantarray":
        return new IrConst(IrType.Bool, this._contracts.ContextOf(this._currentPackage, this._currentSubName) == "list" ? 1 : 0);
      case "int" or "abs" or "sqrt":
        return this.LowerUnaryScalar(call.Name, call.Arguments![0]);
      case "push" or "pop" or "shift" or "unshift":
        return this.LowerListMutation(call);
      case "length":
        return this.LowerLength(call);
      case "substr":
        return this.LowerSubstr(call);
      case "ord":
        return this.LowerOrd(call);
      case "chr":
        return this.LowerChr(call);
      case "vec":
        return this.LowerVec(call);
      case "pack":
        return this.LowerPack(call);
      case "unpack":
        return this.LowerUnpack(call);
      case "join":
        return this.LowerJoin(call);
      case "reverse":
        throw this.Fail(PerlFrontend.ArrayUnsupported, want == PerlWant.Scalar ? "reverse in scalar context" : "reverse");
      case "sort":
        if (call.Block is null && call.Handle is null && call.Arguments is [PerlCall { Builtin: true, Name: "keys" } keys])
          return this.LiftExpr(keys, PerlWant.List);
        throw this.Fail(PerlFrontend.ArrayUnsupported, "sort without supported comparator");
      case "keys" or "values" or "each":
        return this.LowerHashInspection(call, want);
      case "bless":
        throw this.Fail(PerlFrontend.ObjectSystemUnsupported, "bless", cause: "OBJ-CLASSES");
      case "splice":
        throw this.Fail(PerlFrontend.ArrayUnsupported, "splice");
      case "ref":
        throw this.Fail(PerlFrontend.ReferenceUnsupported, "ref()");
      case "eval" or "evalbytes":
        throw this.Fail(PerlFrontend.ErrorHandlingUnsupported, "eval STRING");
      case "warn":
        throw this.Fail(PerlFrontend.ErrorHandlingUnsupported, "warn");
      case "exit":
        throw this.Fail(PerlFrontend.ErrorHandlingUnsupported, "exit");
      case "tie" or "untie" or "tied":
        throw this.Fail(PerlFrontend.MagicUnsupported, call.Name);
      case "die":
        throw this.Fail(PerlFrontend.ErrorHandlingUnsupported, "die as an expression");
      case "pow":
        throw this.Fail(PerlFrontend.ScalarUnsupported, "pow");
    }
    if (_unsupportedStringOps.Contains(call.Name))
      throw this.Fail(PerlFrontend.StringUnsupported, call.Name == "printf" ? "prtf" : call.Name);
    if (_unsupportedScalarOps.Contains(call.Name))
      throw this.Fail(PerlFrontend.ScalarUnsupported, call.Name);
    throw this.Fail(PerlFrontend.ScalarUnsupported, $"builtin {call.Name}");
  }

  /// <summary><c>$f-&gt;(ARGS)</c> on a lexical holding an anonymous sub.</summary>
  private IrExpr LowerCallReference(PerlCallReference call) {
    if (call.Code is PerlVariable { Sigil: '$', Lexical: { } lexical } && this.TryLocal(lexical, out var fnLocal) && fnLocal.Type.IsFunction) {
      var sig = fnLocal.Type.Signature!;
      var rawArgs = (call.Arguments ?? []).Select(a => this.LiftExpr(a, PerlWant.List)).ToArray();
      var argExprs = rawArgs.Select((a, idx) => idx < sig.Parameters.Count ? this.ToType(a, sig.Parameters[idx]) : a).ToArray();
      return new IrInvoke(sig.Result, new IrLocalRef(fnLocal), argExprs);
    }
    throw this.Fail(PerlFrontend.SubUnsupported, "dynamic subroutine call");
  }

  // ---- hashes ----

  private IrExpr LowerPadHvRead(PerlLexical lexical, string name) {
    if (this.TryLocal(lexical, out var local))
      return new IrLocalRef(local);
    throw this.Fail(PerlFrontend.HashOrderUnsupported, $"the hash %{name} of an enclosing scope");
  }

  private IrExpr LowerAnonHash(PerlAnonHash op) {
    var items = op.Items;
    if (items.Count == 0)
      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I64), []);
    if (items.Count % 2 != 0)
      throw this.Fail(PerlFrontend.HashOrderUnsupported, "odd-length hash constructor");

    var fieldNames = new List<string>();
    var fieldExprs = new List<IrExpr>();
    for (var i = 0; i < items.Count; i += 2) {
      if (this.Fold(items[i])?.Text is not { } key)
        throw this.Fail(PerlFrontend.HashOrderUnsupported, "non-constant key in record hash constructor");
      var val = this.LiftExpr(items[i + 1], PerlWant.List);
      var existing = fieldNames.IndexOf(key);
      if (existing >= 0) {
        fieldExprs[existing] = val;
      } else {
        fieldNames.Add(key);
        fieldExprs.Add(val);
      }
    }

    // Create or reuse an IrClassType matching the exact field names & types (reference semantics, #439).
    var shapeKey = string.Join("|", fieldNames.Zip(fieldExprs, (n, e) => $"{n}:{e.Type}"));
    if (!this._recordTypes.TryGetValue(shapeKey, out var cls)) {
      cls = new IrClassType($"PerlHash{this._recordTypes.Count + 1}");
      var fields = fieldNames.Zip(fieldExprs, (n, e) => new IrField(n, e.Type, false)).ToArray();
      cls.Define(fields);
      var ctorName = $"{cls.Name}__ctor";
      var ctor = new IrConstructor(ctorName);
      var selfParam = new IrLocal("self", IrType.Object(cls));
      var ctorParams = fields.Select(f => new IrLocal(f.Name, f.Type)).ToList();
      var ctorBody = fields.Zip(ctorParams, (f, p) => (IrStmt)new IrFieldStore(new IrLocalRef(selfParam), f, new IrLocalRef(p))).Append(new IrReturn(null)).ToArray();
      var ctorFn = new IrFunction(ctorName, false, IrType.Void, [selfParam, .. ctorParams], [], new(ctorBody));
      cls.DefineMembers([ctor], [], [], []);
      this._recordTypes[shapeKey] = cls;
      this._types.Add(cls);
      this._loweredFunctions[ctorName] = ctorFn;
    }

    return new IrNew(IrType.Object(cls), cls.Constructors[0], fieldExprs);
  }

  /// <summary>A key as an index value: a constant (a string, or a number spelled as one), or the value of the key expression.</summary>
  private IrExpr LowerKey(PerlExpr key) => this.Fold(key) is { } folded ? this.LowerString(folded.AsString()) : this.LiftExpr(key, PerlWant.Scalar);

  private IrExpr BuildHashRead(IrExpr target, PerlExpr key, bool simple) {
    if (target.Type.ClassType is { } cls && simple && this.Fold(key) is { } constant) {
      var field = cls.AllFields.FirstOrDefault(f => f.Name == constant.AsString());
      if (field is not null)
        return new IrFieldLoad(target, field);
      return new IrNone(IrType.Optional(IrType.I64));
    }
    if (target.Type.Kind == IrTypeKind.Map) {
      var k = this.LowerKey(key);
      return new IrCollection(IrCollectionOp.MapTryGet, IrCollections.Absent(target.Type.Element!)!, [target, this.ToType(k, target.Type.Key!)]);
    }
    throw this.Fail(PerlFrontend.HashOrderUnsupported, simple ? $"hash read on {target.Type}" : $"helem on {target.Type}");
  }

  private void EmitHashStore(IrExpr target, PerlExpr key, IrExpr value, List<IrStmt> statements) {
    if (target.Type.ClassType is { } cls && IsSimpleKey(key) && this.Fold(key) is { } constant) {
      var name = constant.AsString();
      var field = cls.AllFields.FirstOrDefault(f => f.Name == name) ?? throw this.Fail(PerlFrontend.HashOrderUnsupported, $"unknown record field '{name}'");
      statements.Add(new IrFieldStore(target, field, this.ToType(value, field.Type)));
      return;
    }
    if (target.Type.Kind == IrTypeKind.Map) {
      var k = this.ToType(this.LowerKey(key), target.Type.Key!);
      statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [target, k, this.ToType(value, target.Type.Element!)])));
      return;
    }
    throw this.Fail(PerlFrontend.HashOrderUnsupported, $"hash store on {target.Type}");
  }

  private IrExpr LowerHashExistsOrDelete(PerlCall op, bool isExists) {
    var colOp = isExists ? IrCollectionOp.MapContainsKey : IrCollectionOp.MapRemove;
    if (op.Arguments is [PerlElement { Hash: true } element]) {
      var hash = this.LowerAggregate(element.Aggregate);
      if (isExists && hash.Type.ClassType is { } cls && IsSimpleKey(element.Index) && this.Fold(element.Index) is { } key)
        return new IrConst(IrType.Bool, cls.Field(key.AsString()) is not null ? 1 : 0);
      if (hash.Type.Kind == IrTypeKind.Map)
        return new IrCollection(colOp, IrType.Bool, [hash, this.ToType(this.LowerKey(element.Index), hash.Type.Key!)]);
    }
    throw this.Fail(PerlFrontend.ArrayUnsupported, isExists ? "exists on non-hash" : "delete");
  }

  private IrExpr LowerHashInspection(PerlCall op, PerlWant want) {
    // `scalar(keys %h)` is allowed (#439), unsorted `keys %h` / `values %h` / `each %h` is PLY1741.
    if (op.Name == "keys" && want == PerlWant.Scalar && op.Arguments is [var argument]) {
      var hash = this.LiftExpr(argument, PerlWant.Scalar);
      if (hash.Type.Kind == IrTypeKind.Map)
        return new IrCollection(IrCollectionOp.MapCount, IrType.I32, [hash]);
    }
    throw this.Fail(PerlFrontend.HashOrderUnsupported, op.Name);
  }

  private IrExpr LowerDefined(PerlCall op) {
    var val = this.LiftExpr(op.Arguments![0], PerlWant.Scalar);
    if (val.Type.IsOptional)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNone(val));
    if (val.Type.IsNullable)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNull(val));
    return new IrConst(IrType.Bool, 1);
  }

  // ---- errors ----

  private void LowerDieStatement(PerlCall op, List<IrStmt> statements) {
    var kids = op.Arguments ?? [];
    if (kids.Count == 0) {
      statements.Add(new IrRethrow());
      return;
    }
    var arg = this.LiftExpr(kids[0], PerlWant.List);
    if (arg.Type.IsObject && arg.Type.ClassType is { } cls && !IrExceptionClass.IsException(cls))
      throw this.Fail(PerlFrontend.ErrorHandlingUnsupported, "die with non-string reference");

    var msgExpr = arg.Type == IrType.String ? arg : this.ToStringExpr(arg);
    var exc = new IrNew(IrType.Object(IrExceptionClass.Exception),
      IrExceptionClass.Constructor(IrExceptionClass.Exception, 1), [msgExpr]);
    statements.Add(new IrThrow(exc));
  }

  private void LowerEvalStatement(PerlEvalBlock eval, List<IrStmt> statements) {
    var bodyStmts = new List<IrStmt>();
    var saved = (this._line, this._integer);
    this.LowerStatements(eval.Body.Statements, bodyStmts, null, PerlWant.Void, allowImplicitReturn: false);
    (this._line, this._integer) = saved;

    if (this._evalErrorVar is null) {
      this._evalErrorVar = this.TempLocal(IrType.String, "_eval_err");
      statements.Add(new IrDeclare(this._evalErrorVar, new IrStrConst("")));
    }
    var errLocal = this._evalErrorVar;
    bodyStmts.Add(new IrAssign(errLocal, new IrStrConst("")));

    var caught = this.TempLocal(IrType.Object(IrExceptionClass.Exception), "_ex");
    var catchBody = new IrBlock([
      new IrAssign(errLocal, new IrFieldLoad(new IrLocalRef(caught), IrExceptionClass.Message))
    ]);
    statements.Add(new IrTry(new(bodyStmts), [new IrCatch(IrExceptionClass.Exception, caught, null, catchBody)], null));
  }

  private IrExpr LowerEvalExpr(PerlEvalBlock eval) {
    // `eval { ... }` returning an optional value (value on success, none on exception)
    var bodyOps = eval.Body.Statements.Where(s => !IsOpless(s)).ToList();
    var savedLocalCount = this._locals.Count;
    var bodyStmts = new List<IrStmt>();
    var last = bodyOps.LastOrDefault();
    var before = last is not null ? bodyOps.Take(bodyOps.Count - 1).ToList() : bodyOps;
    var saved = (this._line, this._integer);
    this.LowerStatements(before, bodyStmts, null, PerlWant.Void, allowImplicitReturn: false);
    IrExpr resultExpr;
    if (last is PerlExpressionStatement { Modifier: null } valueStatement) {
      this._line = valueStatement.Line;
      this._integer = valueStatement.Integer;
      resultExpr = this.LiftExpr(valueStatement.Expression, PerlWant.Scalar);
    } else if (last is null) {
      resultExpr = new IrConst(IrType.I64, 1);
    } else {
      throw this.Fail(PerlFrontend.ErrorHandlingUnsupported, "eval BLOCK without a value");
    }
    (this._line, this._integer) = saved;
    var innerLocals = this._locals.GetRange(savedLocalCount, this._locals.Count - savedLocalCount);
    this._locals.RemoveRange(savedLocalCount, this._locals.Count - savedLocalCount);

    var resType = resultExpr.Type.IsOptionalValue ? IrType.Optional(resultExpr.Type) : resultExpr.Type;
    var resLocal = new IrLocal($"_eval{++this._tempCounter}", resType);
    var errLocal = new IrLocal($"_eval_err{++this._tempCounter}", IrType.String);
    var caught = new IrLocal($"_ex{++this._tempCounter}", IrType.Object(IrExceptionClass.Exception));

    var tryBody = new List<IrStmt>(bodyStmts) {
      new IrAssign(resLocal, resultExpr.Type.IsOptionalValue ? new IrSome(resultExpr) : resultExpr),
      new IrAssign(errLocal, new IrStrConst(""))
    };
    var catchBody = new List<IrStmt> {
      new IrAssign(resLocal, resType.IsOptional ? new IrNone(resType) : DefaultExpr(resType)),
      new IrAssign(errLocal, new IrFieldLoad(new IrLocalRef(caught), IrExceptionClass.Message))
    };

    var thunkBody = new IrBlock([
      new IrDeclare(resLocal, resType.IsOptional ? new IrNone(resType) : DefaultExpr(resType)),
      new IrDeclare(errLocal, new IrStrConst("")),
      new IrTry(new(tryBody), [new IrCatch(IrExceptionClass.Exception, caught, null, new(catchBody))], null),
      new IrReturn(new IrLocalRef(resLocal))
    ]);
    var thunk = IrLambda.Of([], resType, [.. innerLocals, resLocal, errLocal, caught], thunkBody);
    return new IrInvoke(resType, thunk, []);
  }

  private void LowerTryCatch(PerlTry node, List<IrStmt> statements) {
    var tryStmts = new List<IrStmt>();
    var saved = (this._line, this._integer);
    this.LowerStatements(node.Body.Statements, tryStmts, null, PerlWant.Void, allowImplicitReturn: false);
    (this._line, this._integer) = saved;

    var catches = new List<IrCatch>();
    if (node.Catch is not null) {
      IrLocal? exLocal = null;
      if (node.CatchVariable is { Lexical: { } lexical } variable)
        exLocal = this.DeclareLocal(variable.Name, IrType.Object(IrExceptionClass.Exception), lexical);
      var catchBlock = this.LowerBlock(node.Catch, PerlWant.Void);
      catches.Add(new IrCatch(IrExceptionClass.Exception, exLocal, null, catchBlock));
    }
    var finallyBlock = node.Finally is null ? null : this.LowerBlock(node.Finally, PerlWant.Void);
    statements.Add(new IrTry(new(tryStmts), catches, finallyBlock));
  }

  // ---- closures ----

  private IrLambda LowerClosureExpr(PerlSub nested) {
    var savedSub = this._currentSub;
    var savedRet = this._currentReturnType;
    var savedVars = new Dictionary<PerlLexical, IrLocal>(this._variables, ReferenceEqualityComparer.Instance);
    var savedLocals = new List<IrLocal>(this._locals);
    var savedCaptured = this._captured;
    var savedLine = (this._line, this._integer);

    // Captured variables map from the outer sub by name.
    var outerByName = new Dictionary<string, IrLocal>(StringComparer.Ordinal);
    if (savedCaptured is not null)
      foreach (var (name, local) in savedCaptured)
        outerByName[name] = local;
    foreach (var v in this._variables.Values)
      outerByName[v.Name] = v;

    this._currentSub = nested;
    this._variables.Clear();
    this._locals.Clear();
    this._captured = outerByName;
    foreach (var lexical in nested.Unit.Lexicals)
      if (lexical.Sigil == '$' && outerByName.TryGetValue(lexical.Name, out var captured))
        this._variables[lexical] = captured;

    var bodyNodes = nested.Body!.Statements;
    var parameters = new List<IrLocal>();
    var consumed = new HashSet<PerlStatement>(ReferenceEqualityComparer.Instance);
    var bodyStmts = new List<IrStmt>();
    this._currentReturnType = IrType.I64;

    this.ExtractParameters(nested, bodyNodes, this._currentPackage, this._currentSubName + "_closure", parameters, consumed);
    this.LowerStatements(bodyNodes, bodyStmts, consumed, PerlWant.Unknown, allowImplicitReturn: true);
    if (bodyStmts.Count == 0 || bodyStmts[^1] is not IrReturn)
      bodyStmts.Add(new IrReturn(new IrConst(IrType.I64, 0)));

    var lambda = IrLambda.Of(parameters, this._currentReturnType, [.. this._locals], new(bodyStmts));

    this._currentSub = savedSub;
    this._currentReturnType = savedRet;
    this._variables.Clear();
    foreach (var kv in savedVars) this._variables[kv.Key] = kv.Value;
    this._locals.Clear();
    this._locals.AddRange(savedLocals);
    this._captured = savedCaptured;
    (this._line, this._integer) = savedLine;

    return lambda;
  }

}
