using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.VBScript.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VBScript.Lowering;

internal sealed partial class VBScriptLowerer {

  private void LowerBlockInto(IReadOnlyList<VbsStmt> statements, ProcContext ctx, List<IrStmt> output) {
    foreach (var stmt in statements) {
      if (stmt is VbsOnErrorStmt onErr) {
        this.LowerOnError(onErr, ctx, output);
        continue;
      }

      if (!ctx.ResumeNext || stmt is VbsDimDecl or VbsConstDecl or VbsExitStmt) {
        this.LowerStatement(stmt, ctx, output);
        continue;
      }

      // Under On Error Resume Next, each statement runs inside a try/catch that records Err and continues.
      var body = new List<IrStmt>();
      this.LowerStatement(stmt, ctx, body);
      var (errNum, errDesc) = this.EnsureErrGlobals();
      var exLocal = ctx.NewTemp(IrExceptionClass.Reference, "__ex");
      var catchBody = new List<IrStmt> {
        new IrIf(
          new IrBinary(IrBinaryOp.Equal, new IrGlobalRef(errNum), new IrConst(IrType.I32, 0)),
          new IrBlock([new IrGlobalAssign(errNum, new IrConst(IrType.I32, 11))]),
          null),
        new IrGlobalAssign(errDesc, new IrFieldLoad(new IrLocalRef(exLocal), IrExceptionClass.Message))
      };
      output.Add(new IrTry(new IrBlock(body), [new IrCatch(IrExceptionClass.Exception, exLocal, null, new IrBlock(catchBody))], null));
    }
  }

  private void LowerOnError(VbsOnErrorStmt onErr, ProcContext ctx, List<IrStmt> output) {
    var (errNum, errDesc) = this.EnsureErrGlobals();
    ctx.ResumeNext = onErr.ResumeNext;
    output.Add(new IrGlobalAssign(errNum, new IrConst(IrType.I32, 0)));
    output.Add(new IrGlobalAssign(errDesc, new IrStrConst("")));
  }

  private void LowerStatement(VbsStmt stmt, ProcContext ctx, List<IrStmt> output) {
    switch (stmt) {
      case VbsDimDecl dim:
        this.LowerDimBounds(dim, ctx, output);
        break;
      case VbsConstDecl c:
        ctx.LocalConsts[c.Name] = this.LowerExpr(c.Value, ctx);
        break;
      case VbsReDimStmt redim:
        this.LowerReDim(redim, ctx, output);
        break;
      case VbsAssignStmt assign:
        this.LowerAssign(assign, ctx, output);
        break;
      case VbsCallStmt call:
        this.LowerCallStmt(call, ctx, output);
        break;
      case VbsIfStmt ifStmt:
        this.LowerIf(ifStmt, ctx, output);
        break;
      case VbsSelectStmt sel:
        this.LowerSelect(sel, ctx, output);
        break;
      case VbsForStmt forStmt:
        this.LowerFor(forStmt, ctx, output);
        break;
      case VbsForEachStmt fe:
        this.LowerForEach(fe, ctx, output);
        break;
      case VbsDoLoopStmt loop:
        this.LowerDoLoop(loop, ctx, output);
        break;
      case VbsExitStmt exit:
        this.LowerExit(exit, ctx, output);
        break;
      case VbsUnsupportedStmt unsup:
        this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, unsup.Line, unsup.Column, unsup.Construct);
        break;
    }
  }

  private void LowerDimBounds(VbsDimDecl dim, ProcContext ctx, List<IrStmt> output) {
    foreach (var v in dim.Variables) {
      if (!v.IsArray || v.UpperBound is null)
        continue;

      var local = ctx.Locals[v.Name];
      var bound = this.Coerce(this.LowerExpr(v.UpperBound, ctx), IrType.I32);
      var length = new IrBinary(IrBinaryOp.Add, bound, new IrConst(IrType.I32, 1));
      output.Add(new IrAssign(local, this.AllocateArray(local.Type, length)));
    }
  }

  private void LowerReDim(VbsReDimStmt redim, ProcContext ctx, List<IrStmt> output) {
    foreach (var v in redim.Variables) {
      if (v.UpperBound is null) {
        this.AddDiagnostic(VBScriptFrontend.SyntaxError, v.Line, v.Column, $"ReDim of '{v.Name}' requires an upper bound");
        continue;
      }

      if (!ctx.Locals.TryGetValue(v.Name, out var local)) {
        this.AddDiagnostic(VBScriptFrontend.UnresolvedSymbol, v.Line, v.Column, v.Name);
        continue;
      }

      if (!local.Type.IsArray) {
        this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, v.Line, v.Column, $"ReDim on non-array '{v.Name}'");
        continue;
      }

      var bound = this.Coerce(this.LowerExpr(v.UpperBound, ctx), IrType.I32);
      var newLen = new IrBinary(IrBinaryOp.Add, bound, new IrConst(IrType.I32, 1));
      if (!redim.Preserve) {
        output.Add(new IrAssign(local, this.AllocateArray(local.Type, newLen)));
      } else {
        var helperName = this.EnsureReDimPreserveHelper(local.Type);
        output.Add(new IrAssign(local, new IrCall(helperName, local.Type, [new IrLocalRef(local), newLen])));
      }
    }
  }

  private void LowerAssign(VbsAssignStmt assign, ProcContext ctx, List<IrStmt> output) {
    if (assign.Target is VbsIdentExpr id) {
      // Assigning to enclosing Function / Property Get name sets the return value.
      if (ctx.ResultLocal is not null && id.Name.Equals(ctx.Proc.Decl.Name, StringComparison.OrdinalIgnoreCase)) {
        var retVal = this.Coerce(this.LowerExpr(assign.Value, ctx), ctx.ResultLocal.Type);
        output.Add(new IrAssign(ctx.ResultLocal, retVal));
        return;
      }

      if (ctx.Locals.TryGetValue(id.Name, out var local)) {
        ctx.UnassignedLocals.Remove(id.Name);
        var val = this.Coerce(this.LowerExpr(assign.Value, ctx), local.Type);
        output.Add(new IrAssign(local, val));
        return;
      }

      if (ctx.Proc.Owner is not null && ctx.Receiver is not null) {
        if (ctx.Proc.Owner.Fields.TryGetValue(id.Name, out var field)) {
          var val = this.Coerce(this.LowerExpr(assign.Value, ctx), field.Type);
          output.Add(new IrFieldStore(new IrLocalRef(ctx.Receiver), field, val));
          return;
        }
        if (ctx.Proc.Owner.Setters.TryGetValue(id.Name, out var setter)) {
          var val = this.Coerce(this.LowerExpr(assign.Value, ctx), setter.ParamTypes[0]);
          output.Add(new IrExprStmt(new IrMethodCall(IrType.Void, setter.Method!, new IrLocalRef(ctx.Receiver), [val])));
          return;
        }
      }

      if (this._globals.TryGetValue(id.Name, out var global)) {
        var val = this.Coerce(this.LowerExpr(assign.Value, ctx), global.Type);
        output.Add(new IrGlobalAssign(global, val));
        return;
      }

      this.AddDiagnostic(VBScriptFrontend.UnresolvedSymbol, id.Line, id.Column, id.Name);
      return;
    }

    if (assign.Target is VbsCallOrIndexExpr idx) {
      var arr = this.LowerExpr(idx.Target, ctx);
      if (!arr.Type.IsArray || idx.Arguments.Count != 1) {
        this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, idx.Line, idx.Column, "indexed assignment");
        return;
      }

      var index = this.Coerce(this.LowerExpr(idx.Arguments[0], ctx), IrType.I32);
      var value = this.Coerce(this.LowerExpr(assign.Value, ctx), arr.Type.Element!);
      output.Add(new IrStore(arr, index, value));
      return;
    }

    if (assign.Target is VbsMemberExpr mem) {
      var recv = this.LowerExpr(mem.Target, ctx);
      if (recv.Type.ClassType is { } cls && this._classes.TryGetValue(cls.Name, out var ci)) {
        if (ci.Fields.TryGetValue(mem.Member, out var field)) {
          var val = this.Coerce(this.LowerExpr(assign.Value, ctx), field.Type);
          output.Add(new IrFieldStore(recv, field, val));
          return;
        }
        if (ci.Setters.TryGetValue(mem.Member, out var setter)) {
          var val = this.Coerce(this.LowerExpr(assign.Value, ctx), setter.ParamTypes[0]);
          output.Add(new IrExprStmt(new IrMethodCall(IrType.Void, setter.Method!, recv, [val])));
          return;
        }
      }

      this.AddDiagnostic(VBScriptFrontend.UnresolvedSymbol, mem.Line, mem.Column, mem.Member);
      return;
    }

    this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, assign.Line, assign.Column, "assignment target");
  }

  private void LowerCallStmt(VbsCallStmt call, ProcContext ctx, List<IrStmt> output) {
    // Handle Err.Raise and Err.Clear statements.
    if (call.Target is VbsMemberExpr { Target: VbsIdentExpr { Name: var objName }, Member: var member }
        && objName.Equals("Err", StringComparison.OrdinalIgnoreCase)) {
      var (errNum, errDesc) = this.EnsureErrGlobals();
      if (member.Equals("Clear", StringComparison.OrdinalIgnoreCase)) {
        output.Add(new IrGlobalAssign(errNum, new IrConst(IrType.I32, 0)));
        output.Add(new IrGlobalAssign(errDesc, new IrStrConst("")));
        return;
      }
      if (member.Equals("Raise", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count >= 1) {
        var numExpr = this.Coerce(this.LowerExpr(call.Arguments[0], ctx), IrType.I32);
        IrExpr descExpr = call.Arguments.Count >= 3
          ? this.ToStringExpr(this.LowerExpr(call.Arguments[2], ctx))
          : new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [numExpr]);
        output.Add(new IrGlobalAssign(errNum, numExpr));
        output.Add(new IrGlobalAssign(errDesc, descExpr));
        var ctor = IrExceptionClass.Constructor(IrExceptionClass.Exception, 1);
        output.Add(new IrThrow(new IrNew(IrExceptionClass.Reference, ctor, [new IrGlobalRef(errDesc)])));
        return;
      }
    }

    var expr = this.LowerCallOrIndex(call.Target, call.Arguments, call.Line, call.Column, ctx);
    output.Add(new IrExprStmt(expr));
  }

  private void LowerIf(VbsIfStmt ifStmt, ProcContext ctx, List<IrStmt> output) {
    IrBlock? elseBlock = null;
    if (ifStmt.ElseBody is not null) {
      var stmts = new List<IrStmt>();
      this.LowerBlockInto(ifStmt.ElseBody, ctx, stmts);
      elseBlock = new IrBlock(stmts);
    }

    for (var i = ifStmt.ElseIfs.Count - 1; i >= 0; --i) {
      var ei = ifStmt.ElseIfs[i];
      var eiBody = new List<IrStmt>();
      this.LowerBlockInto(ei.Body, ctx, eiBody);
      elseBlock = new IrBlock([new IrIf(this.ToBoolExpr(this.LowerExpr(ei.Condition, ctx)), new IrBlock(eiBody), elseBlock)]);
    }

    var thenBody = new List<IrStmt>();
    this.LowerBlockInto(ifStmt.ThenBody, ctx, thenBody);
    output.Add(new IrIf(this.ToBoolExpr(this.LowerExpr(ifStmt.Condition, ctx)), new IrBlock(thenBody), elseBlock));
  }

  private void LowerSelect(VbsSelectStmt sel, ProcContext ctx, List<IrStmt> output) {
    var selExpr = this.LowerExpr(sel.Expression, ctx);
    var temp = ctx.NewTemp(selExpr.Type, "__sel");
    output.Add(new IrDeclare(temp, selExpr));

    IrBlock? elseBlock = null;
    if (sel.ElseBody is not null) {
      var stmts = new List<IrStmt>();
      this.LowerBlockInto(sel.ElseBody, ctx, stmts);
      elseBlock = new IrBlock(stmts);
    }

    for (var i = sel.Cases.Count - 1; i >= 0; --i) {
      var clause = sel.Cases[i];
      IrExpr? cond = null;
      foreach (var pat in clause.Expressions) {
        var patExpr = this.LowerExpr(pat, ctx);
        var eq = this.EmitEquality(new IrLocalRef(temp), patExpr, negate: false);
        cond = cond is null ? eq : new IrBinary(IrBinaryOp.LogicalOr, cond, eq);
      }

      var body = new List<IrStmt>();
      this.LowerBlockInto(clause.Body, ctx, body);
      elseBlock = new IrBlock([new IrIf(cond!, new IrBlock(body), elseBlock)]);
    }

    if (elseBlock is not null)
      output.AddRange(elseBlock.Statements);
  }

  private void LowerFor(VbsForStmt forStmt, ProcContext ctx, List<IrStmt> output) {
    var loopVar = ctx.Locals[forStmt.Variable];
    ctx.UnassignedLocals.Remove(forStmt.Variable);
    var loopType = loopVar.Type;

    var startExpr = this.Coerce(this.LowerExpr(forStmt.Start, ctx), loopType);
    var endExpr = this.Coerce(this.LowerExpr(forStmt.End, ctx), loopType);
    var endTemp = ctx.NewTemp(loopType, "__end");

    output.Add(new IrAssign(loopVar, startExpr));
    output.Add(new IrDeclare(endTemp, endExpr));

    IrExpr stepExpr;
    IrExpr cond;
    if (forStmt.Step is null) {
      stepExpr = loopType == IrType.F64
        ? new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1.0))
        : new IrConst(loopType, 1);
      cond = new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(loopVar), new IrLocalRef(endTemp));
    } else {
      var rawStep = this.Coerce(this.LowerExpr(forStmt.Step, ctx), loopType);
      var stepTemp = ctx.NewTemp(loopType, "__step");
      output.Add(new IrDeclare(stepTemp, rawStep));
      stepExpr = new IrLocalRef(stepTemp);
      var zero = this.DefaultValueOf(loopType);
      cond = new IrConditional(
        new IrBinary(IrBinaryOp.GreaterOrEqual, new IrLocalRef(stepTemp), zero),
        new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(loopVar), new IrLocalRef(endTemp)),
        new IrBinary(IrBinaryOp.GreaterOrEqual, new IrLocalRef(loopVar), new IrLocalRef(endTemp)));
    }

    var body = new List<IrStmt>();
    ctx.LoopStack.Push(VbsExitKind.For);
    this.LowerBlockInto(forStmt.Body, ctx, body);
    ctx.LoopStack.Pop();

    var stepBlock = new IrBlock([new IrAssign(loopVar, new IrBinary(IrBinaryOp.Add, new IrLocalRef(loopVar), stepExpr))]);
    output.Add(new IrFor(cond, new IrBlock(body), stepBlock));
  }

  private void LowerForEach(VbsForEachStmt fe, ProcContext ctx, List<IrStmt> output) {
    var arrExpr = this.LowerExpr(fe.Collection, ctx);
    if (!arrExpr.Type.IsArray) {
      this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, fe.Line, fe.Column, "For Each over non-array");
      return;
    }

    var arrTemp = ctx.NewTemp(arrExpr.Type, "__arr");
    var idxTemp = ctx.NewTemp(IrType.I32, "__idx");
    var itemLocal = ctx.Locals[fe.Variable];
    ctx.UnassignedLocals.Remove(fe.Variable);

    output.Add(new IrDeclare(arrTemp, arrExpr));
    output.Add(new IrDeclare(idxTemp, new IrConst(IrType.I32, 0)));

    var body = new List<IrStmt> {
      new IrAssign(itemLocal, this.Coerce(new IrArrayLoad(new IrLocalRef(arrTemp), new IrLocalRef(idxTemp)), itemLocal.Type))
    };
    ctx.LoopStack.Push(VbsExitKind.For);
    this.LowerBlockInto(fe.Body, ctx, body);
    ctx.LoopStack.Pop();

    var cond = new IrBinary(IrBinaryOp.Less, new IrLocalRef(idxTemp), new IrArrayLength(new IrLocalRef(arrTemp)));
    var step = new IrBlock([new IrAssign(idxTemp, new IrBinary(IrBinaryOp.Add, new IrLocalRef(idxTemp), new IrConst(IrType.I32, 1)))]);
    output.Add(new IrFor(cond, new IrBlock(body), step));
  }

  private void LowerDoLoop(VbsDoLoopStmt loop, ProcContext ctx, List<IrStmt> output) {
    var body = new List<IrStmt>();
    ctx.LoopStack.Push(VbsExitKind.Do);
    this.LowerBlockInto(loop.Body, ctx, body);
    ctx.LoopStack.Pop();

    IrExpr cond = loop.Condition is null
      ? new IrConst(IrType.Bool, 1)
      : this.ToBoolExpr(this.LowerExpr(loop.Condition, ctx));
    if (loop.IsUntil)
      cond = new IrUnary(IrUnaryOp.LogicalNot, cond);

    output.Add(loop.IsPostCondition ? new IrDoWhile(new IrBlock(body), cond) : new IrWhile(cond, new IrBlock(body)));
  }

  private void LowerExit(VbsExitStmt exit, ProcContext ctx, List<IrStmt> output) {
    switch (exit.Kind) {
      case VbsExitKind.Function:
      case VbsExitKind.Property when ctx.ResultLocal is not null:
        output.Add(new IrReturn(new IrLocalRef(ctx.ResultLocal!)));
        break;
      case VbsExitKind.Sub:
      case VbsExitKind.Property:
        output.Add(new IrReturn(null));
        break;
      case VbsExitKind.For or VbsExitKind.Do:
        if (ctx.LoopStack.Count == 0 || ctx.LoopStack.Peek() != exit.Kind) {
          this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, exit.Line, exit.Column, $"Exit {exit.Kind}");
          return;
        }
        output.Add(new IrBreak());
        break;
    }
  }

}
