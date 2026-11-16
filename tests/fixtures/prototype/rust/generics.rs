// Generic structs, impls, methods and traits, monomorphised as rustc does it: a struct for each list
// of type arguments, the methods of a generic impl and the generic methods made once for each,
// generic operator and trait impls, a generic trait implemented with different arguments, and a
// public generic function exported as the instances the crate makes of it.

#[derive(Clone, Copy, PartialEq)]
pub struct Pair<T> {
  pub a: T,
  pub b: T,
}

impl<T: Copy + std::ops::Add<Output = T>> Pair<T> {
  fn new(a: T, b: T) -> Self {
    Pair { a, b }
  }

  fn sum(&self) -> T {
    self.a + self.b
  }

  fn swap(&mut self) {
    let t = self.a;
    self.a = self.b;
    self.b = t;
  }

  fn with<U: Copy>(&self, u: U) -> (T, U) {
    (self.a, u)
  }

  fn twice(&self) -> Self {
    *self + Self::new(self.a, self.b)
  }
}

impl<T: Copy + std::ops::Add<Output = T>> std::ops::Add for Pair<T> {
  type Output = Pair<T>;

  fn add(self, o: Pair<T>) -> Pair<T> {
    Pair { a: self.a + o.a, b: self.b + o.b }
  }
}

struct Wrap<T>(T);

impl<T: Copy> Wrap<T> {
  fn get(&self) -> T {
    self.0
  }
}

pub trait Area {
  fn area(&self) -> u32;

  fn doubled(&self) -> u32 {
    self.area() * 2
  }
}

impl<T: Copy> Area for Wrap<T> {
  fn area(&self) -> u32 {
    7
  }
}

impl Area for Pair<u8> {
  fn area(&self) -> u32 {
    self.a as u32 * self.b as u32
  }
}

pub trait Conv<T> {
  fn conv(&self) -> T;

  fn conv_twice(&self) -> (T, T) {
    (self.conv(), self.conv())
  }
}

impl Conv<u32> for u8 {
  fn conv(&self) -> u32 {
    *self as u32 * 1000
  }
}

impl Conv<i64> for i8 {
  fn conv(&self) -> i64 {
    -(*self as i64)
  }
}

pub trait Tagged {
  fn id(&self) -> u32;

  fn tag(&self) -> u32 {
    self.id() + 100
  }
}

#[derive(Clone, Copy)]
pub struct Tag<T> {
  pub v: T,
}

impl<T: Copy + Conv<u32>> Tagged for Tag<T> {
  fn id(&self) -> u32 {
    self.v.conv()
  }
}

fn through<T: Conv<u32>>(x: T) -> u32 {
  x.conv() + 1
}

pub fn largest<T: PartialOrd + Copy>(a: T, b: T) -> T {
  if a > b { a } else { b }
}

pub fn sums(x: u8, y: i64) -> i64 {
  let p = Pair::new(x, x);
  let q = Pair { a: y, b: 1 };
  p.sum() as i64 + q.sum()
}

pub fn swapped(x: u32, y: u32) -> u32 {
  let mut p = Pair::new(x, y);
  p.swap();
  p.a * 10 + p.b
}

pub fn wrapped(x: u16) -> u32 {
  let w = Wrap(x);
  let v = Wrap(x as u8);
  w.get() as u32 + v.get() as u32
}

pub fn areas(x: u8) -> u32 {
  Pair { a: x, b: 3 }.area() + Wrap(x).doubled() + Pair { a: 2u8, b: x }.doubled()
}

pub fn paired(x: u8) -> u32 {
  let p = Pair::<u8>::new(x, 2);
  let q = p.with(1000u32);
  q.0 as u32 + q.1
}

pub fn equal(x: i32) -> bool {
  let p = Pair::<i32>::new(x, 1);
  p == Pair { a: 1, b: 1 }
}

pub fn vectors(a: i32, b: i32) -> i32 {
  let v = Pair { a, b } + Pair { a: 1, b: 1 };
  let w = v.twice();
  w.a * 100 + w.b
}

pub fn nested(a: u8) -> u8 {
  let p = Pair { a: Pair { a, b: 1 }, b: Pair { a: 2, b: 3 } };
  let q = p + p;
  q.a.a + q.b.b
}

pub fn boxed(a: u16) -> u32 {
  let s: Box<dyn Area> = Box::new(Wrap(a));
  let t: &dyn Area = &Pair { a: 1u8, b: 2u8 };
  s.area() + t.area()
}

pub fn listed(n: u8) -> u32 {
  let mut v: Vec<Pair<u8>> = Vec::new();
  v.push(Pair { a: n, b: 1 });
  v.push(Pair::new(2, 2));
  let mut t = 0u32;
  for p in v.iter() {
    t += p.sum() as u32;
  }
  t
}

pub fn conversions(x: u8, y: i8) -> i64 {
  let (a, b) = x.conv_twice();
  (a + b) as i64 + y.conv() + through(x) as i64
}

pub fn tagged(x: u8) -> u32 {
  Tag { v: x }.tag()
}

pub fn largest_user(a: u8, b: u8, c: i64) -> i64 {
  largest(a, b) as i64 + largest(c, 3)
}
