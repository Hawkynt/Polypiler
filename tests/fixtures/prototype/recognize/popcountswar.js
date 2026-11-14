// O0102: the SWAR population count as JavaScript writes it, and a near miss. Written for this
// repository from Warren, Hacker's Delight, 2nd ed., section 5-1.

// expect: O0102
/**
 * @param {int32} x
 */
export function count(x) {
  x = (x - ((x >>> 1) & 0x55555555)) | 0;
  x = ((x & 0x33333333) + ((x >>> 2) & 0x33333333)) | 0;
  x = (x + (x >>> 4)) & 0x0F0F0F0F;
  return Math.imul(x, 0x01010101) >>> 24;
}

// expect: -
/**
 * @param {int32} x
 */
export function wrongShift(x) {
  x = (x - ((x >>> 1) & 0x55555555)) | 0;
  x = ((x & 0x33333333) + ((x >>> 2) & 0x33333333)) | 0;
  x = (x + (x >>> 4)) & 0x0F0F0F0F;
  return Math.imul(x, 0x01010101) >>> 23;
}
