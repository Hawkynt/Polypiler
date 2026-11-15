// One function per kind of panic the trap table of the Rust runner classifies, and one that only
// fails when debug assertions are on. Written for this repository.
pub fn div(a: i32, b: i32) -> i32 {
    a / b
}

pub fn first(x: u32) -> u32 {
    [1u32, 2, 3].iter().copied().find(|&value| value > x).unwrap()
}

pub fn boom() -> u32 {
    panic!("x")
}

pub fn never() -> u32 {
    unreachable!()
}

pub fn generated() -> u32 {
    panic!("InvalidCast")
}

pub fn checked(x: u32) -> u32 {
    debug_assert!(x > 0);
    x.wrapping_sub(1)
}
