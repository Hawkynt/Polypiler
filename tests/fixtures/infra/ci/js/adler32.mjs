// Adler-32 as RFC 1950 §8.2 defines it: two sums modulo 65521, s2 in the high half.
const BASE = 65521;

export function adler32(bytes) {
  let s1 = 1;
  let s2 = 0;
  for (const byte of bytes) {
    s1 = (s1 + byte) % BASE;
    s2 = (s2 + s1) % BASE;
  }
  return ((s2 << 16) | s1) >>> 0;
}

if (import.meta.main)
  console.log(adler32(new TextEncoder().encode("abc")));
