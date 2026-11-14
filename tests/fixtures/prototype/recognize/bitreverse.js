// O0105: bit reversal as JavaScript writes it, and a near miss. Written for this repository from
// Warren, Hacker's Delight, 2nd ed., section 7-1.

// expect: O0105
/**
 * @param {int32} x
 */
export function cascade(x) {
  x = ((x >>> 1) & 0x55555555) | ((x & 0x55555555) << 1);
  x = ((x >>> 2) & 0x33333333) | ((x & 0x33333333) << 2);
  x = ((x >>> 4) & 0x0F0F0F0F) | ((x & 0x0F0F0F0F) << 4);
  x = ((x >>> 8) & 0x00FF00FF) | ((x & 0x00FF00FF) << 8);
  x = (x >>> 16) | (x << 16);
  return x;
}

// expect: O0105
/**
 * @param {uint32} x
 */
export function loop(x) {
  let r = 0;
  for (let i = 0; i < 32; i++) {
    r = (r << 1) | (x & 1);
    x = x >>> 1;
  }
  return r >>> 0;
}

// expect: -
/**
 * @param {int32} x
 */
export function swapsOnly(x) {
  x = ((x >>> 1) & 0x55555555) | ((x & 0x55555555) << 1);
  return x;
}
