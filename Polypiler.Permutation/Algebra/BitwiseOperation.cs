namespace Polypiler.Permutation.Algebra;

/// <summary>A bitwise operation a mapping may distribute over; see <see cref="Distribution"/>.</summary>
public enum BitwiseOperation {

  /// <summary><c>a &amp; b</c>.</summary>
  And,

  /// <summary><c>a | b</c>.</summary>
  Or,

  /// <summary><c>a ^ b</c>.</summary>
  Xor,

  /// <summary><c>~a</c>, the complement within the width; the second operand is unused.</summary>
  Not

}
