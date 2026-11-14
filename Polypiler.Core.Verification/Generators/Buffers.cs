using System;

namespace Polypiler.Core.Verification.Generators;

/// <summary>Byte buffers and the shapes of strided arrays, drawn from a <see cref="SeededRandom"/>.</summary>
public static class Buffers {

  /// <summary>A buffer of 0…<paramref name="maxLength"/> random bytes; the empty buffer is one of the lengths.</summary>
  /// <exception cref="ArgumentNullException"><paramref name="random"/> is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative or beyond <see cref="Array.MaxLength"/>.</exception>
  public static byte[] Bytes(SeededRandom random, int maxLength) {
    ArgumentNullException.ThrowIfNull(random);
    ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, Array.MaxLength);
    var bytes = new byte[random.NextBelow(maxLength + 1)];
    for (var index = 0; index < bytes.Length; ++index)
      bytes[index] = (byte)random.NextBits(8);

    return bytes;
  }

  /// <summary>
  /// The shape of an array walked with a stride: a length of 0…<paramref name="maxLength"/> elements
  /// and a stride of 1…<paramref name="maxStride"/>.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="random"/> is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative or beyond <see cref="Array.MaxLength"/>, or <paramref name="maxStride"/> is below 1.</exception>
  public static (int Length, int Stride) Shape(SeededRandom random, int maxLength, int maxStride) {
    ArgumentNullException.ThrowIfNull(random);
    ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, Array.MaxLength);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxStride, 1);
    var length = random.NextBelow(maxLength + 1);
    return (length, random.NextBelow(maxStride) + 1);
  }

}
