// Whole values assigned through &mut: `*v = ...` of a Vec, an array, a map, a set, a struct and a
// String, through a parameter, through a local borrow, a reborrow and a borrow of a field, and passed
// on through a nested call. The caller sees the new value, as in Rust.

use std::collections::{HashMap, HashSet};

#[derive(Clone, Copy)]
pub struct Point {
  pub x: i32,
  pub y: i32,
}

pub struct Grid {
  pub cells: [u8; 3],
  pub n: u8,
}

fn set_vec(v: &mut Vec<u8>, k: u8) {
  *v = vec![k, k + 1, k + 2];
}

fn set_arr(a: &mut [u8; 4], k: u8) {
  *a = [k; 4];
}

fn set_point(p: &mut Point, k: i32) {
  *p = Point { x: k, y: k * 2 };
}

fn outer_point(p: &mut Point, k: i32) {
  set_point(p, k);
  p.y += 1;
}

fn set_map(m: &mut HashMap<u8, u8>, k: u8) {
  *m = HashMap::new();
  m.insert(k, k);
}

fn set_set(s: &mut HashSet<u8>, k: u8) {
  *s = HashSet::new();
  s.insert(k);
  s.insert(k / 2);
}

fn set_points(v: &mut Vec<Point>, k: i32) {
  *v = vec![Point { x: k, y: 1 }];
}

fn nested(v: &mut Vec<u8>, k: u8) {
  set_vec(v, k);
  v.push(9);
}

pub fn vec_param(k: u8) -> usize {
  let mut v = vec![9u8];
  set_vec(&mut v, k);
  v.len() * 1000 + v[2] as usize
}

pub fn arr_param(k: u8) -> u8 {
  let mut a = [1u8; 4];
  set_arr(&mut a, k);
  a[0] + a[3]
}

pub fn point_param(k: i32) -> i32 {
  let mut p = Point { x: 1, y: 2 };
  outer_point(&mut p, k);
  p.x * 10 + p.y
}

pub fn map_param(k: u8) -> usize {
  let mut m = HashMap::new();
  m.insert(1u8, 2u8);
  m.insert(3, 4);
  set_map(&mut m, k);
  m.len() * 1000 + m[&k] as usize
}

pub fn set_param(k: u8) -> usize {
  let mut s = HashSet::new();
  s.insert(100u8);
  set_set(&mut s, k);
  s.len() * 10 + if s.contains(&100) { 1 } else { 0 }
}

pub fn nested_param(k: u8) -> usize {
  let mut v = vec![9u8];
  nested(&mut v, k);
  v.len() * 1000 + v[0] as usize + v[3] as usize
}

pub fn struct_vec(k: i32) -> i32 {
  let mut v = vec![Point { x: 3, y: 3 }, Point { x: 4, y: 4 }];
  set_points(&mut v, k);
  v.len() as i32 * 1000 + v[0].x
}

pub fn locals(k: u8) -> usize {
  let mut v = vec![9u8];
  let r = &mut v;
  *r = vec![k, k];
  let mut a = [1u8; 4];
  let ra = &mut a;
  *ra = [k; 4];
  let mut s = String::from("abc");
  let rs = &mut s;
  *rs = String::from("hello");
  v.len() * 10000 + v[1] as usize * 100 + a[3] as usize + s.len() * 1000
}

pub fn borrowed_parts(k: u8) -> usize {
  let mut g = Grid { cells: [1, 2, 3], n: 0 };
  let r = &mut g.cells;
  *r = [k, 8, 7];
  let mut w = vec![1u8];
  let first = &mut w;
  let again = &mut *first;
  *again = vec![k; 3];
  g.cells[0] as usize + g.cells[2] as usize + g.n as usize + w.len() * 1000
}
