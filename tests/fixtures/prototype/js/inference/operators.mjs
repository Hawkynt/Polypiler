// Every operator on doubles, on a double and an integer, and on integers of the two signednesses.
// JSDoc states `number` where an operand is to be a double whatever the function does with it.

/** @param {number} a @param {number} b */
export function add(a, b) { return a + b; }
/** @param {number} a @param {number} b */
export function subtract(a, b) { return a - b; }
/** @param {number} a @param {number} b */
export function multiply(a, b) { return a * b; }
/** @param {number} a @param {number} b */
export function quotient(a, b) { return a / b; }
/** @param {number} a @param {number} b */
export function modulo(a, b) { return a % b; }
/** @param {number} a */
export function negate(a) { return -a; }
/** @param {number} a */
export function plus(a) { return +a; }
/** @param {number} a @param {number} b */
export function or(a, b) { return a | b; }
/** @param {number} a @param {number} b */
export function and(a, b) { return a & b; }
/** @param {number} a @param {number} b */
export function xor(a, b) { return a ^ b; }
/** @param {number} a */
export function not(a) { return ~a; }
/** @param {number} a @param {number} b */
export function shiftLeft(a, b) { return a << b; }
/** @param {number} a @param {number} b */
export function shiftRight(a, b) { return a >> b; }
/** @param {number} a @param {number} b */
export function shiftRightLogical(a, b) { return a >>> b; }
/** @param {number} a @param {number} b */
export function less(a, b) { return a < b; }
/** @param {number} a @param {number} b */
export function lessOrEqual(a, b) { return a <= b; }
/** @param {number} a @param {number} b */
export function greater(a, b) { return a > b; }
/** @param {number} a @param {number} b */
export function greaterOrEqual(a, b) { return a >= b; }
/** @param {number} a @param {number} b */
export function strictlyEqual(a, b) { return a === b; }
/** @param {number} a @param {number} b */
export function strictlyUnequal(a, b) { return a !== b; }
/** @param {number} a @param {number} b */
export function looselyEqual(a, b) { return a == b; }
/** @param {number} a @param {number} b */
export function looselyUnequal(a, b) { return a != b; }
/** @param {number} a */
export function truthy(a) { return a ? 1 : 0; }
/** @param {number} a */
export function falsy(a) { return !a; }
/** @param {number} a @param {number} b */
export function andValue(a, b) { return a && b; }
/** @param {number} a @param {number} b */
export function orValue(a, b) { return a || b; }
/** @param {number} a @param {number} b */
export function andCondition(a, b) { return a && b ? 1 : 0; }
/** @param {number} a @param {number} b */
export function orCondition(a, b) { if (a || b) return 1; return 0; }
/** @param {number} a @param {number} b */
export function choose(a, b) { return a > b ? a : b; }

/** @param {int32} i @param {number} d */
export function addMixed(i, d) { return i + d; }
/** @param {int32} i @param {number} d */
export function multiplyMixed(i, d) { return i * d; }
/** @param {int32} i @param {number} d */
export function moduloMixed(i, d) { return i % d; }
/** @param {int32} i @param {number} d */
export function lessMixed(i, d) { return i < d; }
/** @param {int32} i @param {number} d */
export function equalMixed(i, d) { return i === d; }
/** @param {int32} i @param {number} d */
export function xorMixed(i, d) { return i ^ d; }
/** @param {int32} i @param {number} d */
export function chooseMixed(i, d) { return d > 0 ? i : d; }

/** @param {int32} i @param {uint32} u */
export function lessSignedUnsigned(i, u) { return i < u; }
/** @param {int32} i @param {uint32} u */
export function equalSignedUnsigned(i, u) { return i === u; }
/** @param {int32} i @param {uint32} u */
export function addSignedUnsigned(i, u) { return i + u; }
/** @param {int32} i @param {uint32} u */
export function moduloSignedUnsigned(i, u) { return i % u; }
/** @param {int32} i @param {uint32} u */
export function quotientSignedUnsigned(i, u) { return (i / u) | 0; }
/** @param {int32} i @param {uint8} c */
export function lessIntByte(i, c) { return i < c; }
/** @param {uint32} u @param {uint8} c */
export function lessUintByte(u, c) { return u < c; }
/** @param {int32} i */
export function truthyInt(i) { return i ? 1 : 0; }
/** @param {uint32} u */
export function truthyUint(u) { return u ? 1 : 0; }
/** @param {uint8} c */
export function truthyByte(c) { return c ? 1 : 0; }
/** @param {int32} i @param {int32} j */
export function orValueInts(i, j) { return i || j; }
/** @param {boolean} s @param {boolean} t */
export function andTruths(s, t) { return s && t; }
/** @param {boolean} s @param {boolean} t */
export function equalTruths(s, t) { return s === t; }
/** @param {boolean} s @param {boolean} t */
export function unequalTruths(s, t) { return s != t; }
/** @param {int32} i @param {int32} j */
export function moduloInts(i, j) { return i % j; }
/** @param {int32} i @param {int32} j */
export function moduloIntsIsZero(i, j) { return i % j === 0; }
/** @param {int32} i @param {int32} j */
export function quotientInts(i, j) { return i / j; }
