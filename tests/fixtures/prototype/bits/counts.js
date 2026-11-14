// The leading zeros of Math.clz32, on a uint32 and on what ToUint32 makes of an int16. Written for this repository.

/**
 * @param {uint32} x
 */
export function leading(x) {
  return Math.clz32(x);
}

/**
 * @param {int16} x
 */
export function leadingShort(x) {
  return Math.clz32(x);
}

/**
 * @param {uint32} x
 */
export function log2(x) {
  return 31 - Math.clz32(x | 1);
}
