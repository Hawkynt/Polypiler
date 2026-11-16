//! The package of `ok` with one wrong expectation. Written for this repository.

pub fn double(x: u32) -> u32 {
    x.wrapping_mul(2)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn doubles() {
        assert_eq!(double(21), 43);
    }

    #[test]
    fn wraps() {
        assert_eq!(double(2147483648), 0);
    }
}
