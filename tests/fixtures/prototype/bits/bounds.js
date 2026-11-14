// Math.abs, Math.min, Math.max and Math.sign of proven integers. Written for this repository.

/**
 * @param {int32} x
 */
export function magnitude(x) {
  return Math.abs(x);
}

/**
 * @param {int8} x
 */
export function magnitudeByte(x) {
  return Math.abs(x);
}

/**
 * @param {int16} a
 * @param {uint16} b
 */
export function smaller(a, b) {
  return Math.min(a, b);
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @param {uint8} c
 */
export function largest(a, b, c) {
  return Math.max(a, b, c);
}

/**
 * @param {int32} x
 */
export function signum(x) {
  return Math.sign(x);
}
