// ECMA-262 §7.1.6-7.1.7 (ToInt32, ToUint32), §13.9 (the shift count is masked with 31) and the
// largest exactly representable integer of a Number, 2^53, whose successor rounds back to it.
const mode = process.argv[2];
if (mode === "--limits") {
  console.log(2 ** 53);
  console.log(2 ** 53 + 1);
} else if (mode === "--shifts") {
  console.log(1 << 31);
  console.log(1 << 32);
  console.log(1 << 33);
} else {
  console.log(0xFFFFFFFF | 0);
  console.log(-1 >>> 0);
}
