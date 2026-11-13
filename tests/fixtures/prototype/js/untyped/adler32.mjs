// Adler-32 as RFC 1950 defines it, written the way JavaScript without annotations is written. The
// file carries no type anywhere: what `a` and `b` are follows from the `>>> 0` every value passes
// through, and what `data` is from the array the check passes.

export function compute(data) {
  let a = 1;
  let b = 0;
  for (let i = 0; i < data.length; i++) {
    a = ((a + data[i]) % 65521) >>> 0;
    b = ((b + a) % 65521) >>> 0;
  }
  return ((b << 16) | a) >>> 0;
}

// The published check value: Adler-32 of "Wikipedia" is 0x11E60398.
export function check() {
  return compute(new Uint8Array([87, 105, 107, 105, 112, 101, 100, 105, 97])) === 0x11E60398;
}
