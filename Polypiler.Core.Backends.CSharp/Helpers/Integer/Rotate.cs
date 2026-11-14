namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>The pattern of a value of up to 64 bits rotated left by the count modulo the width.</summary>
  public static CSharpHelper RotateLeftBits { get; } = Rotate("RotateLeftBits", "<<", ">>");

  /// <summary>The pattern of a value of up to 64 bits rotated right by the count modulo the width.</summary>
  public static CSharpHelper RotateRightBits { get; } = Rotate("RotateRightBits", ">>", "<<");

  /// <summary>
  /// A rotation of the pattern in the lowest <c>bits</c> bits. A count that is a multiple of the
  /// width rotates by nothing, which is returned before a shift by the whole width could be needed.
  /// </summary>
  private static CSharpHelper Rotate(string name, string towards, string back) => new(name, "ulong", "ulong value, int count, int bits", [
    "count &= bits - 1;",
    "if (count == 0) {",
    "  return value;",
    "}",
    Mask,
    $"return ((value {towards} count) | (value {back} (bits - count))) & mask;"
  ]);

}
