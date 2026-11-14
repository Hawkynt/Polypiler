// An object written through a second name: in JavaScript p.x becomes 5, in a value record it would
// stay 1. The import refuses it rather than choose. Written for this repository.

export function f() {
  const p = { x: 1, y: 2 };
  const q = p;
  q.x = 5;
  return p.x;
}
