// Marsaglia's xorshift32 with the shifts 13, 17 and 5, seeded with 2463534242 ("Xorshift RNGs",
// J. Stat. Softw. 8(14), 2003), and a count of the numbers it has given. The state lives in
// variables of the module. Written for this repository.

let state = 2463534242;
let count = 0;

export function next() {
  state = (state ^ (state << 13)) >>> 0;
  state = (state ^ (state >>> 17)) >>> 0;
  state = (state ^ (state << 5)) >>> 0;
  count = (count + 1) | 0;
  return state;
}

export function calls() {
  return count;
}
