// Run-length encoding written with reduce: the accumulator is the array of value and count pairs,
// to which every element either adds one to the count of the last run or appends a run of its own.
// Written for this repository.

/** @param {number[]} data */
export function encode(data) {
  const runs = data.reduce((acc, x) => {
    const n = acc.length;
    if (n > 0 && acc[n - 2] === x) {
      acc[n - 1] = acc[n - 1] + 1;
    } else {
      acc.push(x);
      acc.push(1);
    }
    return acc;
  }, []);
  return [...runs];
}

// The counts of the runs add up to the length of the data.
/** @param {number[]} data */
export function counted(data) {
  return encode(data).filter((x, i) => i % 2 === 1).reduce((s, x) => s + x, 0);
}
