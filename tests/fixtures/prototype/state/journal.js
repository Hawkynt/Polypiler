// A journal of letters kept in a string between calls, with a ring of the last four texts it held.
// Module state of the type string and of an array of strings. Written for this repository.

let text = "";
const ring = ["", "", "", ""];
let count = 0;

/**
 * @param {int32} code
 * @returns {int32}
 */
export function add(code) {
  text = text + String.fromCharCode(code);
  ring[count & 3] = text;
  count = (count + 1) | 0;
  return text.length;
}

/**
 * @param {int32} steps
 * @returns {string}
 */
export function back(steps) {
  return ring[(count - steps) & 3];
}
