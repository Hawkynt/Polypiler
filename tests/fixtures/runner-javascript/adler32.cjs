// Adler-32 of RFC 1950 as a CommonJS module, exported at the top and below Lib.
function adler32(data) {
  let a = 1;
  let b = 0;
  for (const byte of data) {
    a = (a + byte) % 65521;
    b = (b + a) % 65521;
  }
  return ((b << 16) | a) >>> 0;
}

module.exports = { adler32, Lib: { adler32 } };
