// Fixture of the Rust frontend: arrays, slices, Vec, references and slice iterators (#465). Each
// public function takes scalars and slices and returns a scalar or a pair; rustc runs it with
// overflow checks on and off, and the final contents of every slice argument follow the result.
// Adler-32 is RFC 1950's (Adler-32 of "Wikipedia" is 0x11E60398); CRC-32 the reflected IEEE 802.3
// polynomial 0xEDB88320 of ISO-HDLC (CRC-32 of "123456789" is 0xCBF43926).

const MODULUS: u32 = 65521;

static SQUARES: [u32; 8] = [0, 1, 4, 9, 16, 25, 36, 49];

pub fn adler32(d: &[u8]) -> u32 {
  let (mut a, mut b) = (1u32, 0u32);
  for &x in d {
    a = (a + x as u32) % MODULUS;
    b = (b + a) % MODULUS;
  }
  (b << 16) + a
}

pub fn crc32_bitwise(d: &[u8]) -> u32 {
  let mut crc = 0xFFFF_FFFFu32;
  for &byte in d.iter() {
    crc ^= byte as u32;
    for _ in 0..8 {
      crc = if crc & 1 != 0 { (crc >> 1) ^ 0xEDB8_8320 } else { crc >> 1 };
    }
  }
  !crc
}

pub fn xor_transform(src: &[u8], dst: &mut [u8]) {
  for (s, d) in src.iter().zip(dst.iter_mut()) {
    *d = *s ^ 0x5A;
  }
}

pub fn weighted_sum(a: &[u32]) -> u32 {
  let mut s = 0u32;
  for (i, &v) in a.iter().enumerate() {
    s += i as u32 * v;
  }
  s
}

pub fn load_be32(d: &[u8]) -> u32 {
  u32::from_be_bytes([d[0], d[1], d[2], d[3]])
}

pub fn load_le16(d: &[u8], at: usize) -> u16 {
  u16::from_le_bytes([d[at], d[at + 1]])
}

pub fn store_le32(x: u32, d: &mut [u8]) {
  d[..4].copy_from_slice(&x.to_le_bytes());
}

pub fn reverse_in_place(a: &mut [i32]) {
  let n = a.len();
  let mut i = 0;
  while i < n / 2 {
    a.swap(i, n - 1 - i);
    i += 1;
  }
}

pub fn reversed(a: &mut [i32]) {
  a.reverse();
}

pub fn chunk_heads(d: &[u8]) -> u32 {
  let mut s = 0u32;
  for c in d.chunks_exact(3) {
    s = s * 10 + c[0] as u32;
  }
  s
}

pub fn chunk_sums(d: &mut [u8]) -> u32 {
  let mut n = 0u32;
  for c in d.chunks_exact_mut(2) {
    c[1] = c[0].wrapping_add(c[1]);
    n += 1;
  }
  n
}

pub fn mid_sum(a: &[u8]) -> u32 {
  let s = &a[1..3];
  s[0] as u32 + s[1] as u32
}

pub fn at(a: &[u8], i: usize) -> u8 {
  a[i]
}

pub fn tail_len(a: &[u8], i: usize, j: usize) -> usize {
  a[i..j].len()
}

pub fn inclusive_len(a: &[u8], i: usize, j: usize) -> usize {
  a[i..=j].len()
}

pub fn from_len(a: &[u8], i: usize) -> usize {
  a[i..].len() + a[..i].len() * 100
}

pub fn put(dst: &mut [u8], src: &[u8]) {
  dst.copy_from_slice(src);
}

pub fn shift_within(a: &mut [u8], from: usize, to: usize, dest: usize) {
  a.copy_within(from..to, dest);
}

pub fn split_and_write(a: &mut [u32], m: usize) -> u32 {
  let (l, r) = a.split_at_mut(m);
  if !l.is_empty() && r.len() > 1 {
    l[0] = r[1];
    r[0] = 9;
  }
  l.len() as u32 * 10 + r.len() as u32
}

pub fn fill_with(a: &mut [u16], v: u16) {
  a.fill(v);
}

pub fn bsum(a: &[u8]) -> u8 {
  let mut s = 0u8;
  for &v in a.iter() {
    s += v;
  }
  s
}

pub fn last_element(a: &[u8]) -> u8 {
  a[a.len() - 1]
}

pub fn assign_order(a: &mut [u8], i: usize, z: u8) {
  a[i] = 10 / z;
}

pub fn wrapping_total(a: &[i64]) -> i64 {
  let mut s: i64 = 0;
  for x in a.iter() {
    s = s.wrapping_add(*x);
  }
  s
}

pub fn float_total(a: &[f64]) -> f64 {
  let mut s = 0.0;
  for &x in a {
    s += x;
  }
  s
}

pub fn adapters(a: &[u32]) -> u64 {
  let mut s = 0u64;
  for x in a.iter().rev().step_by(2) {
    s = s * 10 + *x as u64;
  }
  for (i, x) in a.iter().enumerate().rev() {
    s = s.wrapping_mul(7).wrapping_add(i as u64 * *x as u64);
  }
  for x in a.iter().copied().step_by(3) {
    s = s.wrapping_add(x as u64);
  }
  s
}

pub fn zip_count(a: &[u8], b: &[u8]) -> u32 {
  let mut n = 0;
  for _ in a.iter().zip(b) {
    n += 1;
  }
  n
}

pub fn vec_squares(n: u32) -> u32 {
  let mut v = Vec::new();
  for i in 0..n {
    v.push(i * i);
  }
  let w = v;
  if w.is_empty() { 0 } else { w.len() as u32 * 1000 + w[w.len() - 1] }
}

pub fn vec_macros(n: usize) -> u32 {
  let a = vec![3u32; n];
  let b = vec![1u32, 2, 3];
  let mut c = b.to_vec();
  c.push(a.len() as u32);
  let mut s = 0;
  for x in &c {
    s += x;
  }
  s + a.iter().count() as u32
}

pub fn table(i: usize) -> u32 {
  SQUARES[i] + SQUARES.len() as u32
}

pub fn array_copy(x: u32) -> u32 {
  let a = [x, x + 1, x + 2];
  let mut b = a;
  b[0] = 100;
  let c = bump(a);
  a[0] + b[0] + c
}

fn bump(mut a: [u32; 3]) -> u32 {
  a[0] += 5;
  a[0] + a[2]
}

pub fn byte_order(x: u32) -> u64 {
  let le = x.to_le_bytes();
  let be = (x as u16).to_be_bytes();
  let f = u64::from_be_bytes(17u64.to_be_bytes());
  le[0] as u64 * 1000000 + le[3] as u64 * 1000 + be[0] as u64 + f
}

pub fn scalar_refs(x: u32) -> u32 {
  let mut y = x;
  {
    let r = &mut y;
    *r += 1;
  }
  increment(&mut y);
  let mut pair = [y, 0];
  increment(&mut pair[1]);
  let mut a = 1u32;
  let mut b = 2u32;
  std::mem::swap(&mut a, &mut b);
  read(&pair[0]) * 100 + pair[1] * 10 + std::cmp::max(a, b) + std::cmp::min(a, b)
}

fn increment(x: &mut u32) {
  *x += 1;
}

fn read(x: &u32) -> u32 {
  *x
}

pub fn bytes_sum(n: usize) -> u32 {
  let b = b"polypiler";
  let mut s = 0u32;
  for &c in b[..n].iter() {
    s += c as u32;
  }
  s
}
