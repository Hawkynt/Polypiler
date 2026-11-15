// The ECMA-262 half of tests/vectors/numtext/shortest.txt: the bit patterns of xorshift64 from the
// seed 0x2026, each as the double or (low 32 bits) the float it is, with String(x) for every finite
// double. Generate.cs adds what .NET writes. Usage: node ecma.mjs <f64 count> <f32 count> > ecma.txt
const [f64Count, f32Count] = process.argv.slice(2).map(Number);
const mask = 0xFFFFFFFFFFFFFFFFn;
let state = 0x2026n;
const next = () => {
  state ^= (state << 13n) & mask;
  state ^= state >> 7n;
  state ^= (state << 17n) & mask;
  return state;
};
const view = new DataView(new ArrayBuffer(8));
const lines = [];
for (let count = 0; count < f64Count;) {
  view.setBigUint64(0, next());
  const value = view.getFloat64(0);
  if (!Number.isFinite(value)) continue;
  lines.push(`f64 ${view.getBigUint64(0).toString(16).padStart(16, '0')} ${String(value)}`);
  ++count;
}
for (let count = 0; count < f32Count;) {
  view.setUint32(0, Number(next() & 0xFFFFFFFFn));
  const value = view.getFloat32(0);
  if (!Number.isFinite(value)) continue;
  lines.push(`f32 ${view.getUint32(0).toString(16).padStart(8, '0')}`);
  ++count;
}
process.stdout.write(lines.join('\n') + '\n');
