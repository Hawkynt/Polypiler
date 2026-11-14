// O0101: bit-counting loops as JavaScript writes them, and a near miss. Written for this repository.

// expect: O0101
/**
 * @param {uint32} x
 */
export function kernighan(x) {
  let c = 0;
  while (x !== 0) {
    c = (c + 1) | 0;
    x = (x & (x - 1)) >>> 0;
  }
  return c;
}

// expect: O0101
/**
 * @param {uint32} x
 */
export function shiftTest(x) {
  let c = 0;
  for (let i = 0; i < 32; i++) {
    c = (c + ((x >>> i) & 1)) | 0;
  }
  return c;
}

// expect: -
/**
 * @param {uint32} x
 */
export function halfWidth(x) {
  let c = 0;
  for (let i = 0; i < 16; i++) {
    c = (c + ((x >>> i) & 1)) | 0;
  }
  return c;
}
