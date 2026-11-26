using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

internal sealed partial class MsilMethodCompiler {

  /// <summary>Compiles the factory wrapper for an iterator function (allocating and returning its <c>$Iter_N</c> instance).</summary>
  public static int CompileIteratorFactory(IrFunction function, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, function.ReturnType, [], []);
    for (var i = 0; i < function.Parameters.Count; i++)
      compiler._il.LoadArgument(i);
    compiler._il.OpCode(ILOpCode.Newobj);
    compiler._il.Token(ctx.IterClasses[function].Ctor);
    compiler._il.OpCode(ILOpCode.Ret);
    return bodies.AddMethodBody(compiler._il, maxStack: System.Math.Max(8, function.Parameters.Count + 2));
  }

  /// <summary>Compiles the <c>Enumerate()</c> method of an iterator class <c>$Iter_N</c>.</summary>
  public static int CompileIteratorEnumerate(IrFunction function, IterHandles handles, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var elem = function.ReturnType.Element!;
    var listType = IrType.List(elem);
    var compiler = new MsilMethodCompiler(ctx, listType, [], function.Locals, paramArgOffset: 1);

    // Copy saved parameter fields into fresh local slots so restartable enumerations start clean.
    for (var i = 0; i < function.Parameters.Count; i++) {
      var param = function.Parameters[i];
      if (ctx.Plan.CapturedLocals.Contains(param)) {
        var cell = ctx.CellTypes[param.Type];
        var cellSlot = compiler.AllocCustomTemp(enc => enc.Type(cell.Type, isValueType: false));
        compiler._capturedLocalSlots[param] = cellSlot;
        compiler._il.OpCode(ILOpCode.Newobj);
        compiler._il.Token(cell.Ctor);
        compiler._il.OpCode(ILOpCode.Dup);
        compiler._il.LoadArgument(0);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(handles.ParamFields[i]);
        compiler._il.OpCode(ILOpCode.Stfld);
        compiler._il.Token(cell.ValueField);
        compiler._il.StoreLocal(cellSlot);
      } else {
        var slot = compiler.AllocTemp(param.Type);
        compiler._localSlots[param] = slot;
        compiler._il.LoadArgument(0);
        compiler._il.OpCode(ILOpCode.Ldfld);
        compiler._il.Token(handles.ParamFields[i]);
        compiler._il.StoreLocal(slot);
      }
    }

    // Allocate output List<T>.
    var outSlot = compiler.AllocTemp(listType);
    var listSpec = ctx.GetListSpec(elem);
    compiler._il.OpCode(ILOpCode.Newobj);
    compiler._il.Token(ctx.GetMemberRef($"inst:List<{elem}>..ctor()", listSpec, ".ctor", sig => sig.Parameters(0, r => r.Void(), _ => { })));
    compiler._il.StoreLocal(outSlot);

    var endLabel = compiler._il.DefineLabel();
    compiler._iteratorListSlot = outSlot;
    compiler._iteratorElemType = elem;
    compiler._iteratorEndLabel = endLabel;

    // For single-pass sequences, return empty list on subsequent enumerations.
    if (handles.ConsumedField is { } consumedField) {
      compiler._il.LoadArgument(0);
      compiler._il.OpCode(ILOpCode.Ldfld);
      compiler._il.Token(consumedField);
      compiler._il.Branch(ILOpCode.Brtrue, endLabel);
      compiler._il.LoadArgument(0);
      compiler._il.LoadConstantI4(1);
      compiler._il.OpCode(ILOpCode.Stfld);
      compiler._il.Token(consumedField);
    }

    compiler.EmitStmt(function.Body);
    compiler._il.MarkLabel(endLabel);
    compiler._il.LoadLocal(outSlot);
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 64, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles the <c>Enumerate()</c> method of an array-sequence adapter <c>$ArrSeq_N</c>.</summary>
  public static int CompileArraySeqEnumerate(IrType elem, FieldDefinitionHandle arrayField, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var listType = IrType.List(elem);
    var compiler = new MsilMethodCompiler(ctx, listType, [], []);
    var arrSlot = compiler.AllocTemp(IrType.Array(elem));
    var listSlot = compiler.AllocTemp(listType);
    var idxSlot = compiler.AllocTemp(IrType.I32);

    compiler._il.LoadArgument(0);
    compiler._il.OpCode(ILOpCode.Ldfld);
    compiler._il.Token(arrayField);
    compiler._il.StoreLocal(arrSlot);

    var spec = ctx.GetListSpec(elem);
    compiler._il.LoadLocal(arrSlot);
    compiler._il.OpCode(ILOpCode.Ldlen);
    compiler._il.OpCode(ILOpCode.Conv_i4);
    compiler._il.OpCode(ILOpCode.Newobj);
    compiler._il.Token(ctx.GetMemberRef($"inst:List<{elem}>..ctor(int)", spec, ".ctor", sig => sig.Parameters(1, r => r.Void(), p => p.AddParameter().Type().Int32())));
    compiler._il.StoreLocal(listSlot);

    compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(idxSlot);
    var condLabel = compiler._il.DefineLabel();
    var doneLabel = compiler._il.DefineLabel();
    compiler._il.MarkLabel(condLabel);
    compiler._il.LoadLocal(idxSlot);
    compiler._il.LoadLocal(arrSlot);
    compiler._il.OpCode(ILOpCode.Ldlen);
    compiler._il.OpCode(ILOpCode.Conv_i4);
    compiler._il.Branch(ILOpCode.Bge, doneLabel);

    compiler._il.LoadLocal(listSlot);
    compiler._il.LoadLocal(arrSlot);
    compiler._il.LoadLocal(idxSlot);
    compiler.EmitLoadElement(elem);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(compiler.GetListAddRef(elem));

    compiler._il.LoadLocal(idxSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Add);
    compiler._il.StoreLocal(idxSlot);
    compiler._il.Branch(ILOpCode.Br, condLabel);

    compiler._il.MarkLabel(doneLabel);
    compiler._il.LoadLocal(listSlot);
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  private void EmitYield(IrYield yielded) {
    this._il.LoadLocal(this._iteratorListSlot);
    this.EmitExpr(yielded.Value);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListAddRef(this._iteratorElemType!));
  }

  private void EmitYieldBreak()
    => this._il.Branch(this._tryDepth > 0 ? ILOpCode.Leave : ILOpCode.Br, this._iteratorEndLabel);

  private void EmitYieldFrom(IrYieldFrom delegated) {
    var elem = delegated.Sequence.Type.Element!;
    var innerList = this.AllocTemp(IrType.List(elem));
    var idxSlot = this.AllocTemp(IrType.I32);
    this.EmitExpr(delegated.Sequence);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this._ctx.SeqInterfaces[elem].Enumerate);
    this._il.StoreLocal(innerList);

    this._il.LoadConstantI4(0);
    this._il.StoreLocal(idxSlot);
    var condLabel = this._il.DefineLabel();
    var doneLabel = this._il.DefineLabel();
    this._il.MarkLabel(condLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocal(innerList);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListCountRef(elem));
    this._il.Branch(ILOpCode.Bge, doneLabel);

    this._il.LoadLocal(this._iteratorListSlot);
    this._il.LoadLocal(innerList);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListGetItemRef(elem));
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListAddRef(elem));

    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(1);
    this._il.OpCode(ILOpCode.Add);
    this._il.StoreLocal(idxSlot);
    this._il.Branch(ILOpCode.Br, condLabel);
    this._il.MarkLabel(doneLabel);
  }

  private void EmitForEachSequence(IrForEachSequence loop) {
    var elem = loop.Sequence.Type.Element!;
    var listSlot = this.AllocTemp(IrType.List(elem));
    var idxSlot = this.AllocTemp(IrType.I32);
    this.EmitExpr(loop.Sequence);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this._ctx.SeqInterfaces[elem].Enumerate);
    this._il.StoreLocal(listSlot);

    this._il.LoadConstantI4(0);
    this._il.StoreLocal(idxSlot);
    var condLabel = this._il.DefineLabel();
    var stepLabel = this._il.DefineLabel();
    var breakLabel = this._il.DefineLabel();

    this._il.MarkLabel(condLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocal(listSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListCountRef(elem));
    this._il.Branch(ILOpCode.Bge, breakLabel);

    this.EnsureCapturedCell(loop.Element);
    this._il.LoadLocal(listSlot);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListGetItemRef(elem));
    this.EmitStoreLocalOrParam(loop.Element);

    this._loops.Push(new LoopTarget(breakLabel, stepLabel, this._tryDepth));
    this.EmitStmt(loop.Body);
    this._loops.Pop();

    this._il.MarkLabel(stepLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(1);
    this._il.OpCode(ILOpCode.Add);
    this._il.StoreLocal(idxSlot);
    this._il.Branch(ILOpCode.Br, condLabel);
    this._il.MarkLabel(breakLabel);
  }

  private void EmitArraySequence(IrArraySequence arrSeq) {
    var elem = arrSeq.Type.Element!;
    this.EmitExpr(arrSeq.Array);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.ArrSeqClasses[elem].Ctor);
  }

}
