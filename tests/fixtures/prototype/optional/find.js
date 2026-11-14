// The functions of Find.cs written with JavaScript's absent values: null, undefined, ??, ?. and
// ??=, and JavaScript's own coercion of an absent value in arithmetic. Written for this repository.

/**
 * @param {Int32Array} a
 * @param {number} v
 */
function find(a, v) {
  for (let i = 0; i < a.length; i++)
    if (a[i] === v)
      return i;

  return null;
}

/**
 * @param {Int32Array} a
 * @param {number} v
 */
export function findOr(a, v) {
  return find(a, v) ?? -1;
}

export function next(a) {
  return a == null ? -1 : a + 1;
}

export function isNull(a) {
  return a === null;
}

export function or5(z) {
  return z ?? 5;
}

export function assigned() {
  let q = null;
  q ??= 7;
  q ??= 9;
  return q;
}

/** @param {Int32Array} a */
export function length(a) {
  return a?.length ?? -1;
}

export function coerced(x) {
  const v = x > 0 ? x : null;
  return v + 1;
}

export function missing(x) {
  const v = x > 0 ? x : undefined;
  return v + 1;
}

export function positive(n) {
  if (n > 0)
    return n;
}

export function either(n) {
  return n > 0 ? n : undefined;
}

export function nothing(n) {
  if (n > 0)
    return n;

  return;
}

function scale(x, factor) {
  return factor === undefined ? x : x * factor;
}

export function scaled(x) {
  return scale(x) + scale(x, 3);
}

export function truthy(x) {
  const v = x > 0 ? x : null;
  return v ? 1 : 0;
}
