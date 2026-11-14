// Two sibling blocks that each declare t, one an integer and one a double: a name C# allows twice.
// Written for this repository.

export function h(n) {
  let s = 0;
  if (n > 0) { let t = n | 0; s += t; } else { let t = n * 0.5; s += t; }
  return s;
}
