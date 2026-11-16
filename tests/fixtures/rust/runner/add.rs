// The sum of two u32 with the bare `+`, whose overflow depends on the build: it panics with overflow
// checks on and wraps with them off. Written for this repository.
pub fn add(a: u32, b: u32) -> u32 {
    a + b
}
