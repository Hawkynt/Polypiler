// A block-local record followed by a record of the same name in the enclosing block, which C#
// forbids (CS0136). Written for this repository.

export function f(n) {
  let s = 0;
  if (n > 1) { let p = { x: n | 0, y: 3 }; s += p.x * p.y; }
  let p = { x: 0.5, y: n };
  return s + p.x * p.y;
}
