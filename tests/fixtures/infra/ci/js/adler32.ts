// Adler-32 as RFC 1950 §8.2 defines it, typed: two sums modulo 65521, s2 in the high half.
const BASE: number = 65521;

export function adler32(bytes: Uint8Array): number {
  let s1: number = 1;
  let s2: number = 0;
  for (const byte of bytes) {
    s1 = (s1 + byte) % BASE;
    s2 = (s2 + s1) % BASE;
  }
  return ((s2 << 16) | s1) >>> 0;
}
