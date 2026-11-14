// The members whose meaning differs between JavaScript and C#, one function each: what each must
// keep of its source when it is translated. Written for this repository.

/** @param {string} s */
export function replaceFirst(s) {
  return s.replace("b", "XY");
}

/** @param {string} s */
export function replaceEvery(s) {
  return s.replaceAll("b", "[$&]");
}

/** @param {string} s */
export function trimEcma(s) {
  return s.trim();
}

/** @param {string} s */
export function countPoints(s) {
  let n = 0;
  for (const ch of s) n++;
  return n;
}

/**
 * @param {string} a
 * @param {string} b
 */
export function less(a, b) {
  return a < b;
}

/**
 * @param {string} s
 * @param {int32} a
 * @param {int32} b
 */
export function cut(s, a, b) {
  return s.substring(a, b);
}

/**
 * @param {string} s
 * @param {int32} a
 */
export function tail(s, a) {
  return s.slice(a);
}

/** @param {string} s */
export function pad(s) {
  return s.padStart(5, "ab");
}

/** @param {string} s */
export function parts(s) {
  return s.split(",", 2).join("|");
}

/**
 * @param {string} s
 * @param {int32} i
 */
export function unit(s, i) {
  return s.charCodeAt(i);
}

/**
 * @param {string} s
 * @param {int32} i
 */
export function point(s, i) {
  return s.codePointAt(i);
}

/** @param {string} s */
export function last(s) {
  return s.lastIndexOf(",");
}

/** @param {int32} c */
export function letter(c) {
  return String.fromCharCode(c, c + 1);
}
