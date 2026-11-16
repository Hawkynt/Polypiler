// Trait objects: &dyn Trait, Box<dyn Trait> and Rc<dyn Trait> of the crate's traits, made from
// structs and primitives where the source expects one - a let, an argument, a return value, the arms
// of an if and a match, the elements of a vec! - and called through the trait, provided methods,
// supertraits and &mut self methods included.

use std::rc::Rc;

pub trait Shape {
  fn area(&self) -> u32;

  fn scaled(&self, k: u32) -> u32 {
    self.area() * k
  }
}

pub trait Named {
  fn id(&self) -> u32;
}

pub trait Tagged: Named {
  fn tag(&self) -> u32 {
    self.id() + 100
  }
}

pub trait Counter {
  fn bump(&mut self, by: u32);

  fn total(&self) -> u32;
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
}

impl Shape for Rect {
  fn area(&self) -> u32 {
    self.w * self.h
  }

  fn scaled(&self, k: u32) -> u32 {
    self.w * k + self.h
  }
}

impl Shape for u8 {
  fn area(&self) -> u32 {
    *self as u32
  }
}

impl Named for Square {
  fn id(&self) -> u32 {
    self.side
  }
}

impl Tagged for Square {}

struct Tally {
  n: u32,
}

struct Doubler {
  n: u32,
}

impl Counter for Tally {
  fn bump(&mut self, by: u32) {
    self.n += by;
  }

  fn total(&self) -> u32 {
    self.n
  }
}

impl Counter for Doubler {
  fn bump(&mut self, by: u32) {
    self.n += 2 * by;
  }

  fn total(&self) -> u32 {
    self.n
  }
}

fn make(kind: u32, x: u32) -> Box<dyn Shape> {
  if kind == 0 {
    Box::new(Square { side: x })
  } else if kind == 1 {
    Box::new(Rect { w: x, h: 2 })
  } else {
    Box::new(x as u8)
  }
}

fn counter(k: u8) -> Box<dyn Counter> {
  match k {
    0 => Box::new(Tally { n: 0 }),
    _ => {
      if k > 5 {
        return Box::new(Doubler { n: 1 });
      }
      Box::new(Doubler { n: 0 })
    }
  }
}

fn area_of(shape: &dyn Shape) -> u32 {
  shape.area()
}

fn show(t: &dyn Tagged) -> u32 {
  t.id() * 1000 + t.tag()
}

pub fn boxed_area(kind: u32, x: u32) -> u32 {
  let s = make(kind, x);
  s.area() + s.scaled(3)
}

pub fn borrowed(side: u32, w: u32) -> u32 {
  let sq = Square { side };
  let r = Rect { w, h: 3 };
  let named = &sq;
  area_of(named) + area_of(&r)
}

pub fn listed(n: u32) -> u32 {
  let mut shapes: Vec<Box<dyn Shape>> = vec![Box::new(Square { side: n }), Box::new(Rect { w: n, h: n + 1 })];
  shapes.push(Box::new(7u8));
  shapes.push(make(n % 3, n));
  let mut total = 0;
  for s in shapes.iter() {
    total += s.area();
  }
  total
}

pub fn shared(side: u32) -> u32 {
  let a: Rc<dyn Shape> = Rc::new(Square { side });
  let b = Rc::clone(&a);
  let c = a.clone();
  a.area() + b.area() + c.scaled(2)
}

pub fn tags(n: u32) -> u32 {
  let b: Box<dyn Tagged> = Box::new(Square { side: n });
  show(&*b) + show(&Square { side: 1 })
}

pub fn bumps(k: u8, times: u32) -> u32 {
  let mut counters: Vec<Box<dyn Counter>> = Vec::new();
  counters.push(counter(k));
  counters.push(counter(0));
  for _ in 0..times {
    for c in counters.iter_mut() {
      c.bump(1);
    }
  }
  let mut total = 0;
  for c in &counters {
    total += c.total();
  }
  total
}

pub fn moved(k: u8, by: u32) -> u32 {
  let mut a = counter(k);
  a.bump(by);
  let mut b = a;
  b.bump(by);
  b.total()
}

pub fn picked(k: u32, x: u8, y: u32) -> u32 {
  let small = x;
  let big = Square { side: y };
  let mut total = 0;
  for i in 0..k {
    let s: &dyn Shape = match i % 2 {
      0 => &small,
      _ => &big,
    };
    total += s.scaled(i);
  }
  total
}
