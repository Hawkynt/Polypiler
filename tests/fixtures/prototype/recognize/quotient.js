// O0127: a quotient and a remainder of the same int32 operands as JavaScript writes them under a
// coercion, and a near miss. Written for this repository.

// expect: O0127
/**
 * @param {int32} a
 * @param {int32} b
 */
export function both(a, b) {
  const q = (a / b) | 0;
  const r = (a % b) | 0;
  return (q + r) | 0;
}

// expect: -
/**
 * @param {int32} a
 * @param {int32} b
 * @param {int32} c
 */
export function other(a, b, c) {
  const q = (a / b) | 0;
  const r = (a % c) | 0;
  return (q + r) | 0;
}
