using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

internal sealed partial class MsilMethodCompiler {

  /// <summary>Compiles an async function returning <c>Task</c> or <c>Task&lt;T&gt;</c>, catching faults into <c>Task.FromException</c>.</summary>
  public static int CompileAsyncFunction(IrFunction function, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, function.ReturnType, function.Parameters, function.Locals) {
      _isAsyncMethod = true
    };
    compiler.EmitCapturedParamInit(function.Parameters);
    compiler.EmitAddressedParamInit(function.Parameters);
    compiler.EnsureReturnTarget();

    var tryStart = compiler._il.DefineLabel();
    var tryEnd = compiler._il.DefineLabel();
    var catchStart = compiler._il.DefineLabel();
    var catchEnd = compiler._il.DefineLabel();

    compiler._il.MarkLabel(tryStart);
    compiler._tryDepth++;
    compiler.EmitStmt(function.Body);

    // Fallthrough for async<void> completes with Task.CompletedTask.
    if (function.ReturnType.Element == IrType.Void) {
      compiler._il.Call(ctx.GetTaskCompletedTask());
      compiler._il.StoreLocal(compiler._returnSlot);
    }
    compiler._il.Branch(ILOpCode.Leave, compiler._returnLabel);
    compiler._il.MarkLabel(tryEnd);

    compiler._il.MarkLabel(catchStart);
    if (function.ReturnType.Element == IrType.Void)
      compiler._il.Call(ctx.GetTaskFromExceptionVoid());
    else
      compiler._il.Call(ctx.GetTaskFromException(function.ReturnType.Element!));
    compiler._il.StoreLocal(compiler._returnSlot);
    compiler._il.Branch(ILOpCode.Leave, compiler._returnLabel);
    compiler._il.MarkLabel(catchEnd);
    compiler._tryDepth--;

    compiler._flow.AddCatchRegion(tryStart, tryEnd, catchStart, catchEnd, ctx.GetBclType("System", "Exception"));
    compiler._il.MarkLabel(compiler._returnLabel);
    compiler._il.LoadLocal(compiler._returnSlot);
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 64, localVariablesSignature: localsSig);
  }

  private void EmitAwait(IrAwait awaited) {
    var elem = awaited.Operand.Type.Element!;
    this.EmitExpr(awaited.Operand);
    if (elem == IrType.Void) {
      var awaiterType = this._ctx.GetBclType("System.Runtime.CompilerServices", "TaskAwaiter");
      var awaiterSlot = this.AllocCustomTemp(enc => enc.Type(awaiterType, isValueType: true));
      this._il.OpCode(ILOpCode.Callvirt);
      this._il.Token(this._ctx.GetMemberRef(
        "inst:Task::GetAwaiter()",
        this._ctx.GetBclType("System.Threading.Tasks", "Task"),
        "GetAwaiter",
        sig => sig.Parameters(0, r => r.Type().Type(awaiterType, isValueType: true), _ => { })));
      this._il.StoreLocal(awaiterSlot);
      this._il.LoadLocalAddress(awaiterSlot);
      this._il.Call(this._ctx.GetMemberRef(
        "inst:TaskAwaiter::GetResult()",
        awaiterType,
        "GetResult",
        sig => sig.Parameters(0, r => r.Void(), _ => { })));
    } else {
      var taskSpec = this._ctx.GetTaskSpec(elem);
      var awaiterGeneric = this._ctx.GetBclType("System.Runtime.CompilerServices", "TaskAwaiter`1");
      var awaiterSpec = this._ctx.GetTaskAwaiterSpec(elem);
      var awaiterSlot = this.AllocCustomTemp(enc => {
        var args = enc.GenericInstantiation(awaiterGeneric, 1, isValueType: true);
        this._ctx.EncodeType(args.AddArgument(), elem);
      });
      this._il.OpCode(ILOpCode.Callvirt);
      this._il.Token(this._ctx.GetMemberRef(
        $"inst:Task<{elem}>::GetAwaiter()",
        taskSpec,
        "GetAwaiter",
        sig => sig.Parameters(
          0,
          r => r.Type().GenericInstantiation(awaiterGeneric, 1, isValueType: true).AddArgument().GenericTypeParameter(0),
          _ => { })));
      this._il.StoreLocal(awaiterSlot);
      this._il.LoadLocalAddress(awaiterSlot);
      this._il.Call(this._ctx.GetMemberRef(
        $"inst:TaskAwaiter<{elem}>::GetResult()",
        awaiterSpec,
        "GetResult",
        sig => sig.Parameters(0, r => r.Type().GenericTypeParameter(0), _ => { })));
    }
  }

  private void EmitAsyncOp(IrAsync asyncOp) {
    switch (asyncOp.Op) {
      case IrAsyncOp.Completed:
        this._il.Call(this._ctx.GetTaskCompletedTask());
        break;

      case IrAsyncOp.FromResult:
        this.EmitExpr(asyncOp.Arguments[0]);
        this._il.Call(this._ctx.GetTaskFromResult(asyncOp.Type.Element!));
        break;

      case IrAsyncOp.WhenAll: {
        var resultElem = asyncOp.Type.Element!;
        if (resultElem == IrType.Void) {
          this._il.LoadConstantI4(asyncOp.Arguments.Count);
          this._il.OpCode(ILOpCode.Newarr);
          this._il.Token(this._ctx.GetBclType("System.Threading.Tasks", "Task"));
          for (var i = 0; i < asyncOp.Arguments.Count; i++) {
            this._il.OpCode(ILOpCode.Dup);
            this._il.LoadConstantI4(i);
            this.EmitExpr(asyncOp.Arguments[i]);
            this._il.OpCode(ILOpCode.Stelem_ref);
          }
          this._il.Call(this._ctx.GetTaskWhenAllVoid());
        } else {
          var itemType = resultElem.Element!;
          this._il.LoadConstantI4(asyncOp.Arguments.Count);
          this._il.OpCode(ILOpCode.Newarr);
          this._il.Token(this._ctx.GetTaskSpec(itemType));
          for (var i = 0; i < asyncOp.Arguments.Count; i++) {
            this._il.OpCode(ILOpCode.Dup);
            this._il.LoadConstantI4(i);
            this.EmitExpr(asyncOp.Arguments[i]);
            this._il.OpCode(ILOpCode.Stelem_ref);
          }
          this._il.Call(this._ctx.GetTaskWhenAll(itemType));
        }
        break;
      }
    }
  }

}
