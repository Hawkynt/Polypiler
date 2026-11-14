// Bytes to hexadecimal text through a constant alphabet, and back. Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Hex {

  private const string Digits = "0123456789abcdef";

  public static string Encode(byte[] data) {
    string text = "";
    foreach (byte value in data) {
      text += Digits[value >> 4];
      text += Digits[value & 15];
    }

    return text;
  }

  public static byte[] Decode(string text) {
    byte[] data = new byte[text.Length / 2];
    for (int index = 0; index < data.Length; index++)
      data[index] = (byte)(Digits.IndexOf(text[2 * index]) << 4 | Digits.IndexOf(text[2 * index + 1]));

    return data;
  }

}
