// FNV-1a with 32 bits: offset basis 0x811C9DC5, prime 0x01000193. The product goes through
// Math.imul, which is how JavaScript multiplies in 32 bits.

export function hash(data) {
  let h = 0x811C9DC5;
  for (let i = 0; i < data.length; i++) {
    h = (h ^ data[i]) >>> 0;
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h;
}

// The published check value: FNV-1a of "a" is 0xE40C292C.
export function check() {
  return hash(new Uint8Array([97])) === 0xE40C292C;
}
