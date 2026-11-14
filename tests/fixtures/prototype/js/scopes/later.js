// A block-local let followed by a let of the same name in the enclosing block, which C# forbids
// (CS0136) and JavaScript allows. Written for this repository.

export function f(n) {
  let s = 0;
  if (n > 1) { let t = n | 0; s += t; }
  let t = 0.5;
  return s + t;
}
