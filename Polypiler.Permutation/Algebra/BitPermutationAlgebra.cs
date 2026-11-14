using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Permutation.Algebra;

/// <summary>
/// The group of bit permutations of one width (PRD §20.4; Knuth, TAOCP 1 §1.3.3 and 4A §7.1.3). A
/// permutation <c>p</c> is read as the index function <c>i ↦ p[i]</c>: output bit <c>i</c> reads input
/// bit <c>p[i]</c>. Inversion exists here and nowhere else — only a <see cref="BitPermutation"/> has one,
/// and an untyped <see cref="BitMapping"/> reaches it through <see cref="TryInverse"/> alone. Every
/// operation is a pure function of its arguments.
/// </summary>
public static class BitPermutationAlgebra {

  /// <summary>
  /// The permutation of <c>x ↦ outer(inner(x))</c>: <c>result[i] = inner[outer[i]]</c>. The layout is
  /// kept when both operands share it, otherwise it is the default layout.
  /// </summary>
  /// <param name="outer">The permutation applied second.</param>
  /// <param name="inner">The permutation applied first.</param>
  /// <returns>The composition.</returns>
  /// <exception cref="ArgumentNullException">An operand is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The widths differ (<c>PLY0804</c>).</exception>
  public static BitPermutation Compose(BitPermutation outer, BitPermutation inner) {
    ArgumentNullException.ThrowIfNull(outer);
    ArgumentNullException.ThrowIfNull(inner);
    AlgebraDiagnostics.RequireComposable(inner.Width, outer.Width);

    var outerMap = Indices(outer);
    var innerMap = Indices(inner);
    var result = new int[outerMap.Length];
    for (var bit = 0; bit < result.Length; ++bit)
      result[bit] = innerMap[outerMap[bit]];
    return Build(result, outer.Layout == inner.Layout ? outer.Layout : null);
  }

  /// <summary>The inverse: <c>inverse[p[i]] = i</c>, so that composing it with <paramref name="permutation"/> in either order gives the identity.</summary>
  /// <param name="permutation">The permutation.</param>
  /// <returns>The inverse, with the same layout.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="permutation"/> is <see langword="null"/>.</exception>
  public static BitPermutation Inverse(BitPermutation permutation) {
    ArgumentNullException.ThrowIfNull(permutation);
    var map = Indices(permutation);
    var result = new int[map.Length];
    for (var bit = 0; bit < map.Length; ++bit)
      result[map[bit]] = bit;
    return Build(result, permutation.Layout);
  }

  /// <summary>
  /// The inverse of an untyped mapping, or the reason it has none: the only entry point that inverts a
  /// mapping not yet known to be a bijection.
  /// </summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="inverse">The inverse, or <see langword="null"/>.</param>
  /// <param name="refusal"><c>PLY0803</c> with the reason of rule H1, or <see langword="null"/>.</param>
  /// <returns>Whether the mapping is a bijection.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static bool TryInverse(BitMapping mapping, [NotNullWhen(true)] out BitPermutation? inverse, [NotNullWhen(false)] out Diagnostic? refusal) {
    ArgumentNullException.ThrowIfNull(mapping);
    if (BitSemanticsClassifier.NonBijectivityReason(mapping) is { } reason) {
      inverse = null;
      refusal = Diagnostic.Create(AlgebraDiagnostics.NotInvertible, null, reason);
      return false;
    }

    inverse = Inverse(BitPermutation.From(mapping));
    refusal = null;
    return true;
  }

  /// <summary>
  /// The <paramref name="exponent"/>-th power: the identity for 0, <paramref name="permutation"/>
  /// composed with itself for a positive exponent, the same power of the inverse for a negative one.
  /// It is taken cycle by cycle, so any exponent costs one pass over the bits.
  /// </summary>
  /// <param name="permutation">The permutation.</param>
  /// <param name="exponent">The exponent, any integer.</param>
  /// <returns>The power, with the same layout.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="permutation"/> is <see langword="null"/>.</exception>
  public static BitPermutation Power(BitPermutation permutation, BigInteger exponent) {
    ArgumentNullException.ThrowIfNull(permutation);
    var result = new int[permutation.Width];
    foreach (var cycle in Cycles(permutation)) {
      var shift = (int)(((exponent % cycle.Count) + cycle.Count) % cycle.Count);
      for (var position = 0; position < cycle.Count; ++position)
        result[cycle[position]] = cycle[(position + shift) % cycle.Count];
    }
    return Build(result, permutation.Layout);
  }

  /// <summary>
  /// The cycles of <c>i ↦ p[i]</c>, fixed points included as cycles of length 1: each starts at its
  /// smallest element and continues with <c>p</c> of the previous one, and the cycles are listed in
  /// ascending order of their first element. Every bit lies in exactly one cycle.
  /// </summary>
  /// <param name="permutation">The permutation.</param>
  /// <returns>The cycles.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="permutation"/> is <see langword="null"/>.</exception>
  public static IReadOnlyList<IReadOnlyList<int>> Cycles(BitPermutation permutation) {
    ArgumentNullException.ThrowIfNull(permutation);
    var map = Indices(permutation);
    var seen = new bool[map.Length];
    var cycles = new List<IReadOnlyList<int>>();
    for (var start = 0; start < map.Length; ++start) {
      if (seen[start])
        continue;

      var cycle = new List<int>();
      for (var bit = start; !seen[bit]; bit = map[bit]) {
        seen[bit] = true;
        cycle.Add(bit);
      }
      cycles.Add(cycle.AsReadOnly());
    }
    return cycles.AsReadOnly();
  }

  /// <summary>The order: the least common multiple of the cycle lengths, the smallest positive <c>k</c> with <c>p^k</c> the identity.</summary>
  /// <param name="permutation">The permutation.</param>
  /// <returns>The order, at least 1.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="permutation"/> is <see langword="null"/>.</exception>
  public static BigInteger Order(BitPermutation permutation) {
    var order = BigInteger.One;
    foreach (var cycle in Cycles(permutation))
      order = order / BigInteger.GreatestCommonDivisor(order, cycle.Count) * cycle.Count;
    return order;
  }

  /// <summary>Whether the permutation is its own inverse, <c>p[p[i]] = i</c> for every bit; the identity is one.</summary>
  /// <param name="permutation">The permutation.</param>
  /// <exception cref="ArgumentNullException"><paramref name="permutation"/> is <see langword="null"/>.</exception>
  public static bool IsInvolution(BitPermutation permutation) {
    ArgumentNullException.ThrowIfNull(permutation);
    var map = Indices(permutation);
    for (var bit = 0; bit < map.Length; ++bit)
      if (map[map[bit]] != bit)
        return false;
    return true;
  }

  /// <summary>The index function of a permutation; every entry of a bijection is a plain source.</summary>
  private static int[] Indices(BitPermutation permutation) {
    var map = new int[permutation.Width];
    for (var bit = 0; bit < map.Length; ++bit)
      map[bit] = permutation.Mapping.Outputs[bit].Index;
    return map;
  }

  /// <summary>The permutation of an index function that is known to be a bijection.</summary>
  private static BitPermutation Build(int[] map, BitLayout? layout) {
    var outputs = new BitSource[map.Length];
    for (var bit = 0; bit < map.Length; ++bit)
      outputs[bit] = BitSource.From(map[bit]);
    return BitPermutation.From(BitMapping.Create(map.Length, outputs), layout);
  }

}
