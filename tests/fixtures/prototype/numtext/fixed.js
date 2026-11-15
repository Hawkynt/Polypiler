// Fixed notation: toFixed rounds the exact value, a tie to the larger number. Written for this repository.

/**
 * @param {number} x
 * @returns {string}
 */
export function two(x) {
  return x.toFixed(2);
}

/**
 * @param {number} x
 * @returns {string}
 */
export function none(x) {
  return x.toFixed(0);
}

/**
 * @param {number} x
 * @returns {string}
 */
export function most(x) {
  return x.toFixed(100);
}
