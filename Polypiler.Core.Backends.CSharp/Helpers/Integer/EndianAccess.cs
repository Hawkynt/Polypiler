namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>
  /// The value of <c>size</c> bytes from the index on, the lowest first or the highest first: of 2 and
  /// 4 bytes written out, as cheap as the byte loads it stands for, of 8 bytes one after the other.
  /// </summary>
  public static CSharpHelper Load { get; } = new("Load", "ulong", "byte[] array, int index, int size, bool little", [
    .. Inside,
    "if (size == 2) {",
    "  return little ? (ulong)(array[index] | (array[index + 1] << 8)) : (ulong)((array[index] << 8) | array[index + 1]);",
    "}",
    "if (size == 4) {",
    "  return unchecked(little",
    "    ? (uint)(array[index] | (array[index + 1] << 8) | (array[index + 2] << 16) | (array[index + 3] << 24))",
    "    : (uint)((array[index] << 24) | (array[index + 1] << 16) | (array[index + 2] << 8) | array[index + 3]));",
    "}",
    "ulong value = 0UL;",
    "for (int offset = 0; offset < size; ++offset) {",
    "  value = (value << 8) | array[index + (little ? size - 1 - offset : offset)];",
    "}",
    "return value;"
  ]);

  /// <summary>Writes the lowest <c>size</c> bytes of a value from the index on, the lowest first or the highest first; nothing when they do not fit.</summary>
  public static CSharpHelper Store { get; } = new("Store", "void", "byte[] array, int index, ulong value, int size, bool little", [
    .. Inside,
    "for (int offset = 0; offset < size; ++offset) {",
    "  array[index + (little ? offset : size - 1 - offset)] = unchecked((byte)(value >> (8 * offset)));",
    "}"
  ]);

  /// <summary>The check that every byte lies in the array; the last index is compared without a sum that could overflow.</summary>
  private static string[] Inside => [
    "if (index < 0 || index > array.Length - size) {",
    "  throw new global::System.IndexOutOfRangeException();",
    "}"
  ];

}
