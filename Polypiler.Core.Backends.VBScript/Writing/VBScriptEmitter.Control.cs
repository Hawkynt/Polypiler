using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VBScript;

public static partial class VBScriptEmitter {

  private sealed partial class Writer {

    private void EmitBlock(IrBlock block, StringBuilder text, int indent) {
      for (var i = 0; i < block.Statements.Count; ++i) {
        var stmt = block.Statements[i];

        // Emit idiomatic VBScript `For i = start To bound ... Next` for simple counted loops outside try blocks.
        if (this._tryBodyDepth == 0
            && stmt is IrDeclare { Initializer: not null } decl
            && i + 1 < block.Statements.Count
            && block.Statements[i + 1] is IrFor nextFor
            && this.TryEmitForToNext(decl, nextFor, text, indent)) {
          ++i;
          continue;
        }

        this.EmitStatement(stmt, text, indent);
        if (this._tryBodyDepth > 0 && stmt is not (IrReturn or IrBreak or IrContinue or IrYieldBreak))
          text.Append(Indent(indent)).Append("If Err.Number <> 0 Then Exit Do\n");
      }
    }

    private void EmitStatement(IrStmt statement, StringBuilder text, int indent) {
      var pad = Indent(indent);
      switch (statement) {
        case IrBlock inner:
          this.EmitBlock(inner, text, indent);
          break;

        case IrDeclare declare: {
          var value = declare.Initializer is { } init
            ? this.CopyIfStruct(declare.Local.Type, this.EmitExpr(init, text, indent), text, indent)
            : this.DefaultValue(declare.Local.Type);
          this.EmitLocalInit(declare.Local, value, text, indent);
          break;
        }

        case IrAssign assign: {
          var value = this.CopyIfStruct(assign.Local.Type, this.EmitExpr(assign.Value, text, indent), text, indent);
          this.EmitLocalWrite(assign.Local, value, text, indent);
          break;
        }

        case IrGlobalAssign globalAssign: {
          var value = this.CopyIfStruct(globalAssign.Global.Type, this.EmitExpr(globalAssign.Value, text, indent), text, indent);
          text.Append(pad).Append(AssignPrefix(globalAssign.Global.Type)).Append(this._globalNames[globalAssign.Global]).Append(" = ").Append(value).Append('\n');
          break;
        }

        case IrStore store:
          this.EmitStore(store, text, indent);
          break;

        case IrFieldStore fieldStore:
          this.EmitFieldStore(fieldStore, text, indent);
          break;

        case IrExprStmt exprStmt:
          this.EmitExprStatement(exprStmt.Expression, text, indent);
          break;

        case IrIf @if:
          this.EmitIf(@if, text, indent);
          break;

        case IrWhile @while:
          this.EmitWhile(@while, text, indent);
          break;

        case IrDoWhile doWhile:
          this.EmitDoWhile(doWhile, text, indent);
          break;

        case IrFor @for:
          this.EmitGeneralFor(@for, text, indent);
          break;

        case IrSwitch @switch:
          this.EmitSwitch(@switch, text, indent);
          break;

        case IrBreak:
          this.EmitBreak(text, indent);
          break;

        case IrContinue:
          this.EmitContinue(text, indent);
          break;

        case IrReturn ret:
          this.EmitReturn(ret, text, indent);
          break;

        case IrThrow @throw: {
          this.Use(VBScriptHelpers.ErrSupport);
          var exExpr = this.EmitExpr(@throw.Exception, text, indent);
          text.Append(pad).Append("Call ply_throw(").Append(exExpr).Append(")\n");
          break;
        }

        case IrRethrow:
          this.Use(VBScriptHelpers.ErrSupport);
          text.Append(pad).Append("Call ply_throw(").Append(this._activeCatchErrVar ?? "Nothing").Append(")\n");
          break;

        case IrTry @try:
          this.EmitTry(@try, text, indent);
          break;

        case IrUsing @using:
          this.EmitBlock(@using.Lowered(), text, indent);
          break;

        case IrWriteLine write: {
          var val = this.EmitExpr(write.Value, text, indent);
          if (write.Value is IrStrConst or IrIntrinsic { Op: IrIntrinsicOp.StrConcat })
            text.Append(pad).Append("WScript.Echo ").Append(val).Append('\n');
          else
            text.Append(pad).Append("Call ").Append(this.Use(VBScriptHelpers.WriteLine)).Append('(').Append(val).Append(")\n");
          break;
        }

        case IrForEachCollection forEachCol:
          this.EmitForEachCollection(forEachCol, text, indent);
          break;

        case IrYield yieldStmt:
          this.EmitYield(yieldStmt, text, indent);
          break;

        case IrYieldBreak:
          this.EmitYieldBreak(text, indent);
          break;

        case IrYieldFrom yieldFrom:
          this.EmitYieldFrom(yieldFrom, text, indent);
          break;

        case IrForEachSequence forEachSeq:
          this.EmitForEachSequence(forEachSeq, text, indent);
          break;

        default:
          this.ReportConstruct(statement.GetType().Name);
          break;
      }
    }

    private void EmitStore(IrStore store, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var idx = this.EmitExpr(store.Index, text, indent);
      var val = this.CopyIfStruct(store.Value.Type, this.EmitExpr(store.Value, text, indent), text, indent);

      if (store.Array.Type.IsView) {
        var span = this.EmitExpr(store.Array, text, indent);
        text.Append(pad).Append("Call ").Append(span).Append(".SetAt(").Append(idx).Append(", ").Append(val).Append(")\n");
        return;
      }

      if (store.Array is IrLocalRef arrBoxRef && this._arrayBoxedLocals.Contains(arrBoxRef.Local)) {
        text.Append(pad).Append("Call ").Append(this.LocalName(arrBoxRef.Local)).Append(".SetAtChecked(").Append(idx).Append(", ").Append(val).Append(")\n");
        return;
      }

      var chk = this.Use(VBScriptHelpers.CheckIdx);

      if (store.Array is IrLocalRef boxedArrRef && this._boxedLocals.Contains(boxedArrRef.Local)) {
        var cell = this.LocalName(boxedArrRef.Local);
        var tmpArr = this.NewTemp("ply_a");
        text.Append(pad).Append(tmpArr).Append(" = ").Append(cell).Append(".Val\n");
        text.Append(pad).Append("Call ").Append(chk).Append('(').Append(tmpArr).Append(", ").Append(idx).Append(")\n");
        text.Append(pad).Append(AssignPrefix(store.Value.Type)).Append(tmpArr).Append('(').Append(idx).Append(") = ").Append(val).Append('\n');
        text.Append(pad).Append(cell).Append(".Val = ").Append(tmpArr).Append('\n');
        return;
      }

      // Field-held arrays in VBScript return a copy when read, so write back to the field!
      if (store.Array is IrFieldLoad fieldLoad) {
        var owner = this.EmitExpr(fieldLoad.Target, text, indent);
        if (fieldLoad.Target.Type.IsObject) {
          var tmpObj = this.NewTemp("ply_o");
          text.Append(pad).Append("Set ").Append(tmpObj).Append(" = ").Append(this.Use(VBScriptHelpers.CheckNull)).Append('(').Append(owner).Append(")\n");
          owner = tmpObj;
        }
        var fname = this.FieldName(fieldLoad.Target.Type, fieldLoad.Field);
        var tmpArr = this.NewTemp("ply_a");
        text.Append(pad).Append(tmpArr).Append(" = ").Append(owner).Append('.').Append(fname).Append('\n');
        text.Append(pad).Append("Call ").Append(chk).Append('(').Append(tmpArr).Append(", ").Append(idx).Append(")\n");
        text.Append(pad).Append(AssignPrefix(store.Value.Type)).Append(tmpArr).Append('(').Append(idx).Append(") = ").Append(val).Append('\n');
        text.Append(pad).Append(owner).Append('.').Append(fname).Append(" = ").Append(tmpArr).Append('\n');
        return;
      }

      var arr = this.EmitExpr(store.Array, text, indent);
      text.Append(pad).Append("Call ").Append(chk).Append('(').Append(arr).Append(", ").Append(idx).Append(")\n");
      text.Append(pad).Append(AssignPrefix(store.Value.Type)).Append(arr).Append('(').Append(idx).Append(") = ").Append(val).Append('\n');
    }

    private void EmitFieldStore(IrFieldStore store, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var target = this.EmitPlace(store.Target, text, indent);
      var value = this.CopyIfStruct(store.Field.Type, this.EmitExpr(store.Value, text, indent), text, indent);
      var fname = this.FieldName(store.Target.Type, store.Field);
      text.Append(pad).Append(AssignPrefix(store.Field.Type)).Append(target).Append('.').Append(fname).Append(" = ").Append(value).Append('\n');
    }

    private void EmitExprStatement(IrExpr expr, StringBuilder text, int indent) {
      var pad = Indent(indent);
      if (expr is IrCall call) {
        if (IrExceptionClass.All.SelectMany(c => c.Constructors).Any(c => c.Function == call.Function)) {
          var self = this.EmitExpr(call.Arguments[0], text, indent);
          var msg = call.Arguments.Count > 1 ? this.EmitExpr(call.Arguments[1], text, indent) : "\"\"";
          var inner = call.Arguments.Count > 2 ? this.EmitExpr(call.Arguments[2], text, indent) : "Nothing";
          text.Append(pad).Append(self).Append(".Message = ").Append(msg).Append('\n');
          text.Append(pad).Append("Set ").Append(self).Append(".InnerException = ").Append(inner).Append('\n');
          return;
        }

        var args = this.EmitCallArguments(call.Arguments, text, indent);
        text.Append(pad).Append("Call ").Append(this.ResolveFunctionName(call.Function)).Append('(').Append(args).Append(")\n");
        return;
      }
      if (expr is IrMethodCall methodCall) {
        var callText = this.EmitMethodCallCore(methodCall, text, indent, isStatement: true);
        text.Append(pad).Append("Call ").Append(callText).Append('\n');
        return;
      }
      if (expr is IrInvoke invoke) {
        _ = this.EmitInvokeCore(invoke, text, indent, isStatement: true);
        return;
      }
      if (expr is IrAwait awaitExpr && awaitExpr.Type == IrType.Void) {
        this.Use(VBScriptHelpers.TaskSupport);
        var operand = this.EmitExpr(awaitExpr.Operand, text, indent);
        text.Append(pad).Append("Call ply_task_await_void(").Append(operand).Append(")\n");
        return;
      }
      if (expr is IrCollection col && col.Type == IrType.Void) {
        this.EmitVoidCollection(col, text, indent);
        return;
      }
      if (expr is IrIntrinsic { Op: IrIntrinsicOp.ArrayCopy or IrIntrinsicOp.ArrayFill or IrIntrinsicOp.ArrayClear or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig or IrIntrinsicOp.ViewCopy or IrIntrinsicOp.ViewFill or IrIntrinsicOp.PointerStore } subIntrinsic) {
        this.EmitVoidIntrinsic(subIntrinsic, text, indent);
        return;
      }
      if (expr is IrStep or IrBind or IrThrowExpression) {
        _ = this.EmitExpr(expr, text, indent);
        return;
      }
      var evaluated = this.EmitExpr(expr, text, indent);
      var discard = this.NewTemp("ply_d");
      text.Append(pad).Append(AssignPrefix(expr.Type)).Append(discard).Append(" = ").Append(evaluated).Append('\n');
    }

    private void EmitIf(IrIf @if, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var cond = this.EmitExpr(@if.Condition, text, indent);
      text.Append(pad).Append("If ").Append(cond).Append(" Then\n");
      this.EmitBlock(@if.Then, text, indent + 1);

      var currentElse = @if.Else;
      while (currentElse is { Statements: [IrIf nested] }) {
        var pre = new StringBuilder();
        var nestedCond = this.EmitExpr(nested.Condition, pre, indent + 1);
        if (pre.Length > 0) {
          text.Append(pad).Append("Else\n");
          text.Append(pre);
          text.Append(Indent(indent + 1)).Append("If ").Append(nestedCond).Append(" Then\n");
          this.EmitBlock(nested.Then, text, indent + 2);
          if (nested.Else is not null) {
            text.Append(Indent(indent + 1)).Append("Else\n");
            this.EmitBlock(nested.Else, text, indent + 2);
          }
          text.Append(Indent(indent + 1)).Append("End If\n");
          currentElse = null;
          break;
        }
        text.Append(pad).Append("ElseIf ").Append(nestedCond).Append(" Then\n");
        this.EmitBlock(nested.Then, text, indent + 1);
        currentElse = nested.Else;
      }

      if (currentElse is not null) {
        text.Append(pad).Append("Else\n");
        this.EmitBlock(currentElse, text, indent + 1);
      }
      text.Append(pad).Append("End If\n");
    }

    private void EmitWhile(IrWhile @while, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var hasContinue = ContainsDirectJump(@while.Body, wantBreak: false);
      var hasBreak = ContainsDirectJump(@while.Body, wantBreak: true);
      var preCond = new StringBuilder();
      var condExpr = this.EmitExpr(@while.Condition, preCond, indent + 1);

      if (preCond.Length == 0) {
        text.Append(pad).Append("Do While ").Append(condExpr).Append('\n');
      } else {
        text.Append(pad).Append("Do\n");
        text.Append(preCond);
        text.Append(Indent(indent + 1)).Append("If Not (").Append(condExpr).Append(") Then Exit Do\n");
      }

      this.EmitLoopBody(@while.Body, null, hasBreak, hasContinue, text, indent + 1);
      text.Append(pad).Append("Loop\n");
    }

    private void EmitDoWhile(IrDoWhile doWhile, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var hasContinue = ContainsDirectJump(doWhile.Body, wantBreak: false);
      var hasBreak = ContainsDirectJump(doWhile.Body, wantBreak: true);
      var preCond = new StringBuilder();
      var condExpr = this.EmitExpr(doWhile.Condition, preCond, indent + 1);

      text.Append(pad).Append("Do\n");
      this.EmitLoopBody(doWhile.Body, null, hasBreak, hasContinue, text, indent + 1);
      if (preCond.Length == 0) {
        text.Append(pad).Append("Loop While ").Append(condExpr).Append('\n');
      } else {
        text.Append(preCond);
        text.Append(Indent(indent + 1)).Append("If Not (").Append(condExpr).Append(") Then Exit Do\n");
        text.Append(pad).Append("Loop\n");
      }
    }

    private bool TryEmitForToNext(IrDeclare initDecl, IrFor @for, StringBuilder text, int indent) {
      var iv = initDecl.Local;
      if (iv.Type != IrType.I32 || @for.PerIteration.Count > 0 || this._boxedLocals.Contains(iv) || this._addrBoxedLocals.Contains(iv))
        return false;
      if (@for.Condition is not IrBinary { Left: IrLocalRef { Local: var condLocal } } cmp || condLocal != iv)
        return false;
      if (cmp.Op is not (IrBinaryOp.Less or IrBinaryOp.LessOrEqual))
        return false;

      var isPlusOne = @for.Step.Statements switch {
        [IrExprStmt { Expression: IrStep { Target: IrLocalRef { Local: var s }, Increment: true, Trap: false } }] when s == iv => true,
        [IrAssign { Local: var s, Value: IrBinary { Op: IrBinaryOp.Add, Left: IrLocalRef { Local: var l }, Right: IrConst { Value: 1 } } }] when s == iv && l == iv => true,
        _ => false
      };
      if (!isPlusOne || ContainsDirectJump(@for.Body, wantBreak: false) || IsLocalModified(@for.Body, iv))
        return false;

      var pad = Indent(indent);
      var start = this.EmitExpr(initDecl.Initializer!, text, indent);
      var bound = this.EmitExpr(cmp.Right, text, indent);
      var limit = cmp.Op == IrBinaryOp.Less ? $"({bound}) - 1" : bound;

      text.Append(pad).Append("For ").Append(this.LocalName(iv)).Append(" = ").Append(start).Append(" To ").Append(limit).Append('\n');
      this._loops.Push(new(null, null, IsForLoop: true, this._tryFrames.Count, IsSwitch: false));
      this.EmitBlock(@for.Body, text, indent + 1);
      this._loops.Pop();
      text.Append(pad).Append("Next\n");
      return true;
    }

    private void EmitGeneralFor(IrFor @for, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var hasContinue = ContainsDirectJump(@for.Body, wantBreak: false);
      var hasBreak = ContainsDirectJump(@for.Body, wantBreak: true);
      var perIterBoxed = @for.PerIteration.Where(this._boxedLocals.Contains).ToList();

      foreach (var v in perIterBoxed)
        this.EmitCloneBoxedCell(v, text, indent);

      var preCond = new StringBuilder();
      var condExpr = this.EmitExpr(@for.Condition, preCond, indent + 1);

      if (preCond.Length == 0) {
        text.Append(pad).Append("Do While ").Append(condExpr).Append('\n');
      } else {
        text.Append(pad).Append("Do\n");
        text.Append(preCond);
        text.Append(Indent(indent + 1)).Append("If Not (").Append(condExpr).Append(") Then Exit Do\n");
      }

      this.EmitLoopBody(@for.Body, @for.Step, hasBreak, hasContinue, text, indent + 1, perIterBoxed);
      text.Append(pad).Append("Loop\n");
    }

    private void EmitCloneBoxedCell(IrLocal local, StringBuilder text, int indent) {
      this.Use(VBScriptHelpers.Cell);
      var pad = Indent(indent);
      var cell = this.LocalName(local);
      var tmp = this.NewTemp("ply_nc");
      text.Append(pad).Append("Set ").Append(tmp).Append(" = New ply_Cell\n");
      text.Append(pad).Append(AssignPrefix(local.Type)).Append(tmp).Append(".Val = ").Append(cell).Append(".Val\n");
      text.Append(pad).Append("Set ").Append(cell).Append(" = ").Append(tmp).Append('\n');
    }

    private void EmitLoopBody(IrBlock body, IrBlock? step, bool hasBreak, bool hasContinue, StringBuilder text, int indent, IReadOnlyList<IrLocal>? perIterBoxed = null) {
      var pad = Indent(indent);
      if (hasContinue) {
        var brkFlag = hasBreak ? this.NewTemp("ply_brk") : null;
        if (brkFlag is not null)
          text.Append(pad).Append(brkFlag).Append(" = False\n");
        text.Append(pad).Append("Do\n");
        this._loops.Push(new(brkFlag, null, IsForLoop: false, this._tryFrames.Count, IsSwitch: false));
        this.EmitBlock(body, text, indent + 1);
        this._loops.Pop();
        text.Append(pad).Append("Loop Until True\n");
        if (this._tryBodyDepth > 0)
          text.Append(pad).Append("If Err.Number <> 0 Then Exit Do\n");
        if (brkFlag is not null)
          text.Append(pad).Append("If ").Append(brkFlag).Append(" Then Exit Do\n");
      } else {
        this._loops.Push(new(null, null, IsForLoop: false, this._tryFrames.Count, IsSwitch: false));
        this.EmitBlock(body, text, indent);
        this._loops.Pop();
      }

      if (perIterBoxed is { Count: > 0 })
        foreach (var v in perIterBoxed)
          this.EmitCloneBoxedCell(v, text, indent);

      if (step is { Statements.Count: > 0 })
        this.EmitBlock(step, text, indent);
    }

    private void EmitSwitch(IrSwitch @switch, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var sel = this.EmitExpr(@switch.Value, text, indent);
      var selType = @switch.Value.Type.Integral;

      // Strip trailing IrBreak at the end of a section (Select Case never falls through).
      var cleanedSections = @switch.Sections.Select(section => {
        var stmts = section.Body.Statements;
        return stmts.Count > 0 && stmts[^1] is IrBreak
          ? section with { Body = new IrBlock([.. stmts.Take(stmts.Count - 1)]) }
          : section;
      }).ToList();

      var needsWrapper = cleanedSections.Any(s => ContainsDirectJump(s.Body, wantBreak: true));
      string? contFlag = null;
      if (needsWrapper) {
        var hasCont = this._loops.Count > 0 && cleanedSections.Any(s => ContainsDirectJump(s.Body, wantBreak: false));
        if (hasCont) {
          contFlag = this.NewTemp("ply_cnt");
          text.Append(pad).Append(contFlag).Append(" = False\n");
        }
        text.Append(pad).Append("Do\n");
        this._loops.Push(new(null, contFlag, IsForLoop: false, this._tryFrames.Count, IsSwitch: true));
        ++indent;
        pad = Indent(indent);
      }

      text.Append(pad).Append("Select Case ").Append(sel).Append('\n');
      foreach (var section in cleanedSections.Where(s => !s.IsDefault)) {
        var labels = string.Join(", ", section.Labels.Select(v => FormatIntLiteral(selType, v)));
        text.Append(Indent(indent + 1)).Append("Case ").Append(labels).Append('\n');
        this.EmitBlock(section.Body, text, indent + 2);
      }
      if (cleanedSections.FirstOrDefault(s => s.IsDefault) is { } defSection) {
        text.Append(Indent(indent + 1)).Append("Case Else\n");
        this.EmitBlock(defSection.Body, text, indent + 2);
      }
      text.Append(pad).Append("End Select\n");

      if (needsWrapper) {
        this._loops.Pop();
        var outerPad = Indent(indent - 1);
        text.Append(outerPad).Append("Loop Until True\n");
        if (contFlag is not null) {
          var enclosing = this._loops.Peek();
          text.Append(outerPad).Append("If ").Append(contFlag).Append(" Then ");
          if (enclosing.ContinueFlag is not null)
            text.Append(enclosing.ContinueFlag).Append(" = True : Exit Do\n");
          else
            text.Append("Exit Do\n");
        }
      }
    }

    private void EmitBreak(StringBuilder text, int indent) {
      var pad = Indent(indent);
      var loop = this._loops.Peek();
      if (this._tryFrames.Count > loop.TryDepth) {
        var frame = this._tryFrames.Peek();
        text.Append(pad).Append(frame.BreakFlag).Append(" = True\n");
        text.Append(pad).Append("Exit Do\n");
        return;
      }
      if (loop.BreakFlag is not null)
        text.Append(pad).Append(loop.BreakFlag).Append(" = True\n");
      text.Append(pad).Append(loop.IsForLoop ? "Exit For\n" : "Exit Do\n");
    }

    private void EmitContinue(StringBuilder text, int indent) {
      var pad = Indent(indent);
      var loop = this._loops.Peek();
      if (this._tryFrames.Count > loop.TryDepth) {
        var frame = this._tryFrames.Peek();
        text.Append(pad).Append(frame.ContinueFlag).Append(" = True\n");
        text.Append(pad).Append("Exit Do\n");
        return;
      }
      if (loop.ContinueFlag is not null)
        text.Append(pad).Append(loop.ContinueFlag).Append(" = True\n");
      text.Append(pad).Append("Exit Do\n");
    }

    private void EmitReturn(IrReturn ret, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var value = ret.Value is { } expr
        ? this.CopyIfStruct(expr.Type, this.EmitExpr(expr, text, indent), text, indent)
        : null;

      if (this._tryFrames.Count > 0) {
        var frame = this._tryFrames.Peek();
        if (value is not null && frame.ReturnValue is not null)
          text.Append(pad).Append(AssignPrefix(ret.Value!.Type)).Append(frame.ReturnValue).Append(" = ").Append(value).Append('\n');
        text.Append(pad).Append(frame.ReturnFlag).Append(" = True\n");
        text.Append(pad).Append("Exit Do\n");
        return;
      }

      this.EmitFinalReturn(value, ret.Value?.Type, text, indent);
    }

    private void EmitFinalReturn(string? value, IrType? valueType, StringBuilder text, int indent) {
      var pad = Indent(indent);
      if (this._iteratorResultListVar is { } listVar) {
        this.EmitArrayParamSyncs(text, indent);
        text.Append(pad).Append("Set ").Append(this._currentFunctionVbsName).Append(" = ").Append(listVar).Append('\n');
        text.Append(pad).Append("Exit Function\n");
        return;
      }

      if (this._inAsyncFunction) {
        if (value is not null && valueType is not null) {
          var tmp = this.NewTemp("ply_ar");
          text.Append(pad).Append(AssignPrefix(valueType)).Append(tmp).Append(" = ").Append(value).Append('\n');
          text.Append(pad).Append("If Err.Number <> 0 Then Exit Do\n");
          this.EmitArrayParamSyncs(text, indent);
          text.Append(pad).Append("On Error GoTo 0\n");
          text.Append(pad).Append("Set ").Append(this._currentFunctionVbsName).Append(" = ply_task_from_result(").Append(tmp).Append(")\n");
          text.Append(pad).Append("Exit Function\n");
        } else {
          this.EmitArrayParamSyncs(text, indent);
          text.Append(pad).Append("On Error GoTo 0\n");
          text.Append(pad).Append("Set ").Append(this._currentFunctionVbsName).Append(" = ply_task_completed()\n");
          text.Append(pad).Append("Exit Function\n");
        }
        return;
      }

      if (value is not null && valueType is not null) {
        text.Append(pad).Append(AssignPrefix(valueType)).Append(this._currentFunctionVbsName).Append(" = ").Append(value).Append('\n');
        this.EmitArrayParamSyncs(text, indent);
        text.Append(pad).Append("Exit Function\n");
      } else {
        this.EmitArrayParamSyncs(text, indent);
        text.Append(pad).Append("Exit Sub\n");
      }
    }

    private void EmitTry(IrTry @try, StringBuilder text, int indent) {
      this.Use(VBScriptHelpers.ErrSupport);
      var pad = Indent(indent);
      var errNum = this.NewTemp("ply_enum");
      var errSrc = this.NewTemp("ply_esrc");
      var errDesc = this.NewTemp("ply_edesc");
      var errObj = this.NewTemp("ply_eobj");
      var unhandled = this.NewTemp("ply_unh");

      var hasFinally = @try.Finally is not null;
      var usesRet = hasFinally && (ContainsReturn(@try.Body) || @try.Catches.Any(c => ContainsReturn(c.Body)));
      var usesBrk = hasFinally && this._loops.Count > 0 && (ContainsDirectJump(@try.Body, true) || @try.Catches.Any(c => ContainsDirectJump(c.Body, true)));
      var usesCnt = hasFinally && this._loops.Count > 0 && (ContainsDirectJump(@try.Body, false) || @try.Catches.Any(c => ContainsDirectJump(c.Body, false)));

      var retFlag = usesRet ? this.NewTemp("ply_rf") : null;
      var retVal = usesRet && this._currentReturnType != IrType.Void ? this.NewTemp("ply_rv") : null;
      var brkFlag = usesBrk ? this.NewTemp("ply_bf") : null;
      var cntFlag = usesCnt ? this.NewTemp("ply_cf") : null;

      if (retFlag is not null)
        text.Append(pad).Append(retFlag).Append(" = False\n");
      if (brkFlag is not null)
        text.Append(pad).Append(brkFlag).Append(" = False\n");
      if (cntFlag is not null)
        text.Append(pad).Append(cntFlag).Append(" = False\n");
      text.Append(pad).Append(unhandled).Append(" = False\n");
      text.Append(pad).Append("Set ").Append(errObj).Append(" = Nothing\n");

      var frame = hasFinally ? new TryFrame(retFlag, retVal, brkFlag, cntFlag) : null;
      if (frame is not null)
        this._tryFrames.Push(frame);

      text.Append(pad).Append("On Error Resume Next\n");
      text.Append(pad).Append("Do\n");
      ++this._tryBodyDepth;
      this.EmitBlock(@try.Body, text, indent + 1);
      --this._tryBodyDepth;
      text.Append(pad).Append("Loop Until True\n");
      text.Append(pad).Append(errNum).Append(" = Err.Number\n");
      text.Append(pad).Append(errSrc).Append(" = Err.Source\n");
      text.Append(pad).Append(errDesc).Append(" = Err.Description\n");
      text.Append(pad).Append("Err.Clear\n");
      if (this._tryBodyDepth == 0)
        text.Append(pad).Append("On Error GoTo 0\n");

      text.Append(pad).Append("If ").Append(errNum).Append(" <> 0 Then\n");
      text.Append(Indent(indent + 1)).Append("Set ").Append(errObj).Append(" = ply_capture_err(")
        .Append(errNum).Append(", ").Append(errSrc).Append(", ").Append(errDesc).Append(")\n");

      if (@try.Catches.Count > 0) {
        var matched = this.NewTemp("ply_m");
        text.Append(Indent(indent + 1)).Append(matched).Append(" = False\n");
        var prevCatchErr = this._activeCatchErrVar;
        this._activeCatchErrVar = errObj;

        foreach (var @catch in @try.Catches) {
          var clausePad = Indent(indent + 1);
          var condVar = this.NewTemp("ply_mc");
          text.Append(clausePad).Append("If Not ").Append(matched).Append(" And ").Append(errObj).Append(".ply_is(")
            .Append(VBScriptNaming.StringLiteral(@catch.Class.Name)).Append(") Then\n");

          var innerPad = Indent(indent + 2);
          if (@catch.Local is not null)
            text.Append(innerPad).Append("Set ").Append(this.LocalName(@catch.Local)).Append(" = ").Append(errObj).Append('\n');

          if (@catch.Filter is { } filter) {
            text.Append(innerPad).Append(condVar).Append(" = False\n");
            text.Append(innerPad).Append("On Error Resume Next\n");
            var fExpr = this.EmitExpr(filter, text, indent + 2);
            text.Append(innerPad).Append("If Err.Number = 0 Then ").Append(condVar).Append(" = (").Append(fExpr).Append(")\n");
            text.Append(innerPad).Append("Err.Clear\n");
            if (this._tryBodyDepth == 0)
              text.Append(innerPad).Append("On Error GoTo 0\n");
          } else {
            text.Append(innerPad).Append(condVar).Append(" = True\n");
          }

          text.Append(innerPad).Append("If ").Append(condVar).Append(" Then\n");
          text.Append(Indent(indent + 3)).Append(matched).Append(" = True\n");
          if (hasFinally) {
            var cNum = this.NewTemp("ply_cn");
            var cSrc = this.NewTemp("ply_cs");
            var cDesc = this.NewTemp("ply_cd");
            text.Append(Indent(indent + 3)).Append("On Error Resume Next\n");
            text.Append(Indent(indent + 3)).Append("Do\n");
            ++this._tryBodyDepth;
            this.EmitBlock(@catch.Body, text, indent + 4);
            --this._tryBodyDepth;
            text.Append(Indent(indent + 3)).Append("Loop Until True\n");
            text.Append(Indent(indent + 3)).Append(cNum).Append(" = Err.Number\n");
            text.Append(Indent(indent + 3)).Append(cSrc).Append(" = Err.Source\n");
            text.Append(Indent(indent + 3)).Append(cDesc).Append(" = Err.Description\n");
            text.Append(Indent(indent + 3)).Append("Err.Clear\n");
            if (this._tryBodyDepth == 0)
              text.Append(Indent(indent + 3)).Append("On Error GoTo 0\n");
            text.Append(Indent(indent + 3)).Append("If ").Append(cNum).Append(" <> 0 Then\n");
            text.Append(Indent(indent + 4)).Append("Set ").Append(errObj).Append(" = ply_capture_err(")
              .Append(cNum).Append(", ").Append(cSrc).Append(", ").Append(cDesc).Append(")\n");
            text.Append(Indent(indent + 4)).Append(unhandled).Append(" = True\n");
            text.Append(Indent(indent + 3)).Append("End If\n");
          } else {
            this.EmitBlock(@catch.Body, text, indent + 3);
          }
          text.Append(innerPad).Append("End If\n");
          text.Append(clausePad).Append("End If\n");
        }

        this._activeCatchErrVar = prevCatchErr;
        text.Append(Indent(indent + 1)).Append("If Not ").Append(matched).Append(" Then ").Append(unhandled).Append(" = True\n");
      } else {
        text.Append(Indent(indent + 1)).Append(unhandled).Append(" = True\n");
      }
      text.Append(pad).Append("End If\n");

      if (frame is not null)
        this._tryFrames.Pop();

      if (@try.Finally is not null)
        this.EmitBlock(@try.Finally, text, indent);

      text.Append(pad).Append("If ").Append(unhandled).Append(" Then Call ply_throw(").Append(errObj).Append(")\n");

      if (retFlag is not null) {
        text.Append(pad).Append("If ").Append(retFlag).Append(" Then\n");
        if (this._tryFrames.Count > 0) {
          var outer = this._tryFrames.Peek();
          if (retVal is not null && outer.ReturnValue is not null)
            text.Append(Indent(indent + 1)).Append(AssignPrefix(this._currentReturnType)).Append(outer.ReturnValue).Append(" = ").Append(retVal).Append('\n');
          text.Append(Indent(indent + 1)).Append(outer.ReturnFlag).Append(" = True\n");
          text.Append(Indent(indent + 1)).Append("Exit Do\n");
        } else {
          this.EmitFinalReturn(retVal, retVal is not null ? this._currentReturnType : null, text, indent + 1);
        }
        text.Append(pad).Append("End If\n");
      }

      if (brkFlag is not null) {
        text.Append(pad).Append("If ").Append(brkFlag).Append(" Then\n");
        this.EmitBreak(text, indent + 1);
        text.Append(pad).Append("End If\n");
      }

      if (cntFlag is not null) {
        text.Append(pad).Append("If ").Append(cntFlag).Append(" Then\n");
        this.EmitContinue(text, indent + 1);
        text.Append(pad).Append("End If\n");
      }
    }

    private static bool ContainsDirectJump(IrBlock block, bool wantBreak) {
      foreach (var stmt in block.Statements) {
        if (wantBreak && stmt is IrBreak) return true;
        if (!wantBreak && stmt is IrContinue) return true;
        if (stmt is IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence)
          continue;
        if (wantBreak && stmt is IrSwitch)
          continue;
        foreach (var child in IrTree.Blocks(stmt))
          if (ContainsDirectJump(child, wantBreak))
            return true;
      }
      return false;
    }

    private static bool ContainsReturn(IrBlock block)
      => AllStatements(block).Any(s => s is IrReturn or IrYieldBreak);

    private static bool IsLocalModified(IrBlock block, IrLocal local) {
      foreach (var stmt in AllStatements(block)) {
        if (stmt is IrAssign a && a.Local == local)
          return true;
        if (IrTree.Expressions(stmt).OfType<IrStep>().Any(s => s.Target is IrLocalRef r && r.Local == local))
          return true;
        if (IrTree.Expressions(stmt).OfType<IrBind>().Any(b => b.Local == local))
          return true;
      }
      return false;
    }

    private static IEnumerable<IrStmt> AllStatements(IrBlock block) {
      foreach (var stmt in block.Statements) {
        yield return stmt;
        foreach (var child in IrTree.Blocks(stmt))
          foreach (var nested in AllStatements(child))
            yield return nested;
      }
    }

  }

}
