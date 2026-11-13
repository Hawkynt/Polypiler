// A polynomial evaluated by Horner's rule in doubles: the coefficients from the highest power down
// to the constant, one multiplication and one addition each.

export function evaluate(coefficients, x) {
  let result = 0;
  for (let i = 0; i < coefficients.length; i++)
    result = result * x + coefficients[i];

  return result;
}

// 2x³ - 6x² + 2x - 1 at x = 3 is 5.
export function check() {
  return evaluate(new Float64Array([2, -6, 2, -1]), 3) === 5;
}
