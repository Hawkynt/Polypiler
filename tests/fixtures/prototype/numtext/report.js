// A line of a report: numbers, integers and truth values as JavaScript writes them. Written for this repository.

/**
 * @param {number} x
 * @param {int32} n
 * @param {boolean} flag
 * @returns {string}
 */
export function line(x, n, flag) {
  return "x=" + x + " n=" + n + ` flag=${flag} s=${String(x)}`;
}

/**
 * @param {number} x
 * @returns {string}
 */
export function value(x) {
  return "v=" + x.toString();
}

/**
 * @param {boolean} flag
 * @returns {string}
 */
export function truth(flag) {
  return "b=" + flag;
}

/**
 * @param {uint64} n
 * @returns {string}
 */
export function big(n) {
  return "n=" + n;
}
