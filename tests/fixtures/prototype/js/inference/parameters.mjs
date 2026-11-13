// One function, `scale`, in every way a parameter can get its type: stated in JSDoc, read under a
// coercion only, re-assigned coerced before anything else, used in plain arithmetic, and passed by
// the callers of a function that is not exported.

/** @param {int32} a */
export function documented(a) { return (a * 3) | 0; }
/** @param {uint8} a */
export function documentedByte(a) { return (a * 3) | 0; }
/** @param {number} a */
export function documentedNumber(a) { return (a * 3) | 0; }
/** @param {float32} a */
export function documentedFloat(a) { return a * 3; }
/** @param {int16} a @param {uint16} b @param {int8} c */
export function documentedSmall(a, b, c) { return a + b + c; }
/** @param {int32} a @returns {number} */
export function documentedResult(a) { return a | 0; }
/** @param {uint8} a @returns {int32} */
export function widenedResult(a) { return a; }
/** @param {int32} a @returns {int32} */
export function contradictedResult(a) { return a / 2; }
/** @param {int32} a */
export function reassigned(a) { a = a / 2; return a; }

export function coerced(a) { return (a << 1) + (a | 0); }
export function coercedUnsigned(a) { return (a >>> 0) + (a >>> 1); }
export function coercedBoth(a) { return (a >>> 0) + (a | 0); }
export function coercedByImul(a, b) { return Math.imul(a, b) + Math.clz32(b); }
export function coercedCompound(a, b) { a ^= b; a <<= 3; return a; }
export function prologue(a, b) { a = a | 0; b = b >>> 0; return a * 3 + b; }
export function plain(a) { return a * 3; }
export function coercedAndPlain(a) { return (a | 0) + a; }
export function unused(a) { return 1; }
export function stepped(a) { a++; return a | 0; }

export function throughInteger(a) { return helperOfIntegers(a | 0) + helperOfIntegers(7); }
export function throughBoth(a) { return helperOfBoth(a | 0) + helperOfBoth(a / 2); }
export function throughBytes(a) { return helperOfBytes(a & 0xFF) + helperOfBytes(3); }
function helperOfIntegers(x) { return x * 3; }
function helperOfBoth(x) { return x * 3; }
function helperOfBytes(x) { return x; }
function neverCalled(x) { return x | 0; }
