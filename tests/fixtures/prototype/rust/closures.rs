// Fixture of the Rust frontend: functions, nested functions, function pointers, closures, iterator
// closures and the assertion and panic macros (#464). Each public function is run by rustc with
// overflow checks on and off; the recordings beside it are what those runs did.

pub fn apply_twice(x: u32) -> u32 {
  fn dbl(x: u32) -> u32 {
    x * 2
  }
  let f: fn(u32) -> u32 = dbl;
  f(f(x))
}

fn apply(f: fn(u32) -> u32, x: u32) -> u32 {
  f(x)
}

fn triple(x: u32) -> u32 {
  x * 3
}

pub fn pointer_parameter(x: u32) -> u32 {
  apply(triple, x) + apply(apply_twice, x)
}

pub fn square_sum(n: u32) -> u32 {
  (1..=n).map(|x| x * x).sum()
}

pub fn factorial(n: u32) -> u32 {
  (1..=n).product()
}

pub fn even_squares(a: &[u32]) -> u32 {
  a.iter().filter(|&&x| x % 2 == 0).map(|&x| x * x).sum()
}

pub fn poly_hash(a: &[u32]) -> u32 {
  a.iter().fold(0u32, |h, &x| h.wrapping_mul(31).wrapping_add(x))
}

pub fn total(a: u32, b: u32) -> u32 {
  let mut c = 0u32;
  let mut inc = |d: u32| {
    c += d;
  };
  inc(a);
  inc(b);
  c
}

pub fn offset(k: u32, x: u32) -> u32 {
  let mut base = k;
  let add = move |v: u32| v + base;
  base = 0;
  add(x) + base
}

pub fn probe(a: &[u32], z: u32) -> bool {
  a.iter().map(|&x| x / z).any(|q| q > 1)
}

pub fn counted_probe(a: &[u32], z: u32) -> u32 {
  let mut divisions = 0u32;
  let found = a.iter().map(|&x| {
    divisions += 1;
    x / z
  }).any(|q| q > 1);
  divisions * 10 + found as u32
}

pub fn all_above(a: &[i32], limit: i32) -> bool {
  a.iter().all(|&x| x > limit)
}

pub fn count_odd(a: &[u8]) -> usize {
  a.iter().filter(|x| **x % 2 == 1).count()
}

pub fn for_each_total(a: &[u16]) -> u32 {
  let mut s = 0u32;
  a.iter().for_each(|&x| s += x as u32);
  s
}

pub fn chained(a: &[u32]) -> u32 {
  a.iter().copied().map(|x| x + 1).filter(|x| x % 3 != 0).enumerate().map(|(i, x)| i as u32 * x).sum()
}

pub fn range_chain(n: u32) -> u64 {
  (0..n).rev().step_by(2).map(|x| x as u64 * 3).fold(1u64, |acc, x| acc.wrapping_mul(31).wrapping_add(x))
}

pub fn byte_sum(a: &[u8]) -> u8 {
  a.iter().sum::<u8>()
}

pub fn early_exit(x: i32) -> i32 {
  let clamp = |v: i32| -> i32 {
    if v > 100 {
      return 100;
    }
    if v < 0 {
      return 0;
    }
    v
  };
  clamp(x) + clamp(x * 2)
}

pub fn checked(x: u32) -> u32 {
  assert!(x < 1000);
  assert_eq!(x % 7, 0, "a multiple of seven");
  assert_ne!(x, 14);
  x / 7
}

pub fn panics(x: u32) -> u32 {
  match x {
    0 => panic!("zero"),
    1 => unreachable!(),
    2 => todo!(),
    3 => unimplemented!(),
    _ => x,
  }
}

pub fn fib(n: u32) -> u64 {
  if n < 2 { n as u64 } else { fib(n - 1) + fib(n - 2) }
}

pub fn mutual(n: u32) -> bool {
  even(n)
}

fn even(n: u32) -> bool {
  if n == 0 { true } else { odd(n - 1) }
}

fn odd(n: u32) -> bool {
  if n == 0 { false } else { even(n - 1) }
}

pub fn pattern_parameters(a: u32, b: u32) -> u32 {
  fn combine((x, y): (u32, u32), mut z: u32) -> u32 {
    z += 1;
    x * y + z
  }
  combine((a, b), 1)
}
