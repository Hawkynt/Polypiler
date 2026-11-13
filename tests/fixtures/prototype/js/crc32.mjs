// CRC-32 as ISO 3309 and RFC 1952 section 8 define it, computed bit by bit with the reflected
// polynomial 0xEDB88320. Written for this repository; the check value CRC-32("123456789") =
// 0xCBF43926 is the published one.

/**
 * @param {uint8[]} data
 * @returns {uint32}
 */
export function compute(data) {
  let crc = 0xFFFFFFFF;
  for (let i = 0; i < data.length; i++) {
    crc = (crc ^ data[i]) >>> 0;
    for (let bit = 0; bit < 8; bit++)
      crc = (crc & 1) !== 0 ? ((crc >>> 1) ^ 0xEDB88320) >>> 0 : crc >>> 1;
  }

  return (crc ^ 0xFFFFFFFF) >>> 0;
}
