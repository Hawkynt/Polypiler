using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
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
    private static readonly Dictionary<IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression Node, bool Atomic)>> _integerForms = Register(
      RotateForms, CountForms, ReorderingForms, PowerForms, WideMultiplyForms, DivisionForms, BoundForms,
      SaturatingForms, CarryForms, OverflowForms, BitFieldForms, MaskForms, EndianForms);

    private static Dictionary<IrIntrinsicOp, Func<Writer, IrIntrinsic, (JsExpression Node, bool Atomic)>> Register(
      params IEnumerable<(IrIntrinsicOp Op, Func<Writer, IrIntrinsic, (JsExpression Node, bool Atomic)> Form)>[] families)
      => families.SelectMany(family => family).ToDictionary(entry => entry.Op, entry => entry.Form);

    /// <summary>
    /// The integer operation an intrinsic is, written by its family; <see langword="null"/> for an
    /// operation on floating-point numbers, which the emitter writes itself.
    /// </summary>
    private (JsExpression Node, bool Atomic)? Integer(IrIntrinsic intrinsic)
      => _integerForms.TryGetValue(intrinsic.Op, out var form) && !intrinsic.Arguments[0].Type.IsFloat ? form(this, intrinsic) : null;

    /// <summary>The width of the type that tells the signatures of an operation apart, as a literal.</summary>
    private static JsNumber Bits(IrType type) => Number(type.Bits.ToString(CultureInfo.InvariantCulture));

    /// <summary>The mask of the bits of a type of 8 or 16 bits, as a literal.</summary>
    private static JsNumber Mask(IrType type) => Number($"0x{(1 << type.Bits) - 1:X}");

    /// <summary>A number that holds the bits of a value of a type of up to 32 bits, cut to the type: masked, sign-extended or made a 32-bit integer.</summary>
    private static (JsExpression Node, bool Atomic) Cut(IrType type, JsExpression value) => (Narrow(type, Parenthesized(value)), false);

    /// <summary>A <c>BigInt</c> cut to its 64-bit type.</summary>
    private static (JsExpression Node, bool Atomic) Cut64(IrType type, JsExpression value) => Wrap(type, value);

    /// <summary>The 64 bits of a <c>BigInt</c> as an unsigned one, which is how the helpers of 64 bits take it.</summary>
    private JsExpression Unsigned64(IrExpr value)
      => value.Type.IsSigned ? Invoke("BigInt.asUintN", Number("64"), this.Text(value)) : this.Text(value);

    /// <summary>The value of an argument read as its bit pattern of up to 32 bits: as it is, for 32 bits, and masked otherwise.</summary>
    private JsExpression Pattern(IrExpr value) => value.Type.Bits == 32 ? this.Text(value) : Infix(this.Operand(value), "&", Mask(value.Type));

    /// <summary>Whether a 64-bit value is a pair of halves.</summary>
    private bool AsHalves(IrType type) => type.IsLong && this.Halves;

    /// <summary>The signedness of a type as the helpers of halves take it.</summary>
    private static JsBoolean Signed(IrType type) => new(type.IsSigned);

    /// <summary>The smallest and largest value of a type as literals of its representation.</summary>
    private (JsExpression Low, JsExpression High) Range(IrType type) => (this.Literal(type, IrOperators.Minimum(type)), this.Literal(type, IrOperators.Maximum(type)));

    /// <summary>A helper on the arguments as they stand.</summary>
    private (JsExpression Node, bool Atomic) Call(string helper, IrIntrinsic intrinsic, params JsExpression[] further)
      => (this.Helper(helper, [.. intrinsic.Arguments.Select(this.Text), .. further]), true);

    /// <summary>
    /// The result of a helper of the family as the type of the node: a number cut to the type, a
    /// <c>BigInt</c> cut to its 64 bits, or a pair of halves as it is.
    /// </summary>
    private (JsExpression Node, bool Atomic) Typed(IrType type, JsExpression value)
      => !type.IsLong ? Cut(type, value) : this.Halves ? (value, true) : Cut64(type, value);

  }

}
