// Array callbacks of JavaScript: every method runs over the whole array before the next one starts,
// every callback takes (element, index, array) as far as its parameters reach, the length is read
// once, sort is stable, includes finds a NaN that indexOf does not, and reduce of an empty array
// without an initial value throws a TypeError. Lambdas record their calls, so that the order of the
// calls is compared, not only the result. Values are the ones node computes. Written for this
// repository.

function double(x) {
  return x * 2;
}

// Each method runs over the whole array: x for map, then -y for filter.
/** @param {number[]} a */
export function trace(a) {
  const log = [];
  a.map((x) => {
    log.push(x);
    return x * 2;
  }).filter((y) => {
    log.push(-y);
    return y > 2;
  });
  return [...log];
}

/** @param {number[]} a */
export function total(a) {
  return a.map((x) => x * 2).filter((x) => x > 2).reduce((s, x) => s + x, 0);
}

// The chain of LINQ's Deferred, read eagerly: every map, then every filter, then 3, then the first element.
export function eager() {
  let log = 0;
  const q = [1, 2, 3, 4].map((x) => {
    log = log * 10 + 1;
    return x * 2;
  }).filter((x) => {
    log = log * 10 + 2;
    return x > 2;
  });
  log = log * 10 + 3;
  return log * 10 + q[0];
}

// includes finds a NaN by SameValueZero, indexOf does not by ===.
export function nanSearch() {
  const a = [0 / 0, 1];
  return (a.includes(0 / 0) ? 10 : 0) + a.indexOf(0 / 0);
}

/** @param {number[]} a */
export function firstOver(a) {
  return a.findIndex((x) => x > 2);
}

/** @param {number[]} a */
export function found(a) {
  const v = a.find((x) => x > 2);
  return v === undefined ? -1 : v;
}

export function flat() {
  return [1, 2, 3].flatMap((x) => [x, x * 10]);
}

// The indices sorted by their keys, stably: equal keys keep their order.
/** @param {number[]} keys */
export function stable(keys) {
  const order = [0, 1, 2, 3];
  order.sort((i, j) => keys[i] - keys[j]);
  return order;
}

export function emptyReduce() {
  try {
    return [1].filter((x) => x > 5).reduce((s, x) => s + x);
  } catch (e) {
    return e instanceof TypeError ? -1 : -2;
  }
}

export function single() {
  return [5].reduce((s, x) => s + x);
}

// some of nothing is false, every of nothing is true, findIndex of nothing -1.
export function emptyTests() {
  const e = [1].filter((x) => x > 5);
  return (e.some((x) => x > 0) ? 1 : 0) * 10 + (e.every((x) => x > 0) ? 1 : 0) + e.findIndex((x) => x > 0) * 100;
}

export function indexed() {
  const r = ["a", "b"].map((x, i) => x + i);
  return r[0] + r[1];
}

// The length is read before the first call: what the callback pushes is not visited.
export function pushDuring() {
  const a = [1, 2, 3];
  const r = a.map((x) => {
    a.push(x);
    return x;
  });
  return r.length * 10 + a.length;
}

// map runs into the element whose callback throws; LINQ's lazy First would have stopped before it.
export function eagerThrow() {
  const log = [];
  try {
    [1, 2, 3, 4].map((x) => {
      log.push(x);
      if (x === 3) {
        throw new Error("three");
      }
      return x;
    });
  } catch (e) {
    return log.length;
  }
  return -1;
}

export function right() {
  return [1, 2, 3].reduceRight((s, x) => s * 10 + x, 0);
}

export function each() {
  let s = 0;
  [1, 2, 3].forEach((x, i) => {
    s += x * i;
  });
  return s;
}

export function whole() {
  return [1, 2, 3].every((x, i, all) => all[i] === x) ? 1 : 0;
}

export function byName() {
  return [1, 2, 3].map(double).reduce((s, x) => s + x, 0);
}
