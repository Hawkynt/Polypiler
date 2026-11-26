using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VBScript;

public static partial class VBScriptEmitter {

  private sealed partial class Writer {

    private void AnalyzeBodyStorage(IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, IrBlock body) {
      var own = new HashSet<IrLocal>(parameters.Concat(locals), ReferenceEqualityComparer.Instance);
      foreach (var captured in IrClosures.Captured(body))
        if (own.Contains(captured))
          this._boxedLocals.Add(captured);

      foreach (var expr in DirectExpressions(body)) {
        if (expr is IrIntrinsic { Op: IrIntrinsicOp.AddressOf, Arguments: [IrLocalRef target] } && own.Contains(target.Local))
          this._addrBoxedLocals.Add(target.Local);
        else if (expr is IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Pin, Arguments: [IrLocalRef arrTarget, ..] }
            && own.Contains(arrTarget.Local)
            && arrTarget.Local.Type.IsArray
            && arrTarget.Local.Type.Element!.IsNumeric
            && !this._boxedLocals.Contains(arrTarget.Local))
          this._arrayBoxedLocals.Add(arrTarget.Local);
      }
    }

    private static IEnumerable<IrExpr> DirectExpressions(IrBlock block) {
      foreach (var stmt in block.Statements) {
        foreach (var expr in IrTree.Expressions(stmt))
          foreach (var node in DirectExprNodes(expr))
            yield return node;
        foreach (var child in IrTree.Blocks(stmt))
          foreach (var node in DirectExpressions(child))
            yield return node;
      }
    }

    private static IEnumerable<IrExpr> DirectExprNodes(IrExpr expr) {
      yield return expr;
      if (expr is IrLambda)
        yield break;
      foreach (var op in IrTree.Operands(expr))
        foreach (var child in DirectExprNodes(op))
          yield return child;
    }

    private string EmitLocalRead(IrLocal local) {
      var name = this.LocalName(local);
      if (this._boxedLocals.Contains(local))
        return name + ".Val";
      if (this._addrBoxedLocals.Contains(local))
        return name + ".GetElem(0)";
      if (this._arrayBoxedLocals.Contains(local))
        return name + ".Data";
      return name;
    }

    private void EmitLocalInit(IrLocal local, string value, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var name = this.LocalName(local);
      if (this._boxedLocals.Contains(local)) {
        this.Use(VBScriptHelpers.Cell);
        text.Append(pad).Append("Set ").Append(name).Append(" = New ply_Cell\n");
        text.Append(pad).Append(AssignPrefix(local.Type)).Append(name).Append(".Val = ").Append(value).Append('\n');
      } else if (this._addrBoxedLocals.Contains(local)) {
        this.Use(VBScriptHelpers.SpanSupport);
        text.Append(pad).Append("Set ").Append(name).Append(" = ply_buf_single(").Append(value).Append(", ")
          .Append(ElemKindCode(local.Type)).Append(", ").Append(local.Type.Size.ToString(CultureInfo.InvariantCulture)).Append(")\n");
      } else if (this._arrayBoxedLocals.Contains(local)) {
        this.Use(VBScriptHelpers.SpanSupport);
        var elem = local.Type.Element!;
        text.Append(pad).Append("Set ").Append(name).Append(" = ply_buf_wrap(").Append(value).Append(", ")
          .Append(ElemKindCode(elem)).Append(", ").Append(elem.Size.ToString(CultureInfo.InvariantCulture)).Append(")\n");
      } else {
        text.Append(pad).Append(AssignPrefix(local.Type)).Append(name).Append(" = ").Append(value).Append('\n');
      }
    }

    private void EmitLocalWrite(IrLocal local, string value, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var name = this.LocalName(local);
      if (this._boxedLocals.Contains(local)) {
        text.Append(pad).Append(AssignPrefix(local.Type)).Append(name).Append(".Val = ").Append(value).Append('\n');
      } else if (this._addrBoxedLocals.Contains(local)) {
        text.Append(pad).Append("Call ").Append(name).Append(".SetElem(0, ").Append(value).Append(")\n");
      } else if (this._arrayBoxedLocals.Contains(local)) {
        this.Use(VBScriptHelpers.SpanSupport);
        var elem = local.Type.Element!;
        text.Append(pad).Append("Set ").Append(name).Append(" = ply_buf_wrap(").Append(value).Append(", ")
          .Append(ElemKindCode(elem)).Append(", ").Append(elem.Size.ToString(CultureInfo.InvariantCulture)).Append(")\n");
      } else {
        text.Append(pad).Append(AssignPrefix(local.Type)).Append(name).Append(" = ").Append(value).Append('\n');
      }
    }

    private string EmitLambda(IrLambda lambda, StringBuilder pre, int indent) {
      this.Use(VBScriptHelpers.Cell);
      var className = "ply_Clos_" + (this._syntheticCounter++).ToString(CultureInfo.InvariantCulture);

      // Save enclosing function emission state.
      var savedLocals = new Dictionary<IrLocal, string>(this._locals, ReferenceEqualityComparer.Instance);
      var savedTaken = new HashSet<string>(this._localTaken, StringComparer.OrdinalIgnoreCase);
      var savedDims = new List<string>(this._declaredDims);
      var savedLoops = new Stack<LoopFrame>(this._loops.Reverse());
      var savedTryFrames = new Stack<TryFrame>(this._tryFrames.Reverse());
      var savedBoxed = this._boxedLocals;
      var savedAddrBoxed = this._addrBoxedLocals;
      var savedArrBoxed = this._arrayBoxedLocals;
      var savedArrSyncs = this._arrayParamSyncs;
      var savedFnVbsName = this._currentFunctionVbsName;
      var savedRetType = this._currentReturnType;
      var savedInAsync = this._inAsyncFunction;
      var savedIterVar = this._iteratorResultListVar;
      var savedTemp = this._tempCounter;
      var savedTryDepth = this._tryBodyDepth;
      var savedCatchVar = this._activeCatchErrVar;

      this._locals.Clear();
      this._localTaken.Clear();
      this._declaredDims.Clear();
      this._loops.Clear();
      this._tryFrames.Clear();
      this._boxedLocals = new HashSet<IrLocal>(ReferenceEqualityComparer.Instance);
      this._addrBoxedLocals = new HashSet<IrLocal>(ReferenceEqualityComparer.Instance);
      this._arrayBoxedLocals = new HashSet<IrLocal>(ReferenceEqualityComparer.Instance);
      this._arrayParamSyncs = [];
      this._currentFunctionVbsName = "Invoke";
      this._currentReturnType = lambda.ReturnType;
      this._inAsyncFunction = false;
      this._iteratorResultListVar = null;
      this._tempCounter = 0;
      this._tryBodyDepth = 0;
      this._activeCatchErrVar = null;
      this._localTaken.Add("Invoke");

      var cls = new StringBuilder();
      cls.Append("Class ").Append(className).Append('\n');
      for (var i = 0; i < lambda.Captures.Count; ++i) {
        var cap = lambda.Captures[i];
        var fieldName = "c_" + i.ToString(CultureInfo.InvariantCulture);
        this._localTaken.Add(fieldName);
        this._locals[cap] = fieldName;
        this._boxedLocals.Add(cap);
        cls.Append("  Public ").Append(fieldName).Append('\n');
      }

      this.AnalyzeBodyStorage(lambda.Parameters, lambda.Locals, lambda.Body);

      var paramDecls = new List<string>(lambda.Parameters.Count);
      var prologue = new StringBuilder();
      foreach (var p in lambda.Parameters) {
        var pname = VBScriptNaming.Free(VBScriptNaming.Identifier(p.Name), this._localTaken);
        this._locals[p] = pname;
        if (this._boxedLocals.Contains(p)) {
          var rawName = VBScriptNaming.Free("ply_p_" + pname, this._localTaken);
          paramDecls.Add($"{(p.Type.IsArray ? "ByRef" : "ByVal")} {rawName}");
          this._declaredDims.Add(pname);
          prologue.Append("    Set ").Append(pname).Append(" = New ply_Cell\n");
          prologue.Append("    ").Append(AssignPrefix(p.Type)).Append(pname).Append(".Val = ").Append(rawName).Append('\n');
        } else {
          paramDecls.Add($"{(p.Type.IsArray ? "ByRef" : "ByVal")} {pname}");
        }
      }
      foreach (var l in lambda.Locals) {
        var lname = VBScriptNaming.Free(VBScriptNaming.Identifier(l.Name), this._localTaken);
        this._locals[l] = lname;
        this._declaredDims.Add(lname);
      }

      var bodyText = new StringBuilder();
      bodyText.Append(prologue);
      this.EmitBlock(lambda.Body, bodyText, 2);

      var isSub = lambda.ReturnType == IrType.Void;
      cls.Append(isSub ? "  Public Sub Invoke(" : "  Public Function Invoke(");
      cls.AppendJoin(", ", paramDecls);
      cls.Append(")\n");
      if (this._declaredDims.Count > 0)
        cls.Append("    Dim ").AppendJoin(", ", this._declaredDims).Append('\n');
      cls.Append(bodyText);
      cls.Append(isSub ? "  End Sub\n" : "  End Function\n");
      cls.Append("End Class\n");
      this._syntheticClasses.Add(cls.ToString());

      // Restore enclosing function emission state.
      this._locals.Clear();
      foreach (var kv in savedLocals)
        this._locals[kv.Key] = kv.Value;
      this._localTaken.Clear();
      foreach (var t in savedTaken)
        this._localTaken.Add(t);
      this._declaredDims.Clear();
      this._declaredDims.AddRange(savedDims);
      this._loops.Clear();
      foreach (var f in savedLoops.Reverse())
        this._loops.Push(f);
      this._tryFrames.Clear();
      foreach (var f in savedTryFrames.Reverse())
        this._tryFrames.Push(f);
      this._boxedLocals = savedBoxed;
      this._addrBoxedLocals = savedAddrBoxed;
      this._arrayBoxedLocals = savedArrBoxed;
      this._arrayParamSyncs = savedArrSyncs;
      this._currentFunctionVbsName = savedFnVbsName;
      this._currentReturnType = savedRetType;
      this._inAsyncFunction = savedInAsync;
      this._iteratorResultListVar = savedIterVar;
      this._tempCounter = savedTemp;
      this._tryBodyDepth = savedTryDepth;
      this._activeCatchErrVar = savedCatchVar;

      var pad = Indent(indent);
      var tmp = this.NewTemp("ply_fn");
      pre.Append(pad).Append("Set ").Append(tmp).Append(" = New ").Append(className).Append('\n');
      for (var i = 0; i < lambda.Captures.Count; ++i)
        pre.Append(pad).Append("Set ").Append(tmp).Append(".c_").Append(i.ToString(CultureInfo.InvariantCulture))
          .Append(" = ").Append(this.LocalName(lambda.Captures[i])).Append('\n');
      return tmp;
    }

    private string EmitFunctionRef(IrFunctionRef fnRef) {
      if (!this._fnRefClasses.TryGetValue(fnRef.Function, out var className)) {
        className = "ply_FnRef_" + (this._syntheticCounter++).ToString(CultureInfo.InvariantCulture);
        this._fnRefClasses[fnRef.Function] = className;
        var sig = fnRef.Type.Signature!;
        var targetFn = this.ResolveFunctionName(fnRef.Function);
        var cls = new StringBuilder();
        var pDecls = sig.Parameters.Select((p, i) => $"{(p.IsArray ? "ByRef" : "ByVal")} p_{i.ToString(CultureInfo.InvariantCulture)}").ToList();
        var pArgs = Enumerable.Range(0, pDecls.Count).Select(i => $"p_{i.ToString(CultureInfo.InvariantCulture)}");
        cls.Append("Class ").Append(className).Append('\n');
        if (sig.Result == IrType.Void) {
          cls.Append("  Public Sub Invoke(").AppendJoin(", ", pDecls).Append(")\n");
          cls.Append("    Call ").Append(targetFn).Append('(').AppendJoin(", ", pArgs).Append(")\n");
          cls.Append("  End Sub\n");
        } else {
          cls.Append("  Public Function Invoke(").AppendJoin(", ", pDecls).Append(")\n");
          cls.Append("    ").Append(AssignPrefix(sig.Result)).Append("Invoke = ").Append(targetFn).Append('(').AppendJoin(", ", pArgs).Append(")\n");
          cls.Append("  End Function\n");
        }
        cls.Append("End Class\n");
        this._syntheticClasses.Add(cls.ToString());
      }
      return "New " + className;
    }

    private string EmitBoundMethod(IrBoundMethod bm, StringBuilder pre, int indent) {
      if (bm.Receiver is null)
        return this.EmitFunctionRef(new IrFunctionRef(bm.Type, bm.Method.Function));

      var key = (bm.Method, bm.Kind);
      if (!this._boundMethodClasses.TryGetValue(key, out var className)) {
        className = "ply_Bind_" + (this._syntheticCounter++).ToString(CultureInfo.InvariantCulture);
        this._boundMethodClasses[key] = className;
        var sig = bm.Type.Signature!;
        var pDecls = sig.Parameters.Select((p, i) => $"{(p.IsArray ? "ByRef" : "ByVal")} p_{i.ToString(CultureInfo.InvariantCulture)}").ToList();
        var pArgs = Enumerable.Range(0, pDecls.Count).Select(i => $"p_{i.ToString(CultureInfo.InvariantCulture)}").ToList();
        var callTarget = bm.Kind is IrMethodCallKind.Virtual or IrMethodCallKind.Interface
          ? $"recv.{this._methodSlotNames[bm.Method.Root]}({string.Join(", ", pArgs)})"
          : $"{this.ResolveFunctionName(bm.Method.Function)}({string.Join(", ", pArgs.Prepend("recv"))})";

        var cls = new StringBuilder();
        cls.Append("Class ").Append(className).Append('\n');
        cls.Append("  Public recv\n");
        if (sig.Result == IrType.Void) {
          cls.Append("  Public Sub Invoke(").AppendJoin(", ", pDecls).Append(")\n");
          cls.Append("    Call ").Append(callTarget).Append('\n');
          cls.Append("  End Sub\n");
        } else {
          cls.Append("  Public Function Invoke(").AppendJoin(", ", pDecls).Append(")\n");
          cls.Append("    ").Append(AssignPrefix(sig.Result)).Append("Invoke = ").Append(callTarget).Append('\n');
          cls.Append("  End Function\n");
        }
        cls.Append("End Class\n");
        this._syntheticClasses.Add(cls.ToString());
      }

      var pad = Indent(indent);
      var recv = this.EmitExpr(bm.Receiver, pre, indent);
      var tmp = this.NewTemp("ply_bm");
      pre.Append(pad).Append("Set ").Append(tmp).Append(" = New ").Append(className).Append('\n');
      pre.Append(pad).Append("Set ").Append(tmp).Append(".recv = ").Append(this.Use(VBScriptHelpers.CheckNull)).Append('(').Append(recv).Append(")\n");
      return tmp;
    }

    private string EmitInvokeCore(IrInvoke invoke, StringBuilder pre, int indent, bool isStatement) {
      var pad = Indent(indent);
      var callee = this.EmitExpr(invoke.Callee, pre, indent);
      var fnTmp = this.NewTemp("ply_cfn");
      pre.Append(pad).Append("Set ").Append(fnTmp).Append(" = ").Append(callee).Append('\n');
      var args = new List<string>(invoke.Arguments.Count);
      foreach (var arg in invoke.Arguments) {
        var val = this.CopyIfStruct(arg.Type, this.EmitExpr(arg, pre, indent), pre, indent);
        var aTmp = this.NewTemp("ply_ca");
        pre.Append(pad).Append(AssignPrefix(arg.Type)).Append(aTmp).Append(" = ").Append(val).Append('\n');
        args.Add(aTmp);
      }
      pre.Append(pad).Append("Set ").Append(fnTmp).Append(" = ").Append(this.Use(VBScriptHelpers.CheckNull)).Append('(').Append(fnTmp).Append(")\n");
      var callText = $"{fnTmp}.Invoke({string.Join(", ", args)})";
      if (isStatement) {
        pre.Append(pad).Append("If Not (").Append(fnTmp).Append(" Is Nothing) Then Call ").Append(callText).Append('\n');
        return "";
      }
      var resTmp = this.NewTemp("ply_ir");
      pre.Append(pad).Append("If Not (").Append(fnTmp).Append(" Is Nothing) Then ").Append(AssignPrefix(invoke.Type)).Append(resTmp).Append(" = ").Append(callText).Append('\n');
      return resTmp;
    }

    private string EmitIteratorFunction(IrFunction function) {
      this.Use(VBScriptHelpers.CollectionSupport);
      this._currentFunction = function;
      this._currentFunctionVbsName = "Enumerate";
      this._inAsyncFunction = false;
      this._currentReturnType = IrType.Void;
      this._iteratorResultListVar = "ply_yields";
      this._locals.Clear();
      this._localTaken.Clear();
      this._declaredDims.Clear();
      this._loops.Clear();
      this._tryFrames.Clear();
      this._boxedLocals.Clear();
      this._addrBoxedLocals.Clear();
      this._arrayBoxedLocals.Clear();
      this._arrayParamSyncs.Clear();
      this._tempCounter = 0;
      this._tryBodyDepth = 0;
      this._activeCatchErrVar = null;

      this._localTaken.Add("Enumerate");
      this._localTaken.Add("ply_yields");
      this._localTaken.Add("m_done");

      if (!this.CheckType(function.ReturnType, function.Name))
        return "";
      foreach (var p in function.Parameters)
        this.CheckType(p.Type, function.Name);
      foreach (var l in function.Locals)
        this.CheckType(l.Type, function.Name);

      this.AnalyzeBodyStorage(function.Parameters, function.Locals, function.Body);

      var seqClassName = "ply_Seq_" + (this._syntheticCounter++).ToString(CultureInfo.InvariantCulture);
      var restartable = function.ReturnType.Restartable;

      var cls = new StringBuilder();
      cls.Append("Class ").Append(seqClassName).Append('\n');
      for (var i = 0; i < function.Parameters.Count; ++i) {
        var fName = "p_" + i.ToString(CultureInfo.InvariantCulture);
        this._localTaken.Add(fName);
        cls.Append("  Public ").Append(fName).Append('\n');
      }
      if (!restartable) {
        cls.Append("  Private m_done\n");
        cls.Append("  Private Sub Class_Initialize()\n    m_done = False\n  End Sub\n");
      }

      var prologue = new StringBuilder();
      this._declaredDims.Add("ply_yields");
      prologue.Append("    Set ply_yields = ply_new_list()\n");
      if (!restartable)
        prologue.Append("    If m_done Then Set Enumerate = ply_yields : Exit Function\n    m_done = True\n");

      for (var i = 0; i < function.Parameters.Count; ++i) {
        var p = function.Parameters[i];
        var pname = VBScriptNaming.Free(VBScriptNaming.Identifier(p.Name), this._localTaken);
        this._locals[p] = pname;
        this._declaredDims.Add(pname);
        var fieldRef = "p_" + i.ToString(CultureInfo.InvariantCulture);
        var initVal = p.Type.IsStruct ? $"{fieldRef}.ply_clone()" : fieldRef;
        this.EmitLocalInit(p, initVal, prologue, 2);
      }

      foreach (var l in function.Locals) {
        var lname = VBScriptNaming.Free(VBScriptNaming.Identifier(l.Name), this._localTaken);
        this._locals[l] = lname;
        this._declaredDims.Add(lname);
      }

      var bodyText = new StringBuilder();
      bodyText.Append(prologue);
      this.EmitBlock(function.Body, bodyText, 2);
      bodyText.Append("    Set Enumerate = ply_yields\n");

      cls.Append("  Public Function Enumerate()\n");
      if (this._declaredDims.Count > 0)
        cls.Append("    Dim ").AppendJoin(", ", this._declaredDims).Append('\n');
      cls.Append(bodyText);
      cls.Append("  End Function\n");
      cls.Append("End Class\n");
      this._syntheticClasses.Add(cls.ToString());

      var fnName = this._functionNames[function.Name];
      var fnHeader = new StringBuilder();
      var fnParams = function.Parameters.Select((p, i) => $"{(p.Type.IsArray ? "ByRef" : "ByVal")} a_{i.ToString(CultureInfo.InvariantCulture)}").ToList();
      fnHeader.Append("Function ").Append(fnName).Append('(').AppendJoin(", ", fnParams).Append(")\n");
      fnHeader.Append("  Dim ply_seq\n");
      fnHeader.Append("  Set ply_seq = New ").Append(seqClassName).Append('\n');
      for (var i = 0; i < function.Parameters.Count; ++i) {
        var p = function.Parameters[i];
        var aRef = "a_" + i.ToString(CultureInfo.InvariantCulture);
        var copied = p.Type.IsStruct ? $"{aRef}.ply_clone()" : aRef;
        fnHeader.Append("  ").Append(AssignPrefix(p.Type)).Append("ply_seq.p_").Append(i.ToString(CultureInfo.InvariantCulture))
          .Append(" = ").Append(copied).Append('\n');
      }
      fnHeader.Append("  Set ").Append(fnName).Append(" = ply_seq\n");
      fnHeader.Append("End Function\n");

      this._currentFunction = null;
      this._iteratorResultListVar = null;
      return fnHeader.ToString();
    }

    private void EmitYield(IrYield yieldStmt, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var val = this.CopyIfStruct(yieldStmt.Value.Type, this.EmitExpr(yieldStmt.Value, text, indent), text, indent);
      text.Append(pad).Append("Call ").Append(this._iteratorResultListVar).Append(".AddItem(").Append(val).Append(")\n");
    }

    private void EmitYieldBreak(StringBuilder text, int indent) {
      var pad = Indent(indent);
      if (this._tryFrames.Count > 0) {
        var frame = this._tryFrames.Peek();
        text.Append(pad).Append(frame.ReturnFlag).Append(" = True\n");
        text.Append(pad).Append("Exit Do\n");
        return;
      }
      this.EmitFinalReturn(null, null, text, indent);
    }

    private void EmitYieldFrom(IrYieldFrom yieldFrom, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var seq = this.EmitExpr(yieldFrom.Sequence, text, indent);
      var seqTmp = this.NewTemp("ply_ys");
      var listTmp = this.NewTemp("ply_yl");
      var idxTmp = this.NewTemp("ply_yi");
      text.Append(pad).Append("Set ").Append(seqTmp).Append(" = ").Append(seq).Append('\n');
      text.Append(pad).Append("Set ").Append(listTmp).Append(" = ").Append(seqTmp).Append(".Enumerate()\n");
      text.Append(pad).Append("For ").Append(idxTmp).Append(" = 0 To ").Append(listTmp).Append(".Count - 1\n");
      text.Append(Indent(indent + 1)).Append("Call ").Append(this._iteratorResultListVar).Append(".AddItem(")
        .Append(listTmp).Append(".GetAt(").Append(idxTmp).Append("))\n");
      text.Append(pad).Append("Next\n");
    }

    private void EmitForEachSequence(IrForEachSequence loop, StringBuilder text, int indent) {
      this.Use(VBScriptHelpers.CollectionSupport);
      var pad = Indent(indent);
      var seq = this.EmitExpr(loop.Sequence, text, indent);
      var seqTmp = this.NewTemp("ply_sq");
      var listTmp = this.NewTemp("ply_sl");
      var idxTmp = this.NewTemp("ply_si");
      var hasContinue = ContainsDirectJump(loop.Body, wantBreak: false);
      var hasBreak = ContainsDirectJump(loop.Body, wantBreak: true);

      text.Append(pad).Append("Set ").Append(seqTmp).Append(" = ").Append(seq).Append('\n');
      text.Append(pad).Append("Set ").Append(listTmp).Append(" = ").Append(seqTmp).Append(".Enumerate()\n");
      text.Append(pad).Append(idxTmp).Append(" = 0\n");
      text.Append(pad).Append("Do While ").Append(idxTmp).Append(" < ").Append(listTmp).Append(".Count\n");

      var innerPad = Indent(indent + 1);
      var elemVal = this.CopyIfStruct(loop.Element.Type, $"{listTmp}.GetAt({idxTmp})", text, indent + 1);
      this.EmitLocalInit(loop.Element, elemVal, text, indent + 1);

      this.EmitLoopBody(loop.Body, null, hasBreak, hasContinue, text, indent + 1);
      text.Append(innerPad).Append(idxTmp).Append(" = ").Append(idxTmp).Append(" + 1\n");
      text.Append(pad).Append("Loop\n");
    }

    private string EmitArraySequence(IrArraySequence arrSeq, StringBuilder pre, int indent) {
      this.Use(VBScriptHelpers.ArrSeq);
      var arr = this.EmitExpr(arrSeq.Array, pre, indent);
      var isStruct = arrSeq.Type.Element!.IsStruct ? "True" : "False";
      return $"ply_arr_seq({arr}, {isStruct})";
    }

    private string EmitAsyncExpr(IrAsync asyncExpr, StringBuilder pre, int indent) {
      this.Use(VBScriptHelpers.TaskSupport);
      switch (asyncExpr.Op) {
        case IrAsyncOp.Completed:
          return "ply_task_completed()";
        case IrAsyncOp.FromResult: {
          var val = this.CopyIfStruct(asyncExpr.Arguments[0].Type, this.EmitExpr(asyncExpr.Arguments[0], pre, indent), pre, indent);
          return $"ply_task_from_result({val})";
        }
        default: {
          var pad = Indent(indent);
          var taskTemps = new List<string>(asyncExpr.Arguments.Count);
          foreach (var arg in asyncExpr.Arguments) {
            var evaluated = this.EmitExpr(arg, pre, indent);
            var t = this.NewTemp("ply_wt");
            pre.Append(pad).Append("Set ").Append(t).Append(" = ").Append(evaluated).Append('\n');
            taskTemps.Add(t);
          }
          var isVoid = asyncExpr.Type.Element == IrType.Void ? "True" : "False";
          return $"ply_task_when_all(Array({string.Join(", ", taskTemps)}), {isVoid})";
        }
      }
    }

    private string EmitAwait(IrAwait awaitExpr, StringBuilder pre, int indent) {
      this.Use(VBScriptHelpers.TaskSupport);
      var operand = this.EmitExpr(awaitExpr.Operand, pre, indent);
      var res = $"ply_task_await({operand})";
      return this.CopyIfStruct(awaitExpr.Type, res, pre, indent);
    }

  }

}
