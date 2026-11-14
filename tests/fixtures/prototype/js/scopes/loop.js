// A loop variable followed by a let of the same name in the enclosing block. Written for this
// repository.

export function g(n) {
  let s = 0;
  for (let i = 0; i < n; i++) { s += n; }
  let i = 10;
  return n > 0 ? s : i;
}
