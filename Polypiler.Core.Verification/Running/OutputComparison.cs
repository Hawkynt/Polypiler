using System;

namespace Polypiler.Core.Verification.Running;

/// <summary>How <see cref="OutputComparison"/> compares two outputs.</summary>
public enum ComparisonMode {

  /// <summary>Byte for byte.</summary>
  Exact,

  /// <summary>Byte for byte after every <c>\r\n</c> and every lone <c>\r</c> has become <c>\n</c> in both.</summary>
  NormalizedNewlines

}

/// <summary>
/// The result of comparing an expected output with an actual one: equal, or the first offset at which
/// they differ with the byte each has there.
/// </summary>
/// <param name="IsEqual">Whether the two are equal.</param>
/// <param name="Offset">The first offset at which they differ, counted in the compared form; -1 when they are equal.</param>
/// <param name="Expected">The expected byte at that offset; <see langword="null"/> when the expected output ends before it.</param>
/// <param name="Actual">The actual byte at that offset; <see langword="null"/> when the actual output ends before it.</param>
public sealed record ComparisonResult(bool IsEqual, long Offset, byte? Expected, byte? Actual) {

  /// <summary>The two outputs are equal.</summary>
  public static ComparisonResult Equal { get; } = new(true, -1, null, null);

  /// <summary>The result as a test reports it.</summary>
  public override string ToString()
    => this.IsEqual ? "Equal" : $"differs at byte offset {this.Offset}: expected {Hex(this.Expected)}, actual {Hex(this.Actual)}";

  private static string Hex(byte? value) => value is { } b ? $"0x{b:X2}" : "end of output";

}

/// <summary>Compares captured output with an expected value, as bytes: output is never decoded as text first.</summary>
public static class OutputComparison {

  /// <summary>Compares two outputs.</summary>
  /// <param name="expected">The output expected.</param>
  /// <param name="actual">The output captured.</param>
  /// <param name="mode">Whether line ends count as they are or are normalized first.</param>
  /// <returns><see cref="ComparisonResult.Equal"/>, or where the two first differ.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The mode is not one of <see cref="ComparisonMode"/>.</exception>
  public static ComparisonResult Compare(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, ComparisonMode mode) {
    switch (mode) {
      case ComparisonMode.Exact:
        return CompareBytes(expected, actual);
      case ComparisonMode.NormalizedNewlines:
        return CompareBytes(NormalizeNewlines(expected), NormalizeNewlines(actual));
      default:
        throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown comparison mode");
    }
  }

  /// <summary>Turns every <c>\r\n</c> and every lone <c>\r</c> into <c>\n</c>.</summary>
  /// <param name="bytes">The output.</param>
  /// <returns>A copy with normalized line ends.</returns>
  public static byte[] NormalizeNewlines(ReadOnlySpan<byte> bytes) {
    var result = new byte[bytes.Length];
    var length = 0;
    for (var index = 0; index < bytes.Length; ++index) {
      var value = bytes[index];
      if (value == '\r') {
        result[length++] = (byte)'\n';
        if (index + 1 < bytes.Length && bytes[index + 1] == '\n')
          ++index;
      } else
        result[length++] = value;
    }

    return result[..length];
  }

  private static ComparisonResult CompareBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) {
    var common = expected.CommonPrefixLength(actual);
    if (common == expected.Length && common == actual.Length)
      return ComparisonResult.Equal;

    return new(false, common, common < expected.Length ? expected[common] : null, common < actual.Length ? actual[common] : null);
  }

}
