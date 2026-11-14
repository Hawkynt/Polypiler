// The methods of a DataView made of a Uint8Array of the function. Written for this repository.

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 */
export function little32(bytes, i) {
  const view = new DataView(bytes.buffer);
  return view.getUint32(i, true);
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 */
export function big16(bytes, i) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return view.getInt16(i);
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 */
export function big64(bytes, i) {
  const view = new DataView(bytes.buffer);
  return view.getBigUint64(i, false);
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 * @param {int32} x
 */
export function store(bytes, i, x) {
  const view = new DataView(bytes.buffer);
  view.setInt32(i, x, true);
  view.setUint8(0, i);
  return bytes;
}
