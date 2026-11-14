using System;
using System.Diagnostics.CodeAnalysis;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// A bit permutation (PRD §20.2; Knuth, TAOCP 4A §7.1.3): a square mapping whose outputs are plain
/// sources and which reads every input exactly once. It is the only class that can be inverted and
/// composed as a bijection; it exists only where <see cref="BitMapping.IsBijective"/> holds (rule H1).
/// </summary>
public sealed class BitPermutation : IBitSemantics {

  private BitPermutation(BitMapping mapping, BitLayout layout) {
    this.Mapping = mapping;
    this.Layout = layout;
  }

  /// <summary>The width of input and output.</summary>
  public int Width => this.Mapping.InputWidth;

  /// <summary>The bijective mapping.</summary>
  public BitMapping Mapping { get; }

  /// <inheritdoc/>
  public int InputWidth => this.Width;

  /// <inheritdoc/>
  public int OutputWidth => this.Width;

  /// <inheritdoc/>
  public BitLayout Layout { get; }

  BitSemanticClass IBitSemantics.DeclaredClass => BitSemanticClass.Permutation;

  /// <summary>Declares a mapping a permutation.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="layout">The lanes; <see cref="BitLayout.Default"/> when <see langword="null"/>.</param>
  /// <returns>The permutation.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The mapping is not a bijection (<c>PLY0805</c>, rule H1).</exception>
  public static BitPermutation From(BitMapping mapping, BitLayout? layout = null) {
    ArgumentNullException.ThrowIfNull(mapping);
    foreach (var diagnostic in BitSemanticsVerifier.H1(mapping))
      throw new PermutationModelException(diagnostic);

    return new(mapping, layout ?? BitLayout.Default(mapping.InputWidth, mapping.OutputWidth));
  }

  /// <summary>Declares a mapping a permutation, or reports why it is none.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="diagnostics">Receives <c>PLY0805</c> (rule H1) when the mapping is not a bijection.</param>
  /// <param name="permutation">The permutation, or <see langword="null"/>.</param>
  /// <param name="layout">The lanes; <see cref="BitLayout.Default"/> when <see langword="null"/>.</param>
  /// <returns>Whether the mapping is a permutation.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> or <paramref name="diagnostics"/> is <see langword="null"/>.</exception>
  public static bool TryFrom(BitMapping mapping, DiagnosticBag diagnostics, [NotNullWhen(true)] out BitPermutation? permutation, BitLayout? layout = null) {
    ArgumentNullException.ThrowIfNull(mapping);
    ArgumentNullException.ThrowIfNull(diagnostics);
    try {
      permutation = From(mapping, layout);
      return true;
    } catch (PermutationModelException refusal) {
      diagnostics.Report(refusal.Diagnostic);
      permutation = null;
      return false;
    }
  }

  /// <summary>The mapping in its text notation.</summary>
  public override string ToString() => this.Mapping.ToString();

}
