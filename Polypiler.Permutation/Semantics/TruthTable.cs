using System;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// Truth tables of <see cref="BoolExpr"/> over at most <see cref="BitSemanticsDiagnostics.TruthTableLimit"/>
/// inputs, bit-sliced: bit <c>x</c> of the table is the value of the expression for input <c>x</c>, 64
/// inputs to a word. An input bit at or above the width reads 0.
/// </summary>
internal static class TruthTable {

  /// <summary>Input bit <c>j</c> for <c>j &lt; 6</c> over the 64 inputs of one word.</summary>
  private static readonly ulong[] _lowInputs = [
    0xAAAAAAAAAAAAAAAAUL, 0xCCCCCCCCCCCCCCCCUL, 0xF0F0F0F0F0F0F0F0UL,
    0xFF00FF00FF00FF00UL, 0xFFFF0000FFFF0000UL, 0xFFFFFFFF00000000UL
  ];

  /// <summary>The table of an expression over <paramref name="width"/> inputs, 1 to 16.</summary>
  internal static ulong[] Evaluate(BoolExpr expression, int width) {
    var words = width >= 6 ? 1 << (width - 6) : 1;
    var mask = width >= 6 ? ulong.MaxValue : (1UL << (1 << width)) - 1;
    var table = Evaluate(expression, width, words);
    for (var word = 0; word < words; ++word)
      table[word] &= mask;
    return table;
  }

  private static ulong[] Evaluate(BoolExpr expression, int width, int words) {
    var table = new ulong[words];
    switch (expression) {
      case BoolExpr.Input input when input.Index < width:
        for (var word = 0; word < words; ++word)
          table[word] = input.Index < 6 ? _lowInputs[input.Index] : ((word >> (input.Index - 6)) & 1) != 0 ? ulong.MaxValue : 0;
        break;
      case BoolExpr.Input:
        break;
      case BoolExpr.Const constant:
        Array.Fill(table, constant.Value ? ulong.MaxValue : 0);
        break;
      case BoolExpr.Not not:
        var operand = Evaluate(not.Operand, width, words);
        for (var word = 0; word < words; ++word)
          table[word] = ~operand[word];
        break;
      case BoolExpr.And and:
        Combine(table, Evaluate(and.Left, width, words), Evaluate(and.Right, width, words), static (a, b) => a & b);
        break;
      case BoolExpr.Or or:
        Combine(table, Evaluate(or.Left, width, words), Evaluate(or.Right, width, words), static (a, b) => a | b);
        break;
      default:
        var xor = (BoolExpr.Xor)expression;
        Combine(table, Evaluate(xor.Left, width, words), Evaluate(xor.Right, width, words), static (a, b) => a ^ b);
        break;
    }
    return table;
  }

  private static void Combine(ulong[] table, ulong[] left, ulong[] right, Func<ulong, ulong, ulong> operation) {
    for (var word = 0; word < table.Length; ++word)
      table[word] = operation(left[word], right[word]);
  }

  /// <summary>
  /// Holds a folded output to the truth table of the expression it came from. A difference means the
  /// folding is wrong, which is an internal error and never resolved by picking one of the two answers.
  /// </summary>
  /// <exception cref="PermutationModelException">The two tables differ (<c>PLY0001</c>).</exception>
  internal static void CrossCheck(BoolExpr original, BoolExpr folded, int width, int output) {
    if (Evaluate(original, width).AsSpan().SequenceEqual(Evaluate(folded, width)))
      return;

    throw new PermutationModelException(Diagnostic.Create(DiagnosticDescriptor.InternalError, null,
      $"output bit {output}: {original} folds to {folded}, which disagrees with its truth table over {width} inputs"));
  }

}
