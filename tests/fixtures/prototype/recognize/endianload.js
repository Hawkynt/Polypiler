// O0140: byte loads joined by shifts as JavaScript writes them, and a near miss. Written for this
// repository.

// expect: O0140
/**
 * @param {Uint8Array} b
 * @param {int32} i
 */
export function little32(b, i) {
  return (b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)) >>> 0;
}

// expect: O0140
/**
 * @param {Uint8Array} b
 * @param {int32} i
 */
export function big32(b, i) {
  return (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
}

// expect: -
/**
 * @param {Uint8Array} b
 * @param {int32} i
 */
export function lastFirst(b, i) {
  return (b[i + 3] << 24) | (b[i + 2] << 16) | (b[i + 1] << 8) | b[i];
}

// expect: -
/**
 * @param {Uint8Array} b
 * @param {int32} i
 */
export function gap(b, i) {
  return (b[i] | (b[i + 1] << 8) | (b[i + 3] << 16) | (b[i + 4] << 24)) >>> 0;
}
