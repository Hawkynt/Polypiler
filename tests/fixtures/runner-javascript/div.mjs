// Truncating division over i64 with BigInt: a zero divisor is the RangeError "Division by zero".
export function div(a, b) {
  return a / b;
}
