using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// How each integer and bit operation is written, one family per file. An integer of up to 32
    /// bits is a number and most operations go through a helper told the width, whose result is cut
    /// back to the type; one of 64 bits is a <c>BigInt</c> from ECMAScript 2020 on, cut back with
    /// <c>BigInt.asIntN</c> or <c>BigInt.asUintN</c>, and below that the pair of halves its helpers
    /// compute on. Where ECMAScript has the operation - <c>Math.clz32</c>, <c>Math.abs</c>,
    /// <c>Math.min</c>, the arithmetic of <c>BigInt</c> - it is written as it is.
    /// </summary>
    private static readonly Dictionary<IrIntrinsicOp, Func<Writer, IrIntrinsic, (string Text, bool Atomic)>> _integerForms = Register(
      RotateForms, CountForms, ReorderingForms, PowerForms, WideMultiplyForms, DivisionForms, BoundForms,
      SaturatingForms, CarryForms, OverflowForms, BitFieldForms, MaskForms, EndianForms);

    private static Dictionary<IrIntrinsicOp, Func<Writer, IrIntrinsic, (string Text, bool Atomic)>> Register(
      params IEnumerable<(IrIntrinsicOp Op, Func<Writer, IrIntrinsic, (string Text, bool Atomic)> Form)>[] families)
      => families.SelectMany(family => family).ToDictionary(entry => entry.Op, entry => entry.Form);

    /// <summary>
    /// The integer operation an intrinsic is, written by its family; <see langword="null"/> for an
    /// operation on floating-point numbers, which the emitter writes itself.
    /// </summary>
    private (string Text, bool Atomic)? Integer(IrIntrinsic intrinsic)
      => _integerForms.TryGetValue(intrinsic.Op, out var form) && !intrinsic.Arguments[0].Type.IsFloat ? form(this, intrinsic) : null;

    /// <summary>The width of the type that tells the signatures of an operation apart, as a literal.</summary>
    private static string Bits(IrType type) => type.Bits.ToString(CultureInfo.InvariantCulture);

    /// <summary>The mask of the bits of a type of 8 or 16 bits, as a literal.</summary>
    private static string Mask(IrType type) => $"0x{(1 << type.Bits) - 1:X}";

    /// <summary>A number that holds the bits of a value of a type of up to 32 bits, cut to the type: masked, sign-extended or made a 32-bit integer.</summary>
    private static (string Text, bool Atomic) Cut(IrType type, string text) => (Narrow(type, $"({text})"), false);

    /// <summary>A <c>BigInt</c> cut to its 64-bit type.</summary>
    private static (string Text, bool Atomic) Cut64(IrType type, string text) => Wrap(type, text);

    /// <summary>The 64 bits of a <c>BigInt</c> as an unsigned one, which is how the helpers of 64 bits take it.</summary>
    private string Unsigned64(IrExpr value)
      => value.Type.IsSigned ? $"BigInt.asUintN(64, {this.Text(value)})" : this.Text(value);

    /// <summary>The value of an argument read as its bit pattern of up to 32 bits: as it is, for 32 bits, and masked otherwise.</summary>
    private string Pattern(IrExpr value) => value.Type.Bits == 32 ? this.Text(value) : $"{this.Operand(value)} & {Mask(value.Type)}";

    /// <summary>Whether a 64-bit value is a pair of halves.</summary>
    private bool AsHalves(IrType type) => type.IsLong && this.Halves;

    /// <summary>The signedness of a type as the helpers of halves take it.</summary>
    private static string Signed(IrType type) => type.IsSigned ? "true" : "false";

    /// <summary>The smallest and largest value of a type as literals of its representation.</summary>
    private (string Low, string High) Range(IrType type) => (this.Literal(type, IrOperators.Minimum(type)), this.Literal(type, IrOperators.Maximum(type)));

    /// <summary>A helper on the arguments as they stand.</summary>
    private (string Text, bool Atomic) Call(string helper, IrIntrinsic intrinsic, params string[] further)
      => (this.Helper(helper, [.. intrinsic.Arguments.Select(this.Text), .. further]), true);

    /// <summary>
    /// The result of a helper of the family as the type of the node: a number cut to the type, a
    /// <c>BigInt</c> cut to its 64 bits, or a pair of halves as it is.
    /// </summary>
    private (string Text, bool Atomic) Typed(IrType type, string text)
      => !type.IsLong ? Cut(type, text) : this.Halves ? (text, true) : Cut64(type, text);

  }

}
