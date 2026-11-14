// A block-local let followed by a let of the same name and the same type in the enclosing block.
// Written for this repository.

export function f(n) {
  let s = 0;
  if (n > 1) { let t = n | 0; s += t; }
  let t = n | 0;
  return s + t;
}
