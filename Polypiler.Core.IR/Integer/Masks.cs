using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// Masks of the lowest bits, the forms of the lowest set bit (Warren, <i>Hacker's Delight</i>,
  /// 2nd ed., §2-1) and the extension of a field at the bottom to the whole width, of every integer
  /// type.
  /// </summary>
  private static IEnumerable<IntegerOperation> Masks => [
    new(IrIntrinsicOp.LowMask, [.. _integers.Select(type => (type, (IReadOnlyList<IrType>)[IrType.I32]))],
      (result, _, arguments) => LowMask(result, Count(arguments, 0)), false),
    Same(IrIntrinsicOp.IsolateLowestSet, _integers, type => [type], (_, types, arguments) => IsolateLowestSet(types[0], Long(arguments, 0))),
    Same(IrIntrinsicOp.ClearLowestSet, _integers, type => [type], (_, types, arguments) => ClearLowestSet(types[0], Long(arguments, 0))),
    Same(IrIntrinsicOp.MaskToLowestSet, _integers, type => [type], (_, types, arguments) => MaskToLowestSet(types[0], Long(arguments, 0))),
    Same(IrIntrinsicOp.SignExtend, _integers, type => [type, IrType.I32], (_, types, arguments) => SignExtend(types[0], Long(arguments, 0), Count(arguments, 1))),
    Same(IrIntrinsicOp.ZeroExtend, _integers, type => [type, IrType.I32], (_, types, arguments) => ZeroExtend(types[0], Long(arguments, 0), Count(arguments, 1)))
  ];

  /// <summary>The value of the type whose lowest <paramref name="count"/> bits are set: 0 for 0, all ones for the width.</summary>
  /// <param name="type">An integer type, the type of the result.</param>
  /// <param name="count">The number of bits, 0 to the width.</param>
  /// <returns>The mask.</returns>
  /// <exception cref="IrTrapException">The count is outside 0 to the width: an invalid operation.</exception>
  public static long LowMask(IrType type, int count) {
    Require(count >= 0 && count <= Width(type));
    return Value(type, Ones(count));
  }

  /// <summary>The lowest set bit of the value alone, <c>x &amp; -x</c>; 0 for 0.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The lowest set bit.</returns>
  public static long IsolateLowestSet(IrType type, long value) {
    var pattern = Pattern(type, value);
    return Value(type, pattern & (~pattern + 1));
  }

  /// <summary>The value with its lowest set bit cleared, <c>x &amp; (x - 1)</c>; 0 for 0.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The value without its lowest set bit.</returns>
  public static long ClearLowestSet(IrType type, long value) {
    var pattern = Pattern(type, value);
    return Value(type, pattern & (pattern - 1));
  }

  /// <summary>The bits up to and including the lowest set bit, <c>x ^ (x - 1)</c>; all ones for 0.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The mask.</returns>
  public static long MaskToLowestSet(IrType type, long value) {
    var pattern = Pattern(type, value);
    return Value(type, pattern ^ (pattern - 1));
  }

  /// <summary>The lowest <paramref name="bits"/> bits of the value with the highest of them copied into every bit above.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="bits">The width of the field, 1 to the width of the type.</param>
  /// <returns>The field extended by its sign.</returns>
  /// <exception cref="IrTrapException">The width of the field is outside 1 to the width of the type: an invalid operation.</exception>
  public static long SignExtend(IrType type, long value, int bits) {
    Require(bits >= 1 && bits <= Width(type));
    return IrOperators.Wrap(type, (long)(Pattern(type, value) << (64 - bits)) >> (64 - bits));
  }

  /// <summary>The lowest <paramref name="bits"/> bits of the value with zeros above them.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="value">The value.</param>
  /// <param name="bits">The width of the field, 1 to the width of the type.</param>
  /// <returns>The field extended by zeros.</returns>
  /// <exception cref="IrTrapException">The width of the field is outside 1 to the width of the type: an invalid operation.</exception>
  public static long ZeroExtend(IrType type, long value, int bits) {
    Require(bits >= 1 && bits <= Width(type));
    return Value(type, Pattern(type, value) & Ones(bits));
  }

}
