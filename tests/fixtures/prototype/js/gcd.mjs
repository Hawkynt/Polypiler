// The greatest common divisor by Euclid's algorithm. Written for this repository.

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function gcd(a, b) {
  while (b !== 0) {
    const remainder = a % b;
    a = b;
    b = remainder;
  }

  return a;
}
