using System;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>One input of a fixture with, where it was published, the output it must produce.</summary>
/// <param name="Name">The vector's name, unique in its file.</param>
/// <param name="Input">The input bytes.</param>
/// <param name="Expected">The published output; <see langword="null"/> when only an oracle program knows it.</param>
public sealed record TestVector(string Name, byte[] Input, byte[]? Expected) {

  /// <summary>The vector's name.</summary>
  public string Name { get; } = Name ?? throw new ArgumentNullException(nameof(Name));

  /// <summary>The input bytes.</summary>
  public byte[] Input { get; } = Input ?? throw new ArgumentNullException(nameof(Input));

  /// <summary>The trap the call must end in, in place of an expected output; <see langword="null"/> unless the vector file states one.</summary>
  public TrapClass? ExpectedTrap { get; init; }

}

/// <summary>The hexadecimal text of the vector files and of the oracle programs' standard input and output.</summary>
public static class Hex {

  /// <summary>Bytes as lower-case hexadecimal text without separators; the empty sequence is the empty text.</summary>
  /// <param name="bytes">The bytes.</param>
  public static string Format(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

  /// <summary>Reads hexadecimal text: lower- or upper-case digits, an even number of them, no separators.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The bytes, or <see langword="null"/> when the text is not of that form.</returns>
  public static byte[]? TryParse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length % 2 != 0)
      return null;

    foreach (var c in text)
      if (!char.IsAsciiHexDigit(c))
        return null;

    return Convert.FromHexString(text);
  }

}
