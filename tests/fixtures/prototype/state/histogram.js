// A histogram of four buckets: the elements of a typed array of the module change, the array does
// not. Written for this repository.

const buckets = new Uint32Array(4);

export function add(value) {
  const bucket = value & 3;
  buckets[bucket] = (buckets[bucket] + 1) >>> 0;
  return buckets[bucket];
}
