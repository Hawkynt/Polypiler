// CRC-32 of ISO 3309 / RFC 1952 section 8 with the reflected polynomial 0xEDB88320, by a table that
// the first call fills and every later call reuses, guarded by a flag. Written for this repository;
// the check value CRC-32("123456789") = 0xCBF43926 is the published one.

const table = new Uint32Array(256);
let filled = false;

function fill() {
  for (let n = 0; n < 256; n = (n + 1) | 0) {
    let c = n >>> 0;
    for (let bit = 0; bit < 8; bit = (bit + 1) | 0)
      c = (c & 1) !== 0 ? ((c >>> 1) ^ 0xEDB88320) >>> 0 : c >>> 1;

    table[n] = c;
  }

  filled = true;
}

/**
 * @param {uint8[]} data
 * @returns {uint32}
 */
export function compute(data) {
  if (!filled)
    fill();

  let crc = 0xFFFFFFFF;
  for (let i = 0; i < data.length; i++)
    crc = (table[(crc ^ data[i]) & 0xFF] ^ (crc >>> 8)) >>> 0;

  return (crc ^ 0xFFFFFFFF) >>> 0;
}

export function isFilled() {
  return filled;
}
