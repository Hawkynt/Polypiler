// CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) bit by bit, without a table and without a type.

export function compute(data) {
  let crc = 0xFFFFFFFF;
  for (let i = 0; i < data.length; i++) {
    crc = (crc ^ data[i]) >>> 0;
    for (let bit = 0; bit < 8; bit++)
      crc = ((crc >>> 1) ^ (0xEDB88320 & -(crc & 1))) >>> 0;
  }
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

// The published check value: CRC-32 of "123456789" is 0xCBF43926.
export function check() {
  return compute(new Uint8Array([49, 50, 51, 52, 53, 54, 55, 56, 57])) === 0xCBF43926;
}
