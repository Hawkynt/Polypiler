// FNV-1a with 32 bits as Fowler, Noll and Vo define it: offset basis 0x811C9DC5, prime 0x01000193.
// Written for this repository; the test vector FNV-1a-32("a") = 0xE40C292C is the published one.

/**
 * @param {uint8[]} data
 * @returns {uint32}
 */
export function hash(data) {
  let value = 0x811C9DC5;
  for (let i = 0; i < data.length; i++)
    value = Math.imul(value ^ data[i], 0x01000193) >>> 0;

  return value;
}
