// Typed arrays: creation, element reads and writes, length. Written for this repository; the tests
// compare what these functions do in node with what the imported functions do. Outside an array the
// two differ on purpose - JavaScript reads undefined and ignores a write, the imported code traps.

/**
 * @param {uint8[]} data
 * @param {int32} index
 * @returns {uint8}
 */
export function readByte(data, index) {
  return data[index];
}

/**
 * @param {Int32Array} data
 * @param {int32} index
 * @returns {int32}
 */
export function readInt(data, index) {
  return data[index];
}

/**
 * @param {Uint32Array} data
 * @param {int32} index
 * @returns {uint32}
 */
export function readUint(data, index) {
  return data[index];
}

/**
 * @param {Uint8Array} data
 * @param {int32} index
 * @returns {int32}
 */
export function readCoerced(data, index) {
  return data[index] | 0;
}

/**
 * @param {int32[]} data
 * @param {uint32} index
 * @returns {int32}
 */
export function readAtUnsignedIndex(data, index) {
  return data[index];
}

/**
 * @param {int32[]} data
 * @param {uint8} index
 * @returns {int32}
 */
export function readAtByteIndex(data, index) {
  return data[index];
}

/**
 * @param {uint32[]} data
 * @param {int32} a
 * @param {int32} b
 * @returns {uint32}
 */
export function readAtSum(data, a, b) {
  return data[a + b];
}

/**
 * @param {uint8[]} data
 * @param {int32} index
 * @param {int32} value
 */
export function writeByte(data, index, value) {
  data[index] = value;
}

/**
 * @param {int32[]} data
 * @param {int32} index
 * @param {uint32} value
 */
export function writeInt(data, index, value) {
  data[index] = value;
}

/**
 * @param {uint32[]} data
 * @param {int32} index
 * @param {int32} value
 */
export function writeUint(data, index, value) {
  data[index] = value;
}

/**
 * @param {uint8[]} data
 * @param {int32} index
 * @param {int32} a
 * @param {int32} b
 */
export function writeSum(data, index, a, b) {
  data[index] = a + b;
}

/**
 * @param {uint8[]} data
 * @returns {int32}
 */
export function lengthOfBytes(data) {
  return data.length;
}

/**
 * @param {int32[]} data
 * @returns {int32}
 */
export function lengthOfInts(data) {
  return data.length;
}

/**
 * @param {uint32[]} data
 * @returns {int32}
 */
export function lengthOfUints(data) {
  return data.length;
}

/**
 * @param {int32} length
 * @returns {uint8[]}
 */
export function countUp(length) {
  const result = new Uint8Array(length);
  for (let i = 0; i < length; i++)
    result[i] = i;

  return result;
}

/**
 * @param {int32} length
 * @returns {int32[]}
 */
export function powers(length) {
  const result = new Int32Array(length);
  let power = 1;
  for (let i = 0; i < length; ++i) {
    result[i] = power;
    power = Math.imul(power, -3);
  }

  return result;
}

/**
 * @param {uint8} length
 * @returns {uint32[]}
 */
export function ramp(length) {
  const result = new Uint32Array(length);
  for (let i = 0; i < length; i++)
    result[i] = 0xFFFFFFFF - i;

  return result;
}

/**
 * @param {int32[]} data
 * @returns {int32}
 */
export function sum(data) {
  let total = 0;
  for (let i = 0; i < data.length; i++)
    total = (total + data[i]) | 0;

  return total;
}

/**
 * @param {int32[]} data
 */
export function reverse(data) {
  let low = 0;
  let high = (data.length - 1) | 0;
  while (low < high) {
    const kept = data[low];
    data[low] = data[high];
    data[high] = kept;
    low++;
    high--;
  }
}

/**
 * Every element but the first becomes the sum of itself and its predecessors.
 * @param {uint32[]} data
 */
export function accumulate(data) {
  for (let i = 1; i < data.length; i++)
    data[i] += data[i - 1];
}

/**
 * The compound assignment reads and writes one element, whose index is computed once.
 * @param {uint8[]} data
 * @param {int32} seed
 */
export function scramble(data, seed) {
  for (let i = 0; i < data.length; i++) {
    data[(i + seed) & 3] ^= (seed >> i) & 0xFF;
    data[i] <<= 1;
    data[i] |= 1;
    data[i] -= seed;
  }
}

/**
 * @param {uint8[]} first
 * @param {uint8[]} second
 * @param {boolean} takeFirst
 * @returns {int32}
 */
export function lengthOfChosen(first, second, takeFirst) {
  return (takeFirst ? first : second).length;
}
