// The P0 source-language baseline of ADR 0006: one checksum of Cipher, the pinned upstream code under
// cipher/, over a deterministic message, in node.
//
//   node cipher-checksum.js <module> <algorithm> <bytes> <expected hex>
//
// <module> is a file of cipher/algorithms/checksum without its extension, <algorithm> the name it
// registers (Adler-32, Fletcher-32, CRC-32-IEEE). The message is <bytes> bytes of a 32-bit linear
// congruential generator (Numerical Recipes: a = 1664525, c = 1013904223, seed 0), the top byte of
// each state. The program prints "ok" when the checksum is <expected hex> and fails otherwise, so a
// run that computes something else is not a measurement.
'use strict';

const [moduleName, algorithmName, sizeText, expected] = process.argv.slice(2);
const framework = require('./cipher/AlgorithmFramework');
require('./cipher/algorithms/checksum/' + moduleName);

const algorithm = framework.Find(algorithmName);
if (algorithm === null)
  throw new Error(`${moduleName} registers no ${algorithmName}`);

const size = Number.parseInt(sizeText, 10);
const message = new Array(size);
let state = 0;
for (let i = 0; i < size; ++i) {
  state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
  message[i] = state >>> 24;
}

const instance = algorithm.CreateInstance();
instance.Feed(message);
const actual = instance.Result().map(value => value.toString(16).padStart(2, '0')).join('');
if (actual !== expected) {
  process.stderr.write(`${algorithmName} of ${size} bytes is ${actual}, not ${expected}\n`);
  process.exit(1);
}

process.stdout.write('ok\n');
