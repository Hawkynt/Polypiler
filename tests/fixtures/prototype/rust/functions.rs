// Fixture of the Rust frontend: free functions with typed parameters and results, calls between
// them, recursion, and `pub` as the exported surface. Each public function is run by rustc with
// overflow checks on and off; the recordings beside it are what those runs did.

const MODULUS: u32 = 65521;

fn adler_step(a: u32, b: u32, byte: u8) -> u64 {
  let a = (a + byte as u32) % MODULUS;
  let b = (b + a) % MODULUS;
  ((b as u64) << 32) | a as u64
}

pub fn adler32_of_three(x: u8, y: u8, z: u8) -> u32 {
  let s = adler_step(1, 0, x);
  let s = adler_step(s as u32, (s >> 32) as u32, y);
  let s = adler_step(s as u32, (s >> 32) as u32, z);
  ((s >> 32) as u32) << 16 | s as u32
}

pub fn fib(n: u32) -> u64 {
  if n < 2 { n as u64 } else { fib(n - 1) + fib(n - 2) }
}

pub fn is_even(n: u32) -> bool {
  if n == 0 { true } else { is_odd(n - 1) }
}

pub fn is_odd(n: u32) -> bool {
  if n == 0 { false } else { is_even(n - 1) }
}

fn square(x: i64) -> i64 {
  x * x
}

pub fn sum_of_squares(a: i64, b: i64) -> i64 {
  square(a) + square(b)
}

fn divide(a: i32, b: i32) -> i32 {
  a / b
}

pub fn argument_order(a: i32, b: i32, c: i32, d: i32) -> i32 {
  pick(divide(a, b), divide(c, d))
}

fn pick(x: i32, y: i32) -> i32 {
  x * 100 + y
}

fn bump(counter: u32) -> u32 {
  counter.wrapping_add(1)
}

pub fn no_result(n: u32) -> u32 {
  let mut c = 0;
  for _ in 0..n {
    c = bump(c);
    nothing();
  }
  c
}

fn nothing() {}

pub fn xorshift32(mut x: u32) -> u32 {
  x ^= x << 13;
  x ^= x >> 17;
  x ^= x << 5;
  x
}

pub fn power_mod(base: u64, exp: u32, modulus: u64) -> u64 {
  let mut result = 1u64;
  let mut b = base % modulus;
  let mut e = exp;
  while e > 0 {
    if e & 1 == 1 {
      result = mul_mod(result, b, modulus);
    }
    b = mul_mod(b, b, modulus);
    e >>= 1;
  }
  result
}

fn mul_mod(a: u64, b: u64, m: u64) -> u64 {
  let low = a.wrapping_mul(b);
  let high = a.checked_mul(b).is_none();
  if high { ((a % m) * (b % m)) % m } else { low % m }
}
