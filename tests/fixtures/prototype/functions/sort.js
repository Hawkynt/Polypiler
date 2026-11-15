// An insertion sort that takes the comparison as a function. Written for this repository.

function insertionSort(a, compare) {
  for (let i = 1; i < a.length; i++) {
    const v = a[i];
    let j = i - 1;
    while (j >= 0 && compare(a[j], v) > 0) {
      a[j + 1] = a[j];
      j--;
    }

    a[j + 1] = v;
  }
}

/** @param {Int32Array} a */
export function ascending(a) {
  insertionSort(a, (x, y) => x < y ? -1 : x > y ? 1 : 0);
  return a;
}

// By the distance to a pivot the caller names, which the comparison captures.
/** @param {Int32Array} a */
export function byDistance(a, pivot) {
  insertionSort(a, (x, y) => Math.abs(x - pivot) - Math.abs(y - pivot));
  return a;
}
