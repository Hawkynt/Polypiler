using System;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Permutation.Algebra;

/// <summary>
/// Composition of bit routings (PRD §20.4). A routing may duplicate, drop, invert or fix bits, so it has
/// no inverse; composing two of them is still exact, entry by entry. Every operation is a pure function
/// of its arguments.
/// </summary>
public static class BitRoutingAlgebra {

  /// <summary>
  /// The routing of <c>x ↦ outer(inner(x))</c>. Each outer entry is resolved against the inner one it
  /// reads: <c>Source(j)</c> becomes <c>inner[j]</c>, <c>InvertedSource(j)</c> the complement of
  /// <c>inner[j]</c> (source ↔ inverted source, <c>=0</c> ↔ <c>=1</c>, <c>?</c> stays <c>?</c>), and a
  /// constant or a don't-care stays. The condition keeps the inner input condition and requires the
  /// outputs the composed mapping does not leave open; the layout is kept when both operands share it,
  /// otherwise it is the default layout.
  /// </summary>
  /// <param name="outer">The routing applied second.</param>
  /// <param name="inner">The routing applied first.</param>
  /// <returns>The composition.</returns>
  /// <exception cref="ArgumentNullException">An operand is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The inner output width is not the outer input width (<c>PLY0804</c>).</exception>
  public static BitRouting Compose(BitRouting outer, BitRouting inner) {
    ArgumentNullException.ThrowIfNull(outer);
    ArgumentNullException.ThrowIfNull(inner);
    AlgebraDiagnostics.RequireComposable(inner.OutputWidth, outer.InputWidth);

    var outputs = new BitSource[outer.OutputWidth];
    for (var bit = 0; bit < outputs.Length; ++bit) {
      var entry = outer.Mapping.Outputs[bit];
      outputs[bit] = entry.Kind switch {
        BitSourceKind.Source => inner.Mapping.Outputs[entry.Index],
        BitSourceKind.InvertedSource => Complement(inner.Mapping.Outputs[entry.Index]),
        _ => entry
      };
    }

    var mapping = BitMapping.Create(inner.InputWidth, outputs);
    var condition = new BitCondition(inner.Condition.InputKnownZero, inner.Condition.InputKnownOne, mapping.RequiredOutputMask);
    return BitRouting.From(mapping, condition, outer.Layout == inner.Layout ? outer.Layout : null);
  }

  /// <summary>The entry that holds the complement of <paramref name="entry"/>; a don't-care stays one.</summary>
  private static BitSource Complement(BitSource entry) => entry.Kind switch {
    BitSourceKind.Source => BitSource.Not(entry.Index),
    BitSourceKind.InvertedSource => BitSource.From(entry.Index),
    BitSourceKind.Zero => BitSource.One,
    BitSourceKind.One => BitSource.Zero,
    _ => entry
  };

}
