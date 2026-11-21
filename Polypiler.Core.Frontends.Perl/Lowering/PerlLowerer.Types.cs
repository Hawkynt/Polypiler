using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  private IrExpr LowerPadHvRead(int slot) {
    if (this._variables.TryGetValue(slot, out var local))
      return new IrLocalRef(local);
    throw this.Fail(PerlFrontend.HashOrderUnsupported, $"hash lexical slot {slot}");
  }

  private IrExpr LowerAnonHash(PerlOp op) {
    var items = StripMarks(op.Children);
    if (items.Length % 2 != 0)
      throw this.Fail(PerlFrontend.HashOrderUnsupported, "odd-length hash constructor");

    var fieldNames = new List<string>();
    var fieldExprs = new List<IrExpr>();
    for (var i = 0; i < items.Length; i += 2) {
      if (items[i] is not { Name: "const", Constant: { Kind: "string", Value: var key } })
        throw this.Fail(PerlFrontend.HashOrderUnsupported, "non-constant key in record hash constructor");
      var val = this.LiftExpr(items[i + 1]);
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

  private IrExpr BuildHashRead(IrExpr target, PerlDerefStep step, PerlOp[] exprKids, ref int exprIdx) {
    if (target.Type.ClassType is { } cls && step.IndexKind == "const" && step.IndexValue is { } key) {
      var field = cls.Field(key);
      if (field is not null)
        return new IrFieldLoad(target, field);
      return new IrNone(IrType.Optional(IrType.I64));
    }
    if (target.Type.Kind == IrTypeKind.Map) {
      var k = this.ResolveDerefIndex(step, exprKids, ref exprIdx, wantInt: false);
      return new IrCollection(IrCollectionOp.MapTryGet, IrCollections.Absent(target.Type.Element!)!, [target, this.ToType(k, target.Type.Key!)]);
    }
    throw this.Fail(PerlFrontend.HashOrderUnsupported, $"hash read on {target.Type}");
  }

  private IrExpr LowerHashElemRead(PerlOp op) {
    var hash = this.LiftExpr(op.Children[0]);
    if (hash.Type.Kind == IrTypeKind.Map) {
      var k = this.ToType(this.LiftExpr(op.Children[1]), hash.Type.Key!);
      return new IrCollection(IrCollectionOp.MapTryGet, IrCollections.Absent(hash.Type.Element!)!, [hash, k]);
    }
    throw this.Fail(PerlFrontend.HashOrderUnsupported, $"helem on {hash.Type}");
  }

  private void EmitHashStore(IrExpr target, PerlDerefStep step, PerlOp[] exprKids, ref int exprIdx, IrExpr value, List<IrStmt> statements) {
    if (target.Type.ClassType is { } cls && step.IndexKind == "const" && step.IndexValue is { } key) {
      var field = cls.Field(key) ?? throw this.Fail(PerlFrontend.HashOrderUnsupported, $"unknown record field '{key}'");
      statements.Add(new IrFieldStore(target, field, this.ToType(value, field.Type)));
      return;
    }
    if (target.Type.Kind == IrTypeKind.Map) {
      var k = this.ToType(this.ResolveDerefIndex(step, exprKids, ref exprIdx, wantInt: false), target.Type.Key!);
      statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [target, k, this.ToType(value, target.Type.Element!)])));
      return;
    }
    throw this.Fail(PerlFrontend.HashOrderUnsupported, $"hash store on {target.Type}");
  }

  private void EmitDirectHashStore(IrExpr hash, IrExpr key, IrExpr value, List<IrStmt> statements) {
    if (hash.Type.Kind == IrTypeKind.Map) {
      statements.Add(new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void,
        [hash, this.ToType(key, hash.Type.Key!), this.ToType(value, hash.Type.Element!)])));
      return;
    }
    throw this.Fail(PerlFrontend.HashOrderUnsupported, $"hash store on {hash.Type}");
  }

  private IrExpr LowerExists(PerlOp op) => this.LowerHashExistsOrDelete(op, isExists: true);

  private IrExpr LowerDelete(PerlOp op) => this.LowerHashExistsOrDelete(op, isExists: false);

  private IrExpr LowerHashExistsOrDelete(PerlOp op, bool isExists) {
    var colOp = isExists ? IrCollectionOp.MapContainsKey : IrCollectionOp.MapRemove;
    var inner = op.Name == "multideref" ? op : op.Children[0];
    if (inner.Name is "helem" or "null" && inner.Children.Length == 2) {
      var hash = this.LiftExpr(inner.Children[0]);
      if (hash.Type.Kind == IrTypeKind.Map)
        return new IrCollection(colOp, IrType.Bool, [hash, this.ToType(this.LiftExpr(inner.Children[1]), hash.Type.Key!)]);
    }
    if (inner is { Name: "multideref", Deref: [var step] } md && step.Kind == "hash") {
      var exprIdx = 0;
      var exprKids = md.Children.Where(c => c.Name != "null" || c.Children.Length > 0).ToArray();
      var hash = step.Source == "pop" && exprIdx < exprKids.Length
        ? this.LiftExpr(exprKids[exprIdx++])
        : this.LowerPadSvRead(step.Target);
      if (isExists && hash.Type.ClassType is { } cls && step.IndexKind == "const" && step.IndexValue is { } key)
        return new IrConst(IrType.Bool, cls.Field(key) is not null ? 1 : 0);
      if (hash.Type.Kind == IrTypeKind.Map) {
        var k = this.ResolveDerefIndex(step, exprKids, ref exprIdx, wantInt: false);
        return new IrCollection(colOp, IrType.Bool, [hash, this.ToType(k, hash.Type.Key!)]);
      }
    }
    throw this.Fail(PerlFrontend.ArrayUnsupported, isExists ? "exists on non-hash" : "delete");
  }

  private IrExpr LowerHashInspection(PerlOp op) {
    // `scalar(keys %h)` is allowed (#439), unsorted `keys %h` / `values %h` / `each %h` is PLY1741.
    if (op.Name == "keys" && op.Want == "scalar") {
      var hash = this.LiftExpr(op.Children[0]);
      if (hash.Type.Kind == IrTypeKind.Map)
        return new IrCollection(IrCollectionOp.MapCount, IrType.I32, [hash]);
    }
    throw this.Fail(PerlFrontend.HashOrderUnsupported, op.Name);
  }

  private IrExpr LowerDefined(PerlOp op) {
    var val = this.LiftExpr(op.Children[0]);
    if (val.Type.IsOptional)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNone(val));
    if (val.Type.IsNullable)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNull(val));
    return new IrConst(IrType.Bool, 1);
  }

  private void LowerDieStatement(PerlOp op, List<IrStmt> statements) {
    var kids = StripMarks(op.Children);
    if (kids.Length == 0) {
      statements.Add(new IrRethrow());
      return;
    }
    var arg = this.LiftExpr(kids[0]);
    if (arg.Type.IsObject && arg.Type.ClassType is { } cls && !IrExceptionClass.IsException(cls))
      throw this.Fail(PerlFrontend.ErrorHandlingUnsupported, "die with non-string reference");

    var msgExpr = arg.Type == IrType.String ? arg : this.ToStringExpr(arg);
    var exc = new IrNew(IrType.Object(IrExceptionClass.Exception),
      IrExceptionClass.Constructor(IrExceptionClass.Exception, 1), [msgExpr]);
    statements.Add(new IrThrow(exc));
  }

  private void LowerEvalStatement(PerlOp leaveTry, List<IrStmt> statements) {
    var bodyOps = leaveTry.Children.Where(c => c.Name != "entertry").ToArray();
    var bodyStmts = new List<IrStmt>();
    this.LowerStatements(bodyOps, bodyStmts, null, allowImplicitReturn: false);

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

  private IrExpr LowerEvalExpr(PerlOp leaveTry) {
    // `eval { ... }` returning an optional value (value on success, none on exception)
    var bodyOps = leaveTry.Children.Where(c => c.Name != "entertry").ToArray();
    var savedLocalCount = this._locals.Count;
    var bodyStmts = new List<IrStmt>();
    var last = bodyOps.LastOrDefault(o => o.Name != "nextstate");
    var before = last is not null ? bodyOps.TakeWhile(o => !ReferenceEquals(o, last)).ToArray() : bodyOps;
    this.LowerStatements(before, bodyStmts, null, allowImplicitReturn: false);
    var resultExpr = last is not null ? this.LiftExpr(last) : new IrConst(IrType.I64, 1);
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

  private bool TryLowerTryCatchFinally(PerlOp leaveOp, List<IrStmt> statements) {
    var tryCatch = leaveOp.Children.FirstOrDefault(c => c.Name == "leavetrycatch");
    if (tryCatch is null)
      return false;

    var pushDefer = leaveOp.Children.FirstOrDefault(c => c.Name == "pushdefer");
    IrBlock? finallyBlock = null;
    if (pushDefer is not null) {
      var deferBody = pushDefer.Children.FirstOrDefault();
      if (deferBody is not null)
        finallyBlock = this.LowerSingleBody(deferBody);
    }

    this.LowerTryCatch(tryCatch, finallyBlock, statements);
    return true;
  }

  private void LowerTryCatch(PerlOp leaveTryCatch, IrBlock? finallyBlock, List<IrStmt> statements) {
    var popTry = leaveTryCatch.Children.FirstOrDefault(c => c.Name == "poptry");
    var catchOp = leaveTryCatch.Children.FirstOrDefault(c => c.Name == "catch");

    var tryStmts = new List<IrStmt>();
    if (popTry is not null)
      this.LowerStatements(popTry.Children, tryStmts, null, allowImplicitReturn: false);

    var catches = new List<IrCatch>();
    if (catchOp is not null) {
      IrLocal? exLocal = null;
      if (catchOp.Target > 0) {
        var name = this.LexicalName(catchOp.Target);
        exLocal = this.DeclareLocal(name, IrType.Object(IrExceptionClass.Exception), catchOp.Target);
      }
      var catchBodyOp = catchOp.Children.LastOrDefault();
      var catchBlock = catchBodyOp is not null ? this.LowerSingleBody(catchBodyOp) : new IrBlock([]);
      catches.Add(new IrCatch(IrExceptionClass.Exception, exLocal, null, catchBlock));
    }

    statements.Add(new IrTry(new(tryStmts), catches, finallyBlock));
  }

  private IrLambda LowerClosureExpr(PerlSubroutine nested) {
    var savedSub = this._currentSub;
    var savedRet = this._currentReturnType;
    var savedVars = new Dictionary<int, IrLocal>(this._variables);
    var savedLocals = new List<IrLocal>(this._locals);

    // Map captured variables from outer sub by lexical name.
    var outerByName = new Dictionary<string, IrLocal>(StringComparer.Ordinal);
    foreach (var v in this._variables.Values)
      outerByName[v.Name] = v;

    this._currentSub = nested;
    this._variables.Clear();
    this._locals.Clear();
    for (var slot = 1; slot < nested.Lexicals.Length; ++slot) {
      if (nested.Lexicals[slot] is { Length: > 1 } raw && raw[0] == '$' && outerByName.TryGetValue(raw[1..], out var captured))
        this._variables[slot] = captured;
    }

    var bodyNodes = nested.Body!.Children[0].Name == "lineseq" ? nested.Body.Children[0].Children : nested.Body.Children;
    var parameters = new List<IrLocal>();
    var consumed = new HashSet<PerlOp>(ReferenceEqualityComparer.Instance);
    var bodyStmts = new List<IrStmt>();
    this._currentReturnType = IrType.I64;

    this.ExtractParameters(bodyNodes, this._currentPackage, this._currentSubName + "_closure", parameters, consumed, bodyStmts);
    this.LowerStatements(bodyNodes, bodyStmts, consumed, allowImplicitReturn: true);
    if (bodyStmts.Count == 0 || bodyStmts[^1] is not IrReturn)
      bodyStmts.Add(new IrReturn(new IrConst(IrType.I64, 0)));

    var lambda = IrLambda.Of(parameters, this._currentReturnType, [.. this._locals], new(bodyStmts));

    this._currentSub = savedSub;
    this._currentReturnType = savedRet;
    this._variables.Clear();
    foreach (var kv in savedVars) this._variables[kv.Key] = kv.Value;
    this._locals.Clear();
    this._locals.AddRange(savedLocals);

    return lambda;
  }

}
