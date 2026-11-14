// O0108: single-bit idioms as JavaScript writes them, and a near miss. Written for this repository.

// expect: O0108
/**
 * @param {uint32} x
 * @param {int32} i
 */
export function test(x, i) {
  return ((x >>> i) & 1) !== 0;
}

// expect: O0108
/**
 * @param {int32} x
 * @param {int32} i
 */
export function set(x, i) {
  return x | (1 << i);
}

// expect: O0108
/**
 * @param {int32} x
 * @param {int32} i
 */
export function clear(x, i) {
  return x & ~(1 << i);
}

// expect: -
/**
 * @param {int32} x
 * @param {int32} i
 */
export function twoBits(x, i) {
  return x ^ (3 << i);
}
