// Integers in a radix: JavaScript writes a minus sign before the digits of the magnitude. Written for this repository.

/**
 * @param {int32} value
 * @returns {string}
 */
export function hexadecimal(value) {
  return value.toString(16);
}

/**
 * @param {int32} value
 * @returns {string}
 */
export function binary(value) {
  return value.toString(2);
}

/**
 * @param {int32} value
 * @returns {string}
 */
export function base36(value) {
  return value.toString(36);
}

/**
 * @param {int32} value
 * @returns {string}
 */
export function unsigned(value) {
  return (value >>> 0).toString(16);
}

/**
 * @param {int64} value
 * @returns {string}
 */
export function wide(value) {
  return value.toString(16);
}
