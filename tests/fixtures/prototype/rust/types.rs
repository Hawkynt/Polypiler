// Fixture of the Rust frontend: structs with inherent methods, field-less enums, Option and Result
// with the ? operator (#467). Every entry point takes and returns scalars, pairs and slices; rustc
// runs it with overflow checks on and off. The xorshift32 sequence starts with Marsaglia's
// published first output for the seed 2463534242, 723471715 ("Xorshift RNGs", 2003).

pub struct Xorshift32 {
  state: u32,
}

impl Xorshift32 {
  pub fn new(seed: u32) -> Self {
    Self { state: seed }
  }

  fn next(&mut self) -> u32 {
    let mut x = self.state;
    x ^= x << 13;
    x ^= x >> 17;
    x ^= x << 5;
    self.state = x;
    x
  }
}

pub fn xorshift_two(seed: u32) -> (u32, u32) {
  let mut g = Xorshift32::new(seed);
  (g.next(), g.next())
}

#[derive(Clone, Copy, PartialEq, Eq, Default, Debug)]
pub struct P {
  x: i32,
  y: i32,
}

impl P {
  pub const ORIGIN: P = P { x: 0, y: 0 };

  pub fn new(x: i32, y: i32) -> Self {
    P { x, y }
  }

  pub fn manhattan(self) -> i32 {
    self.x.abs() + self.y.abs()
  }

  pub fn shift(&mut self, d: i32) {
    self.x += d;
  }

  pub fn dot(&self, o: &P) -> i32 {
    self.x * o.x + self.y * o.y
  }
}

pub fn point_ops(x: i32, y: i32, d: i32) -> i32 {
  let mut p = P::new(x, y);
  let q = p;
  p.shift(d);
  let r = P { y: 7, ..p };
  p.manhattan() * 1000 + q.dot(&r) + (P::default() == P::ORIGIN) as i32 + (p == q) as i32 * 10
}

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Fault {
  DivZero,
  Negative,
}

fn checked_div(a: i32, b: i32) -> Result<i32, Fault> {
  if b == 0 {
    Err(Fault::DivZero)
  } else if a < 0 {
    Err(Fault::Negative)
  } else {
    Ok(a / b)
  }
}

fn calc(a: i32, b: i32, c: i32) -> Result<i32, Fault> {
  let q = checked_div(a, b)?;
  checked_div(q, c)
}

pub fn code(a: i32, b: i32, c: i32) -> i32 {
  match calc(a, b, c) {
    Ok(v) => v,
    Err(Fault::DivZero) => -1,
    Err(Fault::Negative) => -2,
  }
}

#[repr(u8)]
#[derive(Clone, Copy)]
pub enum Op {
  Add = 1,
  Sub = 5,
  Mul,
  Halt = 255,
}

fn decode(byte: u8) -> Option<Op> {
  match byte {
    1 => Some(Op::Add),
    5 => Some(Op::Sub),
    6 => Some(Op::Mul),
    255 => Some(Op::Halt),
    _ => None,
  }
}

pub fn run(program: &[u8], start: i32) -> i32 {
  let mut acc = start;
  for &byte in program {
    let Some(op) = decode(byte) else { return -1 };
    acc = match op {
      Op::Add => acc.wrapping_add(op as i32),
      Op::Sub => acc.wrapping_sub(op as i32),
      Op::Mul => acc.wrapping_mul(Op::Mul as i32),
      Op::Halt => return acc,
    };
  }
  acc
}

#[repr(i8)]
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Sign {
  Minus = -1,
  Zero,
  Plus,
}

pub fn sign_of(x: i64) -> i8 {
  let s = if x < 0 { Sign::Minus } else if x == 0 { Sign::Zero } else { Sign::Plus };
  if s == Sign::Zero { 0 } else { s as i8 * 10 }
}

pub fn third(a: &[u8]) -> u8 {
  match a.get(2) {
    Some(&v) => v,
    None => 255,
  }
}

pub fn must(a: &[u8]) -> u8 {
  *a.get(2).unwrap()
}

pub fn popsum(n: u32) -> u32 {
  let mut v = Vec::new();
  for i in 1..=n {
    v.push(i);
  }
  let mut s = 0;
  while let Some(x) = v.pop() {
    s = s * 10 + x;
  }
  s
}

pub fn first_plus_one(a: &[u32]) -> u32 {
  let Some(&v) = a.first() else { return 99 };
  v + 1
}

pub fn last_or(a: &[u32], fallback: u32) -> u32 {
  if let Some(v) = a.last() { *v } else { fallback }
}

pub fn options(x: u32) -> i64 {
  let half = |v: u32| if v % 2 == 0 { Some(v / 2) } else { None };
  let a = half(x).unwrap_or(7) as i64;
  let b = half(x).is_some() as i64 * 10 + half(x).is_none() as i64;
  let c = x.checked_mul(3).ok_or(Fault::DivZero).is_ok() as i64;
  let d = match x.checked_sub(10) {
    Some(v) => v as i64,
    None => -5,
  };
  a * 100000 + b * 1000 + c * 100 + d
}

fn quarter(x: u32) -> Option<u32> {
  let h = if x % 2 == 0 { Some(x / 2) } else { None }?;
  if h % 2 == 0 { Some(h / 2) } else { None }
}

pub fn quarter_or(x: u32) -> u32 {
  quarter(x).unwrap_or(1000)
}

pub fn expect_even(x: u32) -> u32 {
  quarter(x).expect("not a multiple of four")
}

pub fn result_methods(a: i32, b: i32) -> i32 {
  let r = checked_div(a, b);
  let ok = r.ok().unwrap_or(-100);
  let e = r.is_err() as i32;
  ok * 10 + e + checked_div(a, 1).unwrap()
}

#[derive(Clone, Copy, PartialEq, Debug)]
struct Sample(f64, f64);

pub fn nan_equal(x: f64) -> bool {
  let s = Sample(x, 1.0);
  s == s
}

pub struct Grid {
  cells: [u8; 4],
  width: u8,
}

impl Grid {
  fn set(&mut self, i: usize, v: u8) {
    self.cells[i] = v;
  }

  fn total(&self) -> u32 {
    let mut t = 0;
    for &c in self.cells.iter() {
      t += c as u32;
    }
    t + self.width as u32
  }
}

pub fn grid(v: u8, i: usize) -> u32 {
  let mut g = Grid { cells: [1, 2, 3, 4], width: 2 };
  g.set(i, v);
  g.total()
}

pub fn field_order(a: &[u8], z: u8) -> u8 {
  let p = P2 { y: 10 / z, x: a[1] };
  p.x + p.y
}

struct P2 {
  x: u8,
  y: u8,
}

pub fn classify(n: u32) -> u32 {
  match n {
    0 => {
      let a = n + 1;
      a * 100
    }
    1 | 2 | 3 => 7,
    10 => {
      let b = n * 3;
      b + 1
    }
    _ => n,
  }
}
