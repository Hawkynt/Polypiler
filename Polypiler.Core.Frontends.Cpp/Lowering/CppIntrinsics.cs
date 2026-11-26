using System.Collections.Generic;
using Polypiler.Core.Frontends.Cpp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Cpp.Lowering;

internal sealed partial class CppLowerer {

  private bool TryLowerStdIntrinsic(
    FunctionContext ctx,
    string rawName,
    IReadOnlyList<CppTypeArg> typeArgs,
    IReadOnlyList<CppExpr> args,
    SourceRange span,
    out IrExpr? result) {
    result = null;
    var name = StripStd(rawName);
    if (name.StartsWith("ranges::", System.StringComparison.Ordinal))
      name = name[8..];

    switch (name) {
      case "abs" or "fabs" when args.Count == 1: {
        var a = this.LowerExpr(ctx, args[0], null);
        if (name == "fabs" && !a.Type.IsFloat)
          a = this.ConvertTo(a, IrType.F64, span);

        result = new IrIntrinsic(IrIntrinsicOp.Abs, a.Type, [a]);
        return true;
      }
      case "min" or "max" when args.Count == 2: {
        var a = this.LowerExpr(ctx, args[0], null);
        var b = this.LowerExpr(ctx, args[1], null);
        var common = typeArgs is [{ Type: { } explicitT }]
          ? this.ResolveType(explicitT, isLocal: false, ctx.TypeSubst)
          : a.Type == b.Type && a.Type.IsNumeric ? a.Type : UsualArithmeticType(a.Type, b.Type);
        result = new IrIntrinsic(
          name == "min" ? IrIntrinsicOp.Min : IrIntrinsicOp.Max,
          common,
          [this.ConvertTo(a, common, args[0].Span), this.ConvertTo(b, common, args[1].Span)]);
        return true;
      }
      case "clamp" when args.Count == 3: {
        var v = this.LowerExpr(ctx, args[0], null);
        var lo = this.LowerExpr(ctx, args[1], v.Type);
        var hi = this.LowerExpr(ctx, args[2], v.Type);
        var common = typeArgs is [{ Type: { } explicitT }]
          ? this.ResolveType(explicitT, isLocal: false, ctx.TypeSubst)
          : v.Type == lo.Type && v.Type == hi.Type && v.Type.IsNumeric
            ? v.Type
            : UsualArithmeticType(UsualArithmeticType(v.Type, lo.Type), hi.Type);
        result = new IrIntrinsic(
          IrIntrinsicOp.Clamp,
          common,
          [this.ConvertTo(v, common, args[0].Span), this.ConvertTo(lo, common, args[1].Span), this.ConvertTo(hi, common, args[2].Span)]);
        return true;
      }
      case "sqrt" or "floor" or "ceil" or "trunc" or "round" when args.Count == 1: {
        var a = this.LowerExpr(ctx, args[0], null);
        var fType = a.Type.IsFloat ? a.Type : IrType.F64;
        var op = name switch {
          "sqrt" => IrIntrinsicOp.Sqrt,
          "floor" => IrIntrinsicOp.Floor,
          "ceil" => IrIntrinsicOp.Ceiling,
          "trunc" => IrIntrinsicOp.Truncate,
          _ => IrIntrinsicOp.RoundAway
        };
        result = new IrIntrinsic(op, fType, [this.ConvertTo(a, fType, args[0].Span)]);
        return true;
      }
      case "copysign" when args.Count == 2: {
        var x = this.LowerExpr(ctx, args[0], null);
        var y = this.LowerExpr(ctx, args[1], null);
        var fType = x.Type == IrType.F32 && y.Type == IrType.F32 ? IrType.F32 : IrType.F64;
        result = new IrIntrinsic(IrIntrinsicOp.CopySign, fType, [this.ConvertTo(x, fType, args[0].Span), this.ConvertTo(y, fType, args[1].Span)]);
        return true;
      }
      case "isnan" or "isinf" or "isfinite" or "isnormal" when args.Count == 1: {
        var x = this.LowerExpr(ctx, args[0], null);
        var fType = x.Type.IsFloat ? x.Type : IrType.F64;
        var op = name switch {
          "isnan" => IrIntrinsicOp.IsNaN,
          "isinf" => IrIntrinsicOp.IsInfinity,
          "isfinite" => IrIntrinsicOp.IsFinite,
          _ => IrIntrinsicOp.IsNormal
        };
        result = new IrIntrinsic(op, IrType.Bool, [this.ConvertTo(x, fType, args[0].Span)]);
        return true;
      }
      case "rotl" or "rotr" when args.Count == 2: {
        var x = this.LowerExpr(ctx, args[0], null);
        var s = this.ConvertTo(this.LowerExpr(ctx, args[1], IrType.I32), IrType.I32, args[1].Span);
        result = new IrIntrinsic(name == "rotl" ? IrIntrinsicOp.RotateLeft : IrIntrinsicOp.RotateRight, x.Type, [x, s]);
        return true;
      }
      case "countl_zero" or "countr_zero" or "popcount" when args.Count == 1: {
        var x = this.LowerExpr(ctx, args[0], null);
        var op = name switch {
          "countl_zero" => IrIntrinsicOp.LeadingZeroCount,
          "countr_zero" => IrIntrinsicOp.TrailingZeroCount,
          _ => IrIntrinsicOp.PopCount
        };
        result = new IrIntrinsic(op, IrType.I32, [x]);
        return true;
      }
      case "byteswap" when args.Count == 1: {
        var x = this.LowerExpr(ctx, args[0], null);
        result = new IrIntrinsic(IrIntrinsicOp.ByteSwap, x.Type, [x]);
        return true;
      }
      case "make_optional" when args.Count == 1: {
        var x = this.LowerExpr(ctx, args[0], null);
        result = new IrSome(x);
        return true;
      }
      case "memcpy" when args.Count == 3:
        result = this.LowerMemcpy(ctx, args[0], args[1], args[2], span);
        return true;
      case "fill" when args.Count is 2 or 3:
        result = this.LowerFill(ctx, args, span);
        return true;
      case "copy" when args.Count is 2 or 3:
        result = this.LowerCopy(ctx, args, span);
        return true;
      default:
        return false;
    }
  }

  private IrExpr LowerBitCast(IrType target, IrExpr operand, SourceRange span) {
    if (operand.Type == target)
      return operand;

    if (operand.Type == IrType.F32 && target == IrType.I32)
      return new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [operand]);

    if (operand.Type == IrType.F32 && target == IrType.U32)
      return new IrConvert(IrType.U32, new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I32, [operand]), IrConversion.Wrap);

    if (operand.Type == IrType.I32 && target == IrType.F32)
      return new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [operand]);

    if (operand.Type == IrType.U32 && target == IrType.F32)
      return new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [new IrConvert(IrType.I32, operand, IrConversion.Wrap)]);

    if (operand.Type == IrType.F64 && target == IrType.I64)
      return new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [operand]);

    if (operand.Type == IrType.F64 && target == IrType.U64)
      return new IrConvert(IrType.U64, new IrIntrinsic(IrIntrinsicOp.FloatBits, IrType.I64, [operand]), IrConversion.Wrap);

    if (operand.Type == IrType.I64 && target == IrType.F64)
      return new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [operand]);

    if (operand.Type == IrType.U64 && target == IrType.F64)
      return new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [new IrConvert(IrType.I64, operand, IrConversion.Wrap)]);

    if (operand.Type.IsInteger && target.IsInteger && operand.Type.Bits == target.Bits)
      return new IrConvert(target, operand, IrConversion.Wrap);

    throw this.UnsupportedCall(span, $"std::bit_cast<{target}>({operand.Type})");
  }

  private IrExpr LowerMemcpy(FunctionContext ctx, CppExpr dstExpr, CppExpr srcExpr, CppExpr countExpr, SourceRange span) {
    var dst = this.UnwrapDataOrSequence(ctx, dstExpr);
    var src = this.UnwrapDataOrSequence(ctx, srcExpr);
    if (!dst.Type.IsWritableSequence || !src.Type.IsSequence || dst.Type.Element != src.Type.Element)
      throw this.UnsupportedCall(span, "std::memcpy");

    var byteCount = this.ToIndex(this.LowerExpr(ctx, countExpr, IrType.I32));
    var elemSize = dst.Type.Element!.Size;
    IrExpr elemCount = elemSize == 1
      ? byteCount
      : new IrBinary(IrBinaryOp.Divide, byteCount, new IrConst(IrType.I32, elemSize));

    if (dst.Type.IsArray && src.Type.IsArray)
      return new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [src, new IrConst(IrType.I32, 0), dst, new IrConst(IrType.I32, 0), elemCount]);

    var srcSlice = src.Type.IsView
      ? new IrIntrinsic(IrIntrinsicOp.Slice, src.Type, [src, new IrConst(IrType.I32, 0), elemCount])
      : new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.ReadOnlySpan(src.Type.Element!), [src, new IrConst(IrType.I32, 0), elemCount]);
    var dstSlice = dst.Type.IsView
      ? new IrIntrinsic(IrIntrinsicOp.Slice, dst.Type, [dst, new IrConst(IrType.I32, 0), elemCount])
      : new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(dst.Type.Element!), [dst, new IrConst(IrType.I32, 0), elemCount]);
    return new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [srcSlice, dstSlice]);
  }

  private IrExpr LowerFill(FunctionContext ctx, IReadOnlyList<CppExpr> args, SourceRange span) {
    var target = args.Count == 3
      ? this.UnwrapBeginEndTarget(ctx, args[0], span)
      : this.LowerExpr(ctx, args[0], null);
    var valExpr = args[^1];
    if (!target.Type.IsWritableSequence)
      throw this.UnsupportedCall(span, "std::fill");

    var val = this.ConvertTo(this.LowerExpr(ctx, valExpr, target.Type.Element), target.Type.Element!, valExpr.Span);
    var op = target.Type.IsView ? IrIntrinsicOp.ViewFill : IrIntrinsicOp.ArrayFill;
    return new IrIntrinsic(op, IrType.Void, [target, val]);
  }

  private IrExpr LowerCopy(FunctionContext ctx, IReadOnlyList<CppExpr> args, SourceRange span) {
    var src = args.Count == 3
      ? this.UnwrapBeginEndTarget(ctx, args[0], span)
      : this.LowerExpr(ctx, args[0], null);
    var dst = args.Count == 3
      ? this.UnwrapBeginEndTarget(ctx, args[2], span)
      : this.LowerExpr(ctx, args[1], null);
    if (!src.Type.IsSequence || !dst.Type.IsWritableSequence || src.Type.Element != dst.Type.Element)
      throw this.UnsupportedCall(span, "std::copy");

    if (src.Type.IsArray && dst.Type.IsArray)
      return new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [src, new IrConst(IrType.I32, 0), dst, new IrConst(IrType.I32, 0), new IrArrayLength(src)]);

    var srcView = src.Type.IsView ? src : new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.ReadOnlySpan(src.Type.Element!), [src]);
    var dstView = dst.Type.IsView ? dst : new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(dst.Type.Element!), [dst]);
    return new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [srcView, dstView]);
  }

  private IrExpr UnwrapDataOrSequence(FunctionContext ctx, CppExpr expr) {
    if (expr is CppCallExpr { Callee: CppMemberExpr { Member: "data" } mem, Arguments.Count: 0 })
      return this.LowerExpr(ctx, mem.Target, null);

    return this.LowerExpr(ctx, expr, null);
  }

  private IrExpr UnwrapBeginEndTarget(FunctionContext ctx, CppExpr expr, SourceRange span) {
    if (expr is CppCallExpr { Callee: CppMemberExpr { Member: "begin" or "end" or "data" } mem, Arguments.Count: 0 })
      return this.LowerExpr(ctx, mem.Target, null);

    throw this.UnsupportedCall(span, "non-container iterator");
  }

  // ---------------------------------------------------------------- member calls

  private IrExpr LowerMemberCall(FunctionContext ctx, CppMemberExpr mem, IReadOnlyList<CppExpr> args, SourceRange span) {
    var target = this.LowerExpr(ctx, mem.Target, null);
    var type = target.Type;
    var method = mem.Member;

    if (type.IsOptional) {
      return method switch {
        "has_value" when args.Count == 0 => new IrUnary(IrUnaryOp.LogicalNot, new IrIsNone(target)),
        "value" when args.Count == 0 => new IrUnwrap(target),
        "value_or" when args.Count == 1 => new IrCoalesce(target, this.ConvertTo(this.LowerExpr(ctx, args[0], type.Underlying), type.Underlying!, args[0].Span)),
        _ => throw this.UnsupportedCall(span, $"optional.{method}")
      };
    }

    if (type.IsString)
      return this.LowerStringMethod(ctx, target, method, args, span);

    if (type.IsView)
      return this.LowerSpanMethod(ctx, target, method, args, span);

    if (type.IsArray)
      return this.LowerArrayMethod(ctx, target, method, args, span);

    if (type.IsList)
      return this.LowerListMethod(ctx, target, method, args, span);

    if (type.ClassType is { } cls && IrExceptionClass.IsException(cls) && method == "what" && args.Count == 0)
      return new IrFieldLoad(target, IrExceptionClass.Message);

    throw this.UnsupportedCall(span, $"{type}.{method}");
  }

  private IrExpr LowerStringMethod(FunctionContext ctx, IrExpr target, string method, IReadOnlyList<CppExpr> args, SourceRange span) => method switch {
    "size" or "length" when args.Count == 0 => new IrConvert(IrType.U64, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [target]), IrConversion.Wrap),
    "empty" when args.Count == 0 => new IrBinary(IrBinaryOp.Equal, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [target]), new IrConst(IrType.I32, 0)),
    "at" when args.Count == 1 => new IrConvert(IrType.I8, new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [target, this.ToIndex(this.LowerExpr(ctx, args[0], IrType.I32))]), IrConversion.Wrap),
    "substr" when args.Count == 1 => new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [target, this.ToIndex(this.LowerExpr(ctx, args[0], IrType.I32))]),
    "substr" when args.Count == 2 => new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [target, this.ToIndex(this.LowerExpr(ctx, args[0], IrType.I32)), this.ToIndex(this.LowerExpr(ctx, args[1], IrType.I32))]),
    "find" when args.Count == 1 => new IrConvert(IrType.U64, new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [target, this.ConvertTo(this.LowerExpr(ctx, args[0], IrType.String), IrType.String, args[0].Span)]), IrConversion.Wrap),
    "find" when args.Count == 2 => new IrConvert(IrType.U64, new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [target, this.ConvertTo(this.LowerExpr(ctx, args[0], IrType.String), IrType.String, args[0].Span), this.ToIndex(this.LowerExpr(ctx, args[1], IrType.I32))]), IrConversion.Wrap),
    "starts_with" when args.Count == 1 => new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [target, this.ConvertTo(this.LowerExpr(ctx, args[0], IrType.String), IrType.String, args[0].Span)]),
    "ends_with" when args.Count == 1 => new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [target, this.ConvertTo(this.LowerExpr(ctx, args[0], IrType.String), IrType.String, args[0].Span)]),
    "contains" when args.Count == 1 => new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [target, this.ConvertTo(this.LowerExpr(ctx, args[0], IrType.String), IrType.String, args[0].Span)]),
    "compare" when args.Count == 1 => new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [target, this.ConvertTo(this.LowerExpr(ctx, args[0], IrType.String), IrType.String, args[0].Span)]),
    _ => throw this.UnsupportedCall(span, $"string.{method}")
  };

  private IrExpr LowerSpanMethod(FunctionContext ctx, IrExpr target, string method, IReadOnlyList<CppExpr> args, SourceRange span) => method switch {
    "size" when args.Count == 0 => new IrConvert(IrType.U64, new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [target]), IrConversion.Wrap),
    "empty" when args.Count == 0 => new IrBinary(IrBinaryOp.Equal, new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [target]), new IrConst(IrType.I32, 0)),
    "data" when args.Count == 0 => target,
    "front" when args.Count == 0 => new IrArrayLoad(target, new IrConst(IrType.I32, 0)),
    "back" when args.Count == 0 => new IrArrayLoad(target, new IrBinary(IrBinaryOp.Subtract, new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [target]), new IrConst(IrType.I32, 1))),
    "subspan" when args.Count == 1 => new IrIntrinsic(IrIntrinsicOp.Slice, target.Type, [target, this.ToIndex(this.LowerExpr(ctx, args[0], IrType.I32))]),
    "subspan" when args.Count == 2 => new IrIntrinsic(IrIntrinsicOp.Slice, target.Type, [target, this.ToIndex(this.LowerExpr(ctx, args[0], IrType.I32)), this.ToIndex(this.LowerExpr(ctx, args[1], IrType.I32))]),
    "first" when args.Count == 1 => new IrIntrinsic(IrIntrinsicOp.Slice, target.Type, [target, new IrConst(IrType.I32, 0), this.ToIndex(this.LowerExpr(ctx, args[0], IrType.I32))]),
    "last" when args.Count == 1 => this.LowerSpanLast(ctx, target, args[0]),
    _ => throw this.UnsupportedCall(span, $"span.{method}")
  };

  private IrExpr LowerSpanLast(FunctionContext ctx, IrExpr target, CppExpr countExpr) {
    var count = this.ToIndex(this.LowerExpr(ctx, countExpr, IrType.I32));
    var len = new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [target]);
    return new IrIntrinsic(IrIntrinsicOp.Slice, target.Type, [target, new IrBinary(IrBinaryOp.Subtract, len, count), count]);
  }

  private IrExpr LowerArrayMethod(FunctionContext ctx, IrExpr target, string method, IReadOnlyList<CppExpr> args, SourceRange span) => method switch {
    "size" when args.Count == 0 => new IrConvert(IrType.U64, new IrArrayLength(target), IrConversion.Wrap),
    "empty" when args.Count == 0 => new IrBinary(IrBinaryOp.Equal, new IrArrayLength(target), new IrConst(IrType.I32, 0)),
    "data" when args.Count == 0 => target,
    "at" when args.Count == 1 => new IrArrayLoad(target, this.ToIndex(this.LowerExpr(ctx, args[0], IrType.I32))),
    "front" when args.Count == 0 => new IrArrayLoad(target, new IrConst(IrType.I32, 0)),
    "back" when args.Count == 0 => new IrArrayLoad(target, new IrBinary(IrBinaryOp.Subtract, new IrArrayLength(target), new IrConst(IrType.I32, 1))),
    "fill" when args.Count == 1 => new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [target, this.ConvertTo(this.LowerExpr(ctx, args[0], target.Type.Element), target.Type.Element!, args[0].Span)]),
    _ => throw this.UnsupportedCall(span, $"array.{method}")
  };

  private IrExpr LowerListMethod(FunctionContext ctx, IrExpr target, string method, IReadOnlyList<CppExpr> args, SourceRange span) {
    var elem = target.Type.Element!;
    return method switch {
      "size" when args.Count == 0 => new IrConvert(IrType.U64, new IrCollection(IrCollectionOp.ListCount, IrType.I32, [target]), IrConversion.Wrap),
      "empty" when args.Count == 0 => new IrBinary(IrBinaryOp.Equal, new IrCollection(IrCollectionOp.ListCount, IrType.I32, [target]), new IrConst(IrType.I32, 0)),
      "at" when args.Count == 1 => new IrCollection(IrCollectionOp.ListGet, elem, [target, this.ToIndex(this.LowerExpr(ctx, args[0], IrType.I32))]),
      "front" when args.Count == 0 => new IrCollection(IrCollectionOp.ListGet, elem, [target, new IrConst(IrType.I32, 0)]),
      "back" when args.Count == 0 => new IrCollection(IrCollectionOp.ListGet, elem, [target, new IrBinary(IrBinaryOp.Subtract, new IrCollection(IrCollectionOp.ListCount, IrType.I32, [target]), new IrConst(IrType.I32, 1))]),
      "push_back" when args.Count == 1 => new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [target, this.ConvertTo(this.LowerExpr(ctx, args[0], elem), elem, args[0].Span)]),
      "pop_back" when args.Count == 0 => new IrCollection(IrCollectionOp.ListRemoveAt, IrType.Void, [target, new IrBinary(IrBinaryOp.Subtract, new IrCollection(IrCollectionOp.ListCount, IrType.I32, [target]), new IrConst(IrType.I32, 1))]),
      "clear" when args.Count == 0 => new IrCollection(IrCollectionOp.ListClear, IrType.Void, [target]),
      _ => throw this.UnsupportedCall(span, $"vector.{method}")
    };
  }

}
