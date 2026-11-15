// Traits and generics, resolved at compile time as rustc monomorphises them: trait methods of the
// crate's types and of primitives, provided methods, supertraits, associated functions, generic
// functions bounded on the crate's traits and on Copy, PartialOrd, Default and Add, and the operator
// traits and PartialEq implemented for a struct.

pub trait Shape {
  fn area(&self) -> u32;

  fn scaled(&self, k: u32) -> u32 {
    self.area() * k
  }

  fn code(&self) -> u8 {
    0
  }
}

#[derive(Clone, Copy)]
pub struct Square {
  pub side: u32,
}

#[derive(Clone, Copy)]
pub struct Rect {
  pub w: u32,
  pub h: u32,
}

impl Shape for Square {
  fn area(&self) -> u32 {
    self.side * self.side
  }

  fn code(&self) -> u8 {
    1
  }
}

impl Shape for Rect {
  fn area(&self) -> u32 {
    self.w * self.h
  }
}

pub fn square_area(side: u32) -> u32 {
  Square { side }.area()
}

pub fn rect_scaled(w: u32, h: u32, k: u32) -> u32 {
  Rect { w, h }.scaled(k)
}

pub fn codes(side: u32) -> u8 {
  Square { side }.code() * 10 + Rect { w: 1, h: side }.code()
}

fn double_area<T: Shape>(shape: &T) -> u32 {
  shape.area() * 2
}

pub fn generic_areas(side: u32, w: u32) -> u32 {
  double_area(&Square { side }) + double_area(&Rect { w, h: 2 })
}

pub trait Doubled {
  fn doubled(self) -> Self;
}

impl Doubled for u8 {
  fn doubled(self) -> u8 {
    self.wrapping_mul(2)
  }
}

impl Doubled for i64 {
  fn doubled(self) -> i64 {
    self * 2
  }
}

pub fn doubled_u8(x: u8) -> u8 {
  x.doubled()
}

pub fn doubled_i64(x: i64) -> i64 {
  x.doubled().doubled()
}

fn larger<T: PartialOrd + Copy>(a: T, b: T) -> T {
  if a > b { a } else { b }
}

pub fn larger_mix(a: u8, b: u8, c: i64, d: i64) -> i64 {
  larger(a, b) as i64 + larger(c, d)
}

fn sum3<T: std::ops::Add<Output = T> + Copy>(a: T, b: T, c: T) -> T {
  a + b + c
}

pub fn sum3_u16(a: u16, b: u16, c: u16) -> u16 {
  sum3(a, b, c)
}

pub fn sum3_i32(a: i32) -> i32 {
  sum3::<i32>(a, -a, a)
}

fn zero<T: Default>() -> T {
  T::default()
}

pub fn zeros() -> u32 {
  zero::<u32>() + zero::<u8>() as u32 + if zero::<bool>() { 1 } else { 0 }
}

#[derive(Clone, Copy)]
pub struct V2 {
  x: i32,
  y: i32,
}

impl std::ops::Add for V2 {
  type Output = V2;

  fn add(self, o: V2) -> V2 {
    V2 { x: self.x + o.x, y: self.y + o.y }
  }
}

impl std::ops::Sub for V2 {
  type Output = Self;

  fn sub(self, o: Self) -> Self::Output {
    V2 { x: self.x - o.x, y: self.y - o.y }
  }
}

impl PartialEq for V2 {
  fn eq(&self, o: &V2) -> bool {
    self.x == o.x && self.y == o.y
  }
}

pub fn vector_sum(a: i32, b: i32) -> i32 {
  let v = V2 { x: a, y: b } + V2 { x: b, y: a } - V2 { x: 1, y: 1 };
  v.x * 1000 + v.y
}

pub fn vector_eq(a: i32, b: i32) -> u8 {
  let p = V2 { x: a, y: b };
  let q = V2 { x: b, y: a };
  (p == q) as u8 * 2 + (p != q) as u8
}

pub fn vector_sum3(a: i32) -> i32 {
  let v = sum3(V2 { x: a, y: 1 }, V2 { x: a, y: 2 }, V2 { x: a, y: 3 });
  v.x + v.y
}

pub trait Named {
  fn id(&self) -> u32;
}

pub trait Tagged: Named {
  fn tag(&self) -> u32 {
    self.id() + 100
  }
}

impl Named for Square {
  fn id(&self) -> u32 {
    self.side
  }
}

impl Tagged for Square {}

pub fn tag_of(side: u32) -> u32 {
  Square { side }.tag()
}

fn describe<T: Shape + Tagged>(x: &T) -> u32 {
  x.area() + x.tag()
}

pub fn describe_square(side: u32) -> u32 {
  describe(&Square { side })
}

pub trait Unit {
  fn unit() -> Self;
}

impl Unit for Square {
  fn unit() -> Self {
    Square { side: 1 }
  }
}

impl Unit for u32 {
  fn unit() -> u32 {
    1
  }
}

fn units<T: Unit>() -> T {
  T::unit()
}

pub fn unit_sum() -> u32 {
  units::<Square>().side + units::<u32>() + Square::unit().area()
}
