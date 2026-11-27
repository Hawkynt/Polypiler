using System.Globalization;
using Polypiler.Core.Backends.Go.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Go;

public static partial class GoEmitter {

  private sealed partial class Writer {

    /// <summary>The unsigned integer type of the width of an integer type.</summary>
    private static IrType Unsigned(IrType type) => type.Bits switch {
      8 => IrType.U8,
      16 => IrType.U16,
      32 => IrType.U32,
      _ => IrType.U64
    };

    /// <summary>A value as its unsigned type of the same width: Go's <c>math/bits</c> takes those.</summary>
    private GoExpr AsUnsigned(IrType type, GoExpr value) => type.IsSigned ? this.Conversion(Unsigned(type), value) : this.Typed(value, type);

    /// <summary>A result of Go's <c>int</c> or of an unsigned type converted to the IR's result type.</summary>
    private GoExpr Result(IrType type, GoExpr value) => this.Conversion(type, value);

    /// <summary>
    /// An operation of the IR the backend writes with Go: <c>math/bits</c> for the rotates, counts and
    /// reversals, <c>math</c> for the exact operations on floating-point numbers (an <c>f32</c> widened
    /// and narrowed, which is exact for each), the built-in <c>min</c> and <c>max</c> from Go 1.21, and
    /// the text of strings and integers. Legalization has replaced every other one by the call of its
    /// helper of the library or its portable implementation; what is left without a form is refused.
    /// </summary>
    private GoExpr Intrinsic(IrIntrinsic intrinsic) {
      var type = intrinsic.Arguments.Count > 0 ? intrinsic.Arguments[0].Type : intrinsic.Type;
      var result = intrinsic.Type;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.FloatBits when type.IsFloat: {
          var value = this.Expression(intrinsic.Arguments[0]);
          return this.Result(result, Call(Member("math", type == IrType.F64 ? "Float64bits" : "Float32bits"), this.Typed(value, type)));
        }
        case IrIntrinsicOp.BitsFloat when type.IsInteger: {
          var value = this.Expression(intrinsic.Arguments[0]);
          return type.IsLong
            ? Call(Member("math", "Float64frombits"), this.AsUnsigned(type, value)) with { Type = result }
            : Call(Member("math", "Float32frombits"), this.AsUnsigned(type, value)) with { Type = result };
        }
        case IrIntrinsicOp.Sqrt or IrIntrinsicOp.Abs or IrIntrinsicOp.Floor or IrIntrinsicOp.Ceiling or IrIntrinsicOp.Truncate
          or IrIntrinsicOp.RoundEven or IrIntrinsicOp.RoundAway when type.IsFloat: {
          var value = this.Expression(intrinsic.Arguments[0]);
          var name = intrinsic.Op switch {
            IrIntrinsicOp.Sqrt => "Sqrt",
            IrIntrinsicOp.Abs => "Abs",
            IrIntrinsicOp.Floor => "Floor",
            IrIntrinsicOp.Ceiling => "Ceil",
            IrIntrinsicOp.Truncate => "Trunc",
            IrIntrinsicOp.RoundEven => "RoundToEven",
            _ => "Round"
          };
          return this.Wide(type, wide => Call(Member("math", name), wide(value)));
        }
        case IrIntrinsicOp.RoundHalfUp when type.IsFloat: {
          var value = this.Expression(intrinsic.Arguments[0]);
          return this.Wide(type, wide => this.CallHelper(GoHelpers.RoundHalfUp, IrType.F64, wide(value)));
        }
        case IrIntrinsicOp.Min or IrIntrinsicOp.Max when type.IsFloat: {
          var operands = this.Operands(intrinsic.Arguments);
          var name = intrinsic.Op == IrIntrinsicOp.Min ? "Min" : "Max";
          return this.Wide(type, wide => Call(Member("math", name), wide(operands[0]), wide(operands[1])));
        }
        case IrIntrinsicOp.Sign when type.IsFloat: {
          var value = this.Expression(intrinsic.Arguments[0]);
          var call = this.CallHelper(GoHelpers.Sign, IrType.I32, type == IrType.F32 ? this.Conversion(IrType.F64, value) : value);
          return result == IrType.I32 ? call : this.Result(result, call);
        }
        case IrIntrinsicOp.Min or IrIntrinsicOp.Max when type.IsInteger: {
          var operands = this.Operands(intrinsic.Arguments);
          var minimum = intrinsic.Op == IrIntrinsicOp.Min;
          if (this.Has(GoCapabilities.MinMax))
            return new GoCall(Name(minimum ? "min" : "max"), [this.Typed(operands[0], type), operands[1]]) { Type = result };

          return this.CallHelper(GoHelpers.Bound(minimum, type), result, operands);
        }
        case IrIntrinsicOp.Clamp when type.IsInteger: {
          var operands = this.Operands(intrinsic.Arguments);
          GoExpr Bound(bool minimum, GoExpr a, GoExpr b) => this.Has(GoCapabilities.MinMax)
            ? new GoCall(Name(minimum ? "min" : "max"), [this.Typed(a, type), b]) { Type = result }
            : this.CallHelper(GoHelpers.Bound(minimum, type), result, a, b);
          return Bound(true, Bound(false, operands[0], operands[1]), operands[2]);
        }
        case IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight when type.IsInteger: {
          var operands = this.Operands(intrinsic.Arguments);
          GoExpr count = new GoCall(Name("int"), [operands[1]]);
          if (intrinsic.Op == IrIntrinsicOp.RotateRight)
            count = new GoUnary("-", count);

          var rotated = Call(Member("bits", "RotateLeft" + Width(type)), this.AsUnsigned(type, operands[0]), count);
          return type.IsSigned ? this.Result(result, rotated) : rotated with { Type = result };
        }
        case IrIntrinsicOp.PopCount or IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount when type.IsInteger: {
          var value = this.Expression(intrinsic.Arguments[0]);
          var name = intrinsic.Op switch {
            IrIntrinsicOp.PopCount => "OnesCount",
            IrIntrinsicOp.LeadingZeroCount => "LeadingZeros",
            _ => "TrailingZeros"
          };
          return this.Result(result, Call(Member("bits", name + Width(type)), this.AsUnsigned(type, value)));
        }
        case IrIntrinsicOp.ByteSwap or IrIntrinsicOp.BitReverse when type.IsInteger: {
          var value = this.Expression(intrinsic.Arguments[0]);
          if (intrinsic.Op == IrIntrinsicOp.ByteSwap && type.Bits == 8)
            return value;

          var reversed = Call(Member("bits", (intrinsic.Op == IrIntrinsicOp.ByteSwap ? "ReverseBytes" : "Reverse") + Width(type)), this.AsUnsigned(type, value));
          return type.IsSigned ? this.Result(result, reversed) : reversed with { Type = result };
        }
        case IrIntrinsicOp.ArrayOf when result.IsArray: {
          var operands = this.Operands(intrinsic.Arguments);
          return new GoComposite(this.TypeOf(result), operands) { Type = result, PerLine = operands.Length > 8 ? 8 : 0 };
        }
        case IrIntrinsicOp.StrConcat when intrinsic.Arguments.Count == 2: {
          var operands = this.Operands(intrinsic.Arguments);
          return new GoBinary(operands[0], "+", operands[1]) { Type = IrType.String };
        }
        case IrIntrinsicOp.StrEquals when intrinsic.Arguments.Count == 2: {
          var operands = this.Operands(intrinsic.Arguments);
          return new GoBinary(operands[0], "==", operands[1]) { Type = IrType.Bool };
        }
        case IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma when type.IsInteger && intrinsic.Arguments.Count == 1: {
          var value = this.Expression(intrinsic.Arguments[0]);
          return type.IsSigned
            ? Call(Member("strconv", "FormatInt"), this.Conversion(IrType.I64, value), new GoInt(10)) with { Type = IrType.String }
            : Call(Member("strconv", "FormatUint"), this.Conversion(IrType.U64, value), new GoInt(10)) with { Type = IrType.String };
        }
        case IrIntrinsicOp.FormatBoolDotNet or IrIntrinsicOp.FormatBoolEcma when intrinsic.Arguments.Count == 1: {
          var value = this.Expression(intrinsic.Arguments[0]);
          var (yes, no) = intrinsic.Op == IrIntrinsicOp.FormatBoolDotNet ? ("True", "False") : ("true", "false");
          var name = Name(this.Temporary());
          this._prelude.Add(new GoAssign([name], ":=", [new GoString(no)]));
          this._prelude.Add(new GoIf(null, value, new GoBlock([new GoAssign([name], "=", [new GoString(yes)])]), null));
          return name with { Type = IrType.String };
        }
        default:
          return this.Inexpressible(intrinsic.Op.ToString() + (intrinsic.Arguments.Count > 0 ? " of " + type : ""));
      }
    }

    private static string Width(IrType type) => type.Bits.ToString(CultureInfo.InvariantCulture);

  }

}
