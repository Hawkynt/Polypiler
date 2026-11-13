// Every kind of typed array read, written and measured, array literals of integers and of other
// numbers, and a new Array that is filled by indexed stores.

/** @param {number} a */
export function int8(a) { const items = new Int8Array(2); items[0] = a; items[1] += a; items[1]++; return items[0] + items[1] + items.length; }
/** @param {number} a */
export function uint8(a) { const items = new Uint8Array(2); items[0] = a; items[1] -= a; items[1]--; return items[0] + items[1] + items.length; }
/** @param {number} a */
export function int16(a) { const items = new Int16Array(2); items[0] = a; items[1] ^= a; return items[0] + items[1] + items.length; }
/** @param {number} a */
export function uint16(a) { const items = new Uint16Array(2); items[0] = a; items[1] |= a; return items[0] + items[1] + items.length; }
/** @param {number} a */
export function int32(a) { const items = new Int32Array(2); items[0] = a; items[1] += a; items[1] <<= 1; return items[0] + items[1] + items.length; }
/** @param {number} a */
export function uint32(a) { const items = new Uint32Array(2); items[0] = a; items[1] -= a; items[1] >>>= 1; return items[0] + items[1] + items.length; }
/** @param {number} a */
export function float32(a) { const items = new Float32Array(2); items[0] = a; items[1] += a; items[1]++; return items[0] + items[1] + items.length; }
/** @param {number} a */
export function float64(a) { const items = new Float64Array(2); items[0] = a; items[1] += a; items[1] /= 3; items[1]--; return items[0] + items[1] + items.length; }
/** @param {int64} a */
export function bigInt64(a) { const items = new BigInt64Array(2); items[0] = a; items[1] = BigInt.asIntN(64, a * 3n); items[1]++; return BigInt.asIntN(64, items[0] + items[1]); }
/** @param {uint64} a */
export function bigUint64(a) { const items = new BigUint64Array(2); items[0] = a; items[1] = BigInt.asUintN(64, a * 3n); items[1]--; return BigInt.asUintN(64, items[0] ^ items[1]); }
/** @param {number} a */
export function postfixElement(a) { const items = new Uint8Array(1); items[0] = a; const before = items[0]++; return before + items[0] * 1000; }
/** @param {number} a */
export function listedElements(a) { const items = new Uint8Array([1, 2, 300, a]); return items[0] + items[1] + items[2] + items[3] + items.length; }
/** @param {number} a */
export function listedFloats(a) { const items = new Float32Array([0.1, a]); return items[0] + items[1]; }
/** @param {int32} n */
export function literalOfIntegers(n) { const items = [1, 2, 3, n]; items[1] = n >> 1; return items[0] + items[1] + items[3] + items.length; }
/** @param {int32} n */
export function literalOfNumbers(n) { const items = [1, 2.5, n]; return items[0] + items[1] + items[2]; }
/** @param {int32} n */
export function literalThenFraction(n) { const items = [1, 2, 3]; items[0] = n / 2; return items[0] + items[1]; }
/** @param {int32} n */
export function literalStepped(n) { const items = [1, 2, n]; items[2]++; items[0] += 0.5; return items[0] + items[2]; }
/** @param {int32} n */
export function emptyLiteral(n) { const items = []; return items.length + n; }
/** @param {int32} n */
export function newArray(n) { const items = new Array(4); for (let i = 0; i < 4; i++) items[i] = (n + i) | 0; return items[0] + items[3] + items.length; }
/** @param {int32} n */
export function newArrayOfNumbers(n) { const items = new Array(n & 3); for (let i = 0; i < items.length; i++) items[i] = n / 2; let sum = 0; for (let i = 0; i < items.length; i++) sum += items[i]; return sum; }
export function newArrayWithoutLength() { const items = new Array(); return items.length; }
/** @param {number} a */
export function fractionalLength(a) { return new Uint8Array(Math.abs(a % 8)).length; }
/** @param {uint32} u */
export function unsignedLength(u) { return new Uint8Array(u & 15).length + new Array(u & 3).length; }
/** @param {uint32} u */
export function unsignedIndex(u) { const items = new Uint8Array([5, 6, 7, 8]); return items[u & 3] + items[u >>> 30]; }
/** @param {int32} n */
export function chosenArray(n) { const first = [1, 2]; const second = [3, 4]; return (n > 0 ? first : second)[1]; }
/** @param {int32} n */
export function returnedLiteral(n) { return pair(n)[1]; }
function pair(n) { return [n & 0xFF, (n >> 8) & 0xFF]; }

/** @param {Int8Array} items */
export function sumInt8(items) { let sum = 0; for (let i = 0; i < items.length; i++) sum += items[i]; return sum; }
/** @param {Uint16Array} items */
export function sumUint16(items) { let sum = 0; for (let i = 0; i < items.length; i++) sum += items[i]; return sum; }
/** @param {Int16Array} items */
export function sumInt16(items) { let sum = 0; for (let i = 0; i < items.length; i++) sum += items[i]; return sum; }
/** @param {Float32Array} items */
export function sumFloat32(items) { let sum = 0; for (let i = 0; i < items.length; i++) sum += items[i]; return sum; }
/** @param {Float64Array} items */
export function scaleFloat64(items) { for (let i = 0; i < items.length; i++) items[i] *= 1.5; return items.length; }
/** @param {Float32Array} items */
export function scaleFloat32(items) { for (let i = 0; i < items.length; i++) items[i] = items[i] * 1.1; return items.length; }
/** @param {BigUint64Array} items */
export function xorBigUint64(items) { let all = 0n; for (let i = 0; i < items.length; i++) all = BigInt.asUintN(64, all ^ items[i]); return all; }
/** @param {BigInt64Array} items */
export function negateBigInt64(items) { for (let i = 0; i < items.length; i++) items[i] = BigInt.asIntN(64, -items[i]); return items.length; }
/** @param {Uint8Array} items @param {number} a */
export function readAt(items, a) { return items[a] | 0; }
/** @param {number[]} items */
export function sumNumbers(items) { let sum = 0; for (let i = 0; i < items.length; i++) sum += items[i]; return sum; }
