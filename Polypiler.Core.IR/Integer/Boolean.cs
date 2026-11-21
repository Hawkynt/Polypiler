using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// The Boolean functions beyond <c>And</c>, <c>Or</c>, <c>Xor</c> and the complement, bit by bit on
  /// operands of one integer type, which is also the type of the result: the negated ones, the ones with
  /// a complemented operand, the bit select, the majority and the three-input function of an
  /// immediate truth table.
  /// </summary>
  private static IEnumerable<IntegerOperation> Boolean => [
    Bitwise2(IrIntrinsicOp.Nand, Nand),
    Bitwise2(IrIntrinsicOp.Nor, Nor),
    Bitwise2(IrIntrinsicOp.Xnor, Xnor),
    Bitwise2(IrIntrinsicOp.AndNot, AndNot),
    Bitwise2(IrIntrinsicOp.OrNot, OrNot),
    Bitwise2(IrIntrinsicOp.Implies, Implies),
    Same(IrIntrinsicOp.BitSelect, _integers, type => [type, type, type],
      (_, types, arguments) => BitSelect(types[0], Long(arguments, 0), Long(arguments, 1), Long(arguments, 2))),
    Same(IrIntrinsicOp.Majority, _integers, type => [type, type, type],
      (_, types, arguments) => Majority(types[0], Long(arguments, 0), Long(arguments, 1), Long(arguments, 2))),
    Same(IrIntrinsicOp.TernaryLogic, _integers, type => [type, type, type, IrType.I32],
      (_, types, arguments) => TernaryLogic(types[0], Long(arguments, 0), Long(arguments, 1), Long(arguments, 2), Count(arguments, 3)))
  ];

  private static IntegerOperation Bitwise2(IrIntrinsicOp op, System.Func<IrType, long, long, long> evaluate)
    => Same(op, _integers, type => [type, type], (_, types, arguments) => evaluate(types[0], Long(arguments, 0), Long(arguments, 1)));

  /// <summary>The complement of the conjunction, <c>~(a &amp; b)</c>.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="a">The first operand.</param>
  /// <param name="b">The second operand.</param>
  /// <returns>The result.</returns>
  public static long Nand(IrType type, long a, long b) => Value(type, ~(Pattern(type, a) & Pattern(type, b)));

  /// <summary>The complement of the disjunction, <c>~(a | b)</c>.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="a">The first operand.</param>
  /// <param name="b">The second operand.</param>
  /// <returns>The result.</returns>
  public static long Nor(IrType type, long a, long b) => Value(type, ~(Pattern(type, a) | Pattern(type, b)));

  /// <summary>The equivalence, the complement of the exclusive or, <c>~(a ^ b)</c>.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="a">The first operand.</param>
  /// <param name="b">The second operand.</param>
  /// <returns>The result.</returns>
  public static long Xnor(IrType type, long a, long b) => Value(type, ~(Pattern(type, a) ^ Pattern(type, b)));

  /// <summary>The first operand without the bits of the second, <c>a &amp; ~b</c>: the second operand is complemented, unlike x86's <c>ANDN</c>.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="a">The first operand.</param>
  /// <param name="b">The second operand.</param>
  /// <returns>The result.</returns>
  public static long AndNot(IrType type, long a, long b) => Value(type, Pattern(type, a) & ~Pattern(type, b));

  /// <summary>The first operand or the complement of the second, <c>a | ~b</c>.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="a">The first operand.</param>
  /// <param name="b">The second operand.</param>
  /// <returns>The result.</returns>
  public static long OrNot(IrType type, long a, long b) => Value(type, Pattern(type, a) | ~Pattern(type, b));

  /// <summary>The implication, <c>~a | b</c>.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="a">The first operand.</param>
  /// <param name="b">The second operand.</param>
  /// <returns>The result.</returns>
  public static long Implies(IrType type, long a, long b) => Value(type, ~Pattern(type, a) | Pattern(type, b));

  /// <summary>Each bit from <paramref name="a"/> where the mask has a 1, from <paramref name="b"/> where it has a 0: <c>(m &amp; a) | (~m &amp; b)</c>.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="mask">The selecting value.</param>
  /// <param name="a">The value selected by a 1.</param>
  /// <param name="b">The value selected by a 0.</param>
  /// <returns>The selected bits.</returns>
  public static long BitSelect(IrType type, long mask, long a, long b) {
    var m = Pattern(type, mask);
    return Value(type, (m & Pattern(type, a)) | (~m & Pattern(type, b)));
  }

  /// <summary>Each bit set where at least two of the three operands have it set.</summary>
  /// <param name="type">An integer type.</param>
  /// <param name="a">The first operand.</param>
  /// <param name="b">The second operand.</param>
  /// <param name="c">The third operand.</param>
  /// <returns>The majority of every bit.</returns>
  public static long Majority(IrType type, long a, long b, long c) {
    var (x, y, z) = (Pattern(type, a), Pattern(type, b), Pattern(type, c));
    return Value(type, (x & y) | (x & z) | (y & z));
  }

  /// <summary>
  /// The three-input function of a truth table, as <c>VPTERNLOG</c> takes it: bit <c>i</c> of the
  /// result is the bit of <paramref name="table"/> whose index is <c>a_i·4 + b_i·2 + c_i</c>. It is
  /// looked up bit by bit, so that <c>TernaryLogic(0xF0, 0xCC, 0xAA)</c> on 8 bits is the table itself.
  /// </summary>
  /// <param name="type">An integer type.</param>
  /// <param name="a">The first operand, the high bit of the index.</param>
  /// <param name="b">The second operand.</param>
  /// <param name="c">The third operand, the low bit of the index.</param>
  /// <param name="table">The truth table, 0 to 255.</param>
  /// <returns>The function of every bit.</returns>
  /// <exception cref="IrTrapException">The table lies outside 0 to 255: an invalid operation.</exception>
  public static long TernaryLogic(IrType type, long a, long b, long c, int table) {
    Require(table is >= 0 and <= 255);
    var (x, y, z) = (Pattern(type, a), Pattern(type, b), Pattern(type, c));
    var result = 0UL;
    for (var bit = 0; bit < Width(type); ++bit) {
      var index = (int)((((x >> bit) & 1) << 2) | (((y >> bit) & 1) << 1) | ((z >> bit) & 1));
      result |= (ulong)((table >> index) & 1) << bit;
    }

    return Value(type, result);
  }

}
