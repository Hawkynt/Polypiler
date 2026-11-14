// The product modulo 2^32 of Math.imul. Written for this repository.

/**
 * @param {int32} a
 * @param {int32} b
 */
export function product(a, b) {
  return Math.imul(a, b);
}

/**
 * @param {uint32} a
 * @param {uint32} b
 */
export function productUnsigned(a, b) {
  return Math.imul(a, b) >>> 0;
}
