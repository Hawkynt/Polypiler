// Points as object literals of fixed shape: built, passed, returned and read, never written, compared
// or extended - so a copy and a reference compute the same. Written for this repository.

function dot(point, other) {
  return point.x * other.x + point.y * other.y;
}

function middle(a, b) {
  const x = (a.x + b.x) / 2;
  const y = (a.y + b.y) / 2;
  return { x, y };
}

export function dotOf(x1, y1, x2, y2) {
  return dot({ x: x1, y: y1 }, { x: x2, y: y2 });
}

export function midX(x1, y1, x2, y2) {
  const { x } = middle({ x: x1, y: y1 }, { x: x2, y: y2 });
  return x;
}

export function pathLength(n) {
  const points = [{ x: 0, y: 0 }, { x: 3, y: 4 }, { x: 3, y: 0 }];
  let total = 0;
  for (let i = 1; i < n && i < points.length; i++) {
    const { x, y } = points[i];
    const previous = points[i - 1];
    total += Math.sqrt((x - previous.x) * (x - previous.x) + (y - previous.y) * (y - previous.y));
  }
  return total;
}

export function boxed(flag) {
  const box = { inner: { x: 1, y: 2 }, on: flag };
  return box.on ? box.inner.x + box.inner.y : 0;
}
