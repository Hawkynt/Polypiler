using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

internal sealed partial class MsilMethodCompiler {

  /// <summary>Compiles a portable bit-permutation helper (<c>$Pext32</c>, <c>$Pext64</c>, <c>$Pdep32</c>, <c>$Pdep64</c>).</summary>
  public static int CompileBitPermutationHelper(bool isExtract, bool is64, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var valType = is64 ? IrType.U64 : IrType.U32;
    var compiler = new MsilMethodCompiler(ctx, valType, [], []);
    var resultSlot = compiler.AllocTemp(valType);
    var nextSlot = compiler.AllocTemp(IrType.I32);
    var posSlot = compiler.AllocTemp(IrType.I32);

    if (is64)
      compiler._il.LoadConstantI8(0);
    else
      compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(resultSlot);
    compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(nextSlot);
    compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(posSlot);

    var condLabel = compiler._il.DefineLabel();
    var skipLabel = compiler._il.DefineLabel();
    var doneLabel = compiler._il.DefineLabel();

    compiler._il.MarkLabel(condLabel);
    compiler._il.LoadLocal(posSlot);
    if (is64)
      compiler._il.LoadConstantI4(64);
    else
      compiler._il.LoadArgument(2); // bits
    compiler._il.Branch(ILOpCode.Bge, doneLabel);

    // if (((mask >> pos) & 1) != 0)
    compiler._il.LoadArgument(1);
    compiler._il.LoadLocal(posSlot);
    compiler._il.OpCode(ILOpCode.Shr_un);
    if (is64)
      compiler._il.LoadConstantI8(1);
    else
      compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.And);
    compiler._il.Branch(ILOpCode.Brfalse, skipLabel);

    // result |= ((value >> srcShift) & 1) << dstShift; next++;
    compiler._il.LoadLocal(resultSlot);
    compiler._il.LoadArgument(0);
    compiler._il.LoadLocal(isExtract ? posSlot : nextSlot);
    compiler._il.OpCode(ILOpCode.Shr_un);
    if (is64)
      compiler._il.LoadConstantI8(1);
    else
      compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.And);
    compiler._il.LoadLocal(isExtract ? nextSlot : posSlot);
    compiler._il.OpCode(ILOpCode.Shl);
    compiler._il.OpCode(ILOpCode.Or);
    compiler._il.StoreLocal(resultSlot);

    compiler._il.LoadLocal(nextSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Add);
    compiler._il.StoreLocal(nextSlot);

    compiler._il.MarkLabel(skipLabel);
    compiler._il.LoadLocal(posSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Add);
    compiler._il.StoreLocal(posSlot);
    compiler._il.Branch(ILOpCode.Br, condLabel);

    compiler._il.MarkLabel(doneLabel);
    compiler._il.LoadLocal(resultSlot);
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles a bounded pointer load helper <c>$PtrLoad_N($Ptr) -&gt; T</c>.</summary>
  public static int CompilePointerLoadHelper(IrType type, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, type, [], []);
    var ptr = ctx.PtrStruct;
    var size = type.Size;

    compiler.EmitPointerBoundsCheck(size);

    // Fast path: if (ptr.Backing is T[] typed && (ptr.ByteOffset % size == 0)) return typed[ptr.ByteOffset / size];
    var slowLabel = compiler._il.DefineLabel();
    var typedArrSlot = compiler.AllocTemp(IrType.Array(type));
    compiler._il.LoadArgumentAddress(0);
    compiler._il.OpCode(ILOpCode.Ldfld);
    compiler._il.Token(ptr.BackingField);
    compiler._il.OpCode(ILOpCode.Isinst);
    compiler._il.Token(ctx.GetTypeToken(IrType.Array(type)));
    compiler._il.OpCode(ILOpCode.Dup);
    compiler._il.StoreLocal(typedArrSlot);
    compiler._il.Branch(ILOpCode.Brfalse, slowLabel);

    if (size > 1) {
      compiler._il.LoadArgumentAddress(0);
      compiler._il.OpCode(ILOpCode.Ldfld);
      compiler._il.Token(ptr.ByteOffsetField);
      compiler._il.LoadConstantI4(size - 1);
      compiler._il.OpCode(ILOpCode.And);
      compiler._il.Branch(ILOpCode.Brtrue, slowLabel);
    }

    compiler._il.LoadLocal(typedArrSlot);
    compiler._il.LoadArgumentAddress(0);
    compiler._il.OpCode(ILOpCode.Ldfld);
    compiler._il.Token(ptr.ByteOffsetField);
    if (size > 1) {
      compiler._il.LoadConstantI4(size);
      compiler._il.OpCode(ILOpCode.Div);
    }
    compiler.EmitLoadElement(type);
    compiler._il.OpCode(ILOpCode.Ret);

    // Cross-type / unaligned byte-by-byte path via System.Buffer.GetByte(Array, int).
    compiler._il.MarkLabel(slowLabel);
    var is64 = size == 8;
    var getByteRef = compiler.GetBufferGetByteRef();
    for (var i = 0; i < size; i++) {
      compiler._il.LoadArgumentAddress(0);
      compiler._il.OpCode(ILOpCode.Ldfld);
      compiler._il.Token(ptr.BackingField);
      compiler._il.LoadArgumentAddress(0);
      compiler._il.OpCode(ILOpCode.Ldfld);
      compiler._il.Token(ptr.ByteOffsetField);
      if (i > 0) {
        compiler._il.LoadConstantI4(i);
        compiler._il.OpCode(ILOpCode.Add);
      }
      compiler._il.Call(getByteRef);
      if (is64)
        compiler._il.OpCode(ILOpCode.Conv_u8);
      if (i > 0) {
        compiler._il.LoadConstantI4(i * 8);
        compiler._il.OpCode(ILOpCode.Shl);
        compiler._il.OpCode(ILOpCode.Or);
      }
    }

    if (type == IrType.I8)
      compiler._il.OpCode(ILOpCode.Conv_i1);
    else if (type == IrType.I16)
      compiler._il.OpCode(ILOpCode.Conv_i2);
    else if (type == IrType.F32)
      compiler._il.Call(ctx.GetStaticMethodRef("System", "BitConverter", "Int32BitsToSingle", IrType.F32, IrType.I32));
    else if (type == IrType.F64)
      compiler._il.Call(ctx.GetStaticMethodRef("System", "BitConverter", "Int64BitsToDouble", IrType.F64, IrType.I64));
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles a bounded pointer store helper <c>$PtrStore_N($Ptr, T) -&gt; void</c>.</summary>
  public static int CompilePointerStoreHelper(IrType type, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.Void, [], []);
    var ptr = ctx.PtrStruct;
    var size = type.Size;

    compiler.EmitPointerBoundsCheck(size);

    var slowLabel = compiler._il.DefineLabel();
    var typedArrSlot = compiler.AllocTemp(IrType.Array(type));
    compiler._il.LoadArgumentAddress(0);
    compiler._il.OpCode(ILOpCode.Ldfld);
    compiler._il.Token(ptr.BackingField);
    compiler._il.OpCode(ILOpCode.Isinst);
    compiler._il.Token(ctx.GetTypeToken(IrType.Array(type)));
    compiler._il.OpCode(ILOpCode.Dup);
    compiler._il.StoreLocal(typedArrSlot);
    compiler._il.Branch(ILOpCode.Brfalse, slowLabel);

    if (size > 1) {
      compiler._il.LoadArgumentAddress(0);
      compiler._il.OpCode(ILOpCode.Ldfld);
      compiler._il.Token(ptr.ByteOffsetField);
      compiler._il.LoadConstantI4(size - 1);
      compiler._il.OpCode(ILOpCode.And);
      compiler._il.Branch(ILOpCode.Brtrue, slowLabel);
    }

    compiler._il.LoadLocal(typedArrSlot);
    compiler._il.LoadArgumentAddress(0);
    compiler._il.OpCode(ILOpCode.Ldfld);
    compiler._il.Token(ptr.ByteOffsetField);
    if (size > 1) {
      compiler._il.LoadConstantI4(size);
      compiler._il.OpCode(ILOpCode.Div);
    }
    compiler._il.LoadArgument(1);
    compiler.EmitStoreElement(type);
    compiler._il.OpCode(ILOpCode.Ret);

    compiler._il.MarkLabel(slowLabel);
    var is64 = size == 8;
    var bitsSlot = compiler.AllocTemp(is64 ? IrType.U64 : IrType.U32);
    compiler._il.LoadArgument(1);
    if (type == IrType.F32)
      compiler._il.Call(ctx.GetStaticMethodRef("System", "BitConverter", "SingleToInt32Bits", IrType.I32, IrType.F32));
    else if (type == IrType.F64)
      compiler._il.Call(ctx.GetStaticMethodRef("System", "BitConverter", "DoubleToInt64Bits", IrType.I64, IrType.F64));
    compiler._il.StoreLocal(bitsSlot);

    var setByteRef = compiler.GetBufferSetByteRef();
    for (var i = 0; i < size; i++) {
      compiler._il.LoadArgumentAddress(0);
      compiler._il.OpCode(ILOpCode.Ldfld);
      compiler._il.Token(ptr.BackingField);
      compiler._il.LoadArgumentAddress(0);
      compiler._il.OpCode(ILOpCode.Ldfld);
      compiler._il.Token(ptr.ByteOffsetField);
      if (i > 0) {
        compiler._il.LoadConstantI4(i);
        compiler._il.OpCode(ILOpCode.Add);
      }
      compiler._il.LoadLocal(bitsSlot);
      if (i > 0) {
        compiler._il.LoadConstantI4(i * 8);
        compiler._il.OpCode(ILOpCode.Shr_un);
      }
      compiler._il.OpCode(ILOpCode.Conv_u1);
      compiler._il.Call(setByteRef);
    }
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  private void EmitPointerBoundsCheck(int size) {
    var ptr = this._ctx.PtrStruct;
    var notNull = this._il.DefineLabel();
    this._il.LoadArgumentAddress(0);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.BackingField);
    this._il.Branch(ILOpCode.Brtrue, notNull);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.NullReferenceException, 0));
    this._il.OpCode(ILOpCode.Throw);
    this._il.MarkLabel(notNull);

    var failLabel = this._il.DefineLabel();
    var okLabel = this._il.DefineLabel();

    // ByteOffset < RegionStart
    this._il.LoadArgumentAddress(0);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.ByteOffsetField);
    this._il.LoadArgumentAddress(0);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.RegionStartField);
    this._il.Branch(ILOpCode.Blt, failLabel);

    // ByteOffset > RegionStart + RegionBytes - size
    this._il.LoadArgumentAddress(0);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.ByteOffsetField);
    this._il.LoadArgumentAddress(0);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.RegionStartField);
    this._il.LoadArgumentAddress(0);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.RegionBytesField);
    this._il.OpCode(ILOpCode.Add);
    this._il.LoadConstantI4(size);
    this._il.OpCode(ILOpCode.Sub);
    this._il.Branch(ILOpCode.Ble, okLabel);

    this._il.MarkLabel(failLabel);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.IndexOutOfRangeException, 0));
    this._il.OpCode(ILOpCode.Throw);
    this._il.MarkLabel(okLabel);
  }

  private MemberReferenceHandle GetBufferGetByteRef()
    => this._ctx.GetMemberRef(
      "stat:System.Buffer::GetByte(Array,int)",
      this._ctx.GetBclType("System", "Buffer"),
      "GetByte",
      sig => sig.Parameters(2, r => r.Type().Byte(), p => {
        p.AddParameter().Type().Type(this._ctx.GetBclType("System", "Array"), isValueType: false);
        p.AddParameter().Type().Int32();
      }));

  private MemberReferenceHandle GetBufferSetByteRef()
    => this._ctx.GetMemberRef(
      "stat:System.Buffer::SetByte(Array,int,byte)",
      this._ctx.GetBclType("System", "Buffer"),
      "SetByte",
      sig => sig.Parameters(3, r => r.Void(), p => {
        p.AddParameter().Type().Type(this._ctx.GetBclType("System", "Array"), isValueType: false);
        p.AddParameter().Type().Int32();
        p.AddParameter().Type().Byte();
      }));

  private void EmitViewLoad(IrArrayLoad load) {
    var elem = load.Type;
    var slice = this._ctx.SliceStructs[elem];
    var viewSlot = this.AllocTemp(load.Array.Type);
    var idxSlot = this.AllocTemp(IrType.I32);
    this.EmitExpr(load.Array);
    this._il.StoreLocal(viewSlot);
    this.EmitExpr(load.Index);
    this._il.StoreLocal(idxSlot);
    this.EmitViewIndexCheck(viewSlot, idxSlot, slice);

    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Add);
    this.EmitLoadElement(elem);
  }

  private void EmitViewStore(IrStore store) {
    var elem = store.Array.Type.Element!;
    var slice = this._ctx.SliceStructs[elem];
    var viewSlot = this.AllocTemp(store.Array.Type);
    var idxSlot = this.AllocTemp(IrType.I32);
    var valSlot = this.AllocTemp(elem);
    this.EmitExpr(store.Array);
    this._il.StoreLocal(viewSlot);
    this.EmitExpr(store.Index);
    this._il.StoreLocal(idxSlot);
    this.EmitExpr(store.Value);
    this._il.StoreLocal(valSlot);
    this.EmitViewIndexCheck(viewSlot, idxSlot, slice);

    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Add);
    this._il.LoadLocal(valSlot);
    this.EmitStoreElement(elem);
  }

  private void EmitViewIndexCheck(int viewSlot, int idxSlot, SliceHandles slice) {
    var failLabel = this._il.DefineLabel();
    var okLabel = this._il.DefineLabel();
    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(0);
    this._il.Branch(ILOpCode.Blt, failLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Branch(ILOpCode.Blt, okLabel);

    this._il.MarkLabel(failLabel);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.IndexOutOfRangeException, 0));
    this._il.OpCode(ILOpCode.Throw);
    this._il.MarkLabel(okLabel);
  }

  private bool TryEmitViewOrPointerIntrinsic(IrIntrinsic intrinsic) {
    var args = intrinsic.Arguments;
    switch (intrinsic.Op) {
      case IrIntrinsicOp.ParallelBitExtract:
        this.EmitBitPermutationCall(isExtract: true, intrinsic);
        return true;

      case IrIntrinsicOp.ParallelBitDeposit:
        this.EmitBitPermutationCall(isExtract: false, intrinsic);
        return true;

      case IrIntrinsicOp.ViewOf:
        this.EmitViewOfOrSlice(intrinsic.Type, args, fromSlice: false);
        return true;

      case IrIntrinsicOp.Slice:
        this.EmitViewOfOrSlice(intrinsic.Type, args, fromSlice: true);
        return true;

      case IrIntrinsicOp.ViewLength: {
        var elem = args[0].Type.Element!;
        var slice = this._ctx.SliceStructs[elem];
        var slot = this.AllocTemp(args[0].Type);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(slot);
        this._il.LoadLocalAddress(slot);
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(slice.LengthField);
        return true;
      }

      case IrIntrinsicOp.StackAlloc when intrinsic.Type.IsView: {
        var elem = intrinsic.Type.Element!;
        var slice = this._ctx.SliceStructs[elem];
        var lenSlot = this.AllocTemp(IrType.I32);
        var arrSlot = this.AllocTemp(IrType.Array(elem));
        var resSlot = this.AllocTemp(intrinsic.Type);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(lenSlot);

        var okLabel = this._il.DefineLabel();
        this._il.LoadLocal(lenSlot);
        this._il.LoadConstantI4(0);
        this._il.Branch(ILOpCode.Bge, okLabel);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.OverflowException, 0));
        this._il.OpCode(ILOpCode.Throw);
        this._il.MarkLabel(okLabel);

        this._il.LoadLocal(lenSlot);
        this._il.OpCode(ILOpCode.Newarr);
        this._il.Token(this._ctx.GetTypeToken(elem));
        this._il.StoreLocal(arrSlot);

        this.EmitBuildSlice(resSlot, slice, () => this._il.LoadLocal(arrSlot), () => this._il.LoadConstantI4(0), () => this._il.LoadLocal(lenSlot));
        this._il.LoadLocal(resSlot);
        return true;
      }

      case IrIntrinsicOp.StackAlloc when intrinsic.Type.IsPointer: {
        var elem = intrinsic.Type.Element!;
        var lenSlot = this.AllocTemp(IrType.I32);
        var arrSlot = this.AllocTemp(IrType.Array(elem));
        var ptrSlot = this.AllocTemp(intrinsic.Type);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(lenSlot);

        var okLabel = this._il.DefineLabel();
        this._il.LoadLocal(lenSlot);
        this._il.LoadConstantI4(0);
        this._il.Branch(ILOpCode.Bge, okLabel);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.OverflowException, 0));
        this._il.OpCode(ILOpCode.Throw);
        this._il.MarkLabel(okLabel);

        this._il.LoadLocal(lenSlot);
        this._il.OpCode(ILOpCode.Newarr);
        this._il.Token(this._ctx.GetTypeToken(elem));
        this._il.StoreLocal(arrSlot);

        this.EmitBuildPtr(
          ptrSlot,
          () => this._il.LoadLocal(arrSlot),
          () => this._il.LoadConstantI4(0),
          () => this._il.LoadConstantI4(0),
          () => {
            this._il.LoadLocal(lenSlot);
            this._il.LoadConstantI4(elem.Size);
            this._il.OpCode(ILOpCode.Mul);
          });
        this._il.LoadLocal(ptrSlot);
        return true;
      }

      case IrIntrinsicOp.ViewCopy or IrIntrinsicOp.ViewTryCopy:
        this.EmitViewCopy(args[0], args[1], tryCopy: intrinsic.Op == IrIntrinsicOp.ViewTryCopy);
        return true;

      case IrIntrinsicOp.ViewFill:
        this.EmitViewFill(args[0], args[1]);
        return true;

      case IrIntrinsicOp.ViewEqual:
        this.EmitViewEqual(args[0], args[1]);
        return true;

      case IrIntrinsicOp.ViewToArray:
        this.EmitViewToArray(args[0]);
        return true;

      case IrIntrinsicOp.Pin:
        this.EmitPin(intrinsic);
        return true;

      case IrIntrinsicOp.AddressOf: {
        var local = ((IrLocalRef)args[0]).Local;
        var ptrSlot = this.AllocTemp(intrinsic.Type);
        this.EmitBuildPtr(
          ptrSlot,
          () => this._il.LoadLocal(this._addressedLocalSlots[local]),
          () => this._il.LoadConstantI4(0),
          () => this._il.LoadConstantI4(0),
          () => this._il.LoadConstantI4(local.Type.Size));
        this._il.LoadLocal(ptrSlot);
        return true;
      }

      case IrIntrinsicOp.PointerAdd: {
        var elem = intrinsic.Type.Element!;
        var ptr = this._ctx.PtrStruct;
        var pSlot = this.AllocTemp(intrinsic.Type);
        var deltaSlot = this.AllocTemp(IrType.I32);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(pSlot);
        this.EmitExpr(args[1]);
        this._il.StoreLocal(deltaSlot);

        this._il.LoadLocalAddress(pSlot);
        this._il.LoadLocalAddress(pSlot);
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(ptr.ByteOffsetField);
        this._il.LoadLocal(deltaSlot);
        this._il.LoadConstantI4(elem.Size);
        this._il.OpCode(ILOpCode.Mul);
        this._il.OpCode(ILOpCode.Add);
        this._il.OpCode(ILOpCode.Stfld);
        this._il.Token(ptr.ByteOffsetField);
        this._il.LoadLocal(pSlot);
        return true;
      }

      case IrIntrinsicOp.PointerDifference: {
        var elem = args[0].Type.Element!;
        var ptr = this._ctx.PtrStruct;
        var leftSlot = this.AllocTemp(args[0].Type);
        var rightSlot = this.AllocTemp(args[1].Type);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(leftSlot);
        this.EmitExpr(args[1]);
        this._il.StoreLocal(rightSlot);
        this.EmitPointerSameRegionCheck(leftSlot, rightSlot, ptr);

        this._il.LoadLocalAddress(leftSlot);
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(ptr.ByteOffsetField);
        this._il.LoadLocalAddress(rightSlot);
        this._il.OpCode(ILOpCode.Ldfld);
        this._il.Token(ptr.ByteOffsetField);
        this._il.OpCode(ILOpCode.Sub);
        this._il.OpCode(ILOpCode.Conv_i8);
        if (elem.Size > 1) {
          this._il.LoadConstantI8(elem.Size);
          this._il.OpCode(ILOpCode.Div);
        }
        return true;
      }

      case IrIntrinsicOp.PointerEqual or IrIntrinsicOp.PointerNotEqual
        or IrIntrinsicOp.PointerLess or IrIntrinsicOp.PointerLessOrEqual
        or IrIntrinsicOp.PointerGreater or IrIntrinsicOp.PointerGreaterOrEqual:
        this.EmitPointerComparison(intrinsic.Op, args[0], args[1]);
        return true;

      case IrIntrinsicOp.PointerCast:
        this.EmitExpr(args[0]);
        return true;

      case IrIntrinsicOp.PointerLoad:
        this.EmitExpr(args[0]);
        this._il.Call(this._ctx.PtrLoadHandles[intrinsic.Type]);
        return true;

      case IrIntrinsicOp.PointerStore:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.Call(this._ctx.PtrStoreHandles[args[1].Type]);
        return true;

      default:
        return false;
    }
  }

  private void EmitBitPermutationCall(bool isExtract, IrIntrinsic intrinsic) {
    var type = intrinsic.Type;
    this.EmitExpr(intrinsic.Arguments[0]);
    this.EmitExpr(intrinsic.Arguments[1]);
    if (type.IsLong) {
      this._il.Call(isExtract ? this._ctx.Pext64Handle : this._ctx.Pdep64Handle);
    } else {
      this._il.LoadConstantI4(type.Bits);
      this._il.Call(isExtract ? this._ctx.Pext32Handle : this._ctx.Pdep32Handle);
      if (type == IrType.I8)
        this._il.OpCode(ILOpCode.Conv_i1);
      else if (type == IrType.U8)
        this._il.OpCode(ILOpCode.Conv_u1);
      else if (type == IrType.I16)
        this._il.OpCode(ILOpCode.Conv_i2);
      else if (type == IrType.U16)
        this._il.OpCode(ILOpCode.Conv_u2);
    }
  }

  private void EmitViewOfOrSlice(IrType resultType, IReadOnlyList<IrExpr> args, bool fromSlice) {
    var elem = resultType.Element!;
    var slice = this._ctx.SliceStructs[elem];
    var srcType = args[0].Type;
    var srcArrSlot = this.AllocTemp(IrType.Array(elem));
    var srcOffSlot = this.AllocTemp(IrType.I32);
    var srcLenSlot = this.AllocTemp(IrType.I32);

    if (srcType.IsArray) {
      this.EmitExpr(args[0]);
      this._il.StoreLocal(srcArrSlot);
      this._il.LoadConstantI4(0);
      this._il.StoreLocal(srcOffSlot);

      var nullLabel = this._il.DefineLabel();
      var gotLenLabel = this._il.DefineLabel();
      this._il.LoadLocal(srcArrSlot);
      this._il.Branch(ILOpCode.Brfalse, nullLabel);
      this._il.LoadLocal(srcArrSlot);
      this._il.OpCode(ILOpCode.Ldlen);
      this._il.OpCode(ILOpCode.Conv_i4);
      this._il.Branch(ILOpCode.Br, gotLenLabel);
      this._il.MarkLabel(nullLabel);
      this._il.LoadConstantI4(0);
      this._il.MarkLabel(gotLenLabel);
      this._il.StoreLocal(srcLenSlot);
    } else {
      var srcViewSlot = this.AllocTemp(srcType);
      this.EmitExpr(args[0]);
      this._il.StoreLocal(srcViewSlot);
      this._il.LoadLocalAddress(srcViewSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(slice.ArrayField);
      this._il.StoreLocal(srcArrSlot);
      this._il.LoadLocalAddress(srcViewSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(slice.OffsetField);
      this._il.StoreLocal(srcOffSlot);
      this._il.LoadLocalAddress(srcViewSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(slice.LengthField);
      this._il.StoreLocal(srcLenSlot);
    }

    var resSlot = this.AllocTemp(resultType);
    if (!fromSlice && args.Count == 1) {
      this.EmitBuildSlice(resSlot, slice, () => this._il.LoadLocal(srcArrSlot), () => this._il.LoadLocal(srcOffSlot), () => this._il.LoadLocal(srcLenSlot));
      this._il.LoadLocal(resSlot);
      return;
    }

    var startSlot = this.AllocTemp(IrType.I32);
    var countSlot = this.AllocTemp(IrType.I32);
    this.EmitExpr(args[1]);
    this._il.StoreLocal(startSlot);
    if (args.Count == 3) {
      this.EmitExpr(args[2]);
      this._il.StoreLocal(countSlot);
    } else {
      this._il.LoadLocal(srcLenSlot);
      this._il.LoadLocal(startSlot);
      this._il.OpCode(ILOpCode.Sub);
      this._il.StoreLocal(countSlot);
    }

    var failLabel = this._il.DefineLabel();
    var okLabel = this._il.DefineLabel();
    this._il.LoadLocal(startSlot);
    this._il.LoadConstantI4(0);
    this._il.Branch(ILOpCode.Blt, failLabel);
    this._il.LoadLocal(countSlot);
    this._il.LoadConstantI4(0);
    this._il.Branch(ILOpCode.Blt, failLabel);
    this._il.LoadLocal(startSlot);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.LoadLocal(countSlot);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.OpCode(ILOpCode.Add);
    this._il.LoadLocal(srcLenSlot);
    this._il.OpCode(ILOpCode.Conv_i8);
    this._il.Branch(ILOpCode.Ble, okLabel);

    this._il.MarkLabel(failLabel);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.ArgumentOutOfRangeException, 0));
    this._il.OpCode(ILOpCode.Throw);

    this._il.MarkLabel(okLabel);
    this.EmitBuildSlice(
      resSlot,
      slice,
      () => this._il.LoadLocal(srcArrSlot),
      () => {
        this._il.LoadLocal(srcOffSlot);
        this._il.LoadLocal(startSlot);
        this._il.OpCode(ILOpCode.Add);
      },
      () => this._il.LoadLocal(countSlot));
    this._il.LoadLocal(resSlot);
  }

  private void EmitViewCopy(IrExpr srcExpr, IrExpr dstExpr, bool tryCopy) {
    var elem = srcExpr.Type.Element!;
    var slice = this._ctx.SliceStructs[elem];
    var srcSlot = this.AllocTemp(srcExpr.Type);
    var dstSlot = this.AllocTemp(dstExpr.Type);
    this.EmitExpr(srcExpr);
    this._il.StoreLocal(srcSlot);
    this.EmitExpr(dstExpr);
    this._il.StoreLocal(dstSlot);

    var tooShortLabel = this._il.DefineLabel();
    var skipCopyLabel = this._il.DefineLabel();
    var endLabel = this._il.DefineLabel();

    this._il.LoadLocalAddress(srcSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.LoadLocalAddress(dstSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Branch(ILOpCode.Bgt, tooShortLabel);

    // Only call Array.Copy when Length > 0.
    this._il.LoadLocalAddress(srcSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Branch(ILOpCode.Brfalse, skipCopyLabel);

    this._il.LoadLocalAddress(srcSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(srcSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocalAddress(dstSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(dstSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocalAddress(srcSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Call(this.GetArrayCopyRef());

    this._il.MarkLabel(skipCopyLabel);
    if (tryCopy)
      this._il.LoadConstantI4(1);
    this._il.Branch(ILOpCode.Br, endLabel);

    this._il.MarkLabel(tooShortLabel);
    if (tryCopy) {
      this._il.LoadConstantI4(0);
    } else {
      this._il.OpCode(ILOpCode.Newobj);
      this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.ArgumentOutOfRangeException, 0));
      this._il.OpCode(ILOpCode.Throw);
    }
    this._il.MarkLabel(endLabel);
  }

  private void EmitViewFill(IrExpr viewExpr, IrExpr valExpr) {
    var elem = viewExpr.Type.Element!;
    var slice = this._ctx.SliceStructs[elem];
    var viewSlot = this.AllocTemp(viewExpr.Type);
    var valSlot = this.AllocTemp(elem);
    var idxSlot = this.AllocTemp(IrType.I32);
    this.EmitExpr(viewExpr);
    this._il.StoreLocal(viewSlot);
    this.EmitExpr(valExpr);
    this._il.StoreLocal(valSlot);
    this._il.LoadConstantI4(0);
    this._il.StoreLocal(idxSlot);

    var condLabel = this._il.DefineLabel();
    var doneLabel = this._il.DefineLabel();
    this._il.MarkLabel(condLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Branch(ILOpCode.Bge, doneLabel);

    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Add);
    this._il.LoadLocal(valSlot);
    this.EmitStoreElement(elem);

    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(1);
    this._il.OpCode(ILOpCode.Add);
    this._il.StoreLocal(idxSlot);
    this._il.Branch(ILOpCode.Br, condLabel);
    this._il.MarkLabel(doneLabel);
  }

  private void EmitViewEqual(IrExpr leftExpr, IrExpr rightExpr) {
    var elem = leftExpr.Type.Element!;
    var slice = this._ctx.SliceStructs[elem];
    var leftSlot = this.AllocTemp(leftExpr.Type);
    var rightSlot = this.AllocTemp(rightExpr.Type);
    var idxSlot = this.AllocTemp(IrType.I32);
    this.EmitExpr(leftExpr);
    this._il.StoreLocal(leftSlot);
    this.EmitExpr(rightExpr);
    this._il.StoreLocal(rightSlot);

    var falseLabel = this._il.DefineLabel();
    var condLabel = this._il.DefineLabel();
    var trueLabel = this._il.DefineLabel();
    var endLabel = this._il.DefineLabel();

    this._il.LoadLocalAddress(leftSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.LoadLocalAddress(rightSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Branch(ILOpCode.Bne_un, falseLabel);

    this._il.LoadConstantI4(0);
    this._il.StoreLocal(idxSlot);
    this._il.MarkLabel(condLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocalAddress(leftSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Branch(ILOpCode.Bge, trueLabel);

    var aSlot = this.AllocTemp(elem);
    var bSlot = this.AllocTemp(elem);
    this._il.LoadLocalAddress(leftSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(leftSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Add);
    this.EmitLoadElement(elem);
    this._il.StoreLocal(aSlot);

    this._il.LoadLocalAddress(rightSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(rightSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Add);
    this.EmitLoadElement(elem);
    this._il.StoreLocal(bSlot);

    if (elem == IrType.F32) {
      this._il.LoadLocalAddress(aSlot);
      this._il.LoadLocal(bSlot);
      this._il.Call(this._ctx.GetInstanceMethodRef("System", "Single", "Equals", IrType.Bool, IrType.F32));
      this._il.Branch(ILOpCode.Brfalse, falseLabel);
    } else if (elem == IrType.F64) {
      this._il.LoadLocalAddress(aSlot);
      this._il.LoadLocal(bSlot);
      this._il.Call(this._ctx.GetInstanceMethodRef("System", "Double", "Equals", IrType.Bool, IrType.F64));
      this._il.Branch(ILOpCode.Brfalse, falseLabel);
    } else {
      this._il.LoadLocal(aSlot);
      this._il.LoadLocal(bSlot);
      this._il.Branch(ILOpCode.Bne_un, falseLabel);
    }

    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(1);
    this._il.OpCode(ILOpCode.Add);
    this._il.StoreLocal(idxSlot);
    this._il.Branch(ILOpCode.Br, condLabel);

    this._il.MarkLabel(trueLabel);
    this._il.LoadConstantI4(1);
    this._il.Branch(ILOpCode.Br, endLabel);
    this._il.MarkLabel(falseLabel);
    this._il.LoadConstantI4(0);
    this._il.MarkLabel(endLabel);
  }

  private void EmitViewToArray(IrExpr viewExpr) {
    var elem = viewExpr.Type.Element!;
    var slice = this._ctx.SliceStructs[elem];
    var viewSlot = this.AllocTemp(viewExpr.Type);
    var resSlot = this.AllocTemp(IrType.Array(elem));
    this.EmitExpr(viewExpr);
    this._il.StoreLocal(viewSlot);

    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.OpCode(ILOpCode.Newarr);
    this._il.Token(this._ctx.GetTypeToken(elem));
    this._il.StoreLocal(resSlot);

    var doneLabel = this._il.DefineLabel();
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Branch(ILOpCode.Brfalse, doneLabel);

    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocal(resSlot);
    this._il.LoadConstantI4(0);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.Call(this.GetArrayCopyRef());

    this._il.MarkLabel(doneLabel);
    this._il.LoadLocal(resSlot);
  }

  private void EmitPin(IrIntrinsic pin) {
    var srcType = pin.Arguments[0].Type;
    var elem = srcType.Element!;
    var elemSize = elem.Size;
    var arrSlot = this.AllocTemp(IrType.Array(elem));
    var offSlot = this.AllocTemp(IrType.I32);
    var lenSlot = this.AllocTemp(IrType.I32);
    var ptrSlot = this.AllocTemp(pin.Type);

    if (srcType.IsArray) {
      this.EmitExpr(pin.Arguments[0]);
      this._il.StoreLocal(arrSlot);
      this._il.LoadConstantI4(0);
      this._il.StoreLocal(offSlot);

      var nullLabel = this._il.DefineLabel();
      var gotLen = this._il.DefineLabel();
      this._il.LoadLocal(arrSlot);
      this._il.Branch(ILOpCode.Brfalse, nullLabel);
      this._il.LoadLocal(arrSlot);
      this._il.OpCode(ILOpCode.Ldlen);
      this._il.OpCode(ILOpCode.Conv_i4);
      this._il.Branch(ILOpCode.Br, gotLen);
      this._il.MarkLabel(nullLabel);
      if (pin.Arguments.Count == 2) {
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.NullReferenceException, 0));
        this._il.OpCode(ILOpCode.Throw);
      } else {
        this._il.LoadConstantI4(0);
      }
      this._il.MarkLabel(gotLen);
      this._il.StoreLocal(lenSlot);
    } else {
      var slice = this._ctx.SliceStructs[elem];
      var viewSlot = this.AllocTemp(srcType);
      this.EmitExpr(pin.Arguments[0]);
      this._il.StoreLocal(viewSlot);
      this._il.LoadLocalAddress(viewSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(slice.ArrayField);
      this._il.StoreLocal(arrSlot);
      this._il.LoadLocalAddress(viewSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(slice.OffsetField);
      this._il.StoreLocal(offSlot);
      this._il.LoadLocalAddress(viewSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(slice.LengthField);
      this._il.StoreLocal(lenSlot);
    }

    if (pin.Arguments.Count == 1) {
      var emptyLabel = this._il.DefineLabel();
      var endLabel = this._il.DefineLabel();
      this._il.LoadLocal(lenSlot);
      this._il.Branch(ILOpCode.Brfalse, emptyLabel);

      this.EmitBuildPtr(
        ptrSlot,
        () => this._il.LoadLocal(arrSlot),
        () => { this._il.LoadLocal(offSlot); this._il.LoadConstantI4(elemSize); this._il.OpCode(ILOpCode.Mul); },
        () => { this._il.LoadLocal(offSlot); this._il.LoadConstantI4(elemSize); this._il.OpCode(ILOpCode.Mul); },
        () => { this._il.LoadLocal(lenSlot); this._il.LoadConstantI4(elemSize); this._il.OpCode(ILOpCode.Mul); });
      this._il.Branch(ILOpCode.Br, endLabel);

      this._il.MarkLabel(emptyLabel);
      this._il.LoadLocalAddress(ptrSlot);
      this._il.OpCode(ILOpCode.Initobj);
      this._il.Token(this._ctx.PtrStruct.Type);
      this._il.MarkLabel(endLabel);
      this._il.LoadLocal(ptrSlot);
    } else {
      var idxSlot = this.AllocTemp(IrType.I32);
      this.EmitExpr(pin.Arguments[1]);
      this._il.StoreLocal(idxSlot);

      var failLabel = this._il.DefineLabel();
      var okLabel = this._il.DefineLabel();
      this._il.LoadLocal(idxSlot);
      this._il.LoadConstantI4(0);
      this._il.Branch(ILOpCode.Blt, failLabel);
      this._il.LoadLocal(idxSlot);
      this._il.LoadLocal(lenSlot);
      this._il.Branch(ILOpCode.Blt, okLabel);

      this._il.MarkLabel(failLabel);
      this._il.OpCode(ILOpCode.Newobj);
      this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.IndexOutOfRangeException, 0));
      this._il.OpCode(ILOpCode.Throw);

      this._il.MarkLabel(okLabel);
      this.EmitBuildPtr(
        ptrSlot,
        () => this._il.LoadLocal(arrSlot),
        () => {
          this._il.LoadLocal(offSlot);
          this._il.LoadLocal(idxSlot);
          this._il.OpCode(ILOpCode.Add);
          this._il.LoadConstantI4(elemSize);
          this._il.OpCode(ILOpCode.Mul);
        },
        () => { this._il.LoadLocal(offSlot); this._il.LoadConstantI4(elemSize); this._il.OpCode(ILOpCode.Mul); },
        () => { this._il.LoadLocal(lenSlot); this._il.LoadConstantI4(elemSize); this._il.OpCode(ILOpCode.Mul); });
      this._il.LoadLocal(ptrSlot);
    }
  }

  private void EmitPointerComparison(IrIntrinsicOp op, IrExpr leftExpr, IrExpr rightExpr) {
    var ptr = this._ctx.PtrStruct;
    var leftSlot = this.AllocTemp(leftExpr.Type);
    var rightSlot = this.AllocTemp(rightExpr.Type);
    this.EmitExpr(leftExpr);
    this._il.StoreLocal(leftSlot);
    this.EmitExpr(rightExpr);
    this._il.StoreLocal(rightSlot);

    if (op is IrIntrinsicOp.PointerEqual or IrIntrinsicOp.PointerNotEqual) {
      var diffLabel = this._il.DefineLabel();
      var endLabel = this._il.DefineLabel();
      this._il.LoadLocalAddress(leftSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(ptr.BackingField);
      this._il.LoadLocalAddress(rightSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(ptr.BackingField);
      this._il.Branch(ILOpCode.Bne_un, diffLabel);

      this._il.LoadLocalAddress(leftSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(ptr.ByteOffsetField);
      this._il.LoadLocalAddress(rightSlot);
      this._il.OpCode(ILOpCode.Ldfld);
      this._il.Token(ptr.ByteOffsetField);
      this._il.OpCode(ILOpCode.Ceq);
      if (op == IrIntrinsicOp.PointerNotEqual) {
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ceq);
      }
      this._il.Branch(ILOpCode.Br, endLabel);

      this._il.MarkLabel(diffLabel);
      this._il.LoadConstantI4(op == IrIntrinsicOp.PointerNotEqual ? 1 : 0);
      this._il.MarkLabel(endLabel);
      return;
    }

    this.EmitPointerSameRegionCheck(leftSlot, rightSlot, ptr);
    this._il.LoadLocalAddress(leftSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.ByteOffsetField);
    this._il.LoadLocalAddress(rightSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.ByteOffsetField);

    switch (op) {
      case IrIntrinsicOp.PointerLess:
        this._il.OpCode(ILOpCode.Clt);
        break;
      case IrIntrinsicOp.PointerGreater:
        this._il.OpCode(ILOpCode.Cgt);
        break;
      case IrIntrinsicOp.PointerLessOrEqual:
        this._il.OpCode(ILOpCode.Cgt);
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ceq);
        break;
      default:
        this._il.OpCode(ILOpCode.Clt);
        this._il.LoadConstantI4(0);
        this._il.OpCode(ILOpCode.Ceq);
        break;
    }
  }

  private void EmitPointerSameRegionCheck(int leftSlot, int rightSlot, PtrHandles ptr) {
    var okLabel = this._il.DefineLabel();
    this._il.LoadLocalAddress(leftSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.BackingField);
    this._il.LoadLocalAddress(rightSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(ptr.BackingField);
    this._il.Branch(ILOpCode.Beq, okLabel);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.ArithmeticException, 0));
    this._il.OpCode(ILOpCode.Throw);
    this._il.MarkLabel(okLabel);
  }

  private void EmitBuildSlice(int slot, SliceHandles slice, Action loadArr, Action loadOff, Action loadLen) {
    this._il.LoadLocalAddress(slot);
    this._il.OpCode(ILOpCode.Initobj);
    this._il.Token(slice.Type);
    this._il.LoadLocalAddress(slot);
    loadArr();
    this._il.OpCode(ILOpCode.Stfld);
    this._il.Token(slice.ArrayField);
    this._il.LoadLocalAddress(slot);
    loadOff();
    this._il.OpCode(ILOpCode.Stfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocalAddress(slot);
    loadLen();
    this._il.OpCode(ILOpCode.Stfld);
    this._il.Token(slice.LengthField);
  }

  private void EmitBuildPtr(int slot, Action loadBacking, Action loadByteOff, Action loadRegStart, Action loadRegBytes) {
    var ptr = this._ctx.PtrStruct;
    this._il.LoadLocalAddress(slot);
    this._il.OpCode(ILOpCode.Initobj);
    this._il.Token(ptr.Type);
    this._il.LoadLocalAddress(slot);
    loadBacking();
    this._il.OpCode(ILOpCode.Stfld);
    this._il.Token(ptr.BackingField);
    this._il.LoadLocalAddress(slot);
    loadByteOff();
    this._il.OpCode(ILOpCode.Stfld);
    this._il.Token(ptr.ByteOffsetField);
    this._il.LoadLocalAddress(slot);
    loadRegStart();
    this._il.OpCode(ILOpCode.Stfld);
    this._il.Token(ptr.RegionStartField);
    this._il.LoadLocalAddress(slot);
    loadRegBytes();
    this._il.OpCode(ILOpCode.Stfld);
    this._il.Token(ptr.RegionBytesField);
  }

  private void EmitEndianLoadOnView(IrType resultType, IrExpr viewExpr, IrExpr indexExpr, bool littleEndian) {
    var size = resultType.Size;
    var slice = this._ctx.SliceStructs[IrType.U8];
    var viewSlot = this.AllocTemp(viewExpr.Type);
    var idxSlot = this.AllocTemp(IrType.I32);
    var arrSlot = this.AllocTemp(IrType.Array(IrType.U8));
    var absIdxSlot = this.AllocTemp(IrType.I32);

    this.EmitExpr(viewExpr);
    this._il.StoreLocal(viewSlot);
    this.EmitExpr(indexExpr);
    this._il.StoreLocal(idxSlot);
    this.EmitEndianViewBoundsCheck(viewSlot, idxSlot, size, slice);

    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.StoreLocal(arrSlot);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Add);
    this._il.StoreLocal(absIdxSlot);

    this.EmitEndianLoadFromSlots(resultType, arrSlot, absIdxSlot, littleEndian);
  }

  private void EmitEndianStoreOnView(IrExpr viewExpr, IrExpr indexExpr, IrExpr valueExpr, bool littleEndian) {
    var valueType = valueExpr.Type;
    var size = valueType.Size;
    var slice = this._ctx.SliceStructs[IrType.U8];
    var viewSlot = this.AllocTemp(viewExpr.Type);
    var idxSlot = this.AllocTemp(IrType.I32);
    var arrSlot = this.AllocTemp(IrType.Array(IrType.U8));
    var absIdxSlot = this.AllocTemp(IrType.I32);
    var bitsSlot = this.AllocTemp(size == 8 ? IrType.U64 : IrType.U32);

    this.EmitExpr(viewExpr);
    this._il.StoreLocal(viewSlot);
    this.EmitExpr(indexExpr);
    this._il.StoreLocal(idxSlot);
    this.EmitExpr(valueExpr);
    if (valueType == IrType.F32)
      this._il.Call(this._ctx.GetStaticMethodRef("System", "BitConverter", "SingleToInt32Bits", IrType.I32, IrType.F32));
    else if (valueType == IrType.F64)
      this._il.Call(this._ctx.GetStaticMethodRef("System", "BitConverter", "DoubleToInt64Bits", IrType.I64, IrType.F64));
    this._il.StoreLocal(bitsSlot);

    this.EmitEndianViewBoundsCheck(viewSlot, idxSlot, size, slice);

    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.ArrayField);
    this._il.StoreLocal(arrSlot);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.OffsetField);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Add);
    this._il.StoreLocal(absIdxSlot);

    this.EmitEndianStoreFromSlots(arrSlot, absIdxSlot, bitsSlot, size, littleEndian);
  }

  private void EmitEndianViewBoundsCheck(int viewSlot, int idxSlot, int size, SliceHandles slice) {
    var failLabel = this._il.DefineLabel();
    var okLabel = this._il.DefineLabel();
    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(0);
    this._il.Branch(ILOpCode.Blt, failLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocalAddress(viewSlot);
    this._il.OpCode(ILOpCode.Ldfld);
    this._il.Token(slice.LengthField);
    this._il.LoadConstantI4(size);
    this._il.OpCode(ILOpCode.Sub);
    this._il.Branch(ILOpCode.Ble, okLabel);

    this._il.MarkLabel(failLabel);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.ArgumentOutOfRangeException, 0));
    this._il.OpCode(ILOpCode.Throw);
    this._il.MarkLabel(okLabel);
  }

  private MemberReferenceHandle GetArrayCopyRef()
    => this._ctx.GetMemberRef(
      "stat:System.Array::Copy(Array,int,Array,int,int)",
      this._ctx.GetBclType("System", "Array"),
      "Copy",
      sig => sig.Parameters(5, r => r.Void(), p => {
        p.AddParameter().Type().Type(this._ctx.GetBclType("System", "Array"), isValueType: false);
        p.AddParameter().Type().Int32();
        p.AddParameter().Type().Type(this._ctx.GetBclType("System", "Array"), isValueType: false);
        p.AddParameter().Type().Int32();
        p.AddParameter().Type().Int32();
      }));

}
