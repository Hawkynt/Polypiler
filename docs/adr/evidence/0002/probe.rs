use std::hint::black_box as b;
use std::panic;
fn t<T: std::fmt::Debug>(f: impl FnOnce() -> T + panic::UnwindSafe) -> String {
    match panic::catch_unwind(f) {
        Ok(v) => format!("{:?}", v),
        Err(e) => format!("panic({})", e.downcast_ref::<String>().cloned().or(e.downcast_ref::<&str>().map(|s| s.to_string())).unwrap_or_default()),
    }
}
fn main() {
    panic::set_hook(Box::new(|_| {}));
    let (max, min, one, zero, m1) = (b(i32::MAX), b(i32::MIN), b(1i32), b(0i32), b(-1i32));
    println!("add: {}  sub: {}  mul: {}  neg: {}", t(move || max + one), t(move || min - one), t(move || b(65536i32) * b(65536)), t(move || -min));
    println!("wrapping_add: {}  checked_add: {:?}  saturating_add: {}  overflowing_add: {:?}", max.wrapping_add(one), max.checked_add(one), max.saturating_add(one), max.overflowing_add(one));
    println!("1 << 32: {}  1 << 33: {}  -8 >> 33: {}", t(move || one << b(32u32)), t(move || one << b(33u32)), t(move || b(-8i32) >> b(33u32)));
    println!("1 << -1i32: {}", t(move || one << m1));
    println!("wrapping_shl(33): {}  checked_shl(32): {:?}  unbounded_shl(32): {}  unbounded_shr(-8, 33): {}", one.wrapping_shl(b(33)), one.checked_shl(b(32)), one.unbounded_shl(b(32)), b(-8i32).unbounded_shr(b(33)));
    println!("7 / 0: {}  7 % 0: {}", t(move || b(7i32) / zero), t(move || b(7i32) % zero));
    println!("MIN / -1: {}  MIN % -1: {}", t(move || min / m1), t(move || min % m1));
    println!("wrapping_div(MIN,-1): {}  wrapping_rem(MIN,-1): {}  checked_div(7,0): {:?}", min.wrapping_div(m1), min.wrapping_rem(m1), b(7i32).checked_div(zero));
    println!("-7 / 3: {}  -7 % 3: {}  7 % -3: {}  rem_euclid(-7,3): {}  div_euclid(-7,3): {}  rem_euclid(7,-3): {}", b(-7i32) / b(3), b(-7i32) % b(3), b(7i32) % b(-3), b(-7i32).rem_euclid(3), b(-7i32).div_euclid(3), b(7i32).rem_euclid(-3));
    println!("3e9 as i32: {}  -3e9 as i32: {}  NaN as i32: {}  inf as i32: {}  -1.0 as u32: {}  300.0 as u8: {}  2.7 as i32: {}  -2.7 as i32: {}", b(3e9f64) as i32, b(-3e9f64) as i32, b(f64::NAN) as i32, b(f64::INFINITY) as i32, b(-1.0f64) as u32, b(300.0f64) as u8, b(2.7f64) as i32, b(-2.7f64) as i32);
    println!("300i32 as u8: {}  u8::try_from(300) is_err: {:?}  1.0 / 0.0: {}", b(300i32) as u8, u8::try_from(b(300i32)).is_err(), b(1.0f64) / b(0.0));
}
