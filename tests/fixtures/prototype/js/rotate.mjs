// A 32-bit rotation, written the way JavaScript has to write it: the shifts take their count modulo
// 32, so a count of 0 or 32 leaves the value as it is. Written for this repository.

/**
 * @param {uint32} value
 * @param {int32} count
 * @returns {uint32}
 */
export function rotateLeft(value, count) {
  return ((value << count) | (value >>> (32 - count))) >>> 0;
}

/**
 * @param {uint32} value
 * @param {int32} count
 * @returns {uint32}
 */
export function rotateRight(value, count) {
  return ((value >>> count) | (value << (32 - count))) >>> 0;
}
