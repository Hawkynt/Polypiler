// Floating-point numbers in byte buffers: binary32 and binary64 read and written in either byte
// order through a DataView of a Uint8Array, and the sample block of a WAV file of IEEE float samples
// (format tag 3, 32 bits per sample) summed as a double. Written for this repository.

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 */
export function getF32LE(bytes, i) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return view.getFloat32(i, true);
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 */
export function getF32BE(bytes, i) {
  const view = new DataView(bytes.buffer);
  return view.getFloat32(i);
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 */
export function getF64LE(bytes, i) {
  const view = new DataView(bytes.buffer);
  return view.getFloat64(i, true);
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 */
export function getF64BE(bytes, i) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return view.getFloat64(i, false);
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 * @param {number} x
 */
export function putF32LE(bytes, i, x) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  view.setFloat32(i, x, true);
  return bytes;
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 * @param {number} x
 */
export function putF32BE(bytes, i, x) {
  const view = new DataView(bytes.buffer);
  view.setFloat32(i, x);
  return bytes;
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 * @param {number} x
 */
export function putF64LE(bytes, i, x) {
  const view = new DataView(bytes.buffer);
  view.setFloat64(i, x, true);
  return bytes;
}

/**
 * @param {Uint8Array} bytes
 * @param {int32} i
 * @param {number} x
 */
export function putF64BE(bytes, i, x) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  view.setFloat64(i, x, false);
  return bytes;
}

// The canonical 44-byte header: "RIFF", the format chunk at 12 with the format tag at 20 and the
// bits per sample at 34, the size of the data chunk at 40 and its samples from 44 on. A file of
// another format, or one shorter than its data chunk says, has no sum: -1.
/**
 * @param {Uint8Array} wav
 */
export function sumSamples(wav) {
  const view = new DataView(wav.buffer, wav.byteOffset, wav.byteLength);
  if (wav.length < 44 || view.getUint16(20, true) !== 3 || view.getUint16(34, true) !== 32)
    return -1;

  const size = view.getInt32(40, true);
  if (size < 0 || size > wav.length - 44)
    return -1;

  let sum = 0;
  for (let i = 44; i + 4 <= 44 + size; i = (i + 4) | 0)
    sum += view.getFloat32(i, true);

  return sum;
}
