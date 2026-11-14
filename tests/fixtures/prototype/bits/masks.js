// BigInt.asUintN and BigInt.asIntN of widths below 64: the zero and the sign extension of a field at the bottom. Written for this repository.

/**
 * @param {uint64} x
 */
export function low12(x) {
  return BigInt.asUintN(12, x);
}

/**
 * @param {int64} x
 */
export function signed20(x) {
  return BigInt.asIntN(20, x);
}

/**
 * @param {uint64} x
 */
export function wrapped(x) {
  return BigInt.asIntN(64, x * 3n);
}
