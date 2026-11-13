// Adler-32 as RFC 1950 section 8.2 defines it: two sums modulo 65521, the largest prime below 2^16.
// Written for this repository; the test vector Adler-32("Wikipedia") = 0x11E60398 is the published one.

/**
 * @param {uint8[]} data
 * @returns {uint32}
 */
export function compute(data) {
  let a = 1;
  let b = 0;
  for (let i = 0; i < data.length; i++) {
    a = ((a + data[i]) | 0) % 65521;
    b = ((b + a) | 0) % 65521;
  }

  return ((b << 16) | a) >>> 0;
}
