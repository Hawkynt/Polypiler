// CRC-16/ARC: the bit-reversed polynomial 0xA001, initial value 0, input and output reflected, no
// final xor. Two bytes at a time are read little-endian through a DataView and run through sixteen
// shifts, which for a reflected CRC is the same as one byte after the other: the upper byte reaches
// the feedback bit only after the lower one has been shifted out. Written for this repository; the
// published check value is CRC-16/ARC("123456789") = 0xBB3D.

/**
 * @param {Uint8Array} data
 */
export function compute(data) {
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  let crc = 0;
  let i = 0;
  for (; i + 2 <= data.length; i = (i + 2) | 0) {
    crc ^= view.getUint16(i, true);
    for (let bit = 0; bit < 16; ++bit)
      crc = (crc & 1) !== 0 ? (crc >>> 1) ^ 0xA001 : crc >>> 1;
  }

  if (i < data.length) {
    crc ^= view.getUint8(i);
    for (let bit = 0; bit < 8; ++bit)
      crc = (crc & 1) !== 0 ? (crc >>> 1) ^ 0xA001 : crc >>> 1;
  }

  return crc;
}
