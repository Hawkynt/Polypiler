using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic.Net;

internal sealed partial class VbNetWriter {

  private const string Ordinal = "System.StringComparison.Ordinal";

  private static VnInvoke Call(string function, params VnExpression[] arguments) => new(new VnName(function), arguments);

  private static VnInvoke Method(VnExpression target, string method, params VnExpression[] arguments) => new(new VnMember(target, method), arguments);

  /// <summary>A code unit of the IR, an unsigned 16-bit integer, as a <c>Char</c>.</summary>
  private static VnInvoke Character(VnExpression unit) => Call("System.Convert.ToChar", unit);

  /// <summary>
  /// An index of an array: an <c>Integer</c> as it is, a narrower integer widened by Visual Basic, a
  /// wider or unsigned one through <c>PlyIndex</c>, which gives an index outside every array for a
  /// value outside <c>Integer</c>, so the access throws as the IR traps.
  /// </summary>
  private VnExpression Index(IrExpr index)
    => index.Type.Kind is IrTypeKind.U32 or IrTypeKind.I64 or IrTypeKind.U64 ? new VnInvoke(this.Helper(VbNetHelpers.Index(this.TypeName(index.Type))), [this.Expression(index)]) : this.Expression(index);

  /// <summary>
  /// The operations of the IR Visual Basic .NET writes itself: the floating-point ones legalization
  /// leaves to the backend, the string operations of <c>System.String</c> with ordinal comparison, the
  /// text of numbers and Booleans in the invariant culture, and the array operations of <c>System.Array</c>.
  /// </summary>
  private VnExpression Intrinsic(IrIntrinsic intrinsic) {
    var op = intrinsic.Op;
    var arguments = intrinsic.Arguments.Select(this.Expression).ToList();
    var first = intrinsic.Arguments.Count > 0 ? intrinsic.Arguments[0].Type : IrType.Void;
    if (first.IsFloat && this.FloatIntrinsic(op, first, arguments) is { } written)
      return written;
    if (first.IsInteger && op is IrIntrinsicOp.Min or IrIntrinsicOp.Max)
      return Call("System.Math." + op, arguments[0], arguments[1]);
    if (first.IsInteger && op == IrIntrinsicOp.Abs)
      return first.IsSigned ? Call("System.Math.Abs", arguments[0]) : arguments[0];

    switch (op) {
      case IrIntrinsicOp.BitsFloat when first == IrType.I64:
        return Call("System.BitConverter.Int64BitsToDouble", arguments[0]);
      case IrIntrinsicOp.BitsFloat when first == IrType.I32:
        return this.Has(VbNetCapabilities.SingleBits) ? Call("System.BitConverter.Int32BitsToSingle", arguments[0])
          : Call("System.BitConverter.ToSingle", Call("System.BitConverter.GetBytes", arguments[0]), new VnLiteral("0"));
      case IrIntrinsicOp.ArrayOf:
        return new VnNewArray(this.TypeName(intrinsic.Type.Element!), null, arguments);
      case IrIntrinsicOp.ArrayCopy:
        return Call("System.Array.Copy", [.. arguments]);
      case IrIntrinsicOp.ArrayClear:
        return new VnInvoke(this.Helper(VbNetHelpers.Clear), arguments);
      case IrIntrinsicOp.ArrayFill:
        return new VnInvoke(this.Helper(VbNetHelpers.Fill), arguments);
      case IrIntrinsicOp.FormatBoolDotNet:
        return new VnTernary(arguments[0], new VnString("True"), new VnString("False"));
      case IrIntrinsicOp.FormatDotNet when first.IsInteger:
        return Method(arguments[0], "ToString", new VnName(Invariant));
      case IrIntrinsicOp.FormatRadix when intrinsic.Arguments is [_, IrConst { Value: 10 }, IrConst { Value: 1 }, _, IrConst { Value: (long)RadixMode.SignMagnitude }]:
        return Method(arguments[0], "ToString", new VnName(Invariant));
      case IrIntrinsicOp.FormatRadix when intrinsic.Arguments is [_, IrConst { Value: 16 }, IrConst { Value: var digits }, IrConst { Value: var upper }, IrConst { Value: (long)RadixMode.TwoComplement }]:
        return Method(arguments[0], "ToString", new VnString((upper != 0 ? "X" : "x") + digits.ToString(CultureInfo.InvariantCulture)), new VnName(Invariant));
      default:
        return this.StringIntrinsic(intrinsic, arguments) ?? this.Inexpressible(op.ToString());
    }
  }

  /// <summary>
  /// A floating-point operation the backend writes: through <c>System.Math</c>, the result of a
  /// <c>Single</c> rounded back with <c>CSng</c>; the minimum and maximum where the framework orders
  /// the zeros, otherwise <c>PlyMin</c>/<c>PlyMax</c>; <see langword="null"/> for any other.
  /// </summary>
  private VnExpression? FloatIntrinsic(IrIntrinsicOp op, IrType type, List<VnExpression> arguments) {
    var single = type == IrType.F32;
    VnExpression Rounded(VnExpression result) => single ? Call("CSng", result) : result;
    switch (op) {
      case IrIntrinsicOp.Sqrt:
        return Rounded(Call("System.Math.Sqrt", arguments[0]));
      case IrIntrinsicOp.Abs:
        return Call("System.Math.Abs", arguments[0]);
      case IrIntrinsicOp.Floor:
        return Rounded(Call("System.Math.Floor", arguments[0]));
      case IrIntrinsicOp.Ceiling:
        return Rounded(Call("System.Math.Ceiling", arguments[0]));
      case IrIntrinsicOp.Truncate:
        return Rounded(Call("System.Math.Truncate", arguments[0]));
      case IrIntrinsicOp.RoundEven:
        return Rounded(Call("System.Math.Round", arguments[0]));
      case IrIntrinsicOp.RoundAway:
        return Rounded(Call("System.Math.Round", arguments[0], new VnName("System.MidpointRounding.AwayFromZero")));
      case IrIntrinsicOp.RoundHalfUp:
        return Rounded(new VnInvoke(this.Helper(VbNetHelpers.RoundHalfUp), [single ? Call("CDbl", arguments[0]) : arguments[0]]));
      case IrIntrinsicOp.Min or IrIntrinsicOp.Max:
        var method = op == IrIntrinsicOp.Min ? "Min" : "Max";
        return this.Has(VbNetCapabilities.SignedZeroMinMax) ? Call("System.Math." + method, arguments[0], arguments[1])
          : new VnInvoke(this.Helper(op == IrIntrinsicOp.Min ? VbNetHelpers.Min : VbNetHelpers.Max), [arguments[0], arguments[1]]);
      case IrIntrinsicOp.Sign:
        return Call("System.Math.Sign", arguments[0]);
      case IrIntrinsicOp.FloatBits when single:
        return this.Has(VbNetCapabilities.SingleBits) ? Call("System.BitConverter.SingleToInt32Bits", arguments[0])
          : Call("System.BitConverter.ToInt32", Call("System.BitConverter.GetBytes", arguments[0]), new VnLiteral("0"));
      case IrIntrinsicOp.FloatBits:
        return Call("System.BitConverter.DoubleToInt64Bits", arguments[0]);
      case IrIntrinsicOp.FormatDotNet when this.Has(VbNetCapabilities.ShortestFloatText):
        return Method(arguments[0], "ToString", new VnName(Invariant));
      default:
        return null;
    }
  }

  /// <summary>A string operation of the IR through <c>System.String</c>, comparing by code unit; <see langword="null"/> for one it has no form of.</summary>
  private VnExpression? StringIntrinsic(IrIntrinsic intrinsic, List<VnExpression> arguments) {
    bool Unit(int index) => intrinsic.Arguments[index].Type == IrType.U16;
    VnExpression Needle(int index) => Unit(index) ? Character(arguments[index]) : arguments[index];
    switch (intrinsic.Op) {
      case IrIntrinsicOp.StrConcat:
        return Call("System.String.Concat", [.. arguments]);
      case IrIntrinsicOp.StrLength:
        return new VnMember(arguments[0], "Length");
      case IrIntrinsicOp.StrCodeUnitAt:
        return Call("System.Convert.ToUInt16", Method(arguments[0], "Chars", arguments[1]));
      case IrIntrinsicOp.StrFromCodeUnit:
        return new VnNew("System.String", [Character(arguments[0]), new VnLiteral("1")]);
      case IrIntrinsicOp.StrRepeat when Unit(0):
        return new VnNew("System.String", [Character(arguments[0]), arguments[1]]);
      case IrIntrinsicOp.StrSubstring:
        return Method(arguments[0], "Substring", [.. arguments.Skip(1)]);
      case IrIntrinsicOp.StrEquals:
        return Call("System.String.Equals", arguments[0], arguments[1]);
      case IrIntrinsicOp.StrCompareOrdinal:
        return Call("System.Math.Sign", Call("System.String.CompareOrdinal", arguments[0], arguments[1]));
      case IrIntrinsicOp.StrIndexOf when Unit(1):
        return Method(arguments[0], "IndexOf", [Needle(1), .. arguments.Skip(2)]);
      case IrIntrinsicOp.StrIndexOf:
        return Method(arguments[0], "IndexOf", [arguments[1], .. arguments.Skip(2), new VnName(Ordinal)]);
      case IrIntrinsicOp.StrStartsWith when !Unit(1):
        return Method(arguments[0], "StartsWith", arguments[1], new VnName(Ordinal));
      case IrIntrinsicOp.StrEndsWith when !Unit(1):
        return Method(arguments[0], "EndsWith", arguments[1], new VnName(Ordinal));
      case IrIntrinsicOp.StrContains when !Unit(1):
        return Method(arguments[0], "Contains", arguments[1]);
      case IrIntrinsicOp.StrContains:
        return new VnBinary(Method(arguments[0], "IndexOf", Needle(1)), ">=", new VnLiteral("0"));
      case IrIntrinsicOp.StrReplaceAll:
        return Method(arguments[0], "Replace", Needle(1), Needle(2));
      case IrIntrinsicOp.StrJoin:
        return Call("System.String.Join", arguments[0], arguments[1]);
      default:
        return null;
    }
  }

}
