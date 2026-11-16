// collect() at the end of the iterator chains the frontend streams: into a Vec of values or of the
// shared references iter() yields, into a HashMap or a BTreeMap of pairs - a later key replacing the
// value of an earlier one - and into a HashSet or a BTreeSet, by turbofish or by the type expected.
// Nothing iterates a map or a set.

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};

pub fn squares(n: u32) -> u32 {
  let v: Vec<u32> = (0..n).map(|i| i * i).collect();
  let mut t = 0;
  for x in v.iter() {
    t += x;
  }
  t + v.len() as u32
}

pub fn evens(a: u8, b: u8) -> usize {
  let src = vec![a, b, a, 3, 4];
  let e = src.iter().filter(|x| **x % 2 == 0).copied().collect::<Vec<_>>();
  e.len() * 10 + e.first().copied().unwrap_or(0) as usize
}

pub fn index(n: u8) -> u32 {
  let words = vec![3u32, 5, 3, 9];
  let m: HashMap<u32, usize> = words.iter().copied().enumerate().map(|(i, w)| (w, i)).collect();
  let b = (0..n).map(|i| (i, i as u32 * 2)).collect::<BTreeMap<u8, u32>>();
  m[&3] as u32 * 1000 + m.len() as u32 * 100 + b.get(&(n / 2)).copied().unwrap_or(77)
}

pub fn distinct(a: u8, b: u8) -> usize {
  let s: HashSet<u8> = vec![a, b, a, 1].into_iter().collect();
  let t = vec![a, a].iter().copied().collect::<BTreeSet<_>>();
  s.len() * 10 + t.len()
}

pub fn borrowed(a: u16) -> u16 {
  let src = [a, 2, 3];
  let r: Vec<&u16> = src.iter().collect();
  *r[0] + r.len() as u16
}

pub fn paired(a: i32, b: i32) -> i32 {
  let xs = [a, b, 3];
  let ys: [i32; 3] = [10, 20, 30];
  let sums: Vec<i32> = xs.iter().zip(ys.iter()).map(|(x, y)| x * y).collect();
  sums.iter().sum::<i32>() - sums.len() as i32
}

pub fn letters(n: u32) -> usize {
  let text = "hello, wörld";
  let chars: Vec<char> = text.chars().filter(|c| c.is_ascii_alphabetic()).collect();
  let set: HashSet<char> = text.chars().collect();
  chars.len() * 100 + set.len() + (0..n).map(|i| i % 3).collect::<HashSet<u32>>().len() * 1000
}
