// Adler-32 of RFC 1950 section 8.2 as an ECMAScript 2015 class: the two running sums are fields
// that update keeps from one call to the next, the getter value combines them. Written for this
// repository; the check value Adler-32("Wikipedia") = 0x11E60398 is the published one.

const MODULUS = 65521;

export class Adler32 {

  constructor() {
    this.a = 1;
    this.b = 0;
  }

  /**
   * @param {uint8[]} data
   * @param {int32} offset
   * @param {int32} count
   */
  update(data, offset, count) {
    for (let index = offset; index < offset + count; ++index) {
      this.a = (this.a + data[index]) % MODULUS;
      this.b = (this.b + this.a) % MODULUS;
    }
  }

  get value() {
    return ((this.b << 16) | this.a) >>> 0;
  }
}

// 300286872 = 0x11E60398.
export function ofWikipedia() {
  const adler = new Adler32();
  adler.update(new Uint8Array([87, 105, 107, 105, 112, 101, 100, 105, 97]), 0, 9);
  return adler.value;
}

/** @param {uint8[]} data */
export function of(data) {
  const adler = new Adler32();
  adler.update(data, 0, data.length);
  return adler.value;
}
