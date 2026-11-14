// O0123: clamping as JavaScript writes it with integers, and a near miss. Written for this
// repository.

// expect: O0123
/**
 * @param {int32} x
 */
export function nested(x) {
  return Math.min(Math.max(x, 0), 255);
}

// expect: O0123
/**
 * @param {int32} x
 */
export function chain(x) {
  return x < -5 ? -5 : x > 5 ? 5 : x;
}

// expect: -
/**
 * @param {int32} x
 * @param {int32} high
 */
export function variableBound(x, high) {
  return Math.min(Math.max(x, 0), high);
}
