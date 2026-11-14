// A variable that both absent values reach and that the program tells apart: the IR has one absent
// value, so this is refused with PLY1105. Written for this repository.

export function f(x) { return x === undefined ? 1 : x === null ? 2 : x; }
