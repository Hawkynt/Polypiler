// The mean and the population variance of a list of doubles. Nothing here is an integer but the
// length, and nothing is made one.

export function mean(values) {
  let sum = 0;
  for (let i = 0; i < values.length; i++)
    sum += values[i];

  return sum / values.length;
}

export function variance(values) {
  const center = mean(values);
  let sum = 0;
  for (let i = 0; i < values.length; i++) {
    const distance = values[i] - center;
    sum += distance * distance;
  }
  return sum / values.length;
}

// 2, 4, 4, 4, 5, 5, 7, 9 has the mean 5 and the variance 4.
export function check() {
  const values = new Float64Array([2, 4, 4, 4, 5, 5, 7, 9]);
  return mean(values) === 5 && variance(values) === 4;
}
