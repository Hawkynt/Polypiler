// T::Output and <T as Add>::Output on a type parameter: the result of a generic function is the
// associated type of the impl its type argument has - Big for M, whose Add makes one - or the number
// itself for a primitive, whose operators yield their own type.

#[derive(Clone, Copy)]
pub struct M {
  pub v: u8,
}

#[derive(Clone, Copy)]
pub struct Big {
  pub v: u32,
}

impl std::ops::Add for M {
  type Output = Big;

  fn add(self, o: M) -> Big {
    Big { v: self.v as u32 + o.v as u32 }
  }
}

fn sum<T: std::ops::Add + Copy>(a: T, b: T) -> T::Output {
  a + b
}

fn twice<T>(a: T) -> <T as std::ops::Add>::Output
where
  T: std::ops::Add + Copy,
{
  a + a
}

pub fn sums(x: u8, y: i64) -> i64 {
  sum(x, x) as i64 + sum(y, 1) + sum(M { v: x }, M { v: 200 }).v as i64
}

pub fn literal(x: i32) -> i32 {
  sum(2, x)
}

pub fn doubled(x: u16) -> u32 {
  twice(x) as u32 + twice(M { v: x as u8 }).v
}
