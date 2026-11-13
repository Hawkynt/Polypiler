// Marsaglia's xorshift with 32 bits of state and the shifts 13, 17 and 5. The state is read under a
// bitwise operator only, which is all that says it is an integer.

export function next(state) {
  state ^= state << 13;
  state ^= state >>> 17;
  state ^= state << 5;
  return state >>> 0;
}

export function skip(state, count) {
  let x = state | 0;
  for (let i = 0; i < count; i++)
    x = next(x) | 0;

  return x >>> 0;
}
