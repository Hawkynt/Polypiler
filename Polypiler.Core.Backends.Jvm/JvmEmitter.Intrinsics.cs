using System;
using Polypiler.Core.Backends.Jvm.Bytecode;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

public static partial class JvmEmitter {

  private sealed partial class ModuleEmitter {

    private static bool IsSupportedIntrinsic(IrIntrinsic intrinsic) => intrinsic.Op switch {
      IrIntrinsicOp.Abs or IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Sign
        or IrIntrinsicOp.Sqrt or IrIntrinsicOp.Floor or IrIntrinsicOp.Ceiling or IrIntrinsicOp.Truncate
        or IrIntrinsicOp.RoundEven or IrIntrinsicOp.RoundAway or IrIntrinsicOp.CopySign
        or IrIntrinsicOp.FloatBits or IrIntrinsicOp.BitsFloat
        or IrIntrinsicOp.IsNaN or IrIntrinsicOp.IsInfinity or IrIntrinsicOp.IsFinite
        or IrIntrinsicOp.IsNormal or IrIntrinsicOp.IsSubnormal or IrIntrinsicOp.IsNegative
        or IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight
        or IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.PopCount
        or IrIntrinsicOp.ByteSwap or IrIntrinsicOp.HighWord or IrIntrinsicOp.JoinWords
        or IrIntrinsicOp.ArrayOf or IrIntrinsicOp.ArrayCopy or IrIntrinsicOp.ArrayClear or IrIntrinsicOp.ArrayFill
        or IrIntrinsicOp.StrLength or IrIntrinsicOp.StrConcat or IrIntrinsicOp.StrEquals
        or IrIntrinsicOp.StrCodeUnitAt or IrIntrinsicOp.StrSubstring or IrIntrinsicOp.StrCompareOrdinal
        or IrIntrinsicOp.FormatDotNet => true,
      IrIntrinsicOp.FormatEcma => intrinsic.Arguments[0].Type.IsInteger,
      IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig
        => intrinsic.Arguments[0].Type.IsArray,
      _ => false
    };

    private sealed partial class MethodContext {

      private void EmitIntrinsic(IrIntrinsic intrinsic) {
        var args = intrinsic.Arguments;
        switch (intrinsic.Op) {
          case IrIntrinsicOp.Abs:
            this.EmitAbs(args[0]);
            break;
          case IrIntrinsicOp.Min:
            this.EmitMinMax(args[0], args[1], isMin: true);
            break;
          case IrIntrinsicOp.Max:
            this.EmitMinMax(args[0], args[1], isMin: false);
            break;
          case IrIntrinsicOp.Sign:
            this.EmitFloatSign(args[0]);
            break;
          case IrIntrinsicOp.Sqrt:
            this.EmitMathUnaryFloat(args[0], "sqrt");
            break;
          case IrIntrinsicOp.Floor:
            this.EmitMathUnaryFloat(args[0], "floor");
            break;
          case IrIntrinsicOp.Ceiling:
            this.EmitMathUnaryFloat(args[0], "ceil");
            break;
          case IrIntrinsicOp.Truncate:
            this.EmitTruncate(args[0]);
            break;
          case IrIntrinsicOp.RoundEven:
            this.EmitMathUnaryFloat(args[0], "rint");
            break;
          case IrIntrinsicOp.RoundAway:
            this.EmitRoundAway(args[0]);
            break;
          case IrIntrinsicOp.CopySign:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            if (args[0].Type == IrType.F32)
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "copySign", "(FF)F", 2, VType.Float);
            else
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "copySign", "(DD)D", 2, VType.Double);
            break;
          case IrIntrinsicOp.FloatBits:
            this.EmitExpr(args[0]);
            if (args[0].Type == IrType.F32)
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", "floatToRawIntBits", "(F)I", 1, VType.Int);
            else
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "doubleToRawLongBits", "(D)J", 1, VType.Long);
            break;
          case IrIntrinsicOp.BitsFloat:
            this.EmitExpr(args[0]);
            if (args[0].Type == IrType.I32)
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", "intBitsToFloat", "(I)F", 1, VType.Float);
            else
              this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "longBitsToDouble", "(J)D", 1, VType.Double);
            break;
          case IrIntrinsicOp.IsNaN:
            this.EmitFloatPredicate(args[0], "isNaN");
            break;
          case IrIntrinsicOp.IsInfinity:
            this.EmitFloatPredicate(args[0], "isInfinite");
            break;
          case IrIntrinsicOp.IsFinite:
            this.EmitFloatPredicate(args[0], "isFinite");
            break;
          case IrIntrinsicOp.IsNegative:
            this.EmitIsNegative(args[0]);
            break;
          case IrIntrinsicOp.IsNormal:
            this.EmitIsNormal(args[0]);
            break;
          case IrIntrinsicOp.IsSubnormal:
            this.EmitIsSubnormal(args[0]);
            break;
          case IrIntrinsicOp.RotateLeft:
            this.EmitRotate(args[0], args[1], left: true);
            break;
          case IrIntrinsicOp.RotateRight:
            this.EmitRotate(args[0], args[1], left: false);
            break;
          case IrIntrinsicOp.LeadingZeroCount:
            this.EmitLeadingZeroCount(args[0]);
            break;
          case IrIntrinsicOp.TrailingZeroCount:
            this.EmitTrailingZeroCount(args[0]);
            break;
          case IrIntrinsicOp.PopCount:
            this.EmitPopCount(args[0]);
            break;
          case IrIntrinsicOp.ByteSwap:
            this.EmitByteSwap(args[0]);
            break;
          case IrIntrinsicOp.HighWord:
            this.EmitExpr(args[0]);
            this.Code.EmitConstInt(32);
            this.Code.EmitOp(JvmOpcode.Lushr, 2, VType.Long);
            this.Code.EmitOp(JvmOpcode.L2i, 1, VType.Int);
            break;
          case IrIntrinsicOp.JoinWords:
            this.EmitExpr(args[0]);
            this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
            this.Code.EmitConstInt(32);
            this.Code.EmitOp(JvmOpcode.Lshl, 2, VType.Long);
            this.EmitExpr(args[1]);
            this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
            this.Code.EmitConstLong(0xFFFFFFFFL);
            this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
            this.Code.EmitOp(JvmOpcode.Lor, 2, VType.Long);
            break;
          case IrIntrinsicOp.LoadLittle:
            this.EmitEndianLoad(args[0], args[1], intrinsic.Type, littleEndian: true);
            break;
          case IrIntrinsicOp.LoadBig:
            this.EmitEndianLoad(args[0], args[1], intrinsic.Type, littleEndian: false);
            break;
          case IrIntrinsicOp.StoreLittle:
            this.EmitEndianStore(args[0], args[1], args[2], littleEndian: true);
            break;
          case IrIntrinsicOp.StoreBig:
            this.EmitEndianStore(args[0], args[1], args[2], littleEndian: false);
            break;
          case IrIntrinsicOp.ArrayOf: {
            var elem = intrinsic.Type.Element!;
            this.Code.EmitConstInt(args.Count);
            EmitArrayAlloc(this.Code, elem, JvmTypeMapper.Descriptor(intrinsic.Type));
            for (var i = 0; i < args.Count; ++i) {
              this.Code.EmitDup();
              this.Code.EmitConstInt(i);
              this.EmitExpr(args[i]);
              this.Code.EmitOp(JvmTypeMapper.ArrayStoreOpcode(elem), 3);
            }

            break;
          }
          case IrIntrinsicOp.ArrayCopy:
            foreach (var arg in args)
              this.EmitExpr(arg);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/System", "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", 5);
            break;
          case IrIntrinsicOp.ArrayFill: {
            var elem = args[0].Type.Element!;
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            var elemDesc = JvmTypeMapper.Descriptor(elem);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/util/Arrays", "fill", $"([{elemDesc}{elemDesc})V", 2);
            break;
          }
          case IrIntrinsicOp.ArrayClear: {
            var elem = args[0].Type.Element!;
            this.EmitExpr(args[0]);
            EmitScalarConst(this.Code, elem, 0);
            var elemDesc = JvmTypeMapper.Descriptor(elem);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/util/Arrays", "fill", $"([{elemDesc}{elemDesc})V", 2);
            break;
          }
          case IrIntrinsicOp.StrLength:
            this.EmitExpr(args[0]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "length", "()I", 0, VType.Int);
            break;
          case IrIntrinsicOp.StrConcat:
            this.EmitExpr(args[0]);
            for (var i = 1; i < args.Count; ++i) {
              this.EmitExpr(args[i]);
              this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "concat", "(Ljava/lang/String;)Ljava/lang/String;", 1,
                VType.Object(this.Pool.AddClass("java/lang/String")));
            }

            break;
          case IrIntrinsicOp.StrEquals:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/util/Objects", "equals", "(Ljava/lang/Object;Ljava/lang/Object;)Z", 2, VType.Int);
            break;
          case IrIntrinsicOp.StrCodeUnitAt:
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "charAt", "(I)C", 1, VType.Int);
            break;
          case IrIntrinsicOp.StrSubstring: {
            var strType = VType.Object(this.Pool.AddClass("java/lang/String"));
            if (args.Count == 2) {
              this.EmitExpr(args[0]);
              this.EmitExpr(args[1]);
              this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "substring", "(I)Ljava/lang/String;", 1, strType);
            } else {
              var sSlot = this.Code.AllocateLocal(strType);
              var startSlot = this.Code.AllocateLocal(VType.Int);
              var lenSlot = this.Code.AllocateLocal(VType.Int);
              this.EmitExpr(args[0]);
              this.Code.EmitStore(sSlot, strType);
              this.EmitExpr(args[1]);
              this.Code.EmitStore(startSlot, VType.Int);
              this.EmitExpr(args[2]);
              this.Code.EmitStore(lenSlot, VType.Int);
              this.Code.EmitLoad(sSlot, strType);
              this.Code.EmitLoad(startSlot, VType.Int);
              this.Code.EmitLoad(startSlot, VType.Int);
              this.Code.EmitLoad(lenSlot, VType.Int);
              this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
              this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "substring", "(II)Ljava/lang/String;", 2, strType);
            }

            break;
          }
          case IrIntrinsicOp.StrCompareOrdinal: {
            this.EmitExpr(args[0]);
            this.EmitExpr(args[1]);
            this.Code.EmitInvoke(JvmOpcode.Invokevirtual, "java/lang/String", "compareTo", "(Ljava/lang/String;)I", 1, VType.Int);
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "signum", "(I)I", 1, VType.Int);
            break;
          }
          case IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma:
            this.EmitFormatNumber(args[0]);
            break;
        }
      }

      private void EmitFormatNumber(IrExpr value) {
        var strType = VType.Object(this.Pool.AddClass("java/lang/String"));
        this.EmitExpr(value);
        switch (value.Type.Integral.Kind) {
          case IrTypeKind.U32:
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "toUnsignedString", "(I)Ljava/lang/String;", 1, strType);
            break;
          case IrTypeKind.I64:
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "toString", "(J)Ljava/lang/String;", 1, strType);
            break;
          case IrTypeKind.U64:
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "toUnsignedString", "(J)Ljava/lang/String;", 1, strType);
            break;
          case IrTypeKind.F32:
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", "toString", "(F)Ljava/lang/String;", 1, strType);
            break;
          case IrTypeKind.F64:
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "toString", "(D)Ljava/lang/String;", 1, strType);
            break;
          default:
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "toString", "(I)Ljava/lang/String;", 1, strType);
            break;
        }
      }

      private void EmitAbs(IrExpr arg) {
        this.EmitExpr(arg);
        var type = arg.Type.Integral;
        if (type == IrType.F32) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "abs", "(F)F", 1, VType.Float);
        } else if (type == IrType.F64) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "abs", "(D)D", 1, VType.Double);
        } else if (!type.IsSigned) {
          // Unsigned value is already non-negative.
        } else if (type == IrType.I64) {
          var vSlot = this.Code.AllocateLocal(VType.Long);
          this.Code.EmitStore(vSlot, VType.Long);
          var okLabel = this.Code.DefineLabel();
          this.Code.EmitLoad(vSlot, VType.Long);
          this.Code.EmitConstLong(long.MinValue);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifne, okLabel);
          this.EmitThrowArithmetic("Abs overflow on signed 64-bit minimum value.");
          this.Code.MarkLabel(okLabel);
          this.Code.EmitLoad(vSlot, VType.Long);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "abs", "(J)J", 1, VType.Long);
        } else {
          var vSlot = this.Code.AllocateLocal(VType.Int);
          this.Code.EmitStore(vSlot, VType.Int);
          var okLabel = this.Code.DefineLabel();
          this.Code.EmitLoad(vSlot, VType.Int);
          this.Code.EmitConstInt((int)IrOperators.Minimum(type));
          this.Code.EmitBranch(JvmOpcode.IfIcmpne, okLabel);
          this.EmitThrowArithmetic($"Abs overflow on '{type}' minimum value.");
          this.Code.MarkLabel(okLabel);
          this.Code.EmitLoad(vSlot, VType.Int);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "abs", "(I)I", 1, VType.Int);
        }
      }

      private void EmitMinMax(IrExpr left, IrExpr right, bool isMin) {
        this.EmitExpr(left);
        this.EmitExpr(right);
        var type = left.Type.Integral;
        var method = isMin ? "min" : "max";
        if (type == IrType.F32) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", method, "(FF)F", 2, VType.Float);
        } else if (type == IrType.F64) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", method, "(DD)D", 2, VType.Double);
        } else if (type == IrType.I64) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", method, "(JJ)J", 2, VType.Long);
        } else if (type == IrType.U32 || type == IrType.U64) {
          var vtype = type == IrType.U64 ? VType.Long : VType.Int;
          var rSlot = this.Code.AllocateLocal(vtype);
          var lSlot = this.Code.AllocateLocal(vtype);
          this.Code.EmitStore(rSlot, vtype);
          this.Code.EmitStore(lSlot, vtype);
          var takeLeft = this.Code.DefineLabel();
          var end = this.Code.DefineLabel();
          this.Code.EmitLoad(lSlot, vtype);
          this.Code.EmitLoad(rSlot, vtype);
          if (type == IrType.U64)
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "compareUnsigned", "(JJ)I", 2, VType.Int);
          else
            this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "compareUnsigned", "(II)I", 2, VType.Int);
          this.Code.EmitBranch(isMin ? JvmOpcode.Ifle : JvmOpcode.Ifge, takeLeft);
          this.Code.EmitLoad(rSlot, vtype);
          this.Code.EmitGoto(end);
          this.Code.MarkLabel(takeLeft);
          this.Code.EmitLoad(lSlot, vtype);
          this.Code.MarkLabel(end);
        } else {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", method, "(II)I", 2, VType.Int);
        }
      }

      private void EmitFloatSign(IrExpr arg) {
        this.EmitExpr(arg);
        if (arg.Type == IrType.F32)
          this.Code.EmitOp(JvmOpcode.F2d, 1, VType.Double);

        var xSlot = this.Code.AllocateLocal(VType.Double);
        this.Code.EmitStore(xSlot, VType.Double);
        var okLabel = this.Code.DefineLabel();
        var negLabel = this.Code.DefineLabel();
        var posLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();

        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "isNaN", "(D)Z", 1, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Ifeq, okLabel);
        this.EmitThrowArithmetic("Sign of NaN is undefined.");
        this.Code.MarkLabel(okLabel);

        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitConstDoubleBits(0L);
        this.Code.EmitOp(JvmOpcode.Dcmpg, 2, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Iflt, negLabel);

        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitConstDoubleBits(0L);
        this.Code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Ifgt, posLabel);

        this.Code.EmitConstInt(0);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(negLabel);
        this.Code.EmitConstInt(-1);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(posLabel);
        this.Code.EmitConstInt(1);
        this.Code.MarkLabel(endLabel);
      }

      private void EmitMathUnaryFloat(IrExpr arg, string method) {
        var isF32 = arg.Type == IrType.F32;
        this.EmitExpr(arg);
        if (isF32)
          this.Code.EmitOp(JvmOpcode.F2d, 1, VType.Double);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", method, "(D)D", 1, VType.Double);
        if (isF32)
          this.Code.EmitOp(JvmOpcode.D2f, 1, VType.Float);
      }

      private void EmitTruncate(IrExpr arg) {
        var isF32 = arg.Type == IrType.F32;
        this.EmitExpr(arg);
        if (isF32)
          this.Code.EmitOp(JvmOpcode.F2d, 1, VType.Double);

        var xSlot = this.Code.AllocateLocal(VType.Double);
        this.Code.EmitStore(xSlot, VType.Double);
        var posLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitConstDoubleBits(0L);
        this.Code.EmitOp(JvmOpcode.Dcmpg, 2, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Ifge, posLabel);
        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "ceil", "(D)D", 1, VType.Double);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(posLabel);
        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "floor", "(D)D", 1, VType.Double);
        this.Code.MarkLabel(endLabel);
        if (isF32)
          this.Code.EmitOp(JvmOpcode.D2f, 1, VType.Float);
      }

      private void EmitRoundAway(IrExpr arg) {
        var isF32 = arg.Type == IrType.F32;
        this.EmitExpr(arg);
        if (isF32)
          this.Code.EmitOp(JvmOpcode.F2d, 1, VType.Double);

        var xSlot = this.Code.AllocateLocal(VType.Double);
        var rSlot = this.Code.AllocateLocal(VType.Double);
        this.Code.EmitStore(xSlot, VType.Double);
        var nonNegLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        var halfBits = BitConverter.DoubleToInt64Bits(0.5);
        var oneBits = BitConverter.DoubleToInt64Bits(1.0);

        // If x < 0.0: c = ceil(x); return (c - x >= 0.5) ? c - 1.0 : c
        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitConstDoubleBits(0L);
        this.Code.EmitOp(JvmOpcode.Dcmpg, 2, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Ifge, nonNegLabel);

        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "ceil", "(D)D", 1, VType.Double);
        this.Code.EmitStore(rSlot, VType.Double);
        var negNoBump = this.Code.DefineLabel();
        this.Code.EmitLoad(rSlot, VType.Double);
        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitOp(JvmOpcode.Dsub, 2, VType.Double);
        this.Code.EmitConstDoubleBits(halfBits);
        this.Code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Iflt, negNoBump);
        this.Code.EmitLoad(rSlot, VType.Double);
        this.Code.EmitConstDoubleBits(oneBits);
        this.Code.EmitOp(JvmOpcode.Dsub, 2, VType.Double);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(negNoBump);
        this.Code.EmitLoad(rSlot, VType.Double);
        this.Code.EmitGoto(endLabel);

        // Else (x >= 0.0 or NaN): f = floor(x); return (x - f >= 0.5) ? f + 1.0 : f
        this.Code.MarkLabel(nonNegLabel);
        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Math", "floor", "(D)D", 1, VType.Double);
        this.Code.EmitStore(rSlot, VType.Double);
        var posNoBump = this.Code.DefineLabel();
        this.Code.EmitLoad(xSlot, VType.Double);
        this.Code.EmitLoad(rSlot, VType.Double);
        this.Code.EmitOp(JvmOpcode.Dsub, 2, VType.Double);
        this.Code.EmitConstDoubleBits(halfBits);
        this.Code.EmitOp(JvmOpcode.Dcmpl, 2, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Iflt, posNoBump);
        this.Code.EmitLoad(rSlot, VType.Double);
        this.Code.EmitConstDoubleBits(oneBits);
        this.Code.EmitOp(JvmOpcode.Dadd, 2, VType.Double);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(posNoBump);
        this.Code.EmitLoad(rSlot, VType.Double);

        this.Code.MarkLabel(endLabel);
        if (isF32)
          this.Code.EmitOp(JvmOpcode.D2f, 1, VType.Float);
      }

      private void EmitFloatPredicate(IrExpr arg, string method) {
        this.EmitExpr(arg);
        if (arg.Type == IrType.F32)
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", method, "(F)Z", 1, VType.Int);
        else
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", method, "(D)Z", 1, VType.Int);
      }

      private void EmitIsNegative(IrExpr arg) {
        this.EmitExpr(arg);
        if (arg.Type == IrType.F32) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", "floatToRawIntBits", "(F)I", 1, VType.Int);
          this.Code.EmitConstInt(31);
          this.Code.EmitOp(JvmOpcode.Iushr, 2, VType.Int);
        } else {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "doubleToRawLongBits", "(D)J", 1, VType.Long);
          this.Code.EmitConstInt(63);
          this.Code.EmitOp(JvmOpcode.Lushr, 2, VType.Long);
          this.Code.EmitOp(JvmOpcode.L2i, 1, VType.Int);
        }
      }

      private void EmitIsNormal(IrExpr arg) {
        var falseLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        var expSlot = this.Code.AllocateLocal(VType.Int);
        this.EmitExpr(arg);
        int maxExp;
        if (arg.Type == IrType.F32) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", "floatToRawIntBits", "(F)I", 1, VType.Int);
          this.Code.EmitConstInt(23);
          this.Code.EmitOp(JvmOpcode.Iushr, 2, VType.Int);
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          maxExp = 0xFF;
        } else {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "doubleToRawLongBits", "(D)J", 1, VType.Long);
          this.Code.EmitConstInt(52);
          this.Code.EmitOp(JvmOpcode.Lushr, 2, VType.Long);
          this.Code.EmitOp(JvmOpcode.L2i, 1, VType.Int);
          this.Code.EmitConstInt(0x7FF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          maxExp = 0x7FF;
        }

        this.Code.EmitStore(expSlot, VType.Int);
        this.Code.EmitLoad(expSlot, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Ifeq, falseLabel);
        this.Code.EmitLoad(expSlot, VType.Int);
        this.Code.EmitConstInt(maxExp);
        this.Code.EmitBranch(JvmOpcode.IfIcmpeq, falseLabel);
        this.Code.EmitConstInt(1);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(falseLabel);
        this.Code.EmitConstInt(0);
        this.Code.MarkLabel(endLabel);
      }

      private void EmitIsSubnormal(IrExpr arg) {
        var falseLabel = this.Code.DefineLabel();
        var endLabel = this.Code.DefineLabel();
        this.EmitExpr(arg);
        if (arg.Type == IrType.F32) {
          var bitsSlot = this.Code.AllocateLocal(VType.Int);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", "floatToRawIntBits", "(F)I", 1, VType.Int);
          this.Code.EmitConstInt(0x7FFFFFFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          this.Code.EmitStore(bitsSlot, VType.Int);
          this.Code.EmitLoad(bitsSlot, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifeq, falseLabel);
          this.Code.EmitLoad(bitsSlot, VType.Int);
          this.Code.EmitConstInt(0x00800000);
          this.Code.EmitBranch(JvmOpcode.IfIcmpge, falseLabel);
        } else {
          var bitsSlot = this.Code.AllocateLocal(VType.Long);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "doubleToRawLongBits", "(D)J", 1, VType.Long);
          this.Code.EmitConstLong(0x7FFFFFFFFFFFFFFFL);
          this.Code.EmitOp(JvmOpcode.Land, 2, VType.Long);
          this.Code.EmitStore(bitsSlot, VType.Long);
          this.Code.EmitLoad(bitsSlot, VType.Long);
          this.Code.EmitConstLong(0L);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifeq, falseLabel);
          this.Code.EmitLoad(bitsSlot, VType.Long);
          this.Code.EmitConstLong(0x0010000000000000L);
          this.Code.EmitOp(JvmOpcode.Lcmp, 2, VType.Int);
          this.Code.EmitBranch(JvmOpcode.Ifge, falseLabel);
        }

        this.Code.EmitConstInt(1);
        this.Code.EmitGoto(endLabel);
        this.Code.MarkLabel(falseLabel);
        this.Code.EmitConstInt(0);
        this.Code.MarkLabel(endLabel);
      }

      private void EmitRotate(IrExpr value, IrExpr count, bool left) {
        var type = value.Type.Integral;
        var method = left ? "rotateLeft" : "rotateRight";
        if (type.Bits == 64) {
          this.EmitExpr(value);
          this.EmitExpr(count);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", method, "(JI)J", 2, VType.Long);
        } else if (type.Bits == 32) {
          this.EmitExpr(value);
          this.EmitExpr(count);
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", method, "(II)I", 2, VType.Int);
        } else {
          var bits = type.Bits;
          var mask = (1 << bits) - 1;
          var vSlot = this.Code.AllocateLocal(VType.Int);
          var cSlot = this.Code.AllocateLocal(VType.Int);
          this.EmitExpr(value);
          this.Code.EmitConstInt(mask);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          this.Code.EmitStore(vSlot, VType.Int);
          this.EmitExpr(count);
          this.Code.EmitConstInt(bits - 1);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          this.Code.EmitStore(cSlot, VType.Int);

          // Rotate narrow width: ((v << c) | (v >>> (bits - c))) & mask  (or reversed for right)
          this.Code.EmitLoad(vSlot, VType.Int);
          this.Code.EmitLoad(cSlot, VType.Int);
          this.Code.EmitOp(left ? JvmOpcode.Ishl : JvmOpcode.Iushr, 2, VType.Int);
          this.Code.EmitLoad(vSlot, VType.Int);
          this.Code.EmitConstInt(bits);
          this.Code.EmitLoad(cSlot, VType.Int);
          this.Code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
          this.Code.EmitConstInt(bits - 1);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          this.Code.EmitOp(left ? JvmOpcode.Iushr : JvmOpcode.Ishl, 2, VType.Int);
          this.Code.EmitOp(JvmOpcode.Ior, 2, VType.Int);
          this.EmitIntWrapConvert(IrType.I32, type);
        }
      }

      private void EmitLeadingZeroCount(IrExpr arg) {
        var type = arg.Type.Integral;
        this.EmitExpr(arg);
        if (type.Bits == 64) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "numberOfLeadingZeros", "(J)I", 1, VType.Int);
        } else {
          if (type.Bits < 32) {
            this.Code.EmitConstInt((1 << type.Bits) - 1);
            this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          }

          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "numberOfLeadingZeros", "(I)I", 1, VType.Int);
          if (type.Bits < 32) {
            this.Code.EmitConstInt(32 - type.Bits);
            this.Code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
          }
        }
      }

      private void EmitTrailingZeroCount(IrExpr arg) {
        var type = arg.Type.Integral;
        this.EmitExpr(arg);
        if (type.Bits == 64) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "numberOfTrailingZeros", "(J)I", 1, VType.Int);
        } else {
          if (type.Bits < 32) {
            this.Code.EmitConstInt((1 << type.Bits) - 1);
            this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
            this.Code.EmitConstInt(1 << type.Bits);
            this.Code.EmitOp(JvmOpcode.Ior, 2, VType.Int);
          }

          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "numberOfTrailingZeros", "(I)I", 1, VType.Int);
        }
      }

      private void EmitPopCount(IrExpr arg) {
        var type = arg.Type.Integral;
        this.EmitExpr(arg);
        if (type.Bits == 64) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "bitCount", "(J)I", 1, VType.Int);
        } else {
          if (type.Bits < 32) {
            this.Code.EmitConstInt((1 << type.Bits) - 1);
            this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          }

          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "bitCount", "(I)I", 1, VType.Int);
        }
      }

      private void EmitByteSwap(IrExpr arg) {
        var type = arg.Type.Integral;
        this.EmitExpr(arg);
        if (type.Bits == 64) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Long", "reverseBytes", "(J)J", 1, VType.Long);
        } else if (type.Bits == 32) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Integer", "reverseBytes", "(I)I", 1, VType.Int);
        } else if (type.Bits == 16) {
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Short", "reverseBytes", "(S)S", 1, VType.Int);
          if (!type.IsSigned)
            this.Code.EmitOp(JvmOpcode.I2c, 1, VType.Int);
        }
      }

      private void EmitEndianBoundsCheck(ushort arrSlot, VType arrType, ushort idxSlot, int byteCount) {
        var trapLabel = this.Code.DefineLabel();
        var okLabel = this.Code.DefineLabel();
        this.Code.EmitLoad(idxSlot, VType.Int);
        this.Code.EmitBranch(JvmOpcode.Iflt, trapLabel);
        this.Code.EmitLoad(idxSlot, VType.Int);
        this.Code.EmitLoad(arrSlot, arrType);
        this.Code.EmitOp(JvmOpcode.Arraylength, 1, VType.Int);
        this.Code.EmitConstInt(byteCount);
        this.Code.EmitOp(JvmOpcode.Isub, 2, VType.Int);
        this.Code.EmitBranch(JvmOpcode.IfIcmpgt, trapLabel);
        this.Code.EmitGoto(okLabel);
        this.Code.MarkLabel(trapLabel);
        this.EmitThrowIndexOutOfBounds("Byte buffer access out of range.");
        this.Code.MarkLabel(okLabel);
      }

      private void EmitEndianLoad(IrExpr arrExpr, IrExpr idxExpr, IrType resultType, bool littleEndian) {
        var arrType = VType.Object(this.Pool.AddClass("[B"));
        var arrSlot = this.Code.AllocateLocal(arrType);
        var idxSlot = this.Code.AllocateLocal(VType.Int);
        var byteCount = resultType.Size;

        this.EmitExpr(arrExpr);
        this.Code.EmitStore(arrSlot, arrType);
        this.EmitExpr(idxExpr);
        this.Code.EmitStore(idxSlot, VType.Int);
        this.EmitEndianBoundsCheck(arrSlot, arrType, idxSlot, byteCount);

        var wide = byteCount == 8;
        for (var i = 0; i < byteCount; ++i) {
          var shift = (littleEndian ? i : byteCount - 1 - i) * 8;
          this.Code.EmitLoad(arrSlot, arrType);
          this.Code.EmitLoad(idxSlot, VType.Int);
          if (i > 0) {
            this.Code.EmitConstInt(i);
            this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
          }

          this.Code.EmitOp(JvmOpcode.Baload, 2, VType.Int);
          this.Code.EmitConstInt(0xFF);
          this.Code.EmitOp(JvmOpcode.Iand, 2, VType.Int);
          if (wide) {
            this.Code.EmitOp(JvmOpcode.I2l, 1, VType.Long);
            if (shift > 0) {
              this.Code.EmitConstInt(shift);
              this.Code.EmitOp(JvmOpcode.Lshl, 2, VType.Long);
            }

            if (i > 0)
              this.Code.EmitOp(JvmOpcode.Lor, 2, VType.Long);
          } else {
            if (shift > 0) {
              this.Code.EmitConstInt(shift);
              this.Code.EmitOp(JvmOpcode.Ishl, 2, VType.Int);
            }

            if (i > 0)
              this.Code.EmitOp(JvmOpcode.Ior, 2, VType.Int);
          }
        }

        if (resultType == IrType.I16)
          this.Code.EmitOp(JvmOpcode.I2s, 1, VType.Int);
        else if (resultType == IrType.F32)
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", "intBitsToFloat", "(I)F", 1, VType.Float);
        else if (resultType == IrType.F64)
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "longBitsToDouble", "(J)D", 1, VType.Double);
      }

      private void EmitEndianStore(IrExpr arrExpr, IrExpr idxExpr, IrExpr valExpr, bool littleEndian) {
        var arrType = VType.Object(this.Pool.AddClass("[B"));
        var arrSlot = this.Code.AllocateLocal(arrType);
        var idxSlot = this.Code.AllocateLocal(VType.Int);
        var valType = valExpr.Type.Integral;
        var byteCount = valType.Size;
        var wide = byteCount == 8;
        var valSlot = this.Code.AllocateLocal(wide ? VType.Long : VType.Int);

        this.EmitExpr(arrExpr);
        this.Code.EmitStore(arrSlot, arrType);
        this.EmitExpr(idxExpr);
        this.Code.EmitStore(idxSlot, VType.Int);
        this.EmitExpr(valExpr);
        if (valType == IrType.F32)
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Float", "floatToRawIntBits", "(F)I", 1, VType.Int);
        else if (valType == IrType.F64)
          this.Code.EmitInvoke(JvmOpcode.Invokestatic, "java/lang/Double", "doubleToRawLongBits", "(D)J", 1, VType.Long);
        this.Code.EmitStore(valSlot, wide ? VType.Long : VType.Int);

        this.EmitEndianBoundsCheck(arrSlot, arrType, idxSlot, byteCount);

        for (var i = 0; i < byteCount; ++i) {
          var shift = (littleEndian ? i : byteCount - 1 - i) * 8;
          this.Code.EmitLoad(arrSlot, arrType);
          this.Code.EmitLoad(idxSlot, VType.Int);
          if (i > 0) {
            this.Code.EmitConstInt(i);
            this.Code.EmitOp(JvmOpcode.Iadd, 2, VType.Int);
          }

          if (wide) {
            this.Code.EmitLoad(valSlot, VType.Long);
            if (shift > 0) {
              this.Code.EmitConstInt(shift);
              this.Code.EmitOp(JvmOpcode.Lushr, 2, VType.Long);
            }

            this.Code.EmitOp(JvmOpcode.L2i, 1, VType.Int);
          } else {
            this.Code.EmitLoad(valSlot, VType.Int);
            if (shift > 0) {
              this.Code.EmitConstInt(shift);
              this.Code.EmitOp(JvmOpcode.Iushr, 2, VType.Int);
            }
          }

          this.Code.EmitOp(JvmOpcode.Bastore, 3);
        }
      }

    }

  }

}
