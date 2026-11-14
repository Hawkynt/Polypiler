// Views of a Uint8Array: subarray shares the bytes, slice copies them, and both clamp their positions
// as ECMA-262 says. The bytes d are 0, 1, ..., 15 in the scenarios. Written for this repository.

/**
 * 84148994: the bytes 2 to 5 through a DataView of a subarray, the lowest first.
 * @param {uint8[]} d
 * @returns {uint32}
 */
export function word(d) {
  const v = d.subarray(2, 6);
  const view = new DataView(v.buffer, v.byteOffset, v.byteLength);
  return view.getUint32(0, true);
}

/**
 * 54: the last four bytes.
 * @param {uint8[]} d
 * @returns {int32}
 */
export function tail(d) {
  const v = d.subarray(-4);
  let total = 0;
  for (let i = 0; i < v.length; i++) {
    total = total + v[i];
  }

  return total;
}

/**
 * 3, 0, 4 and 11 of 16 bytes: an end past the length, a begin past the end, a negative begin, a negative end.
 * @param {uint8[]} d
 * @returns {int32[]}
 */
export function lengths(d) {
  const r = new Int32Array(4);
  r[0] = d.subarray(13, 17).length;
  r[1] = d.subarray(5, 2).length;
  r[2] = d.subarray(-4).length;
  r[3] = d.subarray(0, -5).length;
  return r;
}

/**
 * 0 and 99: slice copies, subarray aliases.
 * @param {uint8[]} d
 * @returns {int32[]}
 */
export function sliceVsSub(d) {
  const s = d.slice(0, 1);
  const u = d.subarray(0, 1);
  u[0] = 99;
  const r = new Int32Array(2);
  r[0] = s[0];
  r[1] = d[0];
  return r;
}

/**
 * The sum of the bytes after the four from the offset on are copied over the start and the rest filled with 7.
 * @param {uint8[]} d
 * @param {int32} offset
 * @returns {int32}
 */
export function copied(d, offset) {
  d.set(d.subarray(offset, offset + 4));
  d.fill(7, 4, -2);
  let total = 0;
  for (let i = 0; i < d.length; i++) {
    total = total + d[i];
  }

  return total;
}
