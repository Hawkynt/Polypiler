// A function that ends the process on 0, which no panic handler catches. Written for this repository.
pub fn f(x: u32) -> u32 {
    if x == 0 {
        std::process::abort()
    }
    x
}
