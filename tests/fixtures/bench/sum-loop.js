// Sums the integers 0 to 999 999 and prints the sum, 999 999 * 1 000 000 / 2 = 499999500000.
let sum = 0;
for (let i = 0; i < 1000000; ++i)
  sum += i;
console.log(sum);
