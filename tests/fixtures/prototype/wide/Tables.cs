// Constants and tables: const and static readonly fields of every scalar type and arrays of every
// element type, of the lengths 0, 1 and 256, read by element, by length and with foreach; arrays built
// from element lists inside a function; and Array.Copy, Array.Fill and Array.Clear. Written for this
// repository.
using System;

namespace Polypiler.Fixtures.Prototype;

public static class Tables {

  private const int Limit = 4;
  public const uint Mask = 0xFF;
  internal const double Tenth = 0.1;
  private const long Wide = 1L << 40;
  private static readonly int Offset = 3;
  private static readonly double Half = 0.5;
  private static readonly bool Enabled = true;
  private static readonly ulong Largest = ulong.MaxValue;
  private static readonly float Third = 1f / 3f;
  private static readonly char Letter = 'x', Digit = '7';

  private static readonly byte[] Nothing = { };
  private static readonly byte[] One = { 42 };
  private static readonly ushort[] Squares = [0, 1, 4, 9, 16];
  private static readonly long[] Powers = new long[] { 1, 1L << 20, 1L << 40, long.MinValue };
  private static readonly ulong[] Large = new[] { ulong.MaxValue, 1UL };
  private static readonly sbyte[] Signed = { -128, -1, 0, 127 };
  private static readonly short[] Shorts = new short[3] { -32768, 0, 32767 };
  private static readonly char[] Letters = { 'a', 'b', 'c' };
  private static readonly float[] Weights = { 0.1f, 0.25f, float.MaxValue };
  private static readonly double[] Roots = [1.4142135623730951, double.NaN, -0.0, double.PositiveInfinity];
  private static readonly int[] Signs = { -1, 0, Limit - 3 };
  private static readonly uint[] Ramp = {
    0x00000000, 0x01010101, 0x02020202, 0x03030303, 0x04040404, 0x05050505,
    0x06060606, 0x07070707, 0x08080808, 0x09090909, 0x0A0A0A0A, 0x0B0B0B0B,
    0x0C0C0C0C, 0x0D0D0D0D, 0x0E0E0E0E, 0x0F0F0F0F, 0x10101010, 0x11111111,
    0x12121212, 0x13131313, 0x14141414, 0x15151515, 0x16161616, 0x17171717,
    0x18181818, 0x19191919, 0x1A1A1A1A, 0x1B1B1B1B, 0x1C1C1C1C, 0x1D1D1D1D,
    0x1E1E1E1E, 0x1F1F1F1F, 0x20202020, 0x21212121, 0x22222222, 0x23232323,
    0x24242424, 0x25252525, 0x26262626, 0x27272727, 0x28282828, 0x29292929,
    0x2A2A2A2A, 0x2B2B2B2B, 0x2C2C2C2C, 0x2D2D2D2D, 0x2E2E2E2E, 0x2F2F2F2F,
    0x30303030, 0x31313131, 0x32323232, 0x33333333, 0x34343434, 0x35353535,
    0x36363636, 0x37373737, 0x38383838, 0x39393939, 0x3A3A3A3A, 0x3B3B3B3B,
    0x3C3C3C3C, 0x3D3D3D3D, 0x3E3E3E3E, 0x3F3F3F3F, 0x40404040, 0x41414141,
    0x42424242, 0x43434343, 0x44444444, 0x45454545, 0x46464646, 0x47474747,
    0x48484848, 0x49494949, 0x4A4A4A4A, 0x4B4B4B4B, 0x4C4C4C4C, 0x4D4D4D4D,
    0x4E4E4E4E, 0x4F4F4F4F, 0x50505050, 0x51515151, 0x52525252, 0x53535353,
    0x54545454, 0x55555555, 0x56565656, 0x57575757, 0x58585858, 0x59595959,
    0x5A5A5A5A, 0x5B5B5B5B, 0x5C5C5C5C, 0x5D5D5D5D, 0x5E5E5E5E, 0x5F5F5F5F,
    0x60606060, 0x61616161, 0x62626262, 0x63636363, 0x64646464, 0x65656565,
    0x66666666, 0x67676767, 0x68686868, 0x69696969, 0x6A6A6A6A, 0x6B6B6B6B,
    0x6C6C6C6C, 0x6D6D6D6D, 0x6E6E6E6E, 0x6F6F6F6F, 0x70707070, 0x71717171,
    0x72727272, 0x73737373, 0x74747474, 0x75757575, 0x76767676, 0x77777777,
    0x78787878, 0x79797979, 0x7A7A7A7A, 0x7B7B7B7B, 0x7C7C7C7C, 0x7D7D7D7D,
    0x7E7E7E7E, 0x7F7F7F7F, 0x80808080, 0x81818181, 0x82828282, 0x83838383,
    0x84848484, 0x85858585, 0x86868686, 0x87878787, 0x88888888, 0x89898989,
    0x8A8A8A8A, 0x8B8B8B8B, 0x8C8C8C8C, 0x8D8D8D8D, 0x8E8E8E8E, 0x8F8F8F8F,
    0x90909090, 0x91919191, 0x92929292, 0x93939393, 0x94949494, 0x95959595,
    0x96969696, 0x97979797, 0x98989898, 0x99999999, 0x9A9A9A9A, 0x9B9B9B9B,
    0x9C9C9C9C, 0x9D9D9D9D, 0x9E9E9E9E, 0x9F9F9F9F, 0xA0A0A0A0, 0xA1A1A1A1,
    0xA2A2A2A2, 0xA3A3A3A3, 0xA4A4A4A4, 0xA5A5A5A5, 0xA6A6A6A6, 0xA7A7A7A7,
    0xA8A8A8A8, 0xA9A9A9A9, 0xAAAAAAAA, 0xABABABAB, 0xACACACAC, 0xADADADAD,
    0xAEAEAEAE, 0xAFAFAFAF, 0xB0B0B0B0, 0xB1B1B1B1, 0xB2B2B2B2, 0xB3B3B3B3,
    0xB4B4B4B4, 0xB5B5B5B5, 0xB6B6B6B6, 0xB7B7B7B7, 0xB8B8B8B8, 0xB9B9B9B9,
    0xBABABABA, 0xBBBBBBBB, 0xBCBCBCBC, 0xBDBDBDBD, 0xBEBEBEBE, 0xBFBFBFBF,
    0xC0C0C0C0, 0xC1C1C1C1, 0xC2C2C2C2, 0xC3C3C3C3, 0xC4C4C4C4, 0xC5C5C5C5,
    0xC6C6C6C6, 0xC7C7C7C7, 0xC8C8C8C8, 0xC9C9C9C9, 0xCACACACA, 0xCBCBCBCB,
    0xCCCCCCCC, 0xCDCDCDCD, 0xCECECECE, 0xCFCFCFCF, 0xD0D0D0D0, 0xD1D1D1D1,
    0xD2D2D2D2, 0xD3D3D3D3, 0xD4D4D4D4, 0xD5D5D5D5, 0xD6D6D6D6, 0xD7D7D7D7,
    0xD8D8D8D8, 0xD9D9D9D9, 0xDADADADA, 0xDBDBDBDB, 0xDCDCDCDC, 0xDDDDDDDD,
    0xDEDEDEDE, 0xDFDFDFDF, 0xE0E0E0E0, 0xE1E1E1E1, 0xE2E2E2E2, 0xE3E3E3E3,
    0xE4E4E4E4, 0xE5E5E5E5, 0xE6E6E6E6, 0xE7E7E7E7, 0xE8E8E8E8, 0xE9E9E9E9,
    0xEAEAEAEA, 0xEBEBEBEB, 0xECECECEC, 0xEDEDEDED, 0xEEEEEEEE, 0xEFEFEFEF,
    0xF0F0F0F0, 0xF1F1F1F1, 0xF2F2F2F2, 0xF3F3F3F3, 0xF4F4F4F4, 0xF5F5F5F5,
    0xF6F6F6F6, 0xF7F7F7F7, 0xF8F8F8F8, 0xF9F9F9F9, 0xFAFAFAFA, 0xFBFBFBFB,
    0xFCFCFCFC, 0xFDFDFDFD, 0xFEFEFEFE, 0xFFFFFFFF
  };

  public static int NothingLength() => Nothing.Length;
  public static int ReadNothing(int index) => Nothing[index];
  public static int ReadOne(int index) => One[index];
  public static int Square(int index) => Squares[index];
  public static long Power(int index) => Powers[index];
  public static ulong ReadLarge(uint index) => Large[index];
  public static int ReadSigned(int index) => Signed[index];
  public static short ReadShort(int index) => Shorts[index];
  public static char ReadLetter(int index) => Letters[index];
  public static float Weight(int index) => Weights[index];
  public static double Root(int index) => Roots[index];
  public static int Sign(int index) => Tables.Signs[index];
  public static uint RampAt(byte index) => Ramp[index];
  public static int RampLength() => Ramp.Length + Squares.Length + Powers.Length;

  public static int SumOfSquares() {
    int sum = 0;
    foreach (ushort square in Squares)
      sum += square;

    return sum;
  }

  public static double SumOfWeights() {
    double sum = 0;
    foreach (var weight in Weights) {
      if (weight > 1)
        continue;

      sum += weight;
    }

    return sum;
  }

  public static uint RampChecksum() {
    uint sum = 0;
    for (int i = 0; i < Ramp.Length; ++i)
      sum = (sum << 1 | sum >> 31) ^ Ramp[i];

    return sum;
  }

  // Scalars: the const ones are folded by the compiler, the static readonly ones are read.
  public static double Scaled(int a) => Enabled ? (a + Offset) * Half + Wide + Tenth : Limit;
  public static uint Masked(uint a) => a & Mask & Tables.Mask;
  public static ulong Complement(ulong a) => Largest - a;
  public static float ThirdOf(float a) => a * Third;
  public static int LetterDistance(char c) => c - Letter + Tables.Digit;

  // Arrays built from their elements inside a function.
  public static int[] Triple(int a, int b) => new int[] { a, b, a + b };
  public static long[] Pair(long a) => new[] { a, -a };
  public static byte[] Bytes(byte a) => [a, 1, 2];
  public static double[] Doubled(double a) {
    double[] values = { a, a * 2 };
    return values;
  }

  public static ulong[] NoElements() => new ulong[] { };
  public static float[] NoFloats() => [];
  public static int LengthOfLiteral(short a) => new short[2] { a, a }.Length + new[] { 'x' }[0];

  public static ushort[] CopyOfSquares() {
    ushort[] copy = new ushort[Squares.Length];
    Array.Copy(Squares, 0, copy, 0, Squares.Length);
    return copy;
  }

  public static uint[] RampSlice(int from, int length) {
    uint[] slice = new uint[length];
    Array.Copy(Ramp, from, slice, 0, length);
    return slice;
  }

  public static void Copy(int[] source, int sourceIndex, int[] target, int targetIndex, int length)
    => Array.Copy(source, sourceIndex, target, targetIndex, length);

  // Source and target are one array: the ranges may overlap in either direction.
  public static void Move(long[] data, int from, int to, int length) => Array.Copy(data, from, data, to, length);

  public static void CopyDoubles(double[] source, double[] target) => System.Array.Copy(source, 0, target, 1, source.Length);
  public static void Fill(short[] data, short value) => Array.Fill(data, value);
  public static void FillLarge(ulong[] data, ulong value) => Array.Fill(data, value);
  public static void FillWithConstant(byte[] data) => Array.Fill(data, (byte)Limit);
  public static void FillFloats(float[] data, double value) => Array.Fill(data, (float)value);
  public static void Clear(float[] data) => Array.Clear(data);
  public static void ClearLongs(long[] data) => Array.Clear(data);
  public static void ClearChars(char[] data) => Array.Clear(data);

}
