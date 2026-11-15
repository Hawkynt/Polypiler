// Text to numbers by the grammars of ECMA-262: Number, unary plus, parseFloat and parseInt. Written for this repository.

/**
 * @param {string} s
 * @returns {number}
 */
export function number(s) {
  return Number(s);
}

/**
 * @param {string} s
 * @returns {number}
 */
export function plus(s) {
  return +s;
}

/**
 * @param {string} s
 * @returns {number}
 */
export function float(s) {
  return parseFloat(s);
}

/**
 * @param {string} s
 * @returns {number}
 */
export function integer(s) {
  return parseInt(s);
}

/**
 * @param {string} s
 * @returns {number}
 */
export function hex(s) {
  return parseInt(s, 16);
}

/**
 * @param {string} s
 * @returns {number}
 */
export function base36(s) {
  return parseInt(s, 36);
}
