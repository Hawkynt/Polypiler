// O0125: multiplication by a constant as JavaScript writes it with int32, and a near miss. Written
// for this repository.

// expect: O0125
/**
 * @param {int32} x
 */
export function ten(x) {
  return ((x << 3) + (x << 1)) | 0;
}

// expect: -
/**
 * @param {int32} x
 * @param {int32} y
 */
export function twoValues(x, y) {
  return ((x << 3) + (y << 1)) | 0;
}
