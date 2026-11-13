// Evaluates the ADR 0005 input set with the ECMAScript Math object.
import { readFileSync, writeSync } from 'node:fs';

const view = new DataView(new ArrayBuffer(8));
const value = hex => { view.setBigUint64(0, BigInt('0x' + hex)); return view.getFloat64(0); };
const bits = number => { view.setFloat64(0, number); return view.getBigUint64(0).toString(16).padStart(16, '0'); };
const functions = {
  sin: Math.sin, cos: Math.cos, tan: Math.tan, exp: Math.exp, log: Math.log, log2: Math.log2, log10: Math.log10,
  pow: Math.pow, atan2: Math.atan2, sinh: Math.sinh, cosh: Math.cosh, tanh: Math.tanh, cbrt: Math.cbrt,
  hypot: Math.hypot, add: (x, y) => x + y, sub: (x, y) => x - y, mul: (x, y) => x * y, div: (x, y) => x / y,
  sqrt: Math.sqrt
};

const out = [];
for (const line of readFileSync(0, 'utf8').split('\n')) {
  if (line === '')
    continue;
  const parts = line.split(' ');
  const f = functions[parts[0]];
  out.push(f ? bits(f(...parts.slice(2).map(value))) : 'n/a');
}
writeSync(1, out.join('\n') + '\n');
