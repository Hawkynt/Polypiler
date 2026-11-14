// Floating-point numbers in byte buffers: binary32 and binary64 read and written in either byte
// order through BinaryPrimitives and BitConverter, and the sample block of a WAV file of IEEE float
// samples (format tag 3, 32 bits per sample) summed as a double. Written for this repository.
using System;
using System.Buffers.Binary;

namespace Polypiler.Fixtures.Prototype;

public static class FloatView {

  public static float GetF32LE(byte[] bytes, int i) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i));

  public static float GetF32BE(byte[] bytes, int i) => BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i, 4));

  public static double GetF64LE(byte[] bytes, int i) => BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i));

  public static double GetF64BE(byte[] bytes, int i) => BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(i, 8));

  public static float ToSingle(byte[] bytes, int i) => BitConverter.ToSingle(bytes, i);

  public static double ToDouble(byte[] bytes, int i) => BitConverter.ToDouble(bytes, i);

  public static byte[] PutF32LE(byte[] bytes, int i, double x) {
    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i), (float)x);
    return bytes;
  }

  public static byte[] PutF32BE(byte[] bytes, int i, double x) {
    BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(i, 4), (float)x);
    return bytes;
  }

  public static byte[] PutF64LE(byte[] bytes, int i, double x) {
    BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i), x);
    return bytes;
  }

  public static byte[] PutF64BE(byte[] bytes, int i, double x) {
    BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(i, 8), x);
    return bytes;
  }

  // The canonical 44-byte header: "RIFF", the format chunk at 12 with the format tag at 20 and the
  // bits per sample at 34, the size of the data chunk at 40 and its samples from 44 on. A file of
  // another format, or one shorter than its data chunk says, has no sum: -1.
  public static double SumSamples(byte[] wav) {
    if (wav.Length < 44 || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)) != 3 || BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)) != 32)
      return -1.0;

    int size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
    if (size < 0 || size > wav.Length - 44)
      return -1.0;

    double sum = 0.0;
    for (int i = 44; i + 4 <= 44 + size; i += 4)
      sum += BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(i));

    return sum;
  }

}
