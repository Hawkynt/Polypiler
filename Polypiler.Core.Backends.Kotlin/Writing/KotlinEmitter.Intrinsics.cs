using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.IR;
using static Polypiler.Core.Backends.Kotlin.Syntax.Kt;

namespace Polypiler.Core.Backends.Kotlin;

public static partial class KotlinEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The operations of the IR that legalization left to the writer: those Kotlin, its standard
    /// library or <c>java.lang</c> has (<see cref="KotlinLibrary"/>, <see cref="Writes"/>), the arrays
    /// and the strings. Everything else reached here only where legalization did not run, and is refused.
    /// </summary>
    private KtExpression Intrinsic(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      var type = intrinsic.Type.Integral;
      var operand = arguments.Count > 0 ? arguments[0].Type.Integral : IrType.Void;
      KtExpression First() => this.Expression(arguments[0]);
      KtExpression Second() => this.Expression(arguments[1]);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayOf:
          return this.ArrayOf(intrinsic.Type.Element!, arguments);
        case IrIntrinsicOp.ArrayCopy when arguments[0].Type.Element is { } element && KotlinTypes.PrimitiveArray(element) is not null && !element.IsEnum:
          return this.Helper(KotlinHelpers.Copy(element), First(), this.Index(arguments[1]), this.Expression(arguments[2]), this.Index(arguments[3]), this.Index(arguments[4]));
        case IrIntrinsicOp.ArrayFill when arguments[0].Type.Element is { } element:
          return Method(First(), "fill", this.Stored(element, arguments[1]));
        case IrIntrinsicOp.ArrayClear when arguments[0].Type.Element is { } element && KotlinTypes.Scalar(element) is not null:
          return Method(First(), "fill", this.Stored(element, new IrConst(element, 0)));

        case IrIntrinsicOp.Min or IrIntrinsicOp.Max when operand.IsNumeric:
          // minOf and maxOf take every number type; of Float and Double they are Math.min and Math.max: a NaN wins, -0.0 is below 0.0.
          return Call(intrinsic.Op == IrIntrinsicOp.Min ? "minOf" : "maxOf", First(), Second());
        case IrIntrinsicOp.RoundAway when operand.IsFloat:
          return this.Helper(KotlinHelpers.RoundAway(operand), First());
        case IrIntrinsicOp.Sign when operand.IsFloat:
          return this.Helper(KotlinHelpers.Sign(operand), First());
        case IrIntrinsicOp.Abs when operand.IsFloat:
          return Method(Name("Math"), "abs", First());
        case IrIntrinsicOp.Sqrt when operand == IrType.F64:
          return Method(Name("Math"), "sqrt", First());
        case IrIntrinsicOp.Sqrt when operand == IrType.F32:
          // The square root of a binary32 number in binary64, rounded to binary32, is correctly rounded.
          return Method(Method(Name("Math"), "sqrt", Method(First(), "toDouble")), "toFloat");
        case IrIntrinsicOp.Floor or IrIntrinsicOp.Ceiling or IrIntrinsicOp.RoundEven or IrIntrinsicOp.Truncate when operand.IsFloat:
          return this.Rounded(intrinsic.Op, operand, First());
        case IrIntrinsicOp.FusedMultiplyAdd when operand.IsFloat:
          return Method(Name("Math"), "fma", First(), Second(), this.Expression(arguments[2]));
        case IrIntrinsicOp.FloatBits when operand.IsFloat: {
          var bits = Method(First(), "toRawBits");
          return type == (operand == IrType.F64 ? IrType.I64 : IrType.I32) ? bits : Method(bits, KotlinTypes.To(type));
        }
        case IrIntrinsicOp.BitsFloat when type.IsFloat:
          return Method(Name(type == IrType.F64 ? "Double" : "Float"), "fromBits", operand == (type == IrType.F64 ? IrType.I64 : IrType.I32) ? First() : Method(First(), type == IrType.F64 ? "toLong" : "toInt"));

        case IrIntrinsicOp.PopCount when operand.IsWideInteger:
          return Method(First(), "countOneBits");
        case IrIntrinsicOp.LeadingZeroCount when operand.IsWideInteger:
          return Method(First(), "countLeadingZeroBits");
        case IrIntrinsicOp.TrailingZeroCount when operand.IsWideInteger:
          return Method(First(), "countTrailingZeroBits");
        case IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight when operand.IsWideInteger:
          return Method(First(), intrinsic.Op == IrIntrinsicOp.RotateLeft ? "rotateLeft" : "rotateRight", Second());
        case IrIntrinsicOp.ByteSwap or IrIntrinsicOp.BitReverse when operand.IsWideInteger || operand.Bits == 16:
          return this.Reordered(intrinsic.Op == IrIntrinsicOp.ByteSwap ? "reverseBytes" : "reverse", operand, First());
        case IrIntrinsicOp.CarryOut or IrIntrinsicOp.BorrowOut when operand.IsInteger && !operand.IsSigned && !operand.IsLong: {
          // The exact sum or difference with the carry or borrow in, in Long, compared with the range of the type.
          var carry = new KtIfExpression(this.Expression(arguments[2]), Literal("1L"), Literal("0L"));
          var op = intrinsic.Op == IrIntrinsicOp.CarryOut ? "+" : "-";
          var exact = Binary(Binary(Method(First(), "toLong"), op, Method(Second(), "toLong")), op, carry);
          return intrinsic.Op == IrIntrinsicOp.CarryOut
            ? Binary(exact, ">", Literal(((long)IrOperators.Maximum(operand)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "L"))
            : Binary(exact, "<", Literal("0L"));
        }
        case IrIntrinsicOp.HighWord:
          return Method(Binary(First(), operand.IsSigned ? "ushr" : "shr", Int(32)), "toUInt");
        case IrIntrinsicOp.JoinWords:
          // (u32 high, u32 low) -> i64: UInt.toLong() takes the bits without a sign.
          return Binary(Binary(Method(First(), "toLong"), "shl", Int(32)), "or", Method(Second(), "toLong"));
        case IrIntrinsicOp.MulHigh when operand == IrType.I64:
          return Method(Name("Math"), "multiplyHigh", First(), Second());
        case IrIntrinsicOp.MulHigh when operand == IrType.U64:
          return Method(Method(Name("Math"), "unsignedMultiplyHigh", Method(First(), "toLong"), Method(Second(), "toLong")), "toULong");
        case IrIntrinsicOp.ParallelBitExtract or IrIntrinsicOp.ParallelBitDeposit when operand.IsWideInteger:
          return this.Compressed(intrinsic.Op == IrIntrinsicOp.ParallelBitExtract ? "compress" : "expand", operand, First(), Second());

        case IrIntrinsicOp.StrLength:
          return Member(First(), "length");
        case IrIntrinsicOp.StrCodeUnitAt:
          return Method(Member(new KtIndex(First(), this.Index(arguments[1])), "code"), "toUShort");
        case IrIntrinsicOp.StrConcat:
          return arguments.Skip(1).Aggregate(First(), (left, right) => Binary(left, "+", this.Expression(right)));
        case IrIntrinsicOp.StrFromCodeUnit:
          return Method(this.Character(arguments[0]), "toString");
        case IrIntrinsicOp.StrEquals:
          return Binary(First(), "==", Second());
        case IrIntrinsicOp.StrCompareOrdinal:
          // String.compareTo compares the UTF-16 code units in order, a proper prefix first, as the IR's ordinal comparison does.
          return Method(Name("Integer"), "signum", Method(First(), "compareTo", Second()));
        case IrIntrinsicOp.StrStartsWith or IrIntrinsicOp.StrEndsWith or IrIntrinsicOp.StrContains:
          return Method(First(), intrinsic.Op switch { IrIntrinsicOp.StrStartsWith => "startsWith", IrIntrinsicOp.StrEndsWith => "endsWith", _ => "contains" },
            arguments[1].Type.IsString ? Second() : this.Character(arguments[1]));
        default:
          return this.Inexpressible($"the operation {intrinsic.Op} of {(arguments.Count > 0 ? arguments[0].Type : intrinsic.Type)}");
      }
    }

    /// <summary>A code unit as the <c>Char</c> of its number.</summary>
    private KtExpression Character(IrExpr unit) => Call("Char", unit is IrConst constant ? Int(constant.Value) : Method(this.Expression(unit), "toInt"));

    /// <summary>
    /// A rounding of <c>java.lang.Math</c> - <c>floor</c>, <c>ceil</c>, <c>rint</c> (ties to even) -
    /// or the truncation, which is the floor of a positive number and the ceiling of a negative one;
    /// a binary32 number is rounded in binary64, which holds every result exactly.
    /// </summary>
    private KtExpression Rounded(IrIntrinsicOp op, IrType type, KtExpression value) {
      KtExpression wide = type == IrType.F32 ? Method(value, "toDouble") : value;
      KtExpression rounded = op switch {
        IrIntrinsicOp.Floor => Method(Name("Math"), "floor", wide),
        IrIntrinsicOp.Ceiling => Method(Name("Math"), "ceil", wide),
        IrIntrinsicOp.RoundEven => Method(Name("Math"), "rint", wide),
        _ => Call(Member(Member(Name("kotlin"), "math"), "truncate"), wide)
      };
      return type == IrType.F32 ? Method(rounded, "toFloat") : rounded;
    }

    /// <summary>The box class of <c>java.lang</c> of a width, which has the static bit operations.</summary>
    private static KtExpression Box(int bits) => bits switch {
      16 => Member(Member(Name("java"), "lang"), "Short"),
      32 => Name("Integer"),
      _ => Member(Member(Name("java"), "lang"), "Long")
    };

    /// <summary>A reordering of <c>java.lang.Integer</c>, <c>Long</c> or <c>Short</c> - <c>reverseBytes</c>, <c>reverse</c> - an unsigned value through the signed type of its width.</summary>
    private KtExpression Reordered(string name, IrType type, KtExpression value) {
      var carrier = KotlinTypes.Carrier(type);
      var result = Method(Box(type.Bits), name, carrier == type ? value : Method(value, KotlinTypes.To(carrier)));
      return carrier == type ? result : Method(result, KotlinTypes.To(type));
    }

    /// <summary><c>Integer.compress</c> or <c>expand</c> and the <c>Long</c> pair, an unsigned value through the signed type of its width.</summary>
    private KtExpression Compressed(string name, IrType type, KtExpression value, KtExpression mask) {
      var carrier = KotlinTypes.Carrier(type);
      KtExpression Signed(KtExpression operand) => carrier == type ? operand : Method(operand, KotlinTypes.To(carrier));
      var result = Method(Box(type.Bits), name, Signed(value), Signed(mask));
      return carrier == type ? result : Method(result, KotlinTypes.To(type));
    }

  }

}
