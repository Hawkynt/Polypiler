using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

internal sealed partial class MsilMethodCompiler {

  private bool TryEmitExtendedIntrinsic(IrIntrinsic intrinsic) {
    var args = intrinsic.Arguments;
    switch (intrinsic.Op) {
      case IrIntrinsicOp.EnumHasFlag: {
        var flagType = args[1].Type;
        var flagSlot = this.AllocTemp(flagType);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.StoreLocal(flagSlot);
        this._il.LoadLocal(flagSlot);
        this._il.OpCode(ILOpCode.And);
        this._il.LoadLocal(flagSlot);
        this._il.OpCode(ILOpCode.Ceq);
        return true;
      }

      case IrIntrinsicOp.EnumIsDefined: {
        this.EmitExpr(args[0]);
        this._il.Call(this._ctx.EnumIsDefinedHandles[EnumOperations.Of(intrinsic)]);
        return true;
      }

      case IrIntrinsicOp.EnumToString: {
        this.EmitExpr(args[0]);
        this._il.Call(this._ctx.EnumToStringHandles[EnumOperations.Of(intrinsic)]);
        return true;
      }

      case IrIntrinsicOp.EnumParse: {
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.Call(this._ctx.EnumParseHandles[EnumOperations.Of(intrinsic)]);
        return true;
      }

      case IrIntrinsicOp.EnumNames: {
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Pop);
        var ordered = EnumOperations.Of(intrinsic).Ordered;
        this._il.LoadConstantI4(ordered.Count);
        this._il.OpCode(ILOpCode.Newarr);
        this._il.Token(this._ctx.GetTypeToken(IrType.String));
        for (var i = 0; i < ordered.Count; i++) {
          this._il.OpCode(ILOpCode.Dup);
          this._il.LoadConstantI4(i);
          this.EmitStringLiteral(ordered[i].Name);
          this._il.OpCode(ILOpCode.Stelem_ref);
        }
        return true;
      }

      case IrIntrinsicOp.EnumValues: {
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Pop);
        var enumType = EnumOperations.Of(intrinsic);
        var ordered = enumType.Ordered;
        this._il.LoadConstantI4(ordered.Count);
        this._il.OpCode(ILOpCode.Newarr);
        this._il.Token(this._ctx.GetTypeToken(enumType.Type));
        for (var i = 0; i < ordered.Count; i++) {
          this._il.OpCode(ILOpCode.Dup);
          this._il.LoadConstantI4(i);
          this.EmitConstValue(enumType.Underlying, ordered[i].Value);
          this.EmitStoreElement(enumType.Type);
        }
        return true;
      }

      case IrIntrinsicOp.FormatBoolDotNet or IrIntrinsicOp.FormatBoolEcma: {
        var falseLabel = this._il.DefineLabel();
        var endLabel = this._il.DefineLabel();
        var dotNet = intrinsic.Op == IrIntrinsicOp.FormatBoolDotNet;
        this.EmitExpr(args[0]);
        this._il.Branch(ILOpCode.Brfalse, falseLabel);
        this.EmitStringLiteral(dotNet ? "True" : "true");
        this._il.Branch(ILOpCode.Br, endLabel);
        this._il.MarkLabel(falseLabel);
        this.EmitStringLiteral(dotNet ? "False" : "false");
        this._il.MarkLabel(endLabel);
        return true;
      }

      case IrIntrinsicOp.ByteSwap: {
        var t = args[0].Type.Integral;
        this.EmitExpr(args[0]);
        if (t.Bits >= 16) {
          this._il.Call(this._ctx.GetMemberRef(
            $"stat:System.Buffers.Binary.BinaryPrimitives::ReverseEndianness({t})",
            this._ctx.GetAssemblyType("System.Memory", "System.Buffers.Binary", "BinaryPrimitives"),
            "ReverseEndianness",
            sig => sig.Parameters(1, r => this._ctx.EncodeType(r.Type(), t), p => this._ctx.EncodeType(p.AddParameter().Type(), t))));
        }
        return true;
      }

      case IrIntrinsicOp.HighWord: {
        this.EmitExpr(args[0]);
        this._il.LoadConstantI4(32);
        this._il.OpCode(ILOpCode.Shr_un);
        this._il.OpCode(ILOpCode.Conv_u4);
        return true;
      }

      case IrIntrinsicOp.JoinWords: {
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Conv_u8);
        this._il.LoadConstantI4(32);
        this._il.OpCode(ILOpCode.Shl);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Conv_u8);
        this._il.OpCode(ILOpCode.Or);
        return true;
      }

      case IrIntrinsicOp.BigMul: {
        var conv = args[0].Type.IsSigned ? ILOpCode.Conv_i8 : ILOpCode.Conv_u8;
        this.EmitExpr(args[0]);
        this._il.OpCode(conv);
        this.EmitExpr(args[1]);
        this._il.OpCode(conv);
        this._il.OpCode(ILOpCode.Mul);
        return true;
      }

      case IrIntrinsicOp.Clamp: {
        var t = args[0].Type.Integral;
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this.EmitExpr(args[2]);
        this._il.Call(this._ctx.GetStaticMethodRef("System", "Math", "Clamp", t, t, t, t));
        return true;
      }

      case IrIntrinsicOp.BitReverse: {
        var t = args[0].Type.Integral;
        this.EmitExpr(args[0]);
        if (t.IsLong) {
          this._il.Call(this._ctx.BitRev64Handle);
        } else {
          if (t == IrType.I8)
            this._il.OpCode(ILOpCode.Conv_u1);
          else if (t == IrType.I16)
            this._il.OpCode(ILOpCode.Conv_u2);
          this._il.Call(this._ctx.BitRev32Handle);
          if (t.Bits < 32) {
            this._il.LoadConstantI4(32 - t.Bits);
            this._il.OpCode(ILOpCode.Shr_un);
            this.EmitNarrowTruncate(t);
          }
        }
        return true;
      }

      case IrIntrinsicOp.IsPow2: {
        var t = args[0].Type.Integral;
        var bitArg = t.IsLong
          ? (t.IsSigned ? IrType.I64 : IrType.U64)
          : (t.IsSigned ? IrType.I32 : IrType.U32);
        this.EmitExpr(args[0]);
        this._il.Call(this._ctx.GetStaticMethodRef("System.Numerics", "BitOperations", "IsPow2", IrType.Bool, bitArg));
        return true;
      }

      case IrIntrinsicOp.RoundUpPow2: {
        var t = args[0].Type.Integral;
        var bitArg = t.IsLong ? IrType.U64 : IrType.U32;
        this.EmitExpr(args[0]);
        if (t == IrType.I8)
          this._il.OpCode(ILOpCode.Conv_u1);
        else if (t == IrType.I16)
          this._il.OpCode(ILOpCode.Conv_u2);
        this._il.Call(this._ctx.GetStaticMethodRef("System.Numerics", "BitOperations", "RoundUpToPowerOf2", bitArg, bitArg));
        if (t.Bits < 32)
          this.EmitNarrowTruncate(t);
        return true;
      }

      case IrIntrinsicOp.Log2: {
        var t = args[0].Type.Integral;
        this.EmitExpr(args[0]);
        if (t.IsFloat) {
          var mathType = t == IrType.F32 ? "MathF" : "Math";
          this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, "Log2", t, t));
        } else {
          if (t == IrType.I8)
            this._il.OpCode(ILOpCode.Conv_u1);
          else if (t == IrType.I16)
            this._il.OpCode(ILOpCode.Conv_u2);
          var bitArg = t.IsLong ? IrType.U64 : IrType.U32;
          this._il.Call(this._ctx.GetStaticMethodRef("System.Numerics", "BitOperations", "Log2", IrType.I32, bitArg));
        }
        return true;
      }

      case IrIntrinsicOp.Sin or IrIntrinsicOp.Cos or IrIntrinsicOp.Tan
        or IrIntrinsicOp.Asin or IrIntrinsicOp.Acos or IrIntrinsicOp.Atan
        or IrIntrinsicOp.Sinh or IrIntrinsicOp.Cosh or IrIntrinsicOp.Tanh
        or IrIntrinsicOp.Exp or IrIntrinsicOp.Log or IrIntrinsicOp.Log10 or IrIntrinsicOp.Cbrt: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, intrinsic.Op.ToString(), t, t));
        return true;
      }

      case IrIntrinsicOp.Atan2 or IrIntrinsicOp.Pow: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, intrinsic.Op.ToString(), t, t, t));
        return true;
      }

      case IrIntrinsicOp.FusedMultiplyAdd: {
        var t = args[0].Type;
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this.EmitExpr(args[2]);
        var mathType = t == IrType.F32 ? "MathF" : "Math";
        this._il.Call(this._ctx.GetStaticMethodRef("System", mathType, "FusedMultiplyAdd", t, t, t, t));
        return true;
      }

      case IrIntrinsicOp.StrFromCodeUnit: {
        this.EmitExpr(args[0]);
        this.EmitCharToString();
        return true;
      }

      case IrIntrinsicOp.StrLastIndexOf: {
        var textSlot = this.AllocTemp(IrType.String);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(textSlot);
        this._il.LoadLocal(textSlot);
        this.EmitExpr(args[1]);
        if (args[1].Type == IrType.U16)
          this.EmitCharToString();
        if (args.Count == 3) {
          this.EmitExpr(args[2]);
        } else {
          this._il.LoadLocal(textSlot);
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", "get_Length", IrType.I32));
        }
        this._il.Call(this._ctx.StrLastIndexOfHandle);
        return true;
      }

      case IrIntrinsicOp.StrReplaceAll: {
        if (args[1].Type == IrType.U16) {
          this.EmitExpr(args[0]);
          this.EmitExpr(args[1]);
          this.EmitExpr(args[2]);
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetMemberRef(
            "inst:System.String::Replace(char,char)",
            this._ctx.GetBclType("System", "String"),
            "Replace",
            sig => sig.Parameters(2, r => r.Type().String(), p => {
              p.AddParameter().Type().Char();
              p.AddParameter().Type().Char();
            })));
          return true;
        }

        var textSlot = this.AllocTemp(IrType.String);
        var needleSlot = this.AllocTemp(IrType.String);
        var replSlot = this.AllocTemp(IrType.String);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(textSlot);
        this.EmitExpr(args[1]);
        this._il.StoreLocal(needleSlot);
        this.EmitExpr(args[2]);
        this._il.StoreLocal(replSlot);

        var okLabel = this._il.DefineLabel();
        this._il.LoadLocal(needleSlot);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", "get_Length", IrType.I32));
        this._il.Branch(ILOpCode.Brtrue, okLabel);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.InvalidOperationException, 0));
        this._il.OpCode(ILOpCode.Throw);

        this._il.MarkLabel(okLabel);
        this._il.LoadLocal(textSlot);
        this._il.LoadLocal(needleSlot);
        this._il.LoadLocal(replSlot);
        this._il.LoadConstantI4(4); // StringComparison.Ordinal
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef(
          "inst:System.String::Replace(str,str,StringComparison)",
          this._ctx.GetBclType("System", "String"),
          "Replace",
          sig => sig.Parameters(3, r => r.Type().String(), p => {
            p.AddParameter().Type().String();
            p.AddParameter().Type().String();
            p.AddParameter().Type().Type(this._ctx.GetBclType("System", "StringComparison"), isValueType: true);
          })));
        return true;
      }

      case IrIntrinsicOp.StrReplaceFirst: {
        var textSlot = this.AllocTemp(IrType.String);
        var needleSlot = this.AllocTemp(IrType.String);
        var replSlot = this.AllocTemp(IrType.String);
        var atSlot = this.AllocTemp(IrType.I32);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(textSlot);
        this.EmitExpr(args[1]);
        this._il.StoreLocal(needleSlot);
        this.EmitExpr(args[2]);
        this._il.StoreLocal(replSlot);

        this._il.LoadLocal(textSlot);
        this._il.LoadLocal(needleSlot);
        this._il.LoadConstantI4(4); // StringComparison.Ordinal
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this.GetStringOrdinalInstanceRef("IndexOf", IrType.I32, withStartIndex: false));
        this._il.StoreLocal(atSlot);

        var notFoundLabel = this._il.DefineLabel();
        var endLabel = this._il.DefineLabel();
        this._il.LoadLocal(atSlot);
        this._il.LoadConstantI4(0);
        this._il.Branch(ILOpCode.Blt, notFoundLabel);

        this._il.LoadLocal(textSlot);
        this._il.LoadConstantI4(0);
        this._il.LoadLocal(atSlot);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", "Substring", IrType.String, IrType.I32, IrType.I32));
        this._il.LoadLocal(replSlot);
        this._il.LoadLocal(textSlot);
        this._il.LoadLocal(atSlot);
        this._il.LoadLocal(needleSlot);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", "get_Length", IrType.I32));
        this._il.OpCode(ILOpCode.Add);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", "Substring", IrType.String, IrType.I32));
        this._il.Call(this._ctx.GetStaticMethodRef("System", "String", "Concat", IrType.String, IrType.String, IrType.String, IrType.String));
        this._il.Branch(ILOpCode.Br, endLabel);

        this._il.MarkLabel(notFoundLabel);
        this._il.LoadLocal(textSlot);
        this._il.MarkLabel(endLabel);
        return true;
      }

      case IrIntrinsicOp.StrTrim or IrIntrinsicOp.StrTrimStart or IrIntrinsicOp.StrTrimEnd: {
        var set = StringOperations.SetOf(intrinsic)!.Value;
        this.EmitExpr(args[0]);
        if (set == WhitespaceSet.DotNet) {
          var method = intrinsic.Op switch {
            IrIntrinsicOp.StrTrim => "Trim",
            IrIntrinsicOp.StrTrimStart => "TrimStart",
            _ => "TrimEnd"
          };
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetInstanceMethodRef("System", "String", method, IrType.String));
        } else {
          this._il.LoadConstantI4(intrinsic.Op != IrIntrinsicOp.StrTrimEnd ? 1 : 0);
          this._il.LoadConstantI4(intrinsic.Op != IrIntrinsicOp.StrTrimStart ? 1 : 0);
          this._il.Call(this._ctx.StrTrimEcmaHandle);
        }
        return true;
      }

      case IrIntrinsicOp.StrSplit: {
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        if (args[1].Type == IrType.U16)
          this.EmitCharToString();
        this.EmitExpr(args[2]);
        this._il.Call(this._ctx.StrSplitHandle);
        return true;
      }

      default:
        return false;
    }
  }

  private void EmitCharToString() {
    this._il.LoadConstantI4(1);
    this._il.OpCode(ILOpCode.Newobj);
    this._il.Token(this._ctx.GetMemberRef(
      "inst:System.String..ctor(char,int)",
      this._ctx.GetBclType("System", "String"),
      ".ctor",
      sig => sig.Parameters(2, r => r.Void(), p => {
        p.AddParameter().Type().Char();
        p.AddParameter().Type().Int32();
      })));
  }

  private void EmitNarrowTruncate(IrType type) {
    this._il.OpCode(type.Kind switch {
      IrTypeKind.I8 => ILOpCode.Conv_i1,
      IrTypeKind.U8 => ILOpCode.Conv_u1,
      IrTypeKind.I16 => ILOpCode.Conv_i2,
      _ => ILOpCode.Conv_u2
    });
  }

  private static string BclIntegerTypeName(IrType type) => type.Kind switch {
    IrTypeKind.I8 => "SByte",
    IrTypeKind.U8 => "Byte",
    IrTypeKind.I16 => "Int16",
    IrTypeKind.U16 => "UInt16",
    IrTypeKind.I32 => "Int32",
    IrTypeKind.U32 => "UInt32",
    IrTypeKind.I64 => "Int64",
    _ => "UInt64"
  };

  /// <summary>Compiles a bit-reversal helper (<c>$BitRev32</c> or <c>$BitRev64</c>).</summary>
  public static int CompileBitReverseHelper(bool is64, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var valType = is64 ? IrType.U64 : IrType.U32;
    var compiler = new MsilMethodCompiler(ctx, valType, [], []);
    var xSlot = compiler.AllocTemp(valType);
    compiler._il.LoadArgument(0);
    compiler._il.StoreLocal(xSlot);

    if (is64) {
      static void Step64(MsilMethodCompiler c, int slot, ulong mask, int shift) {
        c._il.LoadLocal(slot);
        c._il.LoadConstantI8(unchecked((long)mask));
        c._il.OpCode(ILOpCode.And);
        c._il.LoadConstantI4(shift);
        c._il.OpCode(ILOpCode.Shl);
        c._il.LoadLocal(slot);
        c._il.LoadConstantI4(shift);
        c._il.OpCode(ILOpCode.Shr_un);
        c._il.LoadConstantI8(unchecked((long)mask));
        c._il.OpCode(ILOpCode.And);
        c._il.OpCode(ILOpCode.Or);
        c._il.StoreLocal(slot);
      }

      Step64(compiler, xSlot, 0x5555555555555555UL, 1);
      Step64(compiler, xSlot, 0x3333333333333333UL, 2);
      Step64(compiler, xSlot, 0x0F0F0F0F0F0F0F0FUL, 4);
      Step64(compiler, xSlot, 0x00FF00FF00FF00FFUL, 8);
      Step64(compiler, xSlot, 0x0000FFFF0000FFFFUL, 16);
      compiler._il.LoadLocal(xSlot);
      compiler._il.LoadConstantI4(32);
      compiler._il.OpCode(ILOpCode.Shl);
      compiler._il.LoadLocal(xSlot);
      compiler._il.LoadConstantI4(32);
      compiler._il.OpCode(ILOpCode.Shr_un);
      compiler._il.OpCode(ILOpCode.Or);
    } else {
      static void Step32(MsilMethodCompiler c, int slot, uint mask, int shift) {
        c._il.LoadLocal(slot);
        c._il.LoadConstantI4(unchecked((int)mask));
        c._il.OpCode(ILOpCode.And);
        c._il.LoadConstantI4(shift);
        c._il.OpCode(ILOpCode.Shl);
        c._il.LoadLocal(slot);
        c._il.LoadConstantI4(shift);
        c._il.OpCode(ILOpCode.Shr_un);
        c._il.LoadConstantI4(unchecked((int)mask));
        c._il.OpCode(ILOpCode.And);
        c._il.OpCode(ILOpCode.Or);
        c._il.StoreLocal(slot);
      }

      Step32(compiler, xSlot, 0x55555555U, 1);
      Step32(compiler, xSlot, 0x33333333U, 2);
      Step32(compiler, xSlot, 0x0F0F0F0FU, 4);
      Step32(compiler, xSlot, 0x00FF00FFU, 8);
      compiler._il.LoadLocal(xSlot);
      compiler._il.LoadConstantI4(16);
      compiler._il.OpCode(ILOpCode.Shl);
      compiler._il.LoadLocal(xSlot);
      compiler._il.LoadConstantI4(16);
      compiler._il.OpCode(ILOpCode.Shr_un);
      compiler._il.OpCode(ILOpCode.Or);
    }

    compiler._il.OpCode(ILOpCode.Ret);
    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles the <c>$EnumIsDefined</c> helper for <paramref name="enumType"/>.</summary>
  public static int CompileEnumIsDefinedHelper(IrEnumType enumType, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.Bool, [], []);
    var trueLabel = compiler._il.DefineLabel();
    var seen = new HashSet<long>();
    foreach (var member in enumType.Members) {
      if (!seen.Add(member.Value))
        continue;

      compiler._il.LoadArgument(0);
      compiler.EmitConstValue(enumType.Underlying, member.Value);
      compiler._il.Branch(ILOpCode.Beq, trueLabel);
    }

    compiler._il.LoadConstantI4(0);
    compiler._il.OpCode(ILOpCode.Ret);
    compiler._il.MarkLabel(trueLabel);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Ret);
    return bodies.AddMethodBody(compiler._il, maxStack: 8);
  }

  /// <summary>Compiles the <c>$EnumToString</c> helper for <paramref name="enumType"/> matching <see cref="EnumOperations.Format"/>.</summary>
  public static int CompileEnumToStringHelper(IrEnumType enumType, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.String, [], []);
    var ordered = enumType.Ordered;
    var underlying = enumType.Underlying;

    // 1. Exact member match
    foreach (var member in ordered) {
      var nextLabel = compiler._il.DefineLabel();
      compiler._il.LoadArgument(0);
      compiler.EmitConstValue(underlying, member.Value);
      compiler._il.Branch(ILOpCode.Bne_un, nextLabel);
      compiler.EmitStringLiteral(member.Name);
      compiler._il.OpCode(ILOpCode.Ret);
      compiler._il.MarkLabel(nextLabel);
    }

    // 2. [Flags] greedy decomposition from largest unsigned value down, prepending for ascending output order
    if (enumType.IsFlags) {
      var is64 = underlying.IsLong;
      var bitsType = is64 ? IrType.U64 : IrType.U32;
      var restSlot = compiler.AllocTemp(bitsType);
      var resultSlot = compiler.AllocTemp(IrType.String);
      var fallbackLabel = compiler._il.DefineLabel();

      compiler._il.LoadArgument(0);
      if (underlying == IrType.I8)
        compiler._il.OpCode(ILOpCode.Conv_u1);
      else if (underlying == IrType.I16)
        compiler._il.OpCode(ILOpCode.Conv_u2);
      compiler._il.StoreLocal(restSlot);
      compiler._il.OpCode(ILOpCode.Ldnull);
      compiler._il.StoreLocal(resultSlot);

      var concat3 = ctx.GetStaticMethodRef("System", "String", "Concat", IrType.String, IrType.String, IrType.String, IrType.String);
      for (var i = ordered.Count - 1; i >= 0; i--) {
        var flag = enumType.Unsigned(ordered[i].Value);
        if (flag == 0)
          continue;

        var skipLabel = compiler._il.DefineLabel();
        var setFirstLabel = compiler._il.DefineLabel();
        var afterNameLabel = compiler._il.DefineLabel();

        compiler._il.LoadLocal(restSlot);
        if (is64)
          compiler._il.LoadConstantI8(unchecked((long)flag));
        else
          compiler._il.LoadConstantI4(unchecked((int)flag));
        compiler._il.OpCode(ILOpCode.And);
        if (is64)
          compiler._il.LoadConstantI8(unchecked((long)flag));
        else
          compiler._il.LoadConstantI4(unchecked((int)flag));
        compiler._il.Branch(ILOpCode.Bne_un, skipLabel);

        // rest &= ~flag
        compiler._il.LoadLocal(restSlot);
        if (is64)
          compiler._il.LoadConstantI8(unchecked((long)~flag));
        else
          compiler._il.LoadConstantI4(unchecked((int)~(uint)flag));
        compiler._il.OpCode(ILOpCode.And);
        compiler._il.StoreLocal(restSlot);

        // result = result == null ? name : string.Concat(name, ", ", result)
        compiler._il.LoadLocal(resultSlot);
        compiler._il.Branch(ILOpCode.Brfalse, setFirstLabel);
        compiler.EmitStringLiteral(ordered[i].Name);
        compiler.EmitStringLiteral(", ");
        compiler._il.LoadLocal(resultSlot);
        compiler._il.Call(concat3);
        compiler._il.StoreLocal(resultSlot);
        compiler._il.Branch(ILOpCode.Br, afterNameLabel);

        compiler._il.MarkLabel(setFirstLabel);
        compiler.EmitStringLiteral(ordered[i].Name);
        compiler._il.StoreLocal(resultSlot);

        compiler._il.MarkLabel(afterNameLabel);
        compiler._il.MarkLabel(skipLabel);
      }

      compiler._il.LoadLocal(restSlot);
      if (is64) {
        compiler._il.LoadConstantI8(0);
        compiler._il.Branch(ILOpCode.Bne_un, fallbackLabel);
      } else {
        compiler._il.Branch(ILOpCode.Brtrue, fallbackLabel);
      }
      compiler._il.LoadLocal(resultSlot);
      compiler._il.Branch(ILOpCode.Brfalse, fallbackLabel);
      compiler._il.LoadLocal(resultSlot);
      compiler._il.OpCode(ILOpCode.Ret);

      compiler._il.MarkLabel(fallbackLabel);
    }

    // 3. Decimal fallback in underlying type
    var valSlot = compiler.AllocTemp(underlying);
    compiler._il.LoadArgument(0);
    compiler._il.StoreLocal(valSlot);
    compiler._il.LoadLocalAddress(valSlot);
    compiler._il.Call(ctx.GetMemberRef(
      "stat:System.Globalization.CultureInfo::get_InvariantCulture()",
      ctx.GetBclType("System.Globalization", "CultureInfo"),
      "get_InvariantCulture",
      sig => sig.Parameters(0, r => r.Type().Type(ctx.GetBclType("System.Globalization", "CultureInfo"), isValueType: false), _ => { })));
    var bclName = BclIntegerTypeName(underlying);
    compiler._il.Call(ctx.GetMemberRef(
      $"inst:System.{bclName}::ToString(IFormatProvider)",
      ctx.GetBclType("System", bclName),
      "ToString",
      sig => sig.Parameters(1, r => r.Type().String(), p =>
        p.AddParameter().Type().Type(ctx.GetBclType("System", "IFormatProvider"), isValueType: false))));
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles the <c>$EnumParse</c> helper for <paramref name="enumType"/> matching <see cref="EnumOperations.Parse"/>.</summary>
  public static int CompileEnumParseHelper(IrEnumType enumType, MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, enumType.Type, [], []);
    var underlying = enumType.Underlying;
    var is64 = underlying.IsLong;
    var bitsType = is64 ? IrType.U64 : IrType.U32;
    var ordered = enumType.Ordered;

    var trimmedSlot = compiler.AllocTemp(IrType.String);
    var firstCharSlot = compiler.AllocTemp(IrType.U16);
    var partsSlot = compiler.AllocTemp(IrType.Array(IrType.String));
    var idxSlot = compiler.AllocTemp(IrType.I32);
    var nameSlot = compiler.AllocTemp(IrType.String);
    var cmpSlot = compiler.AllocTemp(IrType.I32);
    var accSlot = compiler.AllocTemp(bitsType);
    var retSlot = compiler.AllocTemp(enumType.Type);

    var throwInvalidLabel = compiler._il.DefineLabel();
    var afterNumericLabel = compiler._il.DefineLabel();
    var tryNumericLabel = compiler._il.DefineLabel();
    var returnLabel = compiler._il.DefineLabel();

    // trimmed = text.TrimStart()
    compiler._il.LoadArgument(0);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(ctx.GetInstanceMethodRef("System", "String", "TrimStart", IrType.String));
    compiler._il.StoreLocal(trimmedSlot);

    // if (trimmed.Length == 0) throw new ArgumentException("Invalid enum value.")
    compiler._il.LoadLocal(trimmedSlot);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(ctx.GetInstanceMethodRef("System", "String", "get_Length", IrType.I32));
    compiler._il.Branch(ILOpCode.Brfalse, throwInvalidLabel);

    // c = trimmed[0]; if ((c >= '0' && c <= '9') || c == '-' || c == '+') try numeric parse
    compiler._il.LoadLocal(trimmedSlot);
    compiler._il.LoadConstantI4(0);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(ctx.GetMemberRef(
      "inst:System.String::get_Chars(int)",
      ctx.GetBclType("System", "String"),
      "get_Chars",
      sig => sig.Parameters(1, r => r.Type().Char(), p => p.AddParameter().Type().Int32())));
    compiler._il.StoreLocal(firstCharSlot);

    compiler._il.LoadLocal(firstCharSlot);
    compiler._il.LoadConstantI4('-');
    compiler._il.Branch(ILOpCode.Beq, tryNumericLabel);
    compiler._il.LoadLocal(firstCharSlot);
    compiler._il.LoadConstantI4('+');
    compiler._il.Branch(ILOpCode.Beq, tryNumericLabel);
    compiler._il.LoadLocal(firstCharSlot);
    compiler._il.LoadConstantI4('0');
    compiler._il.Branch(ILOpCode.Blt, afterNumericLabel);
    compiler._il.LoadLocal(firstCharSlot);
    compiler._il.LoadConstantI4('9');
    compiler._il.Branch(ILOpCode.Bgt, afterNumericLabel);

    compiler._il.MarkLabel(tryNumericLabel);
    var tryStart = compiler._il.DefineLabel();
    var tryEnd = compiler._il.DefineLabel();
    var catchStart = compiler._il.DefineLabel();
    var catchEnd = compiler._il.DefineLabel();

    compiler._il.MarkLabel(tryStart);
    compiler._il.LoadLocal(trimmedSlot);
    compiler._il.Call(ctx.GetMemberRef(
      "stat:System.Globalization.CultureInfo::get_InvariantCulture()",
      ctx.GetBclType("System.Globalization", "CultureInfo"),
      "get_InvariantCulture",
      sig => sig.Parameters(0, r => r.Type().Type(ctx.GetBclType("System.Globalization", "CultureInfo"), isValueType: false), _ => { })));
    var bclName = BclIntegerTypeName(underlying);
    compiler._il.Call(ctx.GetMemberRef(
      $"stat:System.{bclName}::Parse(str,IFormatProvider)",
      ctx.GetBclType("System", bclName),
      "Parse",
      sig => sig.Parameters(2, r => ctx.EncodeType(r.Type(), underlying), p => {
        p.AddParameter().Type().String();
        p.AddParameter().Type().Type(ctx.GetBclType("System", "IFormatProvider"), isValueType: false);
      })));
    compiler._il.StoreLocal(retSlot);
    compiler._il.Branch(ILOpCode.Leave, returnLabel);
    compiler._il.MarkLabel(tryEnd);

    compiler._il.MarkLabel(catchStart);
    compiler._il.OpCode(ILOpCode.Pop);
    compiler._il.Branch(ILOpCode.Leave, afterNumericLabel);
    compiler._il.MarkLabel(catchEnd);
    compiler._flow.AddCatchRegion(tryStart, tryEnd, catchStart, catchEnd, ctx.GetBclType("System", "FormatException"));

    compiler._il.MarkLabel(afterNumericLabel);
    // cmp = ignoreCase ? 5 (OrdinalIgnoreCase) : 4 (Ordinal)
    var ordLabel = compiler._il.DefineLabel();
    var cmpDoneLabel = compiler._il.DefineLabel();
    compiler._il.LoadArgument(1);
    compiler._il.Branch(ILOpCode.Brfalse, ordLabel);
    compiler._il.LoadConstantI4(5);
    compiler._il.Branch(ILOpCode.Br, cmpDoneLabel);
    compiler._il.MarkLabel(ordLabel);
    compiler._il.LoadConstantI4(4);
    compiler._il.MarkLabel(cmpDoneLabel);
    compiler._il.StoreLocal(cmpSlot);

    // parts = trimmed.Split(',', StringSplitOptions.None)
    compiler._il.LoadLocal(trimmedSlot);
    compiler._il.LoadConstantI4(',');
    compiler._il.LoadConstantI4(0);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(ctx.GetMemberRef(
      "inst:System.String::Split(char,StringSplitOptions)",
      ctx.GetBclType("System", "String"),
      "Split",
      sig => sig.Parameters(2, r => r.Type().SZArray().String(), p => {
        p.AddParameter().Type().Char();
        p.AddParameter().Type().Type(ctx.GetBclType("System", "StringSplitOptions"), isValueType: true);
      })));
    compiler._il.StoreLocal(partsSlot);

    if (is64)
      compiler._il.LoadConstantI8(0);
    else
      compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(accSlot);
    compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(idxSlot);

    var loopCond = compiler._il.DefineLabel();
    var loopDone = compiler._il.DefineLabel();
    var eqRef = compiler.GetStringComparisonStaticRef("Equals", IrType.Bool);
    var trimRef = ctx.GetInstanceMethodRef("System", "String", "Trim", IrType.String);

    compiler._il.MarkLabel(loopCond);
    compiler._il.LoadLocal(idxSlot);
    compiler._il.LoadLocal(partsSlot);
    compiler._il.OpCode(ILOpCode.Ldlen);
    compiler._il.OpCode(ILOpCode.Conv_i4);
    compiler._il.Branch(ILOpCode.Bge, loopDone);

    compiler._il.LoadLocal(partsSlot);
    compiler._il.LoadLocal(idxSlot);
    compiler._il.OpCode(ILOpCode.Ldelem_ref);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(trimRef);
    compiler._il.StoreLocal(nameSlot);

    var nextPartLabel = compiler._il.DefineLabel();
    foreach (var member in ordered) {
      var noMatchLabel = compiler._il.DefineLabel();
      compiler._il.LoadLocal(nameSlot);
      compiler.EmitStringLiteral(member.Name);
      compiler._il.LoadLocal(cmpSlot);
      compiler._il.Call(eqRef);
      compiler._il.Branch(ILOpCode.Brfalse, noMatchLabel);

      compiler._il.LoadLocal(accSlot);
      var memberBits = enumType.Unsigned(member.Value);
      if (is64)
        compiler._il.LoadConstantI8(unchecked((long)memberBits));
      else
        compiler._il.LoadConstantI4(unchecked((int)memberBits));
      compiler._il.OpCode(ILOpCode.Or);
      compiler._il.StoreLocal(accSlot);
      compiler._il.Branch(ILOpCode.Br, nextPartLabel);
      compiler._il.MarkLabel(noMatchLabel);
    }

    compiler._il.Branch(ILOpCode.Br, throwInvalidLabel);

    compiler._il.MarkLabel(nextPartLabel);
    compiler._il.LoadLocal(idxSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Add);
    compiler._il.StoreLocal(idxSlot);
    compiler._il.Branch(ILOpCode.Br, loopCond);

    compiler._il.MarkLabel(loopDone);
    compiler._il.LoadLocal(accSlot);
    if (underlying.Bits < 32)
      compiler.EmitNarrowTruncate(underlying);
    compiler._il.StoreLocal(retSlot);
    compiler._il.Branch(ILOpCode.Br, returnLabel);

    compiler._il.MarkLabel(throwInvalidLabel);
    compiler.EmitStringLiteral($"Requested value was not found in enum '{enumType.Name}'.");
    compiler._il.OpCode(ILOpCode.Newobj);
    compiler._il.Token(ctx.GetExceptionCtor(IrExceptionClass.ArgumentException, 1));
    compiler._il.OpCode(ILOpCode.Throw);

    compiler._il.MarkLabel(returnLabel);
    compiler._il.LoadLocal(retSlot);
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles the <c>$StrLastIndexOf(string text, string needle, int from) -&gt; int</c> helper matching <see cref="StringOperations.LastIndexOf"/>.</summary>
  public static int CompileStrLastIndexOfHelper(MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.I32, [], []);
    var textLenSlot = compiler.AllocTemp(IrType.I32);
    var needleLenSlot = compiler.AllocTemp(IrType.I32);
    var startSlot = compiler.AllocTemp(IrType.I32);

    var lenRef = ctx.GetInstanceMethodRef("System", "String", "get_Length", IrType.I32);
    compiler._il.LoadArgument(0);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(lenRef);
    compiler._il.StoreLocal(textLenSlot);

    var oobLabel = compiler._il.DefineLabel();
    var okLabel = compiler._il.DefineLabel();
    compiler._il.LoadArgument(2);
    compiler._il.LoadConstantI4(0);
    compiler._il.Branch(ILOpCode.Blt, oobLabel);
    compiler._il.LoadArgument(2);
    compiler._il.LoadLocal(textLenSlot);
    compiler._il.Branch(ILOpCode.Ble, okLabel);

    compiler._il.MarkLabel(oobLabel);
    compiler._il.OpCode(ILOpCode.Newobj);
    compiler._il.Token(ctx.GetExceptionCtor(IrExceptionClass.IndexOutOfRangeException, 0));
    compiler._il.OpCode(ILOpCode.Throw);

    compiler._il.MarkLabel(okLabel);
    compiler._il.LoadArgument(1);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(lenRef);
    compiler._il.StoreLocal(needleLenSlot);

    // start = Math.Min(from, textLen - needleLen)
    compiler._il.LoadArgument(2);
    compiler._il.LoadLocal(textLenSlot);
    compiler._il.LoadLocal(needleLenSlot);
    compiler._il.OpCode(ILOpCode.Sub);
    compiler._il.Call(ctx.GetStaticMethodRef("System", "Math", "Min", IrType.I32, IrType.I32, IrType.I32));
    compiler._il.StoreLocal(startSlot);

    var cmpRef = ctx.GetStaticMethodRef("System", "String", "CompareOrdinal", IrType.I32, IrType.String, IrType.I32, IrType.String, IrType.I32, IrType.I32);
    var loopCond = compiler._il.DefineLabel();
    var notFound = compiler._il.DefineLabel();
    var nextLabel = compiler._il.DefineLabel();

    compiler._il.MarkLabel(loopCond);
    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadConstantI4(0);
    compiler._il.Branch(ILOpCode.Blt, notFound);

    compiler._il.LoadArgument(0);
    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadArgument(1);
    compiler._il.LoadConstantI4(0);
    compiler._il.LoadLocal(needleLenSlot);
    compiler._il.Call(cmpRef);
    compiler._il.Branch(ILOpCode.Brtrue, nextLabel);

    compiler._il.LoadLocal(startSlot);
    compiler._il.OpCode(ILOpCode.Ret);

    compiler._il.MarkLabel(nextLabel);
    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Sub);
    compiler._il.StoreLocal(startSlot);
    compiler._il.Branch(ILOpCode.Br, loopCond);

    compiler._il.MarkLabel(notFound);
    compiler._il.LoadConstantI4(-1);
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles the <c>$StrSplit(string text, string sep, int limit) -&gt; string[]</c> helper matching <see cref="StringOperations.Split"/>.</summary>
  public static int CompileStrSplitHelper(MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.Array(IrType.String), [], []);
    var listType = IrType.List(IrType.String);
    var listSpec = ctx.GetListSpec(IrType.String);
    var listSlot = compiler.AllocTemp(listType);
    var startSlot = compiler.AllocTemp(IrType.I32);
    var atSlot = compiler.AllocTemp(IrType.I32);
    var countSlot = compiler.AllocTemp(IrType.I32);
    var arrSlot = compiler.AllocTemp(IrType.Array(IrType.String));

    var lenRef = ctx.GetInstanceMethodRef("System", "String", "get_Length", IrType.I32);
    var addRef = compiler.GetListAddRef(IrType.String);
    var listCountRef = compiler.GetListCountRef(IrType.String);
    var listGetRef = ctx.GetMemberRef(
      "inst:List<str>::get_Item(int)",
      listSpec,
      "get_Item",
      sig => sig.Parameters(1, r => r.Type().GenericTypeParameter(0), p => p.AddParameter().Type().Int32()));
    var sub2Ref = ctx.GetInstanceMethodRef("System", "String", "Substring", IrType.String, IrType.I32, IrType.I32);
    var sub1Ref = ctx.GetInstanceMethodRef("System", "String", "Substring", IrType.String, IrType.I32);
    var indexOfRef = compiler.GetStringOrdinalInstanceRef("IndexOf", IrType.I32, withStartIndex: true);
    var charsRef = ctx.GetMemberRef(
      "inst:System.String::get_Chars(int)",
      ctx.GetBclType("System", "String"),
      "get_Chars",
      sig => sig.Parameters(1, r => r.Type().Char(), p => p.AddParameter().Type().Int32()));

    // parts = new List<string>()
    compiler._il.OpCode(ILOpCode.Newobj);
    compiler._il.Token(ctx.GetMemberRef("inst:List<str>..ctor()", listSpec, ".ctor", sig => sig.Parameters(0, r => r.Void(), _ => { })));
    compiler._il.StoreLocal(listSlot);

    var nonEmptySepLabel = compiler._il.DefineLabel();
    var buildArrayLabel = compiler._il.DefineLabel();

    compiler._il.LoadArgument(1);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(lenRef);
    compiler._il.Branch(ILOpCode.Brtrue, nonEmptySepLabel);

    // Empty separator: one 1-char string per code unit
    compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(startSlot);
    var charLoop = compiler._il.DefineLabel();
    compiler._il.MarkLabel(charLoop);
    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadArgument(0);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(lenRef);
    compiler._il.Branch(ILOpCode.Bge, buildArrayLabel);

    compiler._il.LoadLocal(listSlot);
    compiler._il.LoadArgument(0);
    compiler._il.LoadLocal(startSlot);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(charsRef);
    compiler.EmitCharToString();
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(addRef);

    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Add);
    compiler._il.StoreLocal(startSlot);
    compiler._il.Branch(ILOpCode.Br, charLoop);

    // Non-empty separator
    compiler._il.MarkLabel(nonEmptySepLabel);
    compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(startSlot);
    var sepLoop = compiler._il.DefineLabel();
    var sepDone = compiler._il.DefineLabel();
    compiler._il.MarkLabel(sepLoop);
    compiler._il.LoadArgument(0);
    compiler._il.LoadArgument(1);
    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadConstantI4(4); // StringComparison.Ordinal
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(indexOfRef);
    compiler._il.StoreLocal(atSlot);

    compiler._il.LoadLocal(atSlot);
    compiler._il.LoadConstantI4(0);
    compiler._il.Branch(ILOpCode.Blt, sepDone);

    compiler._il.LoadLocal(listSlot);
    compiler._il.LoadArgument(0);
    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadLocal(atSlot);
    compiler._il.LoadLocal(startSlot);
    compiler._il.OpCode(ILOpCode.Sub);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(sub2Ref);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(addRef);

    compiler._il.LoadLocal(atSlot);
    compiler._il.LoadArgument(1);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(lenRef);
    compiler._il.OpCode(ILOpCode.Add);
    compiler._il.StoreLocal(startSlot);
    compiler._il.Branch(ILOpCode.Br, sepLoop);

    compiler._il.MarkLabel(sepDone);
    compiler._il.LoadLocal(listSlot);
    compiler._il.LoadArgument(0);
    compiler._il.LoadLocal(startSlot);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(sub1Ref);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(addRef);

    // Copy min(parts.Count, limit) (or parts.Count if limit < 0) into string[]
    compiler._il.MarkLabel(buildArrayLabel);
    compiler._il.LoadLocal(listSlot);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(listCountRef);
    compiler._il.StoreLocal(countSlot);

    var noLimitLabel = compiler._il.DefineLabel();
    compiler._il.LoadArgument(2);
    compiler._il.LoadConstantI4(0);
    compiler._il.Branch(ILOpCode.Blt, noLimitLabel);
    compiler._il.LoadArgument(2);
    compiler._il.LoadLocal(countSlot);
    compiler._il.Branch(ILOpCode.Bge, noLimitLabel);
    compiler._il.LoadArgument(2);
    compiler._il.StoreLocal(countSlot);
    compiler._il.MarkLabel(noLimitLabel);

    compiler._il.LoadLocal(countSlot);
    compiler._il.OpCode(ILOpCode.Newarr);
    compiler._il.Token(ctx.GetTypeToken(IrType.String));
    compiler._il.StoreLocal(arrSlot);

    compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(startSlot);
    var copyLoop = compiler._il.DefineLabel();
    var copyDone = compiler._il.DefineLabel();
    compiler._il.MarkLabel(copyLoop);
    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadLocal(countSlot);
    compiler._il.Branch(ILOpCode.Bge, copyDone);

    compiler._il.LoadLocal(arrSlot);
    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadLocal(listSlot);
    compiler._il.LoadLocal(startSlot);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(listGetRef);
    compiler._il.OpCode(ILOpCode.Stelem_ref);

    compiler._il.LoadLocal(startSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Add);
    compiler._il.StoreLocal(startSlot);
    compiler._il.Branch(ILOpCode.Br, copyLoop);

    compiler._il.MarkLabel(copyDone);
    compiler._il.LoadLocal(arrSlot);
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

  /// <summary>Compiles <c>$StrTrimEcma(string text, bool trimStart, bool trimEnd) -&gt; string</c> matching <see cref="WhitespaceSet.Ecma"/>.</summary>
  public static int CompileStrTrimEcmaHelper(MsilMetadataContext ctx, MethodBodyStreamEncoder bodies) {
    var compiler = new MsilMethodCompiler(ctx, IrType.String, [], []);
    var firstSlot = compiler.AllocTemp(IrType.I32);
    var lastSlot = compiler.AllocTemp(IrType.I32);
    var chSlot = compiler.AllocTemp(IrType.I32);

    var lenRef = ctx.GetInstanceMethodRef("System", "String", "get_Length", IrType.I32);
    var charsRef = ctx.GetMemberRef(
      "inst:System.String::get_Chars(int)",
      ctx.GetBclType("System", "String"),
      "get_Chars",
      sig => sig.Parameters(1, r => r.Type().Char(), p => p.AddParameter().Type().Int32()));

    compiler._il.LoadConstantI4(0);
    compiler._il.StoreLocal(firstSlot);
    compiler._il.LoadArgument(0);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(lenRef);
    compiler._il.StoreLocal(lastSlot);

    static void EmitBranchIfEcmaWs(MsilMethodCompiler c, int slot, LabelHandle wsLabel) {
      // 0x09..0x0D
      var afterLow = c._il.DefineLabel();
      c._il.LoadLocal(slot);
      c._il.LoadConstantI4(0x09);
      c._il.Branch(ILOpCode.Blt, afterLow);
      c._il.LoadLocal(slot);
      c._il.LoadConstantI4(0x0D);
      c._il.Branch(ILOpCode.Ble, wsLabel);
      c._il.MarkLabel(afterLow);
      // 0x20, 0xA0, 0x1680
      foreach (var single in new[] { 0x0020, 0x00A0, 0x1680, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF }) {
        c._il.LoadLocal(slot);
        c._il.LoadConstantI4(single);
        c._il.Branch(ILOpCode.Beq, wsLabel);
      }
      // 0x2000..0x200A
      var afterRange = c._il.DefineLabel();
      c._il.LoadLocal(slot);
      c._il.LoadConstantI4(0x2000);
      c._il.Branch(ILOpCode.Blt, afterRange);
      c._il.LoadLocal(slot);
      c._il.LoadConstantI4(0x200A);
      c._il.Branch(ILOpCode.Ble, wsLabel);
      c._il.MarkLabel(afterRange);
    }

    // Start loop
    var startDone = compiler._il.DefineLabel();
    var startLoop = compiler._il.DefineLabel();
    compiler._il.LoadArgument(1);
    compiler._il.Branch(ILOpCode.Brfalse, startDone);
    compiler._il.MarkLabel(startLoop);
    compiler._il.LoadLocal(firstSlot);
    compiler._il.LoadLocal(lastSlot);
    compiler._il.Branch(ILOpCode.Bge, startDone);
    compiler._il.LoadArgument(0);
    compiler._il.LoadLocal(firstSlot);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(charsRef);
    compiler._il.StoreLocal(chSlot);
    var startAdv = compiler._il.DefineLabel();
    EmitBranchIfEcmaWs(compiler, chSlot, startAdv);
    compiler._il.Branch(ILOpCode.Br, startDone);
    compiler._il.MarkLabel(startAdv);
    compiler._il.LoadLocal(firstSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Add);
    compiler._il.StoreLocal(firstSlot);
    compiler._il.Branch(ILOpCode.Br, startLoop);
    compiler._il.MarkLabel(startDone);

    // End loop
    var endDone = compiler._il.DefineLabel();
    var endLoop = compiler._il.DefineLabel();
    compiler._il.LoadArgument(2);
    compiler._il.Branch(ILOpCode.Brfalse, endDone);
    compiler._il.MarkLabel(endLoop);
    compiler._il.LoadLocal(lastSlot);
    compiler._il.LoadLocal(firstSlot);
    compiler._il.Branch(ILOpCode.Ble, endDone);
    compiler._il.LoadArgument(0);
    compiler._il.LoadLocal(lastSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Sub);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(charsRef);
    compiler._il.StoreLocal(chSlot);
    var endDec = compiler._il.DefineLabel();
    EmitBranchIfEcmaWs(compiler, chSlot, endDec);
    compiler._il.Branch(ILOpCode.Br, endDone);
    compiler._il.MarkLabel(endDec);
    compiler._il.LoadLocal(lastSlot);
    compiler._il.LoadConstantI4(1);
    compiler._il.OpCode(ILOpCode.Sub);
    compiler._il.StoreLocal(lastSlot);
    compiler._il.Branch(ILOpCode.Br, endLoop);
    compiler._il.MarkLabel(endDone);

    compiler._il.LoadArgument(0);
    compiler._il.LoadLocal(firstSlot);
    compiler._il.LoadLocal(lastSlot);
    compiler._il.LoadLocal(firstSlot);
    compiler._il.OpCode(ILOpCode.Sub);
    compiler._il.OpCode(ILOpCode.Callvirt);
    compiler._il.Token(ctx.GetInstanceMethodRef("System", "String", "Substring", IrType.String, IrType.I32, IrType.I32));
    compiler._il.OpCode(ILOpCode.Ret);

    var localsSig = ctx.EncodeLocals(compiler._localEncoders);
    return bodies.AddMethodBody(compiler._il, maxStack: 16, localVariablesSignature: localsSig);
  }

}
