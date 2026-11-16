// Maps and sets: HashMap, BTreeMap, HashSet and BTreeSet onto the IR's maps and sets - insertion
// with the value it replaces, lookup, removal, counting, indexing that panics on an absent key and
// entry(k).or_insert(v). No function iterates a map or a set: a HashMap's order is unspecified.

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};

#[derive(Clone, Copy)]
pub struct Point {
  pub x: i32,
  pub y: i32,
}

pub fn letter_counts(s: &str) -> u32 {
  let mut counts: HashMap<char, u32> = HashMap::new();
  for c in s.chars() {
    *counts.entry(c).or_insert(0) += 1;
  }
  let a = match counts.get(&'a') {
    Some(n) => *n,
    None => 0,
  };
  a * 100 + counts.len() as u32
}

pub fn replaced(k: u32, v: u64) -> u64 {
  let mut m = HashMap::new();
  let first = m.insert(k, v);
  let second = m.insert(k, v + 1);
  first.unwrap_or(7) * 1000 + second.unwrap_or(0) + m[&k]
}

pub fn removed(k: u8) -> i32 {
  let mut m: BTreeMap<u8, i32> = BTreeMap::new();
  m.insert(1, 10);
  m.insert(2, 20);
  let r = m.remove(&k);
  let left = m.len() as i32;
  match r {
    Some(v) => v * 10 + left,
    None => -left,
  }
}

pub fn indexed(k: u8) -> u16 {
  let mut m = HashMap::new();
  m.insert(3u8, 30u16);
  m.insert(4, 40);
  m[&k]
}

pub fn distinct(s: &str) -> usize {
  let mut set = HashSet::new();
  for b in s.bytes() {
    set.insert(b);
  }
  set.len()
}

pub fn set_ops(x: i64) -> u8 {
  let mut s: BTreeSet<i64> = BTreeSet::new();
  let a = s.insert(x);
  let b = s.insert(x);
  let c = s.contains(&x);
  let d = s.remove(&(x.wrapping_add(1)));
  let e = s.remove(&x);
  (a as u8) * 16 + (b as u8) * 8 + (c as u8) * 4 + (d as u8) * 2 + e as u8 + s.is_empty() as u8 * 32
}

pub fn word_lengths(s: &str) -> u32 {
  let mut m: HashMap<String, u32> = HashMap::new();
  for w in s.split(' ') {
    *m.entry(w.to_string()).or_default() += w.len() as u32;
  }
  let mut total = 0;
  if m.contains_key("ab") {
    total += m["ab"] * 100;
  }
  total + m.len() as u32
}

pub fn cleared(n: u32) -> bool {
  let mut m = HashMap::with_capacity(4);
  for i in 0..n {
    m.insert(i, i * 2);
  }
  let had = !m.is_empty();
  m.clear();
  had && m.is_empty()
}

pub fn points(k: bool) -> i32 {
  let mut m: HashMap<bool, Point> = HashMap::new();
  m.insert(true, Point { x: 1, y: 2 });
  m.entry(false).or_insert(Point { x: 3, y: 4 });
  m.entry(true).or_insert(Point { x: 5, y: 6 });
  let p = m[&k];
  p.x * 10 + p.y
}

fn bump(m: &mut HashMap<u8, u8>, k: u8) {
  *m.entry(k).or_insert(250) += 3;
}

pub fn bumped(k: u8) -> u8 {
  let mut m = HashMap::new();
  m.insert(1, 1);
  bump(&mut m, k);
  bump(&mut m, k);
  m[&k]
}
