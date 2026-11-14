// O0141: 2, 4 and 8 consecutive byte stores of the bytes of one value, in either order, and near
// misses. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class RecognizeEndianStore {

  // expect: O0141
  public static byte[] Little32(byte[] b, int i, uint v) {
    b[i] = (byte)v;
    b[i + 1] = (byte)(v >> 8);
    b[i + 2] = (byte)(v >> 16);
    b[i + 3] = (byte)(v >> 24);
    return b;
  }

  // expect: O0141
  public static byte[] Big32(byte[] b, int i, int v) {
    b[i] = (byte)(v >> 24);
    b[i + 1] = (byte)(v >> 16);
    b[i + 2] = (byte)(v >> 8);
    b[i + 3] = (byte)v;
    return b;
  }

  // expect: O0141
  public static byte[] Little16(byte[] b, int i, ushort v) {
    b[i] = (byte)v;
    b[i + 1] = (byte)(v >> 8);
    return b;
  }

  // expect: O0141
  public static byte[] Big64(byte[] b, int i, ulong v) {
    b[i] = (byte)(v >> 56);
    b[i + 1] = (byte)(v >> 48);
    b[i + 2] = (byte)(v >> 40);
    b[i + 3] = (byte)(v >> 32);
    b[i + 4] = (byte)(v >> 24);
    b[i + 5] = (byte)(v >> 16);
    b[i + 6] = (byte)(v >> 8);
    b[i + 7] = (byte)v;
    return b;
  }

  // expect: O0141
  public static byte[] Descending(byte[] b, int i, uint v) {
    b[i + 3] = (byte)(v >> 24);
    b[i + 2] = (byte)(v >> 16);
    b[i + 1] = (byte)(v >> 8);
    b[i] = (byte)v;
    return b;
  }

  // expect: O0141
  public static byte[] LowWord(byte[] b, int i, ulong v) {
    b[i] = (byte)v;
    b[i + 1] = (byte)(v >> 8);
    b[i + 2] = (byte)(v >> 16);
    b[i + 3] = (byte)(v >> 24);
    return b;
  }

  // expect: O0141
  public static byte[] Gap(byte[] b, int i, uint v) {
    b[i] = (byte)v;
    b[i + 1] = (byte)(v >> 8);
    b[i + 3] = (byte)(v >> 16);
    b[i + 4] = (byte)(v >> 24);
    return b;
  }

  // expect: -
  public static byte[] WrongByte(byte[] b, int i, uint v) {
    b[i] = (byte)v;
    b[i + 1] = (byte)(v >> 16);
    return b;
  }

  // expect: -
  public static byte[] Interleaved(byte[] b, int i, uint v) {
    b[i] = (byte)v;
    v = v >> 8;
    b[i + 1] = (byte)v;
    return b;
  }

  // expect: -
  public static byte[] TwoValues(byte[] b, int i, uint v, uint w) {
    b[i] = (byte)v;
    b[i + 1] = (byte)(w >> 8);
    return b;
  }

  // expect: -
  public static byte[] Shifted(byte[] b, int i, uint v) {
    b[i] = (byte)(v >> 4);
    b[i + 1] = (byte)(v >> 12);
    return b;
  }

}
