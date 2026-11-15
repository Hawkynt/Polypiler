@EndToEnd @Layer.rust
Feature: Rust frontend: arrays, slices, Vec, borrows and slice iterators
  Slices are views - read-only for `&[T]`, writable and exclusive for `&mut [T]` - every index and
  range is checked and panics outside, `zip` stops at the shorter side and `chunks_exact` drops the
  remainder. The expected values are the ones rustc computes for the same source (#465).

  Scenario Outline: Imported slice kernels compute what rustc computes
    Given the Rust source "<source>"
    When it is imported with overflowChecks panic
    And "f" is called with <args>
    Then the result is <expected>
    And rustc does the same

    Examples:
      | source                                                                                                                                                     | args                   | expected   |
      | pub fn f(d: &[u8]) -> u32 { let (mut a, mut b) = (1u32, 0u32); for &x in d { a = (a + x as u32) % 65521; b = (b + a) % 65521; } (b << 16) + a }         | bytes "Wikipedia"      | 0x11E60398 |
      | pub fn f(d: &[u8]) -> u32 { let (mut a, mut b) = (1u32, 0u32); for &x in d { a = (a + x as u32) % 65521; b = (b + a) % 65521; } (b << 16) + a }         | []                     | 1          |
      | pub fn f(d: &[u8]) -> u32 { u32::from_be_bytes([d[0], d[1], d[2], d[3]]) }                                                                                 | [0x12 0x34 0x56 0x78]  | 0x12345678 |
      | pub fn f(a: &[u32]) -> u32 { let mut s = 0u32; for (i, &v) in a.iter().enumerate() { s += i as u32 * v; } s }                                             | [3 1 4]                | 9          |
      | pub fn f(d: &[u8]) -> u32 { let mut s = 0u32; for c in d.chunks_exact(3) { s = s * 10 + c[0] as u32; } s }                                                 | [1 2 3 4 5 6 7]        | 14         |
      | pub fn f(a: &[u8]) -> u32 { let s = &a[1..3]; s[0] as u32 + s[1] as u32 }                                                                                  | [10 20 30 40]          | 50         |

  Scenario: A mutable slice is written through an exclusive view and zip stops at the shorter side
    Given the Rust source "pub fn f(src: &[u8], dst: &mut [u8]) { for (s, d) in src.iter().zip(dst.iter_mut()) { *d = *s ^ 0x5A; } }"
    When it is imported with overflowChecks panic
    And "f" is called with [0x00 0xFF 0x5A 0x01], [0 0 0 0 0]
    Then the slices after the call are [0 255 90 1] and [0x5A 0xA5 0x00 0x5B 0x00]
    And rustc does the same
    And the parameters of "f" are ReadOnlySpan, Span

  Scenario: An out-of-bounds index is a trap, not a wild read
    Given the Rust source "pub fn f(a: &[u8], i: usize) -> u8 { a[i] }"
    When it is imported with overflowChecks panic
    And "f" is called with [1 2 3], 3
    Then the interpreter reports the panic panic
    And rustc does the same

  Scenario Outline: Range indexing accepts start == len and rejects start > end
    Given the Rust source "pub fn f(a: &[u8], i: usize, j: usize) -> usize { a[i..j].len() }"
    When it is imported with overflowChecks panic
    And "f" is called with [1 2 3], <i>, <j>
    Then rustc does the same

    Examples:
      | i | j |
      | 3 | 3 |
      | 2 | 1 |
      | 1 | 5 |

  Scenario: copy_from_slice refuses unequal lengths
    Given the Rust source "pub fn f(dst: &mut [u8], src: &[u8]) { dst.copy_from_slice(src); }"
    When it is imported with overflowChecks panic
    And "f" is called with [0 0 0], [1 2]
    Then the interpreter reports the panic panic
    And rustc does the same

  Scenario: Unsafe memory access is rejected with a stable code
    Given the Rust source "fn peek(p: *const u8) -> u8 { unsafe { *p } }"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1522 at line 1
    And no IR is produced

  Scenario: Text the IR cannot hold exactly is rejected with a stable code
    Given the Rust source "fn n(s: &mut str) -> usize { s.len() }"
    When it is imported with overflowChecks panic
    Then the import fails with diagnostic PLY1521 at line 1, column 9
