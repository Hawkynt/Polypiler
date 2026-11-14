//! A read that skips the bounds check: defined for an index inside the slice, undefined one past it.

/// The element at `index`, read without a bounds check.
pub fn read_unchecked(values: &[u32], index: usize) -> u32 {
    // SAFETY: none; the caller is trusted to pass an index inside the slice, and one test does not.
    unsafe { *values.get_unchecked(index) }
}

#[cfg(test)]
mod tests {
    use super::read_unchecked;

    #[test]
    fn last_valid_index() {
        assert_eq!(read_unchecked(&[1, 2, 3, 4], 3), 4);
    }

    #[test]
    fn one_past_the_end() {
        let values = [1, 2, 3, 4];
        let _ = read_unchecked(&values, 4);
    }
}
