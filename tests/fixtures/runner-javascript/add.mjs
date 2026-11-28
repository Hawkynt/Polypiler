// The sum of two int32 values; a sum outside int32 is a RangeError.
export function F(a, b) {
  const sum = a + b;
  if ((sum | 0) !== sum)
    throw new RangeError('sum out of range');
  return sum;
}
