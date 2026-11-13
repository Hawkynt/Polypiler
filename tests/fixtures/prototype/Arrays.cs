// Arrays of each element type: creating, reading, writing, measuring, and changing one in place.
// Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Arrays {

  public static void Reverse(int[] data) {
    for (int low = 0, high = data.Length - 1; low < high; ++low, --high) {
      int held = data[low];
      data[low] = data[high];
      data[high] = held;
    }
  }

  public static int ReadInt(int[] data, int index) => data[index];

  public static uint ReadUInt(uint[] data, int index) => data[index];

  public static byte ReadByte(byte[] data, int index) => data[index];

  // An element above the signed range stays unsigned in what is computed from it.
  public static uint HalfOf(uint[] data, int index) => data[index] / 2;

  public static bool IsLarge(uint[] data, int index) => data[index] > 0x7FFFFFFF;

  public static int ReadAtUnsignedIndex(int[] data, uint index) => data[index];

  public static void WriteInt(int[] data, int index, int value) { data[index] = value; }

  public static void WriteUInt(uint[] data, int index, uint value) { data[index] = value; }

  public static void WriteByte(byte[] data, int index, byte value) { data[index] = value; }

  public static void WriteTruncated(byte[] data, int index, int value) { data[index] = (byte)value; }

  public static int LengthOfInts(int[] data) => data.Length;

  public static int LengthOfUInts(uint[] data) => data.Length;

  public static int LengthOfBytes(byte[] data) => data.Length;

  public static int[] CountUp(int length) {
    int[] result = new int[length];
    for (int i = 0; i < result.Length; ++i)
      result[i] = i - 1;

    return result;
  }

  public static uint[] Powers(int length) {
    uint[] result = new uint[length];
    uint power = 1;
    for (int i = 0; i < length; ++i) {
      result[i] = power;
      power *= 0x10001;
    }

    return result;
  }

  public static byte[] Ramp(int length) {
    var result = new byte[length];
    for (int i = 0; i < length; ++i)
      result[i] = (byte)(i * 100);

    return result;
  }

  public static int Sum(int[] data) {
    int sum = 0;
    for (int i = 0; i < data.Length; ++i)
      sum += data[i];

    return sum;
  }

  // The element is read and written; the index expression must be evaluated only once.
  public static void Accumulate(int[] data, int[] cursor) {
    data[Next(cursor)] += 10;
    data[Next(cursor)] -= 1;
    data[cursor[0] - 2]++;
    --data[cursor[0] - 1];
  }

  public static void Scramble(byte[] data, uint[] words) {
    for (int i = 0; i < data.Length; ++i) {
      data[i] ^= 0x5A;
      data[i] += 200;
      data[i]++;
      words[i] <<= i;
      words[i] |= data[i];
    }
  }

  private static int Next(int[] cursor) {
    cursor[0]++;
    return cursor[0] - 1;
  }

}
