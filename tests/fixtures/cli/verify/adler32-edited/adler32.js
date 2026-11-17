'use strict';
// Adler-32 of RFC 1950 section 8.2, written by hand as the generated output the tests of
// "polypiler verify" check: a = 1 and b = 0, then for every byte a = (a + byte) % 65521 and
// b = (b + a) % 65521; the checksum is b * 65536 + a.
// Edited by hand after it was generated: the manifest records the SHA-256 of the original.
const MOD_ADLER = 65521;

function adler32(bytes) {
  let a = 1;
  let b = 0;
  for (const byte of bytes) {
    a = (a + byte) % MOD_ADLER;
    b = (b + a) % MOD_ADLER;
  }

  return b * 65536 + a;
}

module.exports = { adler32 };
