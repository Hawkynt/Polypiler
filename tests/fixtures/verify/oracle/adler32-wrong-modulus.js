'use strict';
// A faulty Adler-32: the modulus is 65536 instead of the 65521 of RFC 1950 section 8.2. Standard
// input is the message as hexadecimal text; standard output is the checksum as 8 hexadecimal digits.
const MOD_ADLER = 65536;

const text = require('fs').readFileSync(0, 'utf8').trim();
let a = 1;
let b = 0;
for (let index = 0; index < text.length; index += 2) {
  a = (a + parseInt(text.substring(index, index + 2), 16)) % MOD_ADLER;
  b = (b + a) % MOD_ADLER;
}

process.stdout.write((((b << 16) | a) >>> 0).toString(16).padStart(8, '0') + '\n');
