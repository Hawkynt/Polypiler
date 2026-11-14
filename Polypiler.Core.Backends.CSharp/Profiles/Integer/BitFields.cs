using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerRows {

  /// <summary>
  /// Bit fields and single bits: a helper in every profile, which no framework has, on the 64 bits of
  /// the pattern and told the width, so that it traps on a field or a position outside it. A single
  /// bit at a position that cannot be outside - a constant inside the width, or a count masked to
  /// it, as the recognition rule O0108 writes it - is the shift and the mask themselves.
  /// </summary>
  private static IEnumerable<CSharpCapability> BitFields => [
    .. _all.SelectMany(kind => (IEnumerable<CSharpCapability>)[
      Helped(IrIntrinsicOp.ExtractBits, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {{2}}, {Digits(Width(kind))})", CSharpIntegerHelpers.ExtractBits),
      Helped(IrIntrinsicOp.InsertBits, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {Pattern(kind, "{1}")}, {{2}}, {{3}}, {Digits(Width(kind))})", CSharpIntegerHelpers.InsertBits),
      Helped(IrIntrinsicOp.TestBit, kind, $"{{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.TestBit)
        .WithTotal(new CSharpForm(null, Template: $"((({{0}} >> {{1}}) & {One(kind)}) != 0)"), arguments => InWidth(arguments[1], kind)),
      Helped(IrIntrinsicOp.SetBit, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.SetBit)
        .WithTotal(new CSharpForm(null, Template: $"({Type(kind)})({{0}} | ({One(kind)} << {{1}}))"), arguments => InWidth(arguments[1], kind)),
      Helped(IrIntrinsicOp.ClearBit, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.ClearBit)
        .WithTotal(new CSharpForm(null, Template: $"({Type(kind)})({{0}} & ~({One(kind)} << {{1}}))"), arguments => InWidth(arguments[1], kind)),
      Helped(IrIntrinsicOp.ToggleBit, kind, $"({Type(kind)}){{h}}({Pattern(kind)}, {{1}}, {Digits(Width(kind))})", CSharpIntegerHelpers.ToggleBit)
        .WithTotal(new CSharpForm(null, Template: $"({Type(kind)})({{0}} ^ ({One(kind)} << {{1}}))"), arguments => InWidth(arguments[1], kind))
    ])
  ];

  /// <summary>The literal one of the type an operator computes in for a type: <c>int</c> for the narrow ones.</summary>
  private static string One(IrTypeKind kind) => kind switch {
    IrTypeKind.U32 => "1u",
    IrTypeKind.I64 => "1L",
    IrTypeKind.U64 => "1UL",
    _ => "1"
  };

  /// <summary>Whether a bit position is inside the width of a type whatever it evaluates to: a constant inside it, or anything masked to it.</summary>
  private static bool InWidth(IrExpr position, IrTypeKind kind) => position switch {
    IrConst constant => constant.Value >= 0 && constant.Value < Width(kind),
    IrBinary { Op: IrBinaryOp.And, Right: IrConst mask } => mask.Value >= 0 && mask.Value < Width(kind),
    _ => false
  };

}
