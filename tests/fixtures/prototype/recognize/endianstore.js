// O0141: byte stores as JavaScript writes them. JavaScript computes an index i + k in doubles and
// the import traps where it passes the largest int32, so a store at i + k is not combined; at
// constant indices it is. Written for this repository.

// expect: O0141
/**
 * @param {Uint8Array} b
 * @param {int32} v
 */
export function header(b, v) {
  b[0] = v >>> 24;
  b[1] = v >>> 16;
  b[2] = v >>> 8;
  b[3] = v;
  return b;
}

// expect: -
/**
 * @param {Uint8Array} b
 * @param {int32} i
 * @param {int32} v
 */
export function indexed(b, i, v) {
  b[i] = v;
  b[i + 1] = v >>> 8;
  return b;
}
