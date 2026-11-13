// One function for every operator the importer accepts, on each of the three integer types. Every
// result is coerced back to the type JSDoc states for it, the way JavaScript code that means a
// fixed-width integer has to. Written for this repository; the tests compare what these functions do
// in node with what the imported functions do.

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function orInt(a, b) {
  return a | b;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function andInt(a, b) {
  return a & b;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function xorInt(a, b) {
  return a ^ b;
}

/**
 * @param {int32} a
 * @returns {int32}
 */
export function notInt(a) {
  return ~a;
}

/**
 * @param {int32} a
 * @param {int32} count
 * @returns {int32}
 */
export function shiftLeftInt(a, count) {
  return a << count;
}

/**
 * @param {int32} a
 * @param {int32} count
 * @returns {int32}
 */
export function shiftRightInt(a, count) {
  return a >> count;
}

/**
 * @param {int32} a
 * @param {int32} count
 * @returns {int32}
 */
export function shiftRightLogicalInt(a, count) {
  return (a >>> count) | 0;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function addInt(a, b) {
  return (a + b) | 0;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function subtractInt(a, b) {
  return (a - b) | 0;
}

/**
 * @param {int32} a
 * @returns {int32}
 */
export function negateInt(a) {
  return (-a) | 0;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function multiplyInt(a, b) {
  return Math.imul(a, b);
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function divideInt(a, b) {
  return (a / b) | 0;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function remainderInt(a, b) {
  return a % b;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {boolean}
 */
export function equalInt(a, b) {
  return a === b;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {boolean}
 */
export function notEqualInt(a, b) {
  return a !== b;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {boolean}
 */
export function lessInt(a, b) {
  return a < b;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {boolean}
 */
export function lessOrEqualInt(a, b) {
  return a <= b;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {boolean}
 */
export function greaterInt(a, b) {
  return a > b;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {boolean}
 */
export function greaterOrEqualInt(a, b) {
  return a >= b;
}

/**
 * @param {boolean} first
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function chooseInt(first, a, b) {
  return first ? a : b;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function orUint(a, b) {
  return (a | b) >>> 0;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function andUint(a, b) {
  return (a & b) >>> 0;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function xorUint(a, b) {
  return (a ^ b) >>> 0;
}

/**
 * @param {uint32} a
 * @returns {uint32}
 */
export function notUint(a) {
  return (~a) >>> 0;
}

/**
 * @param {uint32} a
 * @param {int32} count
 * @returns {uint32}
 */
export function shiftLeftUint(a, count) {
  return (a << count) >>> 0;
}

/**
 * @param {uint32} a
 * @param {int32} count
 * @returns {uint32}
 */
export function shiftRightUint(a, count) {
  return (a >> count) >>> 0;
}

/**
 * @param {uint32} a
 * @param {int32} count
 * @returns {uint32}
 */
export function shiftRightLogicalUint(a, count) {
  return a >>> count;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function addUint(a, b) {
  return (a + b) >>> 0;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function subtractUint(a, b) {
  return (a - b) >>> 0;
}

/**
 * @param {uint32} a
 * @returns {uint32}
 */
export function negateUint(a) {
  return (-a) >>> 0;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function multiplyUint(a, b) {
  return Math.imul(a, b) >>> 0;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function divideUint(a, b) {
  return (a / b) >>> 0;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function remainderUint(a, b) {
  return a % b;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {boolean}
 */
export function equalUint(a, b) {
  return a === b;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {boolean}
 */
export function notEqualUint(a, b) {
  return a !== b;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {boolean}
 */
export function lessUint(a, b) {
  return a < b;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {boolean}
 */
export function lessOrEqualUint(a, b) {
  return a <= b;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {boolean}
 */
export function greaterUint(a, b) {
  return a > b;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {boolean}
 */
export function greaterOrEqualUint(a, b) {
  return a >= b;
}

/**
 * @param {boolean} first
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function chooseUint(first, a, b) {
  return first ? a : b;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function orByte(a, b) {
  return (a | b) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function andByte(a, b) {
  return (a & b) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function xorByte(a, b) {
  return (a ^ b) & 0xFF;
}

/**
 * @param {uint8} a
 * @returns {uint8}
 */
export function notByte(a) {
  return (~a) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {int32} count
 * @returns {uint8}
 */
export function shiftLeftByte(a, count) {
  return (a << count) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {int32} count
 * @returns {uint8}
 */
export function shiftRightByte(a, count) {
  return (a >> count) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {int32} count
 * @returns {uint8}
 */
export function shiftRightLogicalByte(a, count) {
  return (a >>> count) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function addByte(a, b) {
  return (a + b) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function subtractByte(a, b) {
  return (a - b) & 0xFF;
}

/**
 * @param {uint8} a
 * @returns {uint8}
 */
export function negateByte(a) {
  return (-a) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function multiplyByte(a, b) {
  return Math.imul(a, b) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function divideByte(a, b) {
  return ((a / b) | 0) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function remainderByte(a, b) {
  return (a % b) & 0xFF;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {boolean}
 */
export function equalByte(a, b) {
  return a === b;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {boolean}
 */
export function notEqualByte(a, b) {
  return a !== b;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {boolean}
 */
export function lessByte(a, b) {
  return a < b;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {boolean}
 */
export function lessOrEqualByte(a, b) {
  return a <= b;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {boolean}
 */
export function greaterByte(a, b) {
  return a > b;
}

/**
 * @param {uint8} a
 * @param {uint8} b
 * @returns {boolean}
 */
export function greaterOrEqualByte(a, b) {
  return a >= b;
}

/**
 * @param {boolean} first
 * @param {uint8} a
 * @param {uint8} b
 * @returns {uint8}
 */
export function chooseByte(first, a, b) {
  return first ? a : b;
}

/**
 * @param {int32} a
 * @returns {uint32}
 */
export function toUint(a) {
  return a >>> 0;
}

/**
 * @param {uint32} a
 * @returns {int32}
 */
export function toInt(a) {
  return a | 0;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {uint32}
 */
export function divideIntToUint(a, b) {
  return (a / b) >>> 0;
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {int32}
 */
export function divideUintToInt(a, b) {
  return (a / b) | 0;
}

/**
 * @param {int32} a
 * @param {int32} count
 * @returns {uint32}
 */
export function shiftRightLogicalIntToUint(a, count) {
  return a >>> count;
}

/**
 * @param {int32} a
 * @param {uint32} count
 * @returns {int32}
 */
export function shiftByUint(a, count) {
  return a << count;
}

/**
 * @param {int32} a
 * @returns {uint8}
 */
export function maskLowByte(a) {
  return a & 0xFF;
}

/**
 * @param {int32} a
 * @param {uint32} b
 * @param {uint8} c
 * @returns {int32}
 */
export function addThree(a, b, c) {
  return (a + b - c) | 0;
}

/**
 * @param {int32} a
 * @param {int32} b
 * @returns {int32}
 */
export function addInsideXor(a, b) {
  return (a + b) ^ (a - b);
}

/**
 * @param {uint32} a
 * @param {uint32} b
 * @returns {uint32}
 */
export function complementOfSum(a, b) {
  return ~(a + b) >>> 0;
}

/**
 * @param {uint32} a
 * @param {int32} b
 * @returns {uint32}
 */
export function shiftBySum(a, b) {
  return a >>> (32 - b);
}

/**
 * @param {boolean} a
 * @param {boolean} b
 * @returns {boolean}
 */
export function both(a, b) {
  return a && b;
}

/**
 * @param {boolean} a
 * @param {boolean} b
 * @returns {boolean}
 */
export function either(a, b) {
  return a || b;
}

/**
 * @param {boolean} a
 * @returns {boolean}
 */
export function negation(a) {
  return !a;
}

/**
 * @param {boolean} a
 * @param {boolean} b
 * @returns {boolean}
 */
export function equalTruth(a, b) {
  return a === b;
}

/**
 * @param {boolean} a
 * @param {boolean} b
 * @returns {boolean}
 */
export function notEqualTruth(a, b) {
  return a !== b;
}

/**
 * @param {boolean} first
 * @param {boolean} a
 * @param {boolean} b
 * @returns {boolean}
 */
export function chooseTruth(first, a, b) {
  return first ? a : b;
}
