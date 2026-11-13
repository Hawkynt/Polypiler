// The functions of Math that have an exact result.

/** @param {number} a */
export function trunc(a) { return Math.trunc(a); }
/** @param {number} a */
export function floor(a) { return Math.floor(a); }
/** @param {number} a */
export function ceil(a) { return Math.ceil(a); }
/** @param {number} a */
export function round(a) { return Math.round(a); }
/** @param {number} a */
export function abs(a) { return Math.abs(a); }
/** @param {number} a */
export function sign(a) { return Math.sign(a); }
/** @param {number} a */
export function sqrt(a) { return Math.sqrt(a); }
/** @param {number} a */
export function fround(a) { return Math.fround(a); }
/** @param {number} a */
export function froundSum(a) { return Math.fround(a) + Math.fround(0.1); }
/** @param {number} a */
export function clz32(a) { return Math.clz32(a); }
/** @param {number} a @param {number} b */
export function min(a, b) { return Math.min(a, b); }
/** @param {number} a @param {number} b */
export function max(a, b) { return Math.max(a, b); }
/** @param {number} a @param {number} b */
export function minOfThree(a, b) { return Math.min(a, b, 1); }
/** @param {number} a */
export function maxOfOne(a) { return Math.max(a); }
export function minOfNone() { return Math.min(); }
export function maxOfNone() { return Math.max(); }
/** @param {int32} i */
export function absOfInt(i) { return Math.abs(i); }
/** @param {int32} i @param {uint32} u */
export function maxOfIntegers(i, u) { return Math.max(i, u); }
/** @param {int32} i */
export function roundHalf(i) { return Math.round(i / 2); }
/** @param {number} a */
export function roundToInt(a) { return Math.round(a) | 0; }
