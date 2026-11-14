// Views of arrays: Span<T> and ReadOnlySpan<T> made of arrays, sliced, indexed, copied, filled,
// compared and reinterpreted, and stackalloc. The bytes d are 0, 1, ..., 15 in the scenarios.
// Written for this repository.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Polypiler.Fixtures.Prototype;

public static class Spans {

  // 22 for (4, 4), 54 for (12, 4), 0 for (16, 0); a range outside d throws ArgumentOutOfRangeException.
  public static int Sum(byte[] d, int start, int length) {
    ReadOnlySpan<byte> s = d.AsSpan(start, length);
    int total = 0;
    foreach (byte b in s) {
      total += b;
    }

    return total;
  }

  // 84148994: the bytes 2 to 5, the lowest first.
  public static uint Word(byte[] d) {
    ReadOnlySpan<byte> s = d.AsSpan(2, 4);
    return BinaryPrimitives.ReadUInt32LittleEndian(s);
  }

  // 604242948 for 8 bytes, 318898432 for 7, 0 for 3 and for 0: the words xor-ed, and their count in the top bits.
  public static uint CastXor(byte[] d, int n) {
    ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(d.AsSpan(0, n));
    uint x = 0;
    for (int i = 0; i < words.Length; i++) {
      x ^= words[i];
    }

    return words.IsEmpty ? 0 : x ^ ((uint)words.Length << 28);
  }

  // 54: the last four bytes.
  public static int Tail(byte[] d) {
    ReadOnlySpan<byte> s = d;
    return Total(s[^4..]);
  }

  private static int Total(ReadOnlySpan<byte> s) {
    int total = 0;
    for (int i = 0; i < s.Length; i++) {
      total += s[i];
    }

    return total;
  }

  // 15 at 15; IndexOutOfRangeException at 16 and at -1.
  public static int At(byte[] d, int i) {
    Span<byte> s = d;
    return s[i];
  }

  // 0, 1, 0, 1, 2, 3, 4, 5, 6, 7: the copy moves like memmove.
  public static byte[] Overlap() {
    byte[] a = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
    a.AsSpan(0, 8).CopyTo(a.AsSpan(2));
    return a;
  }

  // The same copy backwards: 2, 3, 4, 5, 6, 7, 8, 9, 8, 9.
  public static byte[] OverlapBack() {
    byte[] a = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
    a.AsSpan(2).CopyTo(a);
    return a;
  }

  // Four ints into n: a shorter destination throws ArgumentException.
  public static int[] CopyInto(int n) {
    int[] a = new int[] { 1, 2, 3, 4 };
    int[] b = new int[n];
    a.AsSpan().CopyTo(b);
    return b;
  }

  // TryCopyTo copies nothing and says so when the destination is shorter.
  public static bool TryInto(int n) {
    int[] a = new int[] { 1, 2, 3, 4 };
    Span<int> b = new int[n];
    return a.AsSpan().TryCopyTo(b) && b[0] == 1;
  }

  // True: double.Equals takes NaN as NaN and -0.0 as 0.0.
  public static bool NaNEqual() => new double[] { double.NaN, 0.0 }.AsSpan().SequenceEqual(new double[] { double.NaN, -0.0 });

  // Element-wise equality of two runs of doubles of the arrays.
  public static bool Same(double[] a, double[] b) => a.AsSpan().SequenceEqual(b);

  // 67305985: the four bytes from 1 on, the lowest first.
  public static uint ReadFrom(byte[] d) => MemoryMarshal.Read<uint>(d.AsSpan(1));

  // Writes a word through a span of bytes and returns the bytes.
  public static byte[] WriteAt(byte[] d, int at, uint value) {
    Span<byte> s = d.AsSpan(at);
    MemoryMarshal.Write(s, in value);
    BinaryPrimitives.WriteUInt16BigEndian(s.Slice(4), 0xABCD);
    return d;
  }

  // 7 for 1 and 4; IndexOutOfRangeException at t[0] for 0: stackalloc gives zeros.
  public static int Zeroed(int n) {
    Span<int> t = stackalloc int[n];
    int before = t[0];
    t[n - 1] = 7;
    return before + t[n - 1];
  }

  // A stackalloc of a constant length the C# backend may keep: 1 + 2 + 3 + 4 + 0 * 4.
  public static uint Fixed() {
    Span<uint> t = stackalloc uint[8];
    for (int i = 0; i < 4; i++) {
      t[i] = (uint)(i + 1);
    }

    uint total = 0;
    foreach (uint v in t) {
      total += v;
    }

    return total;
  }

  // Every origin of a view: the whole array, AsSpan(s), AsSpan(s, n), a range, new Span, stackalloc, a slice of a slice.
  public static int Origins(byte[] d) {
    Span<byte> whole = d;
    Span<byte> from = d.AsSpan(3);
    Span<byte> part = d.AsSpan(3, 4);
    Span<byte> ranged = whole[2..6];
    Span<byte> made = new Span<byte>(d, 1, 2);
    Span<byte> nested = from.Slice(1).Slice(2, 3);
    Span<byte> stack = stackalloc byte[2];
    stack[1] = 9;
    return whole[15] + from[0] + part[3] * 10 + ranged[0] * 100 + made.Length * 1000 + nested[2] * 10000 + stack[1] * 100000 + from.Length * 1000000;
  }

  // Fill, Clear, ToArray and a range of an array, which copies.
  public static int[] Filled(int n) {
    int[] a = new int[6];
    Span<int> s = a;
    s.Slice(1, 4).Fill(n);
    s.Slice(2, 1).Clear();
    int[] copy = s[..3].ToArray();
    copy[0] = 5;
    int[] ranged = a[1..3];
    ranged[0] = 9;
    a[5] = copy[0] + ranged[0] + a[1];
    return a;
  }

  // A writable span handed to a private method and written there.
  public static uint[] Doubled(uint[] a) {
    Twice(a.AsSpan(1));
    return a;
  }

  private static void Twice(Span<uint> s) {
    for (int i = 0; i < s.Length; i++) {
      s[i] *= 2;
    }
  }

  // The element types: fill, copy, equality and sum of a view of each.
  public static long Bytes(byte v) {
    Span<byte> s = new byte[3];
    s.Fill(v);
    Span<byte> t = new byte[3];
    s.CopyTo(t);
    return t[0] + t[1] + t[2] + (s.SequenceEqual(t) ? 1000 : 0);
  }

  public static long Shorts(short v) {
    Span<short> s = new short[3];
    s.Fill(v);
    Span<short> t = new short[3];
    s.CopyTo(t);
    return t[0] + t[1] + t[2] + (s.SequenceEqual(t) ? 1000 : 0);
  }

  public static long Ints(int v) {
    Span<int> s = new int[3];
    s.Fill(v);
    Span<int> t = new int[3];
    s.CopyTo(t);
    return (long)t[0] + t[1] + t[2] + (s.SequenceEqual(t) ? 1000 : 0);
  }

  public static long Words(uint v) {
    Span<uint> s = new uint[3];
    s.Fill(v);
    Span<uint> t = new uint[3];
    s.CopyTo(t);
    return (long)t[0] + t[1] + t[2] + (s.SequenceEqual(t) ? 1000 : 0);
  }

  public static long Longs(long v) {
    Span<long> s = new long[3];
    s.Fill(v);
    Span<long> t = new long[3];
    s.CopyTo(t);
    return unchecked(t[0] + t[1] + t[2] + (s.SequenceEqual(t) ? 1000 : 0));
  }

  public static ulong Ulongs(ulong v) {
    Span<ulong> s = new ulong[3];
    s.Fill(v);
    Span<ulong> t = new ulong[3];
    s.CopyTo(t);
    return unchecked(t[0] + t[1] + t[2] + (s.SequenceEqual(t) ? 1000UL : 0UL));
  }

  public static double Singles(float v) {
    Span<float> s = new float[3];
    s.Fill(v);
    Span<float> t = new float[3];
    s.CopyTo(t);
    return t[0] + t[1] + t[2] + (s.SequenceEqual(t) ? 1000 : 0);
  }

  public static double Doubles(double v) {
    Span<double> s = new double[3];
    s.Fill(v);
    Span<double> t = new double[3];
    s.CopyTo(t);
    return t[0] + t[1] + t[2] + (s.SequenceEqual(t) ? 1000 : 0);
  }

  // The bytes of a run of shorts, little-endian, xor-ed.
  public static int ShortBytes(short[] a) {
    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(a.AsSpan());
    int x = 0;
    for (int i = 0; i < bytes.Length; i++) {
      x = (x << 1) ^ bytes[i];
    }

    return x;
  }

}
