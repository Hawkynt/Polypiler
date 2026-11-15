// Generators of JavaScript: laziness, a generator object that runs once, finally on every way out of
// a for...of, yield* of a generator and of an array, and an infinite sequence with int32 wrap-around.
// Every function records the order of events in t[0] = t[0] * 10 + code: 5 the generator object is
// made, 7 the body starts, 1 to 3 a value is produced, 8 the consumer's body runs, 9 a finally block
// runs, 6 the body ends normally, 4 an exception is caught. Written for this repository.

function* traced(t) {
  t[0] = t[0] * 10 + 7;
  try {
    t[0] = t[0] * 10 + 1;
    yield 1;
    t[0] = t[0] * 10 + 2;
    yield 2;
    t[0] = t[0] * 10 + 3;
    yield 3;
  } finally {
    t[0] = t[0] * 10 + 9;
  }
  t[0] = t[0] * 10 + 6;
}

function* up(n) {
  for (let i = 1; i <= n; i++) {
    yield i;
  }
}

function* fib() {
  let a = 0;
  let b = 1;
  while (true) {
    yield a;
    const c = (a + b) | 0;
    a = b;
    b = c;
  }
}

function* inner(t) {
  try {
    yield 1;
    yield 2;
  } finally {
    t[0] = t[0] * 10 + 8;
  }
}

function* outer(t) {
  try {
    yield* inner(t);
    t[0] = t[0] * 10 + 3;
    yield 3;
  } finally {
    t[0] = t[0] * 10 + 9;
  }
}

function* digits() {
  yield* [4, 5, 6];
  yield 7;
}

function* flags() {
  yield true;
  yield false;
  yield true;
}

class Box {
  constructor(value) {
    this.value = value;
  }

  *repeated(count) {
    for (let i = 0; i < count; i++) {
      yield this.value + i;
    }
  }
}

function* boxes() {
  yield new Box(3);
  yield new Box(4);
}

function consume(t) {
  for (const v of traced(t)) {
    if (v === 2) {
      return v;
    }
  }
  return 0;
}

export function earlyBreak() {
  const t = [0];
  const g = traced(t);
  t[0] = t[0] * 10 + 5;
  for (const v of g) {
    t[0] = t[0] * 10 + 8;
    break;
  }
  return t[0];
}

export function full() {
  const t = [0];
  for (const v of traced(t)) {
    t[0] = t[0] * 10 + 8;
  }
  return t[0];
}

export function twice() {
  const g = up(2);
  let sum = 0;
  for (const v of g) {
    sum += v;
  }
  for (const v of g) {
    sum += v;
  }
  return sum;
}

export function fibSum(n) {
  let sum = 0;
  let i = 0;
  for (const v of fib()) {
    if (i === n) {
      break;
    }
    sum = (sum + v) | 0;
    i++;
  }
  return sum;
}

export function returned() {
  const t = [0];
  const r = consume(t);
  t[0] = t[0] * 10 + r;
  return t[0];
}

export function throwInside() {
  const t = [0];
  try {
    for (const v of traced(t)) {
      throw new Error("in the body");
    }
  } catch {
    t[0] = t[0] * 10 + 4;
  }
  return t[0];
}

export function nestedClose() {
  const t = [0];
  for (const v of outer(t)) {
    t[0] = t[0] * 10 + v;
    break;
  }
  return t[0];
}

export function nestedFull() {
  const t = [0];
  for (const v of outer(t)) {
    t[0] = t[0] * 10 + v;
  }
  return t[0];
}

export function fromArray() {
  let sum = 0;
  for (const v of digits()) {
    sum = sum * 10 + v;
  }
  return sum;
}

export function truths() {
  let sum = 0;
  for (const b of flags()) {
    sum = sum * 10 + (b ? 1 : 2);
  }
  return sum;
}

export function objects() {
  let sum = 0;
  for (const box of boxes()) {
    sum = sum * 10 + box.value;
  }
  return sum;
}

export function delegated() {
  const box = new Box(5);
  let sum = 0;
  for (const v of box.repeated(3)) {
    sum = sum * 10 + v;
  }
  return sum;
}
