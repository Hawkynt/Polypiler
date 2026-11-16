pub fn rotl(x: u32, n: u32) -> u32 {
  x.rotate_left(n)
}

pub(crate) fn mix(h: u32, b: u8) -> u32 {
  (h ^ b as u32).wrapping_mul(16777619)
}
