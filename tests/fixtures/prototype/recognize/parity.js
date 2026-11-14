// O0110: parity as JavaScript writes it, and a near miss. Written for this repository.

// expect: O0110
/**
 * @param {uint32} x
 */
export function fold(x) {
  x ^= x >>> 16;
  x ^= x >>> 8;
  x ^= x >>> 4;
  x ^= x >>> 2;
  x ^= x >>> 1;
  return x & 1;
}

// expect: -
/**
 * @param {uint32} x
 */
export function partial(x) {
  x ^= x >>> 16;
  x ^= x >>> 8;
  return x & 1;
}
