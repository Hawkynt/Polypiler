/// Twice `x`, handed back by an explicit `return` that clippy calls needless.
pub fn double(x: u32) -> u32 {
    let x = x * 2;
    return x;
}
