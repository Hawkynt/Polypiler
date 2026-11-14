// O0121: the wrapping absolute value as JavaScript writes it with int32, and a near miss. Written
// for this repository.

// expect: O0121
/**
 * @param {int32} x
 */
export function masked(x) {
  const m = x >> 31;
  return ((x ^ m) - m) | 0;
}

// expect: O0121
/**
 * @param {int32} x
 */
export function selected(x) {
  return x < 0 ? (-x | 0) : x;
}

// expect: -
/**
 * @param {int32} x
 */
export function wrongShift(x) {
  const m = x >> 30;
  return ((x ^ m) - m) | 0;
}
