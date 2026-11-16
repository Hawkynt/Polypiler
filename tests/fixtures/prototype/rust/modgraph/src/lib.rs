// Fixture of the Rust frontend (#464): a library crate of several modules - an inline one, file
// modules beside the root and in a folder of their own, a re-export - with the crate's tests in a
// #[cfg(test)] module. The FNV-1a value of "a" is 0xE40C292C (the published 32-bit test vector).

pub mod hash;
mod util;

pub use util::bits::rotl;

pub fn checksum(data: &[u8]) -> u32 {
  hash::fnv1a(data) ^ rotl(data.len() as u32, 7)
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn fnv1a_of_a() {
    assert_eq!(hash::fnv1a(b"a"), 0xE40C292C);
  }

  #[test]
  fn rotl_wraps() {
    assert!(rotl(0x80000000, 1) == 1);
  }

  #[test]
  fn wrong() {
    assert_eq!(hash::fnv1a(b"a"), 0);
  }
}
