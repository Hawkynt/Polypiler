// ToInt32 and ToUint32 of doubles, sums and differences at the limits of 32 bits under a coercion,
// and products, which stay double multiplications however they are coerced.

/** @param {number} a */
export function toInt32(a) { return a | 0; }
/** @param {number} a */
export function toUint32(a) { return a >>> 0; }
/** @param {number} a */
export function toInt32Twice(a) { return ~~a; }
/** @param {number} a */
export function toByte(a) { const bytes = new Uint8Array(1); bytes[0] = a; return bytes[0]; }
/** @param {number} a */
export function toFloat(a) { return Math.fround(a); }

/** @param {int32} i @param {int32} j */
export function sum(i, j) { return (i + j) | 0; }
/** @param {int32} i @param {int32} j */
export function sumUnsigned(i, j) { return (i + j) >>> 0; }
/** @param {uint32} u @param {uint32} v */
export function difference(u, v) { return (u - v) | 0; }
/** @param {uint32} u @param {uint32} v */
export function differenceUnsigned(u, v) { return (u - v) >>> 0; }
/** @param {int32} i @param {uint32} u */
export function sumMixed(i, u) { return (i + u) | 0; }
/** @param {int32} i @param {int32} j */
export function sumOfThree(i, j) { return (i + j + i) | 0; }
/** @param {int32} i @param {int32} j */
export function sumUnderXor(i, j) { return (i + j) ^ (i - j); }
/** @param {int32} i @param {int32} j */
export function sumUnderShift(i, j) { return (i + j) >>> (i - j); }
/** @param {int32} i */
export function negative(i) { return -i | 0; }
/** @param {uint32} u */
export function negativeUnsigned(u) { return -u >>> 0; }
/** @param {int32} i */
export function negativeUncoerced(i) { return -i; }
/** @param {int32} i @param {int32} j */
export function sumUncoerced(i, j) { return i + j; }
/** @param {int32} i @param {int32} j */
export function longSum(i, j) {
  return (i + j + i + j + i + j + i + j + i + j + i + j + i + j + i + j + i + j + i + j + i + j) | 0;
}
/** @param {int32} i @param {int32} j */
export function sumOfTwenty(i, j) {
  return (i + j + i + j + i + j + i + j + i + j + i + j + i + j + i + j + i + j + i + j + i) | 0;
}

/** @param {int32} i @param {int32} j */
export function product(i, j) { return (i * j) | 0; }
/** @param {number} a @param {number} b */
export function productOfDoubles(a, b) { return (a * b) | 0; }
/** @param {int32} i @param {int32} j */
export function productPlusOne(i, j) { return (i * j + 1) | 0; }
/** @param {int32} i @param {int32} j */
export function imul(i, j) { return Math.imul(i, j); }
/** @param {number} a @param {number} b */
export function imulOfDoubles(a, b) { return Math.imul(a, b); }
/** @param {int32} i @param {int32} j */
export function halfSum(i, j) { return (i / 2 + j / 2) | 0; }
/** @param {int32} i @param {int32} j */
export function divideInts(i, j) { return (i / j) | 0; }
/** @param {int32} i @param {int32} j */
export function remainderInts(i, j) { return (i % j) | 0; }
/** @param {uint32} u @param {uint32} v */
export function divideUints(u, v) { return (u / v) >>> 0; }
/** @param {uint32} u @param {uint32} v */
export function remainderUints(u, v) { return (u % v) >>> 0; }
/** @param {int32} i @param {int32} j */
export function divideIntsToUint(i, j) { return (i / j) >>> 0; }
/** @param {int32} i @param {uint8} c */
export function divideIntByByte(i, c) { return (i / c) | 0; }
