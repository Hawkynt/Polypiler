// Every statement the importer accepts, and the function shapes that can stand in a module. Written
// for this repository; the tests compare what these functions do in node with what the imported
// functions do.

/**
 * @param {int32} value
 * @returns {int32}
 */
export function sign(value) {
  if (value < 0)
    return -1;
  else if (value > 0)
    return 1;

  return 0;
}

/**
 * @param {int32} value
 * @param {int32} low
 * @param {int32} high
 * @returns {int32}
 */
export function clamp(value, low, high) {
  if (value < low) {
    value = low;
  } else {
    if (value > high) {
      value = high;
    }
  }

  return value;
}

/**
 * The sum 1 + 2 + … + count, wrapping around.
 * @param {int32} count
 * @returns {int32}
 */
export function sumTo(count) {
  var total = 0;
  for (var i = 1; i <= count; ++i)
    total = (total + i) | 0;

  return total;
}

/**
 * How often a value can be halved before it is zero.
 * @param {uint32} value
 * @returns {int32}
 */
export function halvings(value) {
  let steps = 0;
  while (value !== 0) {
    value >>>= 1;
    steps++;
  }

  return steps;
}

/**
 * A loop without initializer, condition and update of its own.
 * @param {uint32} count
 * @returns {uint32}
 */
export function countDown(count) {
  let passes = 0 >>> 0;
  for (; count > 0;) {
    --count;
    ++passes;
  }

  return passes;
}

/**
 * @param {int32} size
 * @returns {int32}
 */
export function triangle(size) {
  let cells = 0;
  let row = 0;
  for (row = 0; row < size; row++) {
    for (let column = 0; column <= row; column++) {
      cells++;
    }
  }

  return cells;
}

/**
 * Compound assignments on locals of both 32-bit types.
 * @param {int32} seed
 * @returns {int32}
 */
export function mix(seed) {
  'use strict';
  let state = seed;
  state ^= 0x5BD1E995;
  state &= 0x7FFFFFFF;
  state |= 0x10;
  state <<= 3;
  state >>= 1;
  let word = seed >>> 0;
  word >>>= 4;
  ;
  {
    const folded = (word ^ (word >>> 16)) | 0;
    state ^= folded;
  }

  return state;
}

/**
 * @param {uint8} value
 * @returns {uint8}
 */
export function swapNibbles(value) {
  let low = value & 0x0F;
  const high = value >> 4;
  low <<= 4;
  value = (low | high) & 0xFF;
  return value;
}

/**
 * @param {int32} n
 * @returns {int32}
 */
export function factorial(n) {
  return n <= 1 ? 1 : Math.imul(n, factorial((n - 1) | 0));
}

/**
 * @param {int32} n
 * @returns {boolean}
 */
export function isEven(n) {
  if (n === 0)
    return true;

  return isOdd((n - 1) | 0);
}

/**
 * @param {int32} n
 * @returns {boolean}
 */
function isOdd(n) {
  return n === 0 ? false : isEven((n - 1) | 0);
}

/**
 * @param {int32[]} counter
 * @returns {boolean}
 */
function touch(counter) {
  counter[0] = counter[0] + 1;
  return true;
}

/**
 * The right operand is evaluated only when the left one does not decide.
 * @param {boolean} first
 * @param {int32[]} counter
 * @returns {boolean}
 */
export function both(first, counter) {
  return first && touch(counter);
}

/**
 * @param {boolean} first
 * @param {int32[]} counter
 * @returns {boolean}
 */
export function either(first, counter) {
  return first || touch(counter);
}

/**
 * Operands are evaluated from left to right.
 * @param {int32[]} counter
 * @returns {int32}
 */
export function order(counter) {
  return (next(counter) - Math.imul(next(counter), 10)) | 0;
}

/**
 * @param {int32[]} counter
 * @returns {int32}
 */
function next(counter) {
  counter[0] = counter[0] + 1;
  return counter[0];
}

/**
 * @param {int32[]} data
 * @param {int32} value
 */
export function fill(data, value) {
  if (data.length === 0)
    return;

  for (let i = 0; i < data.length; i++)
    data[i] = value;

  touch(data);
}

/**
 * @param {uint8[]} data
 * @param {uint8} wanted
 * @returns {int32}
 */
export function indexOf(data, wanted) {
  for (let i = 0; i < data.length; i++)
    if (data[i] === wanted)
      return i;

  return -1;
}

/**
 * Where ++ leaves 32 bits, JavaScript counts on and the imported code wraps around.
 * @param {int32} value
 * @returns {boolean}
 */
export function successorIsLarger(value) {
  const before = value;
  value++;
  return value > before;
}
