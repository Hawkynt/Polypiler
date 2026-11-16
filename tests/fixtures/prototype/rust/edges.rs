// Fixture of the Rust frontend: the rarer forms of #465, #467 and #464 - equality of options,
// results and arrays, defaults, values that control flow chooses, places reached through vectors
// and temporaries, function pointers and closures handed to iterators, constant tables and
// patterns over references, tuple structs and constants. Each public function is run by rustc
// with overflow checks on and off; the recordings beside it are what those runs did.

#[derive(Clone, Copy, PartialEq, Eq, Default, Debug)]
pub struct P {
  x: i32,
  y: i32,
}

impl P {
  const ORIGIN: P = P { x: 0, y: 0 };

  fn new(x: i32, y: i32) -> P {
    P { x, y }
  }

  fn shift(&mut self, d: i32) {
    self.x += d;
  }

  fn sum(&self) -> i32 {
    self.x + self.y
  }
}

#[derive(Clone, Copy, PartialEq, Debug, Default)]
pub enum Level {
  #[default]
  Low,
  High,
}

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Fail {
  Small,
}

#[derive(Clone, Copy, Default)]
struct Grid {
  cells: [u8; 3],
  level: Level,
  origin: P,
  pair: (u8, u16),
}

#[derive(Clone, Copy, PartialEq, Debug)]
struct Pair(u8, u8);

const TABLE: [f64; 3] = [1.5, -2.25, 0.0];
const ZEROS: [u16; 4] = [0; 4];
const POINTS: [P; 2] = [P { x: 1, y: 2 }, P { x: 3, y: 4 }];

pub fn equalities(x: u32) -> u32 {
  let a: Option<u32> = if x > 1 { Some(x) } else { None };
  let b: Option<u32> = Some(2);
  let r: Result<u32, Fail> = if x > 3 { Ok(x) } else { Err(Fail::Small) };
  let s: Result<u32, Fail> = Err(Fail::Small);
  let arrays = [x, 1] == [2, 1];
  (a == b) as u32 * 1000 + (r == s) as u32 * 100 + arrays as u32 * 10 + (a == None) as u32
}

fn check(x: u32) -> Result<(), Fail> {
  if x < 5 { Err(Fail::Small) } else { Ok(()) }
}

fn checked_twice(x: u32) -> Result<(), Fail> {
  check(x)?;
  check(x + 1)
}

pub fn unit_results(x: u32) -> u32 {
  let ok = checked_twice(x).is_ok() as u32;
  if x > 100 {
    check(x).unwrap();
  }
  ok * 10 + checked_twice(x).is_err() as u32
}

pub fn unwrap_unit(x: u32) -> u32 {
  check(x).unwrap();
  check(x).expect("too small");
  x
}

pub fn defaults(i: usize) -> u32 {
  let g = Grid::default();
  let mut h = g;
  h.cells[i] = 7;
  h.pair.1 = 500;
  let level = if h.level == Level::Low { 1 } else { 2 };
  g.cells[i] as u32 + h.cells[i] as u32 * 10 + level * 100 + h.pair.1 as u32 + (g.origin == P::ORIGIN) as u32
}

pub fn chosen(c: bool) -> u32 {
  let base = [1u32, 2, 3];
  let view = if c { &base[..1] } else { &base[1..] };
  let list = if c { vec![5u32] } else { vec![] };
  let array = if c { [1u8; 2] } else { [2u8; 2] };
  let f: fn(u32) -> u32 = if c { double } else { negate_wrap };
  let g = if c { Grid { cells: [1, 2, 3], level: Level::High, origin: P::new(1, 1), pair: (1, 2) } } else { Grid::default() };
  view.len() as u32 * 100000 + list.len() as u32 * 10000 + array[0] as u32 * 1000 + f(2) % 1000 + g.cells[0] as u32
}

fn double(x: u32) -> u32 {
  x * 2
}

fn negate_wrap(x: u32) -> u32 {
  0u32.wrapping_sub(x)
}

fn sink(x: u32) {
  let _ = x;
}

pub fn pointers(x: u32) -> u32 {
  let g: fn(u32) = sink;
  g(x);
  let h: fn(u32) -> u32 = double;
  let a = [x, 1, 2];
  let mapped: u32 = a.iter().copied().map(h).sum();
  let square = |v: &u32| v * v;
  let squared: u32 = a.iter().map(square).sum();
  a.iter().copied().for_each(double_and_drop);
  mapped * 1000 + squared
}

fn double_and_drop(x: u32) {
  let _ = x * 2;
}

pub fn float_folds(a: &[f64]) -> f64 {
  let s: f64 = a.iter().sum();
  let p: f64 = a.iter().copied().product();
  s * 10.0 + p
}

pub fn vector_places(n: i32) -> i32 {
  let mut v = vec![P::new(1, 2), P::new(3, 4)];
  v[0].x = n;
  v[1].shift(n);
  for (i, p) in v.iter_mut().enumerate() {
    p.y += i as i32;
  }
  let mut w = vec![1u8, 2, 3];
  w.reverse();
  w.swap(0, 1);
  w.fill(9);
  w.clear();
  v[0].sum() * 100 + v[1].sum() + w.len() as i32
}

pub fn temporaries(n: i32) -> i32 {
  let mut p = P::new(1, 2);
  (&mut p).shift(n);
  P::shift(&mut p, 1);
  P::new(5, 5).shift(1);
  let mut a = [1i32, 2];
  let mut b = [3i32, 4];
  std::mem::swap(&mut a[0], &mut b[1]);
  bump(&mut 10);
  p.x * 100 + a[0] * 10 + b[1]
}

fn bump(x: &mut i32) -> i32 {
  *x += 1;
  *x
}

pub fn struct_copies(i: usize) -> u32 {
  let g = Grid { cells: [1, 2, 3], level: Level::Low, origin: P::ORIGIN, pair: (0, 0) };
  let mut h = g;
  h.cells[i] = 9;
  let k = h.clone();
  g.cells[i] as u32 * 100 + h.cells[i] as u32 * 10 + k.cells[i] as u32
}

pub fn closure_try(x: u32) -> u32 {
  let next = |v: Option<u32>| -> Option<u32> {
    let w = v?;
    Some(w + 1)
  };
  next(Some(x)).unwrap_or(0) * 10 + next(None).unwrap_or(7)
}

pub fn tables(i: usize) -> f64 {
  TABLE[i] + ZEROS[i] as f64 + POINTS[i % 2].y as f64 + TABLE.len() as f64
}

pub fn patterns(x: i32, y: i32) -> i32 {
  let p = P::new(x, y);
  let pair = Pair(x as u8, y as u8);
  let a = match p {
    P::ORIGIN => 1,
    P { x: 1, .. } => 2,
    _ => 3,
  };
  let b = match pair {
    Pair(0, _) => 10,
    Pair(a, b) if a == b => 20,
    Pair(..) => 30,
  };
  let r = &p;
  let c = match r {
    P { x: 0, y } => *y,
    _ => 100,
  };
  let o = &Some(x);
  let d = match o {
    Some(v) if *v > 2 => 1000,
    Some(_) => 2000,
    None => 3000,
  };
  let e = match (None::<u32>, x) {
    (None, 0) => 10000,
    (_, _) => 20000,
  };
  let f = match () {
    () => 0,
  };
  let Pair(first, _) = pair;
  a + b + c + d + e + f + first as i32
}

pub fn switches(n: u32) -> u32 {
  let mut total = 0;
  for i in 0..n {
    match i % 4 {
      0 => {
        total += 1;
        if total > 5 {
          break;
        }
      }
      1 | 2 => total += 10,
      _ => {
        let t = i * 100;
        total += t;
      }
    }
  }
  total
}

fn classify(n: u32) -> u32 {
  match n {
    0 => return 7,
    1 => return 8,
    _ => return 9,
  }
}

pub fn returning_switch(n: u32) -> u32 {
  classify(n) + classify(n + 1)
}

pub fn levels(high: bool) -> u32 {
  let l = if high { Level::High } else { Level::Low };
  let code = match l {
    Level::Low => {
      let a = 3;
      a * 2
    }
    Level::High => {
      let b = 4;
      b * 3
    }
  };
  code + (l == Level::High) as u32 * 100
}

pub fn into_iteration(n: u32) -> u32 {
  let v = vec![n, n + 1, n + 2];
  let mut s = 0;
  for x in &v {
    s += x;
  }
  for x in v.into_iter() {
    s += x * 10;
  }
  s
}

pub fn struct_options(x: i32) -> i32 {
  let found = if x > 0 { Some(P::new(x, 1)) } else { None };
  match found {
    Some(P { x, y }) => x + y,
    None => -1,
  }
}

pub fn nested_return(x: i32) -> i32 {
  let r = loop {
    if x > 10 {
      return x;
    }
    break x * 2;
  };
  r + 1
}

pub fn diverging_assignment(a: &mut [u8], c: bool) -> u8 {
  if c {
    a[0] = 5;
    return a[0];
  }
  let mut x = 1;
  if a.is_empty() {
    x = panic!("empty");
  }
  x + a[0]
}
