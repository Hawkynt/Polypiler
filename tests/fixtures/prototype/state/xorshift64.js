// Marsaglia's xorshift64 with the shifts 13, 7 and 17, seeded with 88172645463325252 ("Xorshift
// RNGs", J. Stat. Softw. 8(14), 2003): 64 bits of state in a BigInt of the module. Written for this
// repository.

let state = 88172645463325252n;

export function next() {
  state = BigInt.asUintN(64, state ^ (state << 13n));
  state = BigInt.asUintN(64, state ^ (state >> 7n));
  state = BigInt.asUintN(64, state ^ (state << 17n));
  return state;
}
