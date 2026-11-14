// O0103: bit-scanning loops as JavaScript writes them, and a near miss. Written for this repository.

// expect: O0103
/**
 * @param {uint32} x
 */
export function trailing(x) {
  let n = 0;
  while (n < 32 && (x & 1) === 0) {
    x = x >>> 1;
    n = (n + 1) | 0;
  }
  return n;
}

// expect: O0103
/**
 * @param {int32} x
 */
export function leading(x) {
  let n = 0;
  while (n < 32 && (x & 0x80000000) === 0) {
    x = x << 1;
    n = (n + 1) | 0;
  }
  return n;
}

// expect: -
/**
 * @param {uint32} x
 */
export function wrongBit(x) {
  let n = 0;
  while (n < 32 && (x & 4) === 0) {
    x = x >>> 1;
    n = (n + 1) | 0;
  }
  return n;
}
