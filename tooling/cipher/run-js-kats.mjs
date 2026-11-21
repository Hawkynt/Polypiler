// Runs the known-answer tests node recorded for one Cipher algorithm file (#1365) on its translation to
// JavaScript, with the Arrays the corpus itself passes: the round trip JavaScript -> IR -> JavaScript
// must compute what the original computes.
//
//   node tooling/cipher/run-js-kats.mjs <translated .mjs> <golden .kat.json>
//
// The translated module is loaded through a copy that also exports its registry (`Algorithms`). Every
// vector node passed and that has an expected result is applied to a fresh instance as it was recorded -
// the properties set and the methods called, in order - then the input is fed and the result compared
// byte for byte. Prints '<passed> passed, <failed> failed' and a line per failure; exits 1 on any failure.

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';

const [translated, golden] = process.argv.slice(2);
if (!translated || !golden) {
  console.error('usage: node tooling/cipher/run-js-kats.mjs <translated .mjs> <golden .kat.json>');
  process.exit(2);
}

const entry = join(dirname(translated), 'kat-entry.mjs');
writeFileSync(entry, readFileSync(translated, 'utf8') + '\nexport { Algorithms as $Algorithms };\n');
const module = await import(pathToFileURL(entry).href);
const kats = JSON.parse(readFileSync(golden, 'utf8'));

/** A recorded value as the corpus passes it: bytes as an Array of numbers. */
const value = v => {
  switch (v.t) {
    case 'bytes': return [...Buffer.from(v.v, 'hex')];
    case 'number': return Number(v.v);
    case 'bigint': return BigInt(v.v);
    case 'string': case 'bool': return v.v;
    case 'null': return null;
    case 'undefined': return undefined;
    case 'array': return v.v.map(value);
    case 'typed': return v.v.map(Number);
    default: throw new Error(`a recorded value of the kind '${v.t}'`);
  }
};

let passed = 0;
let failed = 0;
for (const algorithm of kats.algorithms) {
  const registered = module.$Algorithms.find(candidate => candidate.name === algorithm.name);
  for (const [index, vector] of algorithm.vectors.entries()) {
    if (vector.node?.outcome !== 'passed' || !vector.expected)
      continue;
    try {
      const instance = registered.CreateInstance(vector.inverse);
      for (const step of vector.steps) {
        if (step.set !== undefined)
          instance[step.set] = value(step.value);
        else if (step.call)
          instance[step.name](...step.values.map(value));
        else
          instance[step.name] = value(step.values[0]);
      }
      instance.Feed(value(vector.input));
      const result = Buffer.from(instance.Result()).toString('hex');
      if (result === vector.expected.v) {
        ++passed;
      } else {
        ++failed;
        console.log(`${algorithm.name} #${index + 1}: ${result} where node computed ${vector.expected.v}`);
      }
    } catch (error) {
      ++failed;
      console.log(`${algorithm.name} #${index + 1}: ${error}`);
    }
  }
}

console.log(`${passed} passed, ${failed} failed`);
process.exit(failed === 0 && passed > 0 ? 0 : 1);
