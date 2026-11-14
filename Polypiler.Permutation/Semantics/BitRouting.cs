using System;
using System.Diagnostics.CodeAnalysis;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// A bit routing (PRD §20.2): every output is an input bit, an inverted input bit, a constant or a
/// don't-care; inputs may be duplicated or dropped and the widths may differ, so a routing is not
/// invertible in general and has no inverse. It holds on the inputs its <see cref="Condition"/> admits.
/// </summary>
public sealed class BitRouting : IBitSemantics {

  private BitRouting(BitMapping mapping, BitCondition condition, BitLayout layout) {
    this.Mapping = mapping;
    this.Condition = condition;
    this.Layout = layout;
  }

  /// <summary>The mapping.</summary>
  public BitMapping Mapping { get; }

  /// <summary>The input domain and the required outputs; rules H2 and H3 of <see cref="BitSemanticsVerifier"/> check it.</summary>
  public BitCondition Condition { get; }

  /// <inheritdoc/>
  public int InputWidth => this.Mapping.InputWidth;

  /// <inheritdoc/>
  public int OutputWidth => this.Mapping.OutputWidth;

  /// <inheritdoc/>
  public BitLayout Layout { get; }

  BitSemanticClass IBitSemantics.DeclaredClass => BitSemanticClass.Routing;

  /// <summary>Declares a mapping a routing; every mapping is one. The condition is checked by <see cref="BitSemanticsVerifier"/>, not here.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="condition">The condition; <see cref="BitCondition.ForMapping"/> when <see langword="null"/>.</param>
  /// <param name="layout">The lanes; <see cref="BitLayout.Default"/> when <see langword="null"/>.</param>
  /// <returns>The routing.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static BitRouting From(BitMapping mapping, BitCondition? condition = null, BitLayout? layout = null) {
    ArgumentNullException.ThrowIfNull(mapping);
    return new(mapping, condition ?? BitCondition.ForMapping(mapping), layout ?? BitLayout.Default(mapping.InputWidth, mapping.OutputWidth));
  }

  /// <summary>Widens a permutation to a routing of the same mapping and lanes, with the condition the mapping implies; nothing is lost.</summary>
  /// <param name="permutation">The permutation.</param>
  /// <exception cref="ArgumentNullException"><paramref name="permutation"/> is <see langword="null"/>.</exception>
  public static implicit operator BitRouting(BitPermutation permutation) {
    ArgumentNullException.ThrowIfNull(permutation);
    return new(permutation.Mapping, BitCondition.ForMapping(permutation.Mapping), permutation.Layout);
  }

  /// <summary>
  /// Narrows the routing to a permutation by rule H1 alone: it succeeds exactly when the mapping is a
  /// bijection. A bijection is defined on every input, so it agrees with the routing on the domain of
  /// any condition; the condition itself is not carried over.
  /// </summary>
  /// <param name="permutation">The permutation, or <see langword="null"/>.</param>
  /// <param name="reason">Why the mapping is no bijection, or <see langword="null"/>.</param>
  /// <returns>Whether the routing is a permutation.</returns>
  public bool TryAsPermutation([NotNullWhen(true)] out BitPermutation? permutation, [NotNullWhen(false)] out string? reason) {
    reason = BitSemanticsClassifier.NonBijectivityReason(this.Mapping);
    permutation = reason is null ? BitPermutation.From(this.Mapping, this.Layout) : null;
    return permutation is not null;
  }

  /// <summary>The mapping in its text notation.</summary>
  public override string ToString() => this.Mapping.ToString();

}
