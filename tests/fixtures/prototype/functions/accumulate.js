// A fold over an array with a callback. Written for this repository.

function fold(a, seed, step) {
  let result = seed;
  for (let i = 0; i < a.length; i++)
    result = step(result, a[i]);

  return result;
}

/** @param {Float64Array} a */
export function sum(a) {
  return fold(a, 0, (s, v) => s + v);
}

/** @param {Float64Array} a */
export function sumOfSquares(a) {
  return fold(a, 0, (s, v) => s + v * v);
}

// The callback counts the calls in a variable of the caller.
/** @param {Float64Array} a */
export function calls(a) {
  let count = 0;
  fold(a, 1, (s, v) => {
    count++;
    return s * v;
  });
  return count;
}
