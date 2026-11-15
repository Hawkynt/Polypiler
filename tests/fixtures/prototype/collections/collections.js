// Arrays that change their length: push, pop, splice, a write of length and at a[a.length],
// indexOf and includes, a spread copy, and a for...of that sees what its body pushes. Written for
// this repository.

export function arrayOps() {
  const a = [3, 2];
  a.splice(0, 0, 7);
  a.push(3, 4);
  a.splice(1, 1);
  a.push(9);
  a.pop();
  a[a.length] = 4;
  a[3] = 3;
  if (a.indexOf(3) !== 2 || !a.includes(4)) {
    return -1;
  }
  let r = 0;
  for (const d of a) {
    r = r * 10 + d;
  }
  return r;
}

export function pushWhileIterating() {
  const a = [1, 2];
  let n = 0;
  for (const x of a) {
    n++;
    if (a.length < 4) {
      a.push(x);
    }
  }
  return n;
}

export function popOfEmpty() {
  const a = [];
  a.push(1);
  a.pop();
  return a.pop() === undefined ? 1 : 0;
}

export function popped() {
  const a = [4, 5];
  const last = a.pop();
  return last === undefined ? -1 : last * 10 + a.length;
}

export function searches() {
  const nan = 0 / 0;
  const a = [nan, -0];
  return a.indexOf(nan) * 100 + a.indexOf(0) * 10 + (a.includes(nan) ? 1 : 0);
}

export function cleared() {
  const a = [1, 2, 3];
  a.length = 0;
  a.push(5);
  return a.length * 10 + a[0];
}

export function words() {
  const w = [];
  w.push("a");
  w.push("b");
  w.pop();
  let s = "";
  for (const x of w) {
    s += x;
  }
  return s;
}

export function copied() {
  const a = [1, 2];
  const b = [...a];
  b.push(3);
  return a.length * 10 + b.length;
}

export function stepped() {
  const c = [0, 0];
  c[1]++;
  c[1] += 2;
  c.push(0);
  return c[1] * 10 + c.length;
}

function append(list, value) {
  list.push(value);
}

export function passed() {
  const a = [1];
  append(a, 2);
  append(a, 3);
  return a.length;
}

export function fixedLoop() {
  const a = [1, 2, 3];
  let s = 0;
  for (const x of a) {
    s = s * 10 + x;
  }
  return s;
}

export function mapOrder() {
  const m = new Map();
  m.set(1, 1);
  m.set(2, 2);
  m.set(3, 3);
  m.delete(1);
  m.delete(3);
  m.set(4, 4);
  m.set(5, 5);
  m.set(6, 6);
  let r = 0;
  for (const k of m.keys()) {
    r = r * 10 + k;
  }
  return r;
}

export function setOrder() {
  const s = new Set([1, 2, 3]);
  s.delete(2);
  s.add(4);
  let r = 0;
  for (const x of s) {
    r = r * 10 + x;
  }
  return r;
}

export function firstZeroKey() {
  const m = new Map();
  m.set(-0, 1);
  m.set(0, 2);
  let last = 1;
  for (const k of m.keys()) {
    last = k;
  }
  return 1 / last;
}

export function nanKey() {
  const m = new Map();
  const z = 0;
  m.set(0 / 0, 1);
  m.set(z / z, 2);
  return m.size * 10 + (m.get(0 / 0) ?? 0);
}

export function missing() {
  const m = new Map();
  m.set(1, 2);
  return m.get(5);
}

/**
 * @param {number[]} a
 */
export function count(a) {
  const counts = new Map();
  for (const x of a) {
    counts.set(x, (counts.get(x) ?? 0) + 1);
  }
  let r = 0;
  for (const [k, v] of counts) {
    r = r * 100 + k * 10 + v;
  }
  return r;
}

export function liveMap() {
  const m = new Map([[1, 1], [2, 2], [3, 3]]);
  let r = 0;
  for (const [k, v] of m) {
    r = r * 10 + v;
    if (k === 1) {
      m.delete(2);
      m.set(4, 4);
    }
  }
  return r;
}

export function clearedSet() {
  const s = new Set([1, 2]);
  s.delete(1);
  s.clear();
  s.add(9);
  s.add(8);
  let r = 0;
  for (const x of s.values()) {
    r = r * 10 + x;
  }
  return r + s.size * 1000;
}

export function hasDelete() {
  const m = new Map();
  m.set("a", 1);
  let r = m.has("a") ? 10 : 0;
  if (m.delete("a")) {
    r = r + 1;
  }
  if (m.delete("a")) {
    r = r + 100;
  }
  return r;
}

export function wordSet() {
  const s = new Set();
  s.add("b");
  s.add("a");
  s.add("b");
  let t = "";
  for (const w of s) {
    t += w;
  }
  return t;
}
