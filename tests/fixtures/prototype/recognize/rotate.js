// O0100: rotations as JavaScript writes them - an int32 shifted left and the same value shifted
// right without sign, joined by |, ^ or + - and near misses. Written for this repository.

// expect: O0100
/**
 * @param {int32} x
 */
export function left8(x) {
  return (x << 8) | (x >>> 24);
}

// expect: O0100
/**
 * @param {uint32} x
 */
export function unsigned(x) {
  return ((x << 8) | (x >>> 24)) >>> 0;
}

// expect: O0100
/**
 * @param {int32} x
 * @param {int32} n
 */
export function variable(x, n) {
  return (x << n) | (x >>> (32 - n));
}

// expect: O0100
/**
 * @param {int32} x
 */
export function rightXor(x) {
  return (x >>> 7) ^ (x << 25);
}

// expect: O0100
/**
 * @param {uint16} x
 */
export function short(x) {
  return ((x << 3) | (x >>> 13)) & 0xFFFF;
}

// expect: -
/**
 * @param {int32} x
 */
export function arithmetic(x) {
  return (x << 8) | (x >> 24);
}

// expect: -
/**
 * @param {int32} x
 */
export function wrongAmount(x) {
  return (x << 8) | (x >>> 23);
}

// expect: -
/**
 * @param {int32} x
 * @param {int32} n
 */
export function xorVariable(x, n) {
  return (x << n) ^ (x >>> (32 - n));
}
