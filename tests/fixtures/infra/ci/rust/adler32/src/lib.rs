//! Adler-32 as RFC 1950 section 8.2 defines it: two sums modulo 65521, the second in the high half.

const MOD_ADLER: u32 = 65_521;

/// The Adler-32 checksum of `data`.
pub fn adler32(data: &[u8]) -> u32 {
    let mut a: u32 = 1;
    let mut b: u32 = 0;
    for &byte in data {
        a = (a + u32::from(byte)) % MOD_ADLER;
        b = (b + a) % MOD_ADLER;
    }
    (b << 16) | a
}

#[cfg(feature = "type-error")]
pub const NOT_A_NUMBER: u32 = "38600999";

#[cfg(all(test, not(feature = "no-tests")))]
mod tests {
    use super::adler32;

    #[cfg(not(feature = "wrong-expectation"))]
    const ABC: u32 = 0x024D_0127;
    #[cfg(feature = "wrong-expectation")]
    const ABC: u32 = 0x024D_0128;

    #[test]
    fn checksums() {
        assert_eq!(adler32(b""), 1);
        assert_eq!(adler32(b"a"), 0x0062_0062);
        assert_eq!(adler32(b"abc"), ABC);
    }
}
