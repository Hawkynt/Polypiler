using System;
using System.Globalization;

namespace Polypiler.Permutation.Model;

/// <summary>
/// What one output bit of a <see cref="BitMapping"/> holds: an input bit, plain or inverted, a
/// constant, or a don't-care.
/// </summary>
/// <param name="Kind">The kind of the entry.</param>
/// <param name="Index">The input bit read by <see cref="BitSourceKind.Source"/> and <see cref="BitSourceKind.InvertedSource"/>; -1 for the other kinds.</param>
public readonly record struct BitSource(BitSourceKind Kind, int Index) {

  /// <summary>The constant 0.</summary>
  public static BitSource Zero => new(BitSourceKind.Zero, -1);

  /// <summary>The constant 1.</summary>
  public static BitSource One => new(BitSourceKind.One, -1);

  /// <summary>An unconstrained bit.</summary>
  public static BitSource DontCare => new(BitSourceKind.DontCare, -1);

  /// <summary>Input bit <paramref name="index"/>, unchanged.</summary>
  /// <param name="index">The input bit, 0 = least significant.</param>
  public static BitSource From(int index) => new(BitSourceKind.Source, index);

  /// <summary>Input bit <paramref name="index"/>, inverted.</summary>
  /// <param name="index">The input bit, 0 = least significant.</param>
  public static BitSource Not(int index) => new(BitSourceKind.InvertedSource, index);

  /// <summary>Whether the entry reads an input bit, plain or inverted.</summary>
  public bool ReadsInput => this.Kind is BitSourceKind.Source or BitSourceKind.InvertedSource;

  /// <summary>
  /// Whether kind and index fit together: a defined kind, and an index of -1 for a constant or a
  /// don't-care. Whether a read index lies inside the input is a fact of the mapping, not of the entry.
  /// </summary>
  public bool IsWellFormed => this.Kind switch {
    BitSourceKind.Source or BitSourceKind.InvertedSource => true,
    BitSourceKind.Zero or BitSourceKind.One or BitSourceKind.DontCare => this.Index == -1,
    _ => false
  };

  /// <summary>The entry in the text notation of <see cref="BitMapping.Parse"/>: <c>7</c>, <c>~7</c>, <c>=0</c>, <c>=1</c> or <c>?</c>.</summary>
  /// <exception cref="InvalidOperationException">The entry is not <see cref="IsWellFormed">well formed</see>.</exception>
  public override string ToString() => !this.IsWellFormed
    ? throw new InvalidOperationException($"bit source {this.Kind} with index {this.Index} is not well formed")
    : this.Kind switch {
      BitSourceKind.Source => this.Index.ToString(CultureInfo.InvariantCulture),
      BitSourceKind.InvertedSource => "~" + this.Index.ToString(CultureInfo.InvariantCulture),
      BitSourceKind.Zero => "=0",
      BitSourceKind.One => "=1",
      _ => "?"
    };

}
