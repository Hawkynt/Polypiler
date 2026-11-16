// An RC4 keystream whose methods name the state array of the object through a local, as the cipher
// corpus writes it (`var s = this._s;`): the key schedule fills and swaps it, each step swaps two
// elements and reads a third, a sum reads it in a loop. Written for this repository.
namespace Polypiler.Fixtures.Prototype.Classes;

public sealed class Rc4 {

  private readonly byte[] _s = new byte[256];
  private int _i;
  private int _j;

  public Rc4(byte[] key) {
    var s = this._s;
    for (var n = 0; n < 256; ++n)
      s[n] = (byte)n;

    var j = 0;
    for (var n = 0; n < 256; ++n) {
      j = (j + s[n] + key[n % key.Length]) & 255;
      var t = s[n];
      s[n] = s[j];
      s[j] = t;
    }
  }

  public byte Next() {
    var s = this._s;
    this._i = (this._i + 1) & 255;
    this._j = (this._j + s[this._i]) & 255;
    var t = s[this._i];
    s[this._i] = s[this._j];
    s[this._j] = t;
    return s[(s[this._i] + s[this._j]) & 255];
  }

  public int Sum() {
    var s = this._s;
    var sum = 0;
    foreach (var b in s)
      sum += b;

    return sum;
  }

}

public static class Rc4Demo {

  // The first bytes of the keystream, written over the buffer, then the sum of the state.
  public static int Fill(byte[] key, byte[] buffer) {
    var rc4 = new Rc4(key);
    for (var n = 0; n < buffer.Length; ++n)
      buffer[n] = rc4.Next();

    return rc4.Sum();
  }

}
