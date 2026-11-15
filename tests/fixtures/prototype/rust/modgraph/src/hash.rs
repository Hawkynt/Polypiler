use crate::util::bits;

/// FNV-1a over the bytes, 32 bits: the offset basis 0x811C9DC5 and the prime 16777619.
pub fn fnv1a(data: &[u8]) -> u32 {
  let mut h = 0x811C9DC5u32;
  for &b in data {
    h = bits::mix(h, b);
  }
  h
}
