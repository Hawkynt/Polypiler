// A class that keeps a subarray of bytes in a field and reads it later: the view escapes the function
// that made it, so it is a Memory in C#, where a view in a local only is a Span. for...of goes over a
// view and over a typed array. The bytes d are 0, 1, ..., 15 in the scenarios. Written for this repository.

export class Window {

  /**
   * @param {uint8[]} data
   * @param {int32} start
   * @param {int32} length
   */
  constructor(data, start, length) {
    const part = data.subarray(start, start + length);
    this.bytes = part;
    this.first = data.subarray(start, start + 1);
  }

  sum() {
    let total = 0;
    for (const b of this.bytes) {
      total = total + b;
    }

    return total;
  }

  get length() {
    return this.bytes.length;
  }

  /** @param {int32} value */
  mark(value) {
    this.first[0] = value;
  }

  /**
   * @param {int32} begin
   * @param {int32} end
   */
  narrow(begin, end) {
    this.bytes = this.bytes.subarray(begin, end);
  }
}

/**
 * 22: the window 4..7 of d, summed after the object was made.
 * @param {uint8[]} d
 * @param {int32} start
 * @param {int32} length
 * @returns {int32}
 */
export function windowSum(d, start, length) {
  const w = new Window(d, start, length);
  return w.sum();
}

/**
 * 205 and 100: the window shares the bytes of d both ways.
 * @param {uint8[]} d
 * @returns {int32}
 */
export function shared(d) {
  const w = new Window(d, 4, 4);
  d[5] = 100;
  w.mark(80);
  return w.sum() * 1000 + d[4];
}

/**
 * The length of the window after it was narrowed, clamped as subarray clamps: 2 for (1, 3), 0 for (3, 1), 3 for (-3, 9).
 * @param {uint8[]} d
 * @param {int32} begin
 * @param {int32} end
 * @returns {int32}
 */
export function narrowed(d, begin, end) {
  const w = new Window(d, 4, 4);
  w.narrow(begin, end);
  return w.length * 1000 + w.sum();
}

/**
 * 120: for...of over a typed array and over a view in a local.
 * @param {uint8[]} d
 * @returns {int32}
 */
export function loops(d) {
  let total = 0;
  for (const b of d) {
    total = total + b;
  }

  for (const b of d.subarray(10)) {
    if (b === 12) {
      continue;
    }

    total = total - b;
  }

  return total;
}
