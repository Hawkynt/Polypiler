// A class that keeps a window onto bytes as a ReadOnlyMemory<byte> and reads it later: Memory<T> and
// ReadOnlyMemory<T> as fields of a class, AsMemory, Slice, Span, Length, IsEmpty and ToArray. The bytes
// d are 0, 1, ..., 15 in the scenarios. Written for this repository.
using System;

namespace Polypiler.Fixtures.Prototype;

public sealed class MemoryHolder {

  private readonly ReadOnlyMemory<byte> _window;

  private Memory<byte> _scratch;

  public MemoryHolder(byte[] data, int start, int length) {
    this._window = data.AsMemory(start, length);
    this._scratch = new byte[4];
  }

  public MemoryHolder() { }

  public int Length => this._window.Length;

  public bool IsEmpty => this._window.IsEmpty;

  public int Sum() {
    ReadOnlySpan<byte> s = this._window.Span;
    int total = 0;
    foreach (byte b in s) {
      total += b;
    }

    return total;
  }

  public int SumOf(int start, int length) {
    ReadOnlyMemory<byte> part = this._window.Slice(start, length);
    ReadOnlySpan<byte> s = part.Span;
    int total = 0;
    for (int i = 0; i < s.Length; i++) {
      total += s[i];
    }

    return total;
  }

  public int Rest(int start) => this._window.Slice(start).Length;

  public byte[] Copy() => this._window.ToArray();

  public int Keep(int value) {
    Span<byte> s = this._scratch.Span;
    s[1] = (byte)value;
    return this._scratch.Span[1] + this._scratch.Length;
  }

}

public static class MemoryHolderDemo {

  // 22: the window 4..7 of d, summed after the object was made.
  public static int Window(byte[] d, int start, int length) {
    MemoryHolder holder = new MemoryHolder(d, start, length);
    return holder.Sum();
  }

  // 117: the window shares the bytes, so a later write to d is seen through it.
  public static int SeesLaterWrites(byte[] d) {
    MemoryHolder holder = new MemoryHolder(d, 4, 4);
    d[5] = 100;
    return holder.Sum();
  }

  // The part of the window from start on with length elements; outside it the slice throws ArgumentOutOfRangeException.
  public static int Part(byte[] d, int start, int length) {
    MemoryHolder holder = new MemoryHolder(d, 2, 8);
    return holder.SumOf(start, length);
  }

  public static int Rest(byte[] d, int start) => new MemoryHolder(d, 0, d.Length).Rest(start);

  public static byte[] Copied(byte[] d) => new MemoryHolder(d, 2, 3).Copy();

  // 0, true and 0: a field nothing was stored into is an empty memory.
  public static int Empty() {
    MemoryHolder holder = new MemoryHolder();
    return holder.Length + holder.Sum() + holder.Rest(0) + (holder.IsEmpty ? 1000 : 0);
  }

  // value & 255 plus 4: written through the span of a Memory<byte> field and read back.
  public static int Scratch(byte[] d, int value) => new MemoryHolder(d, 0, 1).Keep(value);

  // A memory as a local: of a whole array by conversion, of a part by its constructor, sliced, read through its span.
  public static int Local(byte[] d) {
    ReadOnlyMemory<byte> whole = d;
    Memory<byte> part = new Memory<byte>(d, 1, 6);
    Memory<byte> tail = part.Slice(2);
    tail.Span[0] = 50;
    return whole.Length * 1000 + part.Span[2] + tail.Length;
  }

}
