// O0109: sign extension as JavaScript writes it, and a near miss. Written for this repository.

// expect: O0109
/**
 * @param {int32} x
 */
export function shiftPair(x) {
  return (x << 24) >> 24;
}

// expect: O0109
/**
 * @param {int32} x
 */
export function xorSubtract(x) {
  return (((x & 0xFFFF) ^ 0x8000) - 0x8000) | 0;
}

// expect: -
/**
 * @param {int32} x
 */
export function logical(x) {
  return (x << 24) >>> 24;
}
