// A helper that its callers use with two kinds of typed array, the JavaScript equivalent of a
// generic method: middle is not exported, so each kind gets a copy of its own. f() is -3.75 in
// node: 2 + -5.75. Written for this repository.

function middle(a) {
  return a[a.length >> 1];
}

export function f() {
  return middle(new Int32Array([1, 2, 3])) + middle(new Float64Array([0.5, -5.75, 2]));
}
