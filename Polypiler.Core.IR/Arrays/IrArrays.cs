using System.Globalization;

namespace Polypiler.Core.IR;

/// <summary>What the verifier, the interpreter and the lowering share about the indices of an element address (#111).</summary>
public static class IrArrays {

  /// <summary>
  /// Whether an index, carried as the interpreter carries a value of its type, lies in a dimension: extended by its
  /// own type - a <c>u64</c> above 2^63 - 1 is above every lower bound and every upper bound, never negative.
  /// </summary>
  /// <param name="dimension">The dimension.</param>
  /// <param name="type">The integer type of the index.</param>
  /// <param name="value">The index as a 64-bit pattern.</param>
  /// <returns><see langword="true"/> when the index lies from the lower to the upper bound.</returns>
  public static bool Inside(IrDimension dimension, IrType type, long value) => (type.Integral != IrType.U64 || value >= 0) && dimension.Contains(value);

  /// <summary>An index as its type reads, a <c>u64</c> unsigned.</summary>
  /// <param name="type">The integer type of the index.</param>
  /// <param name="value">The index as a 64-bit pattern.</param>
  /// <returns>The digits.</returns>
  public static string Spelled(IrType type, long value)
    => type.Integral == IrType.U64 ? ((ulong)value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

}
