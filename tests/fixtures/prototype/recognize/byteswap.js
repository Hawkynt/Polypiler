// O0106: byte swaps as JavaScript writes them, and a near miss. Written for this repository.

// expect: O0106
/**
 * @param {int32} x
 */
export function swap32(x) {
  return (x >>> 24) | ((x >>> 8) & 0xFF00) | ((x << 8) & 0xFF0000) | (x << 24);
}

// expect: O0106
/**
 * @param {uint16} x
 */
export function swap16(x) {
  return ((x >>> 8) | (x << 8)) & 0xFFFF;
}

// expect: -
/**
 * @param {int32} x
 */
export function arithmetic(x) {
  return (x >> 24) | ((x >> 8) & 0xFF00) | ((x << 8) & 0xFF0000) | (x << 24);
}
