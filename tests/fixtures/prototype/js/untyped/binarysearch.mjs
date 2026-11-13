// Binary search in an ascending list: the index of the target, or -1. The middle is found with
// >>> 1, which is the one integer operation here.

export function find(sorted, target) {
  let low = 0;
  let high = sorted.length - 1;
  while (low <= high) {
    const middle = (low + high) >>> 1;
    const value = sorted[middle];
    if (value === target)
      return middle;

    if (value < target)
      low = middle + 1;
    else
      high = middle - 1;
  }
  return -1;
}

export function check() {
  const sorted = new Int32Array([-7, -1, 0, 3, 8, 21]);
  return find(sorted, 8) === 4 && find(sorted, 4) === -1;
}
