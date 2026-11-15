// Adler-32 of RFC 1950 section 8.2 as an object: the two running sums are fields that Update keeps
// from one call to the next, Value combines them. Written for this repository; the check value
// Adler-32("Wikipedia") = 0x11E60398 is the published one.
namespace Polypiler.Fixtures.Prototype.Classes;

public sealed class Adler32 {

  private const uint Modulus = 65521;

  private uint _a = 1;

  private uint _b;

  public void Update(byte[] data, int offset, int count) {
    for (var index = offset; index < offset + count; ++index) {
      this._a = (this._a + data[index]) % Modulus;
      this._b = (this._b + this._a) % Modulus;
    }
  }

  public uint Value => (this._b << 16) | this._a;

}

public static class Adler32Demo {

  // 300286872 = 0x11E60398.
  public static uint OfWikipedia() {
    var adler = new Adler32();
    adler.Update(new byte[] { 87, 105, 107, 105, 112, 101, 100, 105, 97 }, 0, 9);
    return adler.Value;
  }

  public static uint Of(byte[] data) {
    var adler = new Adler32();
    adler.Update(data, 0, data.Length);
    return adler.Value;
  }

  public static uint InTwoParts(byte[] data, int split) {
    var adler = new Adler32();
    adler.Update(data, 0, split);
    adler.Update(data, split, data.Length - split);
    return adler.Value;
  }

}
