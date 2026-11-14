// O0120: minimum and maximum as JavaScript writes them with integers, and a near miss. Written for
// this repository.

// expect: O0120
/**
 * @param {int32} a
 * @param {int32} b
 */
export function smaller(a, b) {
  return a < b ? a : b;
}

// expect: O0120
/**
 * @param {uint32} a
 * @param {uint32} b
 */
export function larger(a, b) {
  return a >= b ? a : b;
}

// expect: -
/**
 * @param {int32} a
 * @param {int32} b
 */
export function equal(a, b) {
  return a === b ? a : b;
}
