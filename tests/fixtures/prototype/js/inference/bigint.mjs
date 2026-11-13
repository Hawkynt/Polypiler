// 64-bit integers: BigInt values that BigInt.asUintN(64, …) or BigInt.asIntN(64, …) bounds.

/** @param {uint64} a @param {uint64} b */
export function add(a, b) { return BigInt.asUintN(64, a + b); }
/** @param {uint64} a @param {uint64} b */
export function subtract(a, b) { return BigInt.asUintN(64, a - b); }
/** @param {uint64} a @param {uint64} b */
export function multiply(a, b) { return BigInt.asUintN(64, a * b); }
/** @param {uint64} a @param {uint64} b */
export function and(a, b) { return BigInt.asUintN(64, a & b); }
/** @param {uint64} a @param {uint64} b */
export function or(a, b) { return BigInt.asUintN(64, a | b); }
/** @param {uint64} a @param {uint64} b */
export function xor(a, b) { return BigInt.asUintN(64, a ^ b); }
/** @param {uint64} a */
export function not(a) { return BigInt.asUintN(64, ~a); }
/** @param {uint64} a */
export function negate(a) { return BigInt.asUintN(64, -a); }
/** @param {uint64} a */
export function shiftLeft(a) { return BigInt.asUintN(64, a << 13n); }
/** @param {uint64} a */
export function shiftLeftBy63(a) { return BigInt.asUintN(64, a << 63n); }
/** @param {uint64} a */
export function shiftRight(a) { return BigInt.asUintN(64, a >> 7n); }
/** @param {uint64} a */
export function shiftRightBy0(a) { return BigInt.asUintN(64, a >> 0n); }
/** @param {uint64} a @param {uint64} b */
export function divide(a, b) { return BigInt.asUintN(64, a / b); }
/** @param {uint64} a @param {uint64} b */
export function remainder(a, b) { return BigInt.asUintN(64, a % b); }
/** @param {uint64} a @param {uint64} b */
export function mix(a, b) { return BigInt.asUintN(64, (a ^ (a >> 30n)) * 0xBF58476D1CE4E5B9n + (b << 1n) - 1n); }
/** @param {uint64} a @param {uint64} b */
export function less(a, b) { return a < b; }
/** @param {uint64} a @param {uint64} b */
export function equal(a, b) { return a === b; }
/** @param {uint64} a */
export function isZero(a) { return a ? 0 : 1; }
/** @param {uint64} a */
export function isLarge(a) { return a >= 9223372036854775808n; }
/** @param {uint64} a */
export function toSigned(a) { return BigInt.asIntN(64, a); }

/** @param {int64} a @param {int64} b */
export function addSigned(a, b) { return BigInt.asIntN(64, a + b); }
/** @param {int64} a @param {int64} b */
export function multiplySigned(a, b) { return BigInt.asIntN(64, a * b); }
/** @param {int64} a */
export function negateSigned(a) { return BigInt.asIntN(64, -a); }
/** @param {int64} a */
export function notSigned(a) { return BigInt.asIntN(64, ~a); }
/** @param {int64} a */
export function shiftRightSigned(a) { return BigInt.asIntN(64, a >> 7n); }
/** @param {int64} a */
export function shiftLeftSigned(a) { return BigInt.asIntN(64, a << 1n); }
/** @param {int64} a @param {int64} b */
export function divideSigned(a, b) { return BigInt.asIntN(64, a / b); }
/** @param {int64} a @param {int64} b */
export function remainderSigned(a, b) { return BigInt.asIntN(64, a % b); }
/** @param {int64} a @param {int64} b */
export function lessSigned(a, b) { return a < b; }
/** @param {int64} a */
export function isNegative(a) { return a < 0n; }
/** @param {int64} a */
export function toUnsigned(a) { return BigInt.asUintN(64, a); }
/** @param {int64} a */
export function steps(a) { let x = a; let count = 0; while (x !== 0n && count < 70) { x = BigInt.asIntN(64, x >> 1n); count = (count + 1) | 0; } return count; }
/** @param {int32} i */
export function fromInt(i) { return BigInt.asIntN(64, BigInt(i) * 4294967296n); }
/** @param {uint32} u */
export function fromUint(u) { return BigInt.asUintN(64, BigInt(u) * 4294967297n); }
export function largest() { return 18446744073709551615n; }
export function smallest() { return -9223372036854775808n; }
