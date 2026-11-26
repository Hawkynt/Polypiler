using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

internal sealed partial class MsilMethodCompiler {

  /// <summary>Compiles a parameterless constructor calling <c>System.Object::.ctor()</c>.</summary>
  public static int CompileDefaultObjectCtor(MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.Void, [], []);
    compiler._il.LoadArgument(0);
    compiler._il.Call(ctx.GetObjectCtor());
    compiler._il.OpCode(ILOpCode.Ret);
    return bodies.AddMethodBody(compiler._il, maxStack: 4);
  }

  /// <summary>Compiles a constructor that calls <c>System.Object::.ctor()</c> and stores each argument into its field.</summary>
  public static int CompileFieldStoringCtor(IReadOnlyList<FieldDefinitionHandle> fields, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.Void, [], []);
    compiler._il.LoadArgument(0);
    compiler._il.Call(ctx.GetObjectCtor());
    for (var i = 0; i < fields.Count; i++) {
      compiler._il.LoadArgument(0);
      compiler._il.LoadArgument(i + 1);
      compiler._il.OpCode(ILOpCode.Stfld);
      compiler._il.Token(fields[i]);
    }
    compiler._il.OpCode(ILOpCode.Ret);
    return bodies.AddMethodBody(compiler._il, maxStack: 8);
  }

  /// <summary>Compiles the <c>Invoke</c> method of a synthesized lambda display class.</summary>
  public static int CompileLambdaInvoke(IrLambda lambda, FieldDefinitionHandle[] captureFields, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, lambda.ReturnType, lambda.Parameters, lambda.Locals, paramArgOffset: 1);
    for (var i = 0; i < lambda.Captures.Count; i++)
      compiler._outerCaptureFields[lambda.Captures[i]] = captureFields[i];
    compiler.EmitCapturedParamInit(lambda.Parameters);
    compiler.EmitStmt(lambda.Body);
    compiler.EmitEpilogue();
    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 64, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles the <c>Invoke</c> method of a synthesized function-reference adapter.</summary>
  public static int CompileFunctionRefInvoke(string functionName, IrFunctionType sig, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, sig.Result, [], []);
    for (var i = 0; i < sig.Parameters.Count; i++)
      compiler._il.LoadArgument(i + 1);
    compiler._il.Call(ctx.FunctionHandles[functionName]);
    compiler._il.OpCode(ILOpCode.Ret);
    return bodies.AddMethodBody(compiler._il, maxStack: Math.Max(8, sig.Parameters.Count + 2));
  }

  /// <summary>Compiles the <c>Invoke</c> method of a synthesized bound-method adapter.</summary>
  public static int CompileBoundMethodInvoke(BoundMethodKey key, FieldDefinitionHandle? receiverField, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, key.Signature.Result, [], []);
    if (receiverField is { } rf) {
      compiler._il.LoadArgument(0);
      compiler._il.OpCode(ILOpCode.Ldfld);
      compiler._il.Token(rf);
    }
    for (var i = 0; i < key.Signature.Parameters.Count; i++)
      compiler._il.LoadArgument(i + 1);

    var token = ctx.GetMethodToken(key.Method);
    var opcode = key.Method.IsStatic || key.Kind == IrMethodCallKind.Base ? ILOpCode.Call : ILOpCode.Callvirt;
    compiler._il.OpCode(opcode);
    compiler._il.Token(token);
    compiler._il.OpCode(ILOpCode.Ret);
    return bodies.AddMethodBody(compiler._il, maxStack: Math.Max(8, key.Signature.Parameters.Count + 4));
  }

  private void EmitLambda(IrLambda lambda) {
    var handles = this._ctx.LambdaClasses[lambda];
    foreach (var cap in lambda.Captures)
      this.EmitLoadCaptureCell(cap);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(handles.Ctor);
  }

  private void EmitFunctionRef(IrFunctionRef fnRef) {
    var handles = this._ctx.FnRefClasses[(fnRef.Function, fnRef.Type.Signature!)];
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(handles.Ctor);
  }

  private void EmitBoundMethod(IrBoundMethod bound) {
    var key = new BoundMethodKey(bound.Method, bound.Kind, bound.Type.Signature!);
    var handles = this._ctx.BoundMethodClasses[key];
    if (bound.Receiver is not null) {
      this.EmitExpr(bound.Receiver);
      var okLabel = this._il.DefineLabel();
      this._il.OpCode(ILOpCode.Dup);
      this._il.Branch(ILOpCode.Brtrue, okLabel);
      this._il.OpCode(ILOpCode.Newobj);
      this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.NullReferenceException, 0));
      this._il.OpCode(ILOpCode.Throw);
      this._il.MarkLabel(okLabel);
    }
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(handles.Ctor);
  }

  private void EmitInvoke(IrInvoke invoke) {
    var sig = invoke.Callee.Type.Signature!;
    this.EmitExpr(invoke.Callee);
    foreach (var arg in invoke.Arguments)
      this.EmitExpr(arg);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this._ctx.FnInterfaces[sig].Invoke);
  }

  private void EmitLoadCaptureCell(IrLocal local) {
    if (this._outerCaptureFields.TryGetValue(local, out var field)) {
      this._il.LoadArgument(0);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(field);
    } else {
      this._il.LoadLocal(this._capturedLocalSlots[local]);
    }
  }

  private void EnsureCapturedCell(IrLocal local) {
    if (!this._capturedLocalSlots.TryGetValue(local, out var cellSlot))
      return;

    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.CellTypes[local.Type].Ctor);
    this._il.StoreLocal(cellSlot);
  }

  private void EmitRenewPerIteration(IReadOnlyList<IrLocal> perIteration) {
    foreach (var local in perIteration) {
      if (!this._capturedLocalSlots.TryGetValue(local, out var cellSlot))
        continue;

      var cell = this._ctx.CellTypes[local.Type];
      var valSlot = this.AllocTemp(local.Type);
      this._il.LoadLocal(cellSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(cell.ValueField);
      this._il.StoreLocal(valSlot);

      this._il.OpCode(ILOpCode.Newobj);
      this._il.Token(cell.Ctor);
      this._il.OpCode(ILOpCode.Dup);
      this._il.LoadLocal(valSlot);
      this._il.OpCode(ILOpCode.Stfld);
      this._il.Token(cell.ValueField);
      this._il.StoreLocal(cellSlot);
    }
  }

}
