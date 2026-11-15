// Unsafe C# with bounded pointers: fixed over arrays, elements and spans, pointer arithmetic, casts
// between pointer types, sizeof, stackalloc into a pointer and the address of a local. The bytes d are
// 0, 1, ..., 15 in the scenarios. Written for this repository.
using System;

namespace Polypiler.Fixtures.Prototype;

public static unsafe class Pointers {

  // 3838341128 for d: a walk from the start to one past the end, s = s * 31 + *p; 0 for no bytes.
  public static uint Checksum(byte[] data) {
    uint s = 0;
    fixed (byte* start = data) {
      byte* end = start + data.Length;
      for (byte* p = start; p < end; p++) {
        s = s * 31 + *p;
      }
    }

    return s;
  }

  // 67305985 for (d, 1), 252579084 for (d, 12): four bytes read as a uint, the lowest first, unaligned.
  public static uint Word(byte[] data, int at) {
    fixed (byte* p = data) {
      return *(uint*)(p + at);
    }
  }

  // 5 for (new int[10], 2, 7), -5 for (new int[10], 7, 2): the difference counts elements.
  public static int Distance(int[] a, int i, int j) {
    fixed (int* p = a) {
      int* x = p + i;
      int* y = p + j;
      return (int)(y - x);
    }
  }

  // 29 for 3: eight ints on the stack, the last one 7 * n, plus the size of a long.
  public static int Stack(int n) {
    int* p = stackalloc int[8];
    for (int i = 0; i < 8; i++) {
      p[i] = i * n;
    }

    return p[7] + sizeof(long);
  }

  // 7: the local is written through its address.
  public static int Local() {
    int x = 6;
    int* p = &x;
    *p += 1;
    return x;
  }

  // true: fixed over an empty array gives the null pointer.
  public static bool EmptyIsNull() {
    fixed (byte* p = new byte[0]) {
      return p == null;
    }
  }

  // 1.5: the bits of a double written as a long.
  public static double Bits() {
    double d = 0;
    *(long*)&d = 0x3FF8000000000000;
    return d;
  }

  // The element at an index; d[n - 1] is read, d[n] and d[-1] trap outside C#.
  public static int At(int[] a, int i) {
    fixed (int* p = a) {
      return p[i];
    }
  }

  // Every origin of a pointer: fixed of an element, of a span, stackalloc and the address of a local.
  public static int Origins(byte[] d) {
    int total = 0;
    fixed (byte* e = &d[3]) {
      total += e[-1] + e[0];
    }

    Span<byte> window = d.AsSpan(4, 4);
    fixed (byte* v = window) {
      total += v[3] * 10;
    }

    long* s = stackalloc long[2];
    s[1] = 1000;
    total += (int)s[1];
    short local = 20000;
    short* l = &local;
    *l += 1;
    return total + local;
  }

  // The elements of every number type read through a pointer of bytes.
  public static short Shorts(byte[] d, int at) {
    fixed (byte* p = d) {
      return *(short*)(p + at);
    }
  }

  public static ushort UShorts(byte[] d, int at) {
    fixed (byte* p = d) {
      return *(ushort*)(p + at);
    }
  }

  public static int Ints(byte[] d, int at) {
    fixed (byte* p = d) {
      return *(int*)(p + at);
    }
  }

  public static long Longs(byte[] d, int at) {
    fixed (byte* p = d) {
      return *(long*)(p + at);
    }
  }

  public static ulong ULongs(byte[] d, int at) {
    fixed (byte* p = d) {
      return *(ulong*)(p + at);
    }
  }

  public static float Floats(byte[] d, int at) {
    fixed (byte* p = d) {
      return *(float*)(p + at);
    }
  }

  public static double Doubles(byte[] d, int at) {
    fixed (byte* p = d) {
      return *(double*)(p + at);
    }
  }

  // A long written into the bytes, which the caller sees.
  public static void Store(byte[] d, int at, long value) {
    fixed (byte* p = d) {
      *(long*)(p + at) = value;
    }
  }

  // Elements of a wider type written and read back through a pointer of their own type, the step included.
  public static ulong Wide(ulong[] a) {
    ulong total = 0;
    fixed (ulong* p = a) {
      ulong* q = p;
      while (q < p + a.Length) {
        *q = *q * 3 + 1;
        total += *q;
        q++;
      }
    }

    return total;
  }

  // Whether an element index lies between the start and one past the end.
  public static bool Inside(int[] a, int i) {
    fixed (int* p = a) {
      int* q = p + i;
      return q >= p && q <= p + a.Length;
    }
  }

  // A pointer through void* and back.
  public static int Through(int[] a) {
    fixed (int* p = a) {
      void* v = p;
      int* back = (int*)v;
      return back[a.Length - 1];
    }
  }

  // A pointer passed to a private function, and moved with += and -=.
  public static int Passed(int[] a) {
    fixed (int* p = a) {
      return SumOf(p, a.Length);
    }
  }

  private static int SumOf(int* p, int n) {
    int total = 0;
    int* q = p;
    q += n;
    while (q > p) {
      q -= 1;
      total += *q;
    }

    return total;
  }

  // Pointers into two arrays: never equal; their order is no value of any target.
  public static bool Apart(int[] a, int[] b) {
    fixed (int* p = a) {
      fixed (int* q = b) {
        return p == q || p != q && q == null;
      }
    }
  }

  public static bool Before(int[] a, int[] b) {
    fixed (int* p = a) {
      fixed (int* q = b) {
        return p < q;
      }
    }
  }

}
