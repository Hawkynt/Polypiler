namespace Polypiler.Permutation.Model;

/// <summary>What an output bit of a <see cref="BitMapping"/> holds (PRD §20.2).</summary>
public enum BitSourceKind {

  /// <summary>An input bit, unchanged.</summary>
  Source,

  /// <summary>An input bit, inverted.</summary>
  InvertedSource,

  /// <summary>The constant 0.</summary>
  Zero,

  /// <summary>The constant 1.</summary>
  One,

  /// <summary>Any value: the bit is unconstrained and excluded from every comparison.</summary>
  DontCare

}
