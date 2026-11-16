// Fixture of the Rust frontend: if, match, loops over ranges, labelled break and continue, and the
// order of evaluation. Each function is run by rustc with overflow checks on and off; the recordings
// beside it are what those runs did (tests/fixtures/prototype/rust/recorded).

pub fn gcd(mut a: u32, mut b: u32) -> u32 {
  while b != 0 {
    let t = a % b;
    a = b;
    b = t;
  }
  a
}

pub fn collatz_steps(mut n: u64) -> u32 {
  let mut s = 0;
  while n != 1 {
    n = if n % 2 == 0 { n / 2 } else { 3 * n + 1 };
    s += 1;
  }
  s
}

pub fn next_power_of_two(n: u32) -> u32 {
  let mut p = 1u32;
  loop {
    if p >= n {
      break p;
    }
    p <<= 1;
  }
}

pub fn sum_to(n: u32) -> u32 {
  let mut s = 0;
  for i in 0..=n {
    s += i;
  }
  s
}

pub fn odd_sum() -> u32 {
  let mut s = 0;
  for i in 0..10u32 {
    if i % 2 == 0 {
      continue;
    }
    s += i;
  }
  s
}

pub fn early(x: i32) -> i32 {
  let y = if x < 0 { return -1 } else { x * 2 };
  y + 1
}

pub fn classify(x: i32) -> u8 {
  match x {
    i32::MIN..=-1 => 0,
    0 => 1,
    1 | 2 => 2,
    n if n % 2 == 0 => 3,
    _ => 4,
  }
}

pub fn classify_statement(x: i32) -> u8 {
  let mut r = 9;
  match x {
    i32::MIN..=-1 => r = 0,
    0 => r = 1,
    1 | 2 => r = 2,
    n if n % 2 == 0 => {
      r = 3;
    }
    _ => {}
  }
  r
}

pub fn first_factor_pair(t: u32) -> u32 {
  let mut r = 0;
  'outer: for i in 1..=9u32 {
    for j in 1..=9u32 {
      if i * j == t {
        r = i * 10 + j;
        break 'outer;
      }
    }
  }
  r
}

pub fn labelled_block(x: i32) -> i32 {
  'b: {
    if x > 10 {
      break 'b 1;
    }
    if x > 5 {
      break 'b 2;
    }
    3
  }
}

pub fn count_bytes() -> u32 {
  let mut n = 0;
  for _ in 0..=u8::MAX {
    n += 1;
  }
  n
}

pub fn count_signed_bytes() -> u32 {
  let mut n = 0;
  for _ in i8::MIN..=i8::MAX {
    n += 1;
  }
  n
}

pub fn stepped_top() -> u32 {
  let mut r = 0;
  for i in (250..=255u8).step_by(2) {
    r = r * 1000 + i as u32;
  }
  r
}

pub fn step_then_rev() -> u32 {
  let mut r = 0;
  for i in (0..11u32).step_by(3).rev() {
    r = r * 16 + i;
  }
  r
}

pub fn rev_then_step() -> u32 {
  let mut r = 0;
  for i in (0..11u32).rev().step_by(3) {
    r = r * 16 + i;
  }
  r
}

pub fn step_zero(n: usize) -> u32 {
  let mut c = 0;
  for _ in (0..10u32).step_by(n) {
    c += 1;
  }
  c
}

// The twelve range forms over i8 bounds; the result encodes the sequence, a trap marks a step of 0.
pub fn range_exclusive(a: i8, b: i8) -> i64 {
  let mut r: i64 = 0;
  for i in a..b {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_inclusive(a: i8, b: i8) -> i64 {
  let mut r: i64 = 0;
  for i in a..=b {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_exclusive_rev(a: i8, b: i8) -> i64 {
  let mut r: i64 = 0;
  for i in (a..b).rev() {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_inclusive_rev(a: i8, b: i8) -> i64 {
  let mut r: i64 = 0;
  for i in (a..=b).rev() {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_exclusive_step(a: i8, b: i8, n: usize) -> i64 {
  let mut r: i64 = 0;
  for i in (a..b).step_by(n) {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_inclusive_step(a: i8, b: i8, n: usize) -> i64 {
  let mut r: i64 = 0;
  for i in (a..=b).step_by(n) {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_exclusive_step_rev(a: i8, b: i8, n: usize) -> i64 {
  let mut r: i64 = 0;
  for i in (a..b).step_by(n).rev() {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_inclusive_step_rev(a: i8, b: i8, n: usize) -> i64 {
  let mut r: i64 = 0;
  for i in (a..=b).step_by(n).rev() {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_exclusive_rev_step(a: i8, b: i8, n: usize) -> i64 {
  let mut r: i64 = 0;
  for i in (a..b).rev().step_by(n) {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_inclusive_rev_step(a: i8, b: i8, n: usize) -> i64 {
  let mut r: i64 = 0;
  for i in (a..=b).rev().step_by(n) {
    r = r.wrapping_mul(131).wrapping_add(i as i64 + 1000);
  }
  r
}

pub fn range_u8_top(a: u8, b: u8) -> u32 {
  let mut r = 0u32;
  for i in (a..=b).rev() {
    r = r.wrapping_mul(7).wrapping_add(i as u32);
  }
  for i in a..=b {
    r = r.wrapping_mul(5).wrapping_add(i as u32);
  }
  r
}

pub fn range_u64(a: u64, b: u64, n: usize) -> u64 {
  let mut r = 0u64;
  let mut k = 0;
  for i in (a..=b).rev().step_by(n) {
    r ^= i.rotate_left(k);
    k += 1;
    if k == 20 {
      break;
    }
  }
  r
}

pub fn bounds_once(n: u32) -> u32 {
  let mut end = n;
  let mut c = 0;
  for i in 0..end {
    end = 0;
    c += i + 1;
  }
  c + end
}

pub fn shadowed_counter() -> u32 {
  let mut c = 0;
  for i in 0..5u32 {
    let i = i * 10;
    c += i;
  }
  c
}

pub fn match_tuple(a: u8, b: bool) -> u32 {
  match (a, b) {
    (0, _) => 1,
    (1..=9, true) => 2,
    (1..=9, false) => 3,
    (x, true) if x > 200 => 4,
    (_, true) | (100, false) => 5,
    _ => 6,
  }
}

pub fn match_bool(a: bool, b: u16) -> u16 {
  match a {
    true => b / 2,
    false => b.wrapping_mul(3),
  }
}

pub fn match_ranges(x: u8) -> u8 {
  match x {
    ..=9 => 1,
    10..20 => 2,
    20..=99 => 3,
    100.. => 4,
  }
}

const LIMIT: i64 = 1000;

pub fn match_constant(x: i64) -> i64 {
  match x {
    LIMIT => 0,
    i64::MIN..0 => -1,
    mut m => {
      m -= 1;
      m
    }
  }
}

pub fn guard_traps(x: i32) -> i32 {
  match x {
    n if 10 / n > 1 => 1,
    _ => 0,
  }
}

pub fn guard_order(x: u32) -> u32 {
  let mut calls = 0;
  match x {
    n if { calls += 1; n > 5 } => calls * 10,
    n if { calls += 1; n > 2 } => calls * 10 + 1,
    _ => calls,
  }
}

pub fn if_statement(x: i32) -> i32 {
  let mut r = 0;
  if x > 0 {
    r = 1;
  } else if x < 0 {
    r = -1;
  }
  r
}

pub fn if_expression(x: i32) -> i32 {
  if x > 0 { 1 } else if x < 0 { -1 } else { 0 }
}

pub fn nested_continue(n: u32) -> u32 {
  let mut c = 0;
  'outer: for i in 0..n {
    let mut j = 0;
    while j < 10 {
      j += 1;
      if j == i {
        continue 'outer;
      }
      if j > 5 {
        continue;
      }
      c += 1;
    }
    c += 100;
  }
  c
}

pub fn loop_with_continue(limit: u32) -> u32 {
  let mut i = 0;
  let mut s = 0;
  'l: loop {
    i += 1;
    if i > limit {
      break 'l s;
    }
    if i % 3 == 0 {
      continue 'l;
    }
    s += i;
  }
}

pub fn while_block_condition(n: u32) -> u32 {
  let mut i = 0;
  let mut s = 0;
  while {
    i += 1;
    i <= n
  } {
    s += i;
  }
  s
}

pub fn labelled_in_loop(n: u32) -> u32 {
  let mut s = 0;
  for i in 0..n {
    let v = 'b: {
      if i % 2 == 0 {
        break 'b 10;
      }
      for j in 0..i {
        if j == 3 {
          break 'b 100;
        }
      }
      1
    };
    s += v;
  }
  s
}

pub fn assign_order(a: u32, b: u32) -> u32 {
  let mut x = a;
  x += {
    x = b;
    1
  };
  x
}

pub fn operand_order(a: i32, b: i32) -> i32 {
  let mut x = a;
  let y = x + {
    x = b;
    x
  };
  y * 1000 + x
}

pub fn infinite_or_return(n: u32) -> u32 {
  let mut i = 0;
  loop {
    i += 1;
    if i * i >= n {
      return i;
    }
  }
}

pub fn short_circuit_effect(a: u32) -> u32 {
  let mut c = 0;
  if a > 3 && {
    c += 1;
    a > 5
  } {
    c += 10;
  }
  c
}
