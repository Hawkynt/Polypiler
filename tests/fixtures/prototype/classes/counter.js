// A counter as an ECMAScript 2022 class: a private field, a public field with a value, a getter, a
// static field that counts the objects made, and functions of the module that use it.
// Written for this repository.

export class Counter {
  #count;
  step = 1;
  static created = 0;

  constructor(start) {
    this.#count = start;
    Counter.created++;
  }

  get count() {
    return this.#count;
  }

  next() {
    this.#count += this.step;
    return this.#count;
  }

  reset() {
    this.#count = 0;
  }
}

// 16: new Counter(10), step = 3, next() twice.
export function run(start, step) {
  const counter = new Counter(start);
  counter.step = step;
  counter.next();
  return counter.next();
}

export function aliased() {
  const a = new Counter(0);
  const b = a;
  b.next();
  return a.count;
}

export function sameObject() {
  const a = new Counter(0);
  const b = a;
  return a === b && a !== new Counter(0);
}

export function made() {
  const before = Counter.created;
  const first = new Counter(5);
  first.reset();
  return Counter.created - before + first.count;
}
