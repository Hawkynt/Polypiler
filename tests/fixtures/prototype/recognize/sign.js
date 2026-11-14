// O0122: the sign as JavaScript writes it with int32, and a near miss. Written for this repository.

// expect: O0122
/**
 * @param {int32} x
 */
export function shifted(x) {
  return (x >> 31) | (-x >>> 31);
}

// expect: -
/**
 * @param {int32} x
 */
export function notNegated(x) {
  return (x >> 31) | (x >>> 31);
}
