// Fixture of the Rust frontend: one public function per operator, method and cast of the scalar
// subset, for every integer type, f32 and f64. Each is run by rustc with overflow checks on and
// off; the recordings beside it are what those runs did (tests/fixtures/prototype/rust/recorded).

// ---- i8
pub fn add_i8(a: i8, b: i8) -> i8 { a + b }
pub fn sub_i8(a: i8, b: i8) -> i8 { a - b }
pub fn mul_i8(a: i8, b: i8) -> i8 { a * b }
pub fn div_i8(a: i8, b: i8) -> i8 { a / b }
pub fn rem_i8(a: i8, b: i8) -> i8 { a % b }
pub fn and_i8(a: i8, b: i8) -> i8 { a & b }
pub fn or_i8(a: i8, b: i8) -> i8 { a | b }
pub fn xor_i8(a: i8, b: i8) -> i8 { a ^ b }
pub fn shl_i8(a: i8, n: u32) -> i8 { a << n }
pub fn shr_i8(a: i8, n: u32) -> i8 { a >> n }
pub fn not_i8(a: i8) -> i8 { !a }
pub fn neg_i8(a: i8) -> i8 { -a }
pub fn lt_i8(a: i8, b: i8) -> bool { a < b }
pub fn ge_i8(a: i8, b: i8) -> bool { a >= b }
pub fn compound_i8(a: i8, b: i8) -> i8 { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_i8(a: i8, b: i8) -> i8 { a.wrapping_add(b) }
pub fn checked_add_or_i8(a: i8, b: i8) -> i8 { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_i8(a: i8, b: i8) -> i8 { a.checked_add(b).unwrap() }
pub fn checked_add_some_i8(a: i8, b: i8) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_i8(a: i8, b: i8) -> i8 { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_i8(a: i8, b: i8) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_i8(a: i8, b: i8) -> i8 { a.wrapping_sub(b) }
pub fn checked_sub_or_i8(a: i8, b: i8) -> i8 { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_i8(a: i8, b: i8) -> i8 { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_i8(a: i8, b: i8) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_i8(a: i8, b: i8) -> i8 { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_i8(a: i8, b: i8) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_i8(a: i8, b: i8) -> i8 { a.wrapping_mul(b) }
pub fn checked_mul_or_i8(a: i8, b: i8) -> i8 { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_i8(a: i8, b: i8) -> i8 { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_i8(a: i8, b: i8) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_i8(a: i8, b: i8) -> i8 { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_i8(a: i8, b: i8) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_i8(a: i8, b: i8) -> i8 { a.wrapping_div(b) }
pub fn checked_div_or_i8(a: i8, b: i8) -> i8 { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_i8(a: i8, b: i8) -> i8 { a.checked_div(b).unwrap() }
pub fn checked_div_some_i8(a: i8, b: i8) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_i8(a: i8, b: i8) -> i8 { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_i8(a: i8, b: i8) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_i8(a: i8, b: i8) -> i8 { a.wrapping_rem(b) }
pub fn checked_rem_or_i8(a: i8, b: i8) -> i8 { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_i8(a: i8, b: i8) -> i8 { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_i8(a: i8, b: i8) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_i8(a: i8, b: i8) -> i8 { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_i8(a: i8, b: i8) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_i8(a: i8) -> i8 { a.wrapping_neg() }
pub fn checked_neg_or_i8(a: i8) -> i8 { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_i8(a: i8) -> i8 { a.checked_neg().unwrap() }
pub fn checked_neg_some_i8(a: i8) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_i8(a: i8) -> i8 { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_i8(a: i8) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_i8(a: i8, b: u32) -> i8 { a.wrapping_shl(b) }
pub fn checked_shl_or_i8(a: i8, b: u32) -> i8 { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_i8(a: i8, b: u32) -> i8 { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_i8(a: i8, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_i8(a: i8, b: u32) -> i8 { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_i8(a: i8, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_i8(a: i8, b: u32) -> i8 { a.wrapping_shr(b) }
pub fn checked_shr_or_i8(a: i8, b: u32) -> i8 { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_i8(a: i8, b: u32) -> i8 { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_i8(a: i8, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_i8(a: i8, b: u32) -> i8 { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_i8(a: i8, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_i8(a: i8, b: u32) -> i8 { a.wrapping_pow(b) }
pub fn checked_pow_or_i8(a: i8, b: u32) -> i8 { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_i8(a: i8, b: u32) -> i8 { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_i8(a: i8, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_i8(a: i8, b: u32) -> i8 { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_i8(a: i8, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn wrapping_abs_i8(a: i8) -> i8 { a.wrapping_abs() }
pub fn checked_abs_or_i8(a: i8) -> i8 { a.checked_abs().unwrap_or(7) }
pub fn checked_abs_unwrap_i8(a: i8) -> i8 { a.checked_abs().unwrap() }
pub fn checked_abs_some_i8(a: i8) -> bool { a.checked_abs().is_some() }
pub fn overflowing_abs_i8(a: i8) -> i8 { let (v, o) = a.overflowing_abs(); if o { v ^ 1 } else { v } }
pub fn overflowing_abs_flag_i8(a: i8) -> bool { a.overflowing_abs().1 }
pub fn saturating_add_i8(a: i8, b: i8) -> i8 { a.saturating_add(b) }
pub fn saturating_sub_i8(a: i8, b: i8) -> i8 { a.saturating_sub(b) }
pub fn saturating_mul_i8(a: i8, b: i8) -> i8 { a.saturating_mul(b) }
pub fn saturating_div_i8(a: i8, b: i8) -> i8 { a.saturating_div(b) }
pub fn saturating_pow_i8(a: i8, b: u32) -> i8 { a.saturating_pow(b) }
pub fn saturating_neg_i8(a: i8) -> i8 { a.saturating_neg() }
pub fn saturating_abs_i8(a: i8) -> i8 { a.saturating_abs() }
pub fn pow_i8(a: i8, b: u32) -> i8 { a.pow(b) }
pub fn abs_i8(a: i8) -> i8 { a.abs() }
pub fn signum_i8(a: i8) -> i8 { a.signum() }
pub fn abs_diff_i8(a: i8, b: i8) -> u8 { a.abs_diff(b) }
pub fn min_max_i8(a: i8, b: i8) -> i8 { a.min(b) ^ a.max(b) }
pub fn clamp_i8(a: i8, b: i8) -> i8 { a.clamp(b, 5) }
pub fn div_euclid_i8(a: i8, b: i8) -> i8 { a.div_euclid(b) }
pub fn rem_euclid_i8(a: i8, b: i8) -> i8 { a.rem_euclid(b) }
pub fn rotate_left_i8(a: i8, b: u32) -> i8 { a.rotate_left(b) }
pub fn rotate_right_i8(a: i8, b: u32) -> i8 { a.rotate_right(b) }
pub fn swap_bytes_i8(a: i8) -> i8 { a.swap_bytes() }
pub fn reverse_bits_i8(a: i8) -> i8 { a.reverse_bits() }
pub fn counts_i8(a: i8) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_i8() -> i8 { i8::MAX ^ i8::MIN ^ (i8::BITS as i8) }
pub fn cast_i8_i16(a: i8) -> i16 { a as i16 }
pub fn cast_i8_i32(a: i8) -> i32 { a as i32 }
pub fn cast_i8_i64(a: i8) -> i64 { a as i64 }
pub fn cast_i8_isize(a: i8) -> isize { a as isize }
pub fn cast_i8_u8(a: i8) -> u8 { a as u8 }
pub fn cast_i8_u16(a: i8) -> u16 { a as u16 }
pub fn cast_i8_u32(a: i8) -> u32 { a as u32 }
pub fn cast_i8_u64(a: i8) -> u64 { a as u64 }
pub fn cast_i8_usize(a: i8) -> usize { a as usize }
pub fn cast_i8_f32(a: i8) -> f32 { a as f32 }
pub fn cast_i8_f64(a: i8) -> f64 { a as f64 }

// ---- i16
pub fn add_i16(a: i16, b: i16) -> i16 { a + b }
pub fn sub_i16(a: i16, b: i16) -> i16 { a - b }
pub fn mul_i16(a: i16, b: i16) -> i16 { a * b }
pub fn div_i16(a: i16, b: i16) -> i16 { a / b }
pub fn rem_i16(a: i16, b: i16) -> i16 { a % b }
pub fn and_i16(a: i16, b: i16) -> i16 { a & b }
pub fn or_i16(a: i16, b: i16) -> i16 { a | b }
pub fn xor_i16(a: i16, b: i16) -> i16 { a ^ b }
pub fn shl_i16(a: i16, n: u32) -> i16 { a << n }
pub fn shr_i16(a: i16, n: u32) -> i16 { a >> n }
pub fn not_i16(a: i16) -> i16 { !a }
pub fn neg_i16(a: i16) -> i16 { -a }
pub fn lt_i16(a: i16, b: i16) -> bool { a < b }
pub fn ge_i16(a: i16, b: i16) -> bool { a >= b }
pub fn compound_i16(a: i16, b: i16) -> i16 { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_i16(a: i16, b: i16) -> i16 { a.wrapping_add(b) }
pub fn checked_add_or_i16(a: i16, b: i16) -> i16 { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_i16(a: i16, b: i16) -> i16 { a.checked_add(b).unwrap() }
pub fn checked_add_some_i16(a: i16, b: i16) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_i16(a: i16, b: i16) -> i16 { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_i16(a: i16, b: i16) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_i16(a: i16, b: i16) -> i16 { a.wrapping_sub(b) }
pub fn checked_sub_or_i16(a: i16, b: i16) -> i16 { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_i16(a: i16, b: i16) -> i16 { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_i16(a: i16, b: i16) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_i16(a: i16, b: i16) -> i16 { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_i16(a: i16, b: i16) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_i16(a: i16, b: i16) -> i16 { a.wrapping_mul(b) }
pub fn checked_mul_or_i16(a: i16, b: i16) -> i16 { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_i16(a: i16, b: i16) -> i16 { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_i16(a: i16, b: i16) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_i16(a: i16, b: i16) -> i16 { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_i16(a: i16, b: i16) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_i16(a: i16, b: i16) -> i16 { a.wrapping_div(b) }
pub fn checked_div_or_i16(a: i16, b: i16) -> i16 { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_i16(a: i16, b: i16) -> i16 { a.checked_div(b).unwrap() }
pub fn checked_div_some_i16(a: i16, b: i16) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_i16(a: i16, b: i16) -> i16 { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_i16(a: i16, b: i16) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_i16(a: i16, b: i16) -> i16 { a.wrapping_rem(b) }
pub fn checked_rem_or_i16(a: i16, b: i16) -> i16 { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_i16(a: i16, b: i16) -> i16 { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_i16(a: i16, b: i16) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_i16(a: i16, b: i16) -> i16 { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_i16(a: i16, b: i16) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_i16(a: i16) -> i16 { a.wrapping_neg() }
pub fn checked_neg_or_i16(a: i16) -> i16 { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_i16(a: i16) -> i16 { a.checked_neg().unwrap() }
pub fn checked_neg_some_i16(a: i16) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_i16(a: i16) -> i16 { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_i16(a: i16) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_i16(a: i16, b: u32) -> i16 { a.wrapping_shl(b) }
pub fn checked_shl_or_i16(a: i16, b: u32) -> i16 { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_i16(a: i16, b: u32) -> i16 { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_i16(a: i16, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_i16(a: i16, b: u32) -> i16 { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_i16(a: i16, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_i16(a: i16, b: u32) -> i16 { a.wrapping_shr(b) }
pub fn checked_shr_or_i16(a: i16, b: u32) -> i16 { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_i16(a: i16, b: u32) -> i16 { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_i16(a: i16, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_i16(a: i16, b: u32) -> i16 { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_i16(a: i16, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_i16(a: i16, b: u32) -> i16 { a.wrapping_pow(b) }
pub fn checked_pow_or_i16(a: i16, b: u32) -> i16 { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_i16(a: i16, b: u32) -> i16 { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_i16(a: i16, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_i16(a: i16, b: u32) -> i16 { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_i16(a: i16, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn wrapping_abs_i16(a: i16) -> i16 { a.wrapping_abs() }
pub fn checked_abs_or_i16(a: i16) -> i16 { a.checked_abs().unwrap_or(7) }
pub fn checked_abs_unwrap_i16(a: i16) -> i16 { a.checked_abs().unwrap() }
pub fn checked_abs_some_i16(a: i16) -> bool { a.checked_abs().is_some() }
pub fn overflowing_abs_i16(a: i16) -> i16 { let (v, o) = a.overflowing_abs(); if o { v ^ 1 } else { v } }
pub fn overflowing_abs_flag_i16(a: i16) -> bool { a.overflowing_abs().1 }
pub fn saturating_add_i16(a: i16, b: i16) -> i16 { a.saturating_add(b) }
pub fn saturating_sub_i16(a: i16, b: i16) -> i16 { a.saturating_sub(b) }
pub fn saturating_mul_i16(a: i16, b: i16) -> i16 { a.saturating_mul(b) }
pub fn saturating_div_i16(a: i16, b: i16) -> i16 { a.saturating_div(b) }
pub fn saturating_pow_i16(a: i16, b: u32) -> i16 { a.saturating_pow(b) }
pub fn saturating_neg_i16(a: i16) -> i16 { a.saturating_neg() }
pub fn saturating_abs_i16(a: i16) -> i16 { a.saturating_abs() }
pub fn pow_i16(a: i16, b: u32) -> i16 { a.pow(b) }
pub fn abs_i16(a: i16) -> i16 { a.abs() }
pub fn signum_i16(a: i16) -> i16 { a.signum() }
pub fn abs_diff_i16(a: i16, b: i16) -> u16 { a.abs_diff(b) }
pub fn min_max_i16(a: i16, b: i16) -> i16 { a.min(b) ^ a.max(b) }
pub fn clamp_i16(a: i16, b: i16) -> i16 { a.clamp(b, 5) }
pub fn div_euclid_i16(a: i16, b: i16) -> i16 { a.div_euclid(b) }
pub fn rem_euclid_i16(a: i16, b: i16) -> i16 { a.rem_euclid(b) }
pub fn rotate_left_i16(a: i16, b: u32) -> i16 { a.rotate_left(b) }
pub fn rotate_right_i16(a: i16, b: u32) -> i16 { a.rotate_right(b) }
pub fn swap_bytes_i16(a: i16) -> i16 { a.swap_bytes() }
pub fn reverse_bits_i16(a: i16) -> i16 { a.reverse_bits() }
pub fn counts_i16(a: i16) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_i16() -> i16 { i16::MAX ^ i16::MIN ^ (i16::BITS as i16) }
pub fn cast_i16_i8(a: i16) -> i8 { a as i8 }
pub fn cast_i16_i32(a: i16) -> i32 { a as i32 }
pub fn cast_i16_i64(a: i16) -> i64 { a as i64 }
pub fn cast_i16_isize(a: i16) -> isize { a as isize }
pub fn cast_i16_u8(a: i16) -> u8 { a as u8 }
pub fn cast_i16_u16(a: i16) -> u16 { a as u16 }
pub fn cast_i16_u32(a: i16) -> u32 { a as u32 }
pub fn cast_i16_u64(a: i16) -> u64 { a as u64 }
pub fn cast_i16_usize(a: i16) -> usize { a as usize }
pub fn cast_i16_f32(a: i16) -> f32 { a as f32 }
pub fn cast_i16_f64(a: i16) -> f64 { a as f64 }

// ---- i32
pub fn add_i32(a: i32, b: i32) -> i32 { a + b }
pub fn sub_i32(a: i32, b: i32) -> i32 { a - b }
pub fn mul_i32(a: i32, b: i32) -> i32 { a * b }
pub fn div_i32(a: i32, b: i32) -> i32 { a / b }
pub fn rem_i32(a: i32, b: i32) -> i32 { a % b }
pub fn and_i32(a: i32, b: i32) -> i32 { a & b }
pub fn or_i32(a: i32, b: i32) -> i32 { a | b }
pub fn xor_i32(a: i32, b: i32) -> i32 { a ^ b }
pub fn shl_i32(a: i32, n: u32) -> i32 { a << n }
pub fn shr_i32(a: i32, n: u32) -> i32 { a >> n }
pub fn not_i32(a: i32) -> i32 { !a }
pub fn neg_i32(a: i32) -> i32 { -a }
pub fn lt_i32(a: i32, b: i32) -> bool { a < b }
pub fn ge_i32(a: i32, b: i32) -> bool { a >= b }
pub fn compound_i32(a: i32, b: i32) -> i32 { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_i32(a: i32, b: i32) -> i32 { a.wrapping_add(b) }
pub fn checked_add_or_i32(a: i32, b: i32) -> i32 { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_i32(a: i32, b: i32) -> i32 { a.checked_add(b).unwrap() }
pub fn checked_add_some_i32(a: i32, b: i32) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_i32(a: i32, b: i32) -> i32 { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_i32(a: i32, b: i32) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_i32(a: i32, b: i32) -> i32 { a.wrapping_sub(b) }
pub fn checked_sub_or_i32(a: i32, b: i32) -> i32 { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_i32(a: i32, b: i32) -> i32 { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_i32(a: i32, b: i32) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_i32(a: i32, b: i32) -> i32 { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_i32(a: i32, b: i32) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_i32(a: i32, b: i32) -> i32 { a.wrapping_mul(b) }
pub fn checked_mul_or_i32(a: i32, b: i32) -> i32 { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_i32(a: i32, b: i32) -> i32 { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_i32(a: i32, b: i32) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_i32(a: i32, b: i32) -> i32 { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_i32(a: i32, b: i32) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_i32(a: i32, b: i32) -> i32 { a.wrapping_div(b) }
pub fn checked_div_or_i32(a: i32, b: i32) -> i32 { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_i32(a: i32, b: i32) -> i32 { a.checked_div(b).unwrap() }
pub fn checked_div_some_i32(a: i32, b: i32) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_i32(a: i32, b: i32) -> i32 { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_i32(a: i32, b: i32) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_i32(a: i32, b: i32) -> i32 { a.wrapping_rem(b) }
pub fn checked_rem_or_i32(a: i32, b: i32) -> i32 { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_i32(a: i32, b: i32) -> i32 { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_i32(a: i32, b: i32) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_i32(a: i32, b: i32) -> i32 { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_i32(a: i32, b: i32) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_i32(a: i32) -> i32 { a.wrapping_neg() }
pub fn checked_neg_or_i32(a: i32) -> i32 { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_i32(a: i32) -> i32 { a.checked_neg().unwrap() }
pub fn checked_neg_some_i32(a: i32) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_i32(a: i32) -> i32 { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_i32(a: i32) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_i32(a: i32, b: u32) -> i32 { a.wrapping_shl(b) }
pub fn checked_shl_or_i32(a: i32, b: u32) -> i32 { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_i32(a: i32, b: u32) -> i32 { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_i32(a: i32, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_i32(a: i32, b: u32) -> i32 { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_i32(a: i32, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_i32(a: i32, b: u32) -> i32 { a.wrapping_shr(b) }
pub fn checked_shr_or_i32(a: i32, b: u32) -> i32 { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_i32(a: i32, b: u32) -> i32 { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_i32(a: i32, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_i32(a: i32, b: u32) -> i32 { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_i32(a: i32, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_i32(a: i32, b: u32) -> i32 { a.wrapping_pow(b) }
pub fn checked_pow_or_i32(a: i32, b: u32) -> i32 { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_i32(a: i32, b: u32) -> i32 { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_i32(a: i32, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_i32(a: i32, b: u32) -> i32 { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_i32(a: i32, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn wrapping_abs_i32(a: i32) -> i32 { a.wrapping_abs() }
pub fn checked_abs_or_i32(a: i32) -> i32 { a.checked_abs().unwrap_or(7) }
pub fn checked_abs_unwrap_i32(a: i32) -> i32 { a.checked_abs().unwrap() }
pub fn checked_abs_some_i32(a: i32) -> bool { a.checked_abs().is_some() }
pub fn overflowing_abs_i32(a: i32) -> i32 { let (v, o) = a.overflowing_abs(); if o { v ^ 1 } else { v } }
pub fn overflowing_abs_flag_i32(a: i32) -> bool { a.overflowing_abs().1 }
pub fn saturating_add_i32(a: i32, b: i32) -> i32 { a.saturating_add(b) }
pub fn saturating_sub_i32(a: i32, b: i32) -> i32 { a.saturating_sub(b) }
pub fn saturating_mul_i32(a: i32, b: i32) -> i32 { a.saturating_mul(b) }
pub fn saturating_div_i32(a: i32, b: i32) -> i32 { a.saturating_div(b) }
pub fn saturating_pow_i32(a: i32, b: u32) -> i32 { a.saturating_pow(b) }
pub fn saturating_neg_i32(a: i32) -> i32 { a.saturating_neg() }
pub fn saturating_abs_i32(a: i32) -> i32 { a.saturating_abs() }
pub fn pow_i32(a: i32, b: u32) -> i32 { a.pow(b) }
pub fn abs_i32(a: i32) -> i32 { a.abs() }
pub fn signum_i32(a: i32) -> i32 { a.signum() }
pub fn abs_diff_i32(a: i32, b: i32) -> u32 { a.abs_diff(b) }
pub fn min_max_i32(a: i32, b: i32) -> i32 { a.min(b) ^ a.max(b) }
pub fn clamp_i32(a: i32, b: i32) -> i32 { a.clamp(b, 5) }
pub fn div_euclid_i32(a: i32, b: i32) -> i32 { a.div_euclid(b) }
pub fn rem_euclid_i32(a: i32, b: i32) -> i32 { a.rem_euclid(b) }
pub fn rotate_left_i32(a: i32, b: u32) -> i32 { a.rotate_left(b) }
pub fn rotate_right_i32(a: i32, b: u32) -> i32 { a.rotate_right(b) }
pub fn swap_bytes_i32(a: i32) -> i32 { a.swap_bytes() }
pub fn reverse_bits_i32(a: i32) -> i32 { a.reverse_bits() }
pub fn counts_i32(a: i32) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_i32() -> i32 { i32::MAX ^ i32::MIN ^ (i32::BITS as i32) }
pub fn cast_i32_i8(a: i32) -> i8 { a as i8 }
pub fn cast_i32_i16(a: i32) -> i16 { a as i16 }
pub fn cast_i32_i64(a: i32) -> i64 { a as i64 }
pub fn cast_i32_isize(a: i32) -> isize { a as isize }
pub fn cast_i32_u8(a: i32) -> u8 { a as u8 }
pub fn cast_i32_u16(a: i32) -> u16 { a as u16 }
pub fn cast_i32_u32(a: i32) -> u32 { a as u32 }
pub fn cast_i32_u64(a: i32) -> u64 { a as u64 }
pub fn cast_i32_usize(a: i32) -> usize { a as usize }
pub fn cast_i32_f32(a: i32) -> f32 { a as f32 }
pub fn cast_i32_f64(a: i32) -> f64 { a as f64 }

// ---- i64
pub fn add_i64(a: i64, b: i64) -> i64 { a + b }
pub fn sub_i64(a: i64, b: i64) -> i64 { a - b }
pub fn mul_i64(a: i64, b: i64) -> i64 { a * b }
pub fn div_i64(a: i64, b: i64) -> i64 { a / b }
pub fn rem_i64(a: i64, b: i64) -> i64 { a % b }
pub fn and_i64(a: i64, b: i64) -> i64 { a & b }
pub fn or_i64(a: i64, b: i64) -> i64 { a | b }
pub fn xor_i64(a: i64, b: i64) -> i64 { a ^ b }
pub fn shl_i64(a: i64, n: u32) -> i64 { a << n }
pub fn shr_i64(a: i64, n: u32) -> i64 { a >> n }
pub fn not_i64(a: i64) -> i64 { !a }
pub fn neg_i64(a: i64) -> i64 { -a }
pub fn lt_i64(a: i64, b: i64) -> bool { a < b }
pub fn ge_i64(a: i64, b: i64) -> bool { a >= b }
pub fn compound_i64(a: i64, b: i64) -> i64 { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_i64(a: i64, b: i64) -> i64 { a.wrapping_add(b) }
pub fn checked_add_or_i64(a: i64, b: i64) -> i64 { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_i64(a: i64, b: i64) -> i64 { a.checked_add(b).unwrap() }
pub fn checked_add_some_i64(a: i64, b: i64) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_i64(a: i64, b: i64) -> i64 { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_i64(a: i64, b: i64) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_i64(a: i64, b: i64) -> i64 { a.wrapping_sub(b) }
pub fn checked_sub_or_i64(a: i64, b: i64) -> i64 { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_i64(a: i64, b: i64) -> i64 { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_i64(a: i64, b: i64) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_i64(a: i64, b: i64) -> i64 { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_i64(a: i64, b: i64) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_i64(a: i64, b: i64) -> i64 { a.wrapping_mul(b) }
pub fn checked_mul_or_i64(a: i64, b: i64) -> i64 { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_i64(a: i64, b: i64) -> i64 { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_i64(a: i64, b: i64) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_i64(a: i64, b: i64) -> i64 { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_i64(a: i64, b: i64) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_i64(a: i64, b: i64) -> i64 { a.wrapping_div(b) }
pub fn checked_div_or_i64(a: i64, b: i64) -> i64 { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_i64(a: i64, b: i64) -> i64 { a.checked_div(b).unwrap() }
pub fn checked_div_some_i64(a: i64, b: i64) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_i64(a: i64, b: i64) -> i64 { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_i64(a: i64, b: i64) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_i64(a: i64, b: i64) -> i64 { a.wrapping_rem(b) }
pub fn checked_rem_or_i64(a: i64, b: i64) -> i64 { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_i64(a: i64, b: i64) -> i64 { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_i64(a: i64, b: i64) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_i64(a: i64, b: i64) -> i64 { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_i64(a: i64, b: i64) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_i64(a: i64) -> i64 { a.wrapping_neg() }
pub fn checked_neg_or_i64(a: i64) -> i64 { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_i64(a: i64) -> i64 { a.checked_neg().unwrap() }
pub fn checked_neg_some_i64(a: i64) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_i64(a: i64) -> i64 { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_i64(a: i64) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_i64(a: i64, b: u32) -> i64 { a.wrapping_shl(b) }
pub fn checked_shl_or_i64(a: i64, b: u32) -> i64 { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_i64(a: i64, b: u32) -> i64 { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_i64(a: i64, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_i64(a: i64, b: u32) -> i64 { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_i64(a: i64, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_i64(a: i64, b: u32) -> i64 { a.wrapping_shr(b) }
pub fn checked_shr_or_i64(a: i64, b: u32) -> i64 { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_i64(a: i64, b: u32) -> i64 { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_i64(a: i64, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_i64(a: i64, b: u32) -> i64 { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_i64(a: i64, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_i64(a: i64, b: u32) -> i64 { a.wrapping_pow(b) }
pub fn checked_pow_or_i64(a: i64, b: u32) -> i64 { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_i64(a: i64, b: u32) -> i64 { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_i64(a: i64, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_i64(a: i64, b: u32) -> i64 { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_i64(a: i64, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn wrapping_abs_i64(a: i64) -> i64 { a.wrapping_abs() }
pub fn checked_abs_or_i64(a: i64) -> i64 { a.checked_abs().unwrap_or(7) }
pub fn checked_abs_unwrap_i64(a: i64) -> i64 { a.checked_abs().unwrap() }
pub fn checked_abs_some_i64(a: i64) -> bool { a.checked_abs().is_some() }
pub fn overflowing_abs_i64(a: i64) -> i64 { let (v, o) = a.overflowing_abs(); if o { v ^ 1 } else { v } }
pub fn overflowing_abs_flag_i64(a: i64) -> bool { a.overflowing_abs().1 }
pub fn saturating_add_i64(a: i64, b: i64) -> i64 { a.saturating_add(b) }
pub fn saturating_sub_i64(a: i64, b: i64) -> i64 { a.saturating_sub(b) }
pub fn saturating_mul_i64(a: i64, b: i64) -> i64 { a.saturating_mul(b) }
pub fn saturating_div_i64(a: i64, b: i64) -> i64 { a.saturating_div(b) }
pub fn saturating_pow_i64(a: i64, b: u32) -> i64 { a.saturating_pow(b) }
pub fn saturating_neg_i64(a: i64) -> i64 { a.saturating_neg() }
pub fn saturating_abs_i64(a: i64) -> i64 { a.saturating_abs() }
pub fn pow_i64(a: i64, b: u32) -> i64 { a.pow(b) }
pub fn abs_i64(a: i64) -> i64 { a.abs() }
pub fn signum_i64(a: i64) -> i64 { a.signum() }
pub fn abs_diff_i64(a: i64, b: i64) -> u64 { a.abs_diff(b) }
pub fn min_max_i64(a: i64, b: i64) -> i64 { a.min(b) ^ a.max(b) }
pub fn clamp_i64(a: i64, b: i64) -> i64 { a.clamp(b, 5) }
pub fn div_euclid_i64(a: i64, b: i64) -> i64 { a.div_euclid(b) }
pub fn rem_euclid_i64(a: i64, b: i64) -> i64 { a.rem_euclid(b) }
pub fn rotate_left_i64(a: i64, b: u32) -> i64 { a.rotate_left(b) }
pub fn rotate_right_i64(a: i64, b: u32) -> i64 { a.rotate_right(b) }
pub fn swap_bytes_i64(a: i64) -> i64 { a.swap_bytes() }
pub fn reverse_bits_i64(a: i64) -> i64 { a.reverse_bits() }
pub fn counts_i64(a: i64) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_i64() -> i64 { i64::MAX ^ i64::MIN ^ (i64::BITS as i64) }
pub fn cast_i64_i8(a: i64) -> i8 { a as i8 }
pub fn cast_i64_i16(a: i64) -> i16 { a as i16 }
pub fn cast_i64_i32(a: i64) -> i32 { a as i32 }
pub fn cast_i64_isize(a: i64) -> isize { a as isize }
pub fn cast_i64_u8(a: i64) -> u8 { a as u8 }
pub fn cast_i64_u16(a: i64) -> u16 { a as u16 }
pub fn cast_i64_u32(a: i64) -> u32 { a as u32 }
pub fn cast_i64_u64(a: i64) -> u64 { a as u64 }
pub fn cast_i64_usize(a: i64) -> usize { a as usize }
pub fn cast_i64_f32(a: i64) -> f32 { a as f32 }
pub fn cast_i64_f64(a: i64) -> f64 { a as f64 }

// ---- isize
pub fn add_isize(a: isize, b: isize) -> isize { a + b }
pub fn sub_isize(a: isize, b: isize) -> isize { a - b }
pub fn mul_isize(a: isize, b: isize) -> isize { a * b }
pub fn div_isize(a: isize, b: isize) -> isize { a / b }
pub fn rem_isize(a: isize, b: isize) -> isize { a % b }
pub fn and_isize(a: isize, b: isize) -> isize { a & b }
pub fn or_isize(a: isize, b: isize) -> isize { a | b }
pub fn xor_isize(a: isize, b: isize) -> isize { a ^ b }
pub fn shl_isize(a: isize, n: u32) -> isize { a << n }
pub fn shr_isize(a: isize, n: u32) -> isize { a >> n }
pub fn not_isize(a: isize) -> isize { !a }
pub fn neg_isize(a: isize) -> isize { -a }
pub fn lt_isize(a: isize, b: isize) -> bool { a < b }
pub fn ge_isize(a: isize, b: isize) -> bool { a >= b }
pub fn compound_isize(a: isize, b: isize) -> isize { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_isize(a: isize, b: isize) -> isize { a.wrapping_add(b) }
pub fn checked_add_or_isize(a: isize, b: isize) -> isize { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_isize(a: isize, b: isize) -> isize { a.checked_add(b).unwrap() }
pub fn checked_add_some_isize(a: isize, b: isize) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_isize(a: isize, b: isize) -> isize { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_isize(a: isize, b: isize) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_isize(a: isize, b: isize) -> isize { a.wrapping_sub(b) }
pub fn checked_sub_or_isize(a: isize, b: isize) -> isize { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_isize(a: isize, b: isize) -> isize { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_isize(a: isize, b: isize) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_isize(a: isize, b: isize) -> isize { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_isize(a: isize, b: isize) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_isize(a: isize, b: isize) -> isize { a.wrapping_mul(b) }
pub fn checked_mul_or_isize(a: isize, b: isize) -> isize { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_isize(a: isize, b: isize) -> isize { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_isize(a: isize, b: isize) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_isize(a: isize, b: isize) -> isize { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_isize(a: isize, b: isize) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_isize(a: isize, b: isize) -> isize { a.wrapping_div(b) }
pub fn checked_div_or_isize(a: isize, b: isize) -> isize { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_isize(a: isize, b: isize) -> isize { a.checked_div(b).unwrap() }
pub fn checked_div_some_isize(a: isize, b: isize) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_isize(a: isize, b: isize) -> isize { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_isize(a: isize, b: isize) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_isize(a: isize, b: isize) -> isize { a.wrapping_rem(b) }
pub fn checked_rem_or_isize(a: isize, b: isize) -> isize { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_isize(a: isize, b: isize) -> isize { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_isize(a: isize, b: isize) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_isize(a: isize, b: isize) -> isize { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_isize(a: isize, b: isize) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_isize(a: isize) -> isize { a.wrapping_neg() }
pub fn checked_neg_or_isize(a: isize) -> isize { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_isize(a: isize) -> isize { a.checked_neg().unwrap() }
pub fn checked_neg_some_isize(a: isize) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_isize(a: isize) -> isize { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_isize(a: isize) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_isize(a: isize, b: u32) -> isize { a.wrapping_shl(b) }
pub fn checked_shl_or_isize(a: isize, b: u32) -> isize { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_isize(a: isize, b: u32) -> isize { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_isize(a: isize, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_isize(a: isize, b: u32) -> isize { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_isize(a: isize, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_isize(a: isize, b: u32) -> isize { a.wrapping_shr(b) }
pub fn checked_shr_or_isize(a: isize, b: u32) -> isize { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_isize(a: isize, b: u32) -> isize { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_isize(a: isize, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_isize(a: isize, b: u32) -> isize { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_isize(a: isize, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_isize(a: isize, b: u32) -> isize { a.wrapping_pow(b) }
pub fn checked_pow_or_isize(a: isize, b: u32) -> isize { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_isize(a: isize, b: u32) -> isize { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_isize(a: isize, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_isize(a: isize, b: u32) -> isize { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_isize(a: isize, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn wrapping_abs_isize(a: isize) -> isize { a.wrapping_abs() }
pub fn checked_abs_or_isize(a: isize) -> isize { a.checked_abs().unwrap_or(7) }
pub fn checked_abs_unwrap_isize(a: isize) -> isize { a.checked_abs().unwrap() }
pub fn checked_abs_some_isize(a: isize) -> bool { a.checked_abs().is_some() }
pub fn overflowing_abs_isize(a: isize) -> isize { let (v, o) = a.overflowing_abs(); if o { v ^ 1 } else { v } }
pub fn overflowing_abs_flag_isize(a: isize) -> bool { a.overflowing_abs().1 }
pub fn saturating_add_isize(a: isize, b: isize) -> isize { a.saturating_add(b) }
pub fn saturating_sub_isize(a: isize, b: isize) -> isize { a.saturating_sub(b) }
pub fn saturating_mul_isize(a: isize, b: isize) -> isize { a.saturating_mul(b) }
pub fn saturating_div_isize(a: isize, b: isize) -> isize { a.saturating_div(b) }
pub fn saturating_pow_isize(a: isize, b: u32) -> isize { a.saturating_pow(b) }
pub fn saturating_neg_isize(a: isize) -> isize { a.saturating_neg() }
pub fn saturating_abs_isize(a: isize) -> isize { a.saturating_abs() }
pub fn pow_isize(a: isize, b: u32) -> isize { a.pow(b) }
pub fn abs_isize(a: isize) -> isize { a.abs() }
pub fn signum_isize(a: isize) -> isize { a.signum() }
pub fn abs_diff_isize(a: isize, b: isize) -> usize { a.abs_diff(b) }
pub fn min_max_isize(a: isize, b: isize) -> isize { a.min(b) ^ a.max(b) }
pub fn clamp_isize(a: isize, b: isize) -> isize { a.clamp(b, 5) }
pub fn div_euclid_isize(a: isize, b: isize) -> isize { a.div_euclid(b) }
pub fn rem_euclid_isize(a: isize, b: isize) -> isize { a.rem_euclid(b) }
pub fn rotate_left_isize(a: isize, b: u32) -> isize { a.rotate_left(b) }
pub fn rotate_right_isize(a: isize, b: u32) -> isize { a.rotate_right(b) }
pub fn swap_bytes_isize(a: isize) -> isize { a.swap_bytes() }
pub fn reverse_bits_isize(a: isize) -> isize { a.reverse_bits() }
pub fn counts_isize(a: isize) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_isize() -> isize { isize::MAX ^ isize::MIN ^ (isize::BITS as isize) }
pub fn cast_isize_i8(a: isize) -> i8 { a as i8 }
pub fn cast_isize_i16(a: isize) -> i16 { a as i16 }
pub fn cast_isize_i32(a: isize) -> i32 { a as i32 }
pub fn cast_isize_i64(a: isize) -> i64 { a as i64 }
pub fn cast_isize_u8(a: isize) -> u8 { a as u8 }
pub fn cast_isize_u16(a: isize) -> u16 { a as u16 }
pub fn cast_isize_u32(a: isize) -> u32 { a as u32 }
pub fn cast_isize_u64(a: isize) -> u64 { a as u64 }
pub fn cast_isize_usize(a: isize) -> usize { a as usize }
pub fn cast_isize_f32(a: isize) -> f32 { a as f32 }
pub fn cast_isize_f64(a: isize) -> f64 { a as f64 }

// ---- u8
pub fn add_u8(a: u8, b: u8) -> u8 { a + b }
pub fn sub_u8(a: u8, b: u8) -> u8 { a - b }
pub fn mul_u8(a: u8, b: u8) -> u8 { a * b }
pub fn div_u8(a: u8, b: u8) -> u8 { a / b }
pub fn rem_u8(a: u8, b: u8) -> u8 { a % b }
pub fn and_u8(a: u8, b: u8) -> u8 { a & b }
pub fn or_u8(a: u8, b: u8) -> u8 { a | b }
pub fn xor_u8(a: u8, b: u8) -> u8 { a ^ b }
pub fn shl_u8(a: u8, n: u32) -> u8 { a << n }
pub fn shr_u8(a: u8, n: u32) -> u8 { a >> n }
pub fn not_u8(a: u8) -> u8 { !a }
pub fn lt_u8(a: u8, b: u8) -> bool { a < b }
pub fn ge_u8(a: u8, b: u8) -> bool { a >= b }
pub fn compound_u8(a: u8, b: u8) -> u8 { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_u8(a: u8, b: u8) -> u8 { a.wrapping_add(b) }
pub fn checked_add_or_u8(a: u8, b: u8) -> u8 { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_u8(a: u8, b: u8) -> u8 { a.checked_add(b).unwrap() }
pub fn checked_add_some_u8(a: u8, b: u8) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_u8(a: u8, b: u8) -> u8 { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_u8(a: u8, b: u8) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_u8(a: u8, b: u8) -> u8 { a.wrapping_sub(b) }
pub fn checked_sub_or_u8(a: u8, b: u8) -> u8 { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_u8(a: u8, b: u8) -> u8 { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_u8(a: u8, b: u8) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_u8(a: u8, b: u8) -> u8 { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_u8(a: u8, b: u8) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_u8(a: u8, b: u8) -> u8 { a.wrapping_mul(b) }
pub fn checked_mul_or_u8(a: u8, b: u8) -> u8 { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_u8(a: u8, b: u8) -> u8 { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_u8(a: u8, b: u8) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_u8(a: u8, b: u8) -> u8 { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_u8(a: u8, b: u8) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_u8(a: u8, b: u8) -> u8 { a.wrapping_div(b) }
pub fn checked_div_or_u8(a: u8, b: u8) -> u8 { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_u8(a: u8, b: u8) -> u8 { a.checked_div(b).unwrap() }
pub fn checked_div_some_u8(a: u8, b: u8) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_u8(a: u8, b: u8) -> u8 { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_u8(a: u8, b: u8) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_u8(a: u8, b: u8) -> u8 { a.wrapping_rem(b) }
pub fn checked_rem_or_u8(a: u8, b: u8) -> u8 { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_u8(a: u8, b: u8) -> u8 { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_u8(a: u8, b: u8) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_u8(a: u8, b: u8) -> u8 { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_u8(a: u8, b: u8) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_u8(a: u8) -> u8 { a.wrapping_neg() }
pub fn checked_neg_or_u8(a: u8) -> u8 { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_u8(a: u8) -> u8 { a.checked_neg().unwrap() }
pub fn checked_neg_some_u8(a: u8) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_u8(a: u8) -> u8 { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_u8(a: u8) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_u8(a: u8, b: u32) -> u8 { a.wrapping_shl(b) }
pub fn checked_shl_or_u8(a: u8, b: u32) -> u8 { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_u8(a: u8, b: u32) -> u8 { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_u8(a: u8, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_u8(a: u8, b: u32) -> u8 { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_u8(a: u8, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_u8(a: u8, b: u32) -> u8 { a.wrapping_shr(b) }
pub fn checked_shr_or_u8(a: u8, b: u32) -> u8 { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_u8(a: u8, b: u32) -> u8 { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_u8(a: u8, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_u8(a: u8, b: u32) -> u8 { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_u8(a: u8, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_u8(a: u8, b: u32) -> u8 { a.wrapping_pow(b) }
pub fn checked_pow_or_u8(a: u8, b: u32) -> u8 { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_u8(a: u8, b: u32) -> u8 { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_u8(a: u8, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_u8(a: u8, b: u32) -> u8 { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_u8(a: u8, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn saturating_add_u8(a: u8, b: u8) -> u8 { a.saturating_add(b) }
pub fn saturating_sub_u8(a: u8, b: u8) -> u8 { a.saturating_sub(b) }
pub fn saturating_mul_u8(a: u8, b: u8) -> u8 { a.saturating_mul(b) }
pub fn saturating_div_u8(a: u8, b: u8) -> u8 { a.saturating_div(b) }
pub fn saturating_pow_u8(a: u8, b: u32) -> u8 { a.saturating_pow(b) }
pub fn pow_u8(a: u8, b: u32) -> u8 { a.pow(b) }
pub fn abs_diff_u8(a: u8, b: u8) -> u8 { a.abs_diff(b) }
pub fn min_max_u8(a: u8, b: u8) -> u8 { a.min(b) ^ a.max(b) }
pub fn clamp_u8(a: u8, b: u8) -> u8 { a.clamp(b, 5) }
pub fn div_euclid_u8(a: u8, b: u8) -> u8 { a.div_euclid(b) }
pub fn rem_euclid_u8(a: u8, b: u8) -> u8 { a.rem_euclid(b) }
pub fn rotate_left_u8(a: u8, b: u32) -> u8 { a.rotate_left(b) }
pub fn rotate_right_u8(a: u8, b: u32) -> u8 { a.rotate_right(b) }
pub fn swap_bytes_u8(a: u8) -> u8 { a.swap_bytes() }
pub fn reverse_bits_u8(a: u8) -> u8 { a.reverse_bits() }
pub fn counts_u8(a: u8) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_u8() -> u8 { u8::MAX ^ u8::MIN ^ (u8::BITS as u8) }
pub fn cast_u8_i8(a: u8) -> i8 { a as i8 }
pub fn cast_u8_i16(a: u8) -> i16 { a as i16 }
pub fn cast_u8_i32(a: u8) -> i32 { a as i32 }
pub fn cast_u8_i64(a: u8) -> i64 { a as i64 }
pub fn cast_u8_isize(a: u8) -> isize { a as isize }
pub fn cast_u8_u16(a: u8) -> u16 { a as u16 }
pub fn cast_u8_u32(a: u8) -> u32 { a as u32 }
pub fn cast_u8_u64(a: u8) -> u64 { a as u64 }
pub fn cast_u8_usize(a: u8) -> usize { a as usize }
pub fn cast_u8_f32(a: u8) -> f32 { a as f32 }
pub fn cast_u8_f64(a: u8) -> f64 { a as f64 }

// ---- u16
pub fn add_u16(a: u16, b: u16) -> u16 { a + b }
pub fn sub_u16(a: u16, b: u16) -> u16 { a - b }
pub fn mul_u16(a: u16, b: u16) -> u16 { a * b }
pub fn div_u16(a: u16, b: u16) -> u16 { a / b }
pub fn rem_u16(a: u16, b: u16) -> u16 { a % b }
pub fn and_u16(a: u16, b: u16) -> u16 { a & b }
pub fn or_u16(a: u16, b: u16) -> u16 { a | b }
pub fn xor_u16(a: u16, b: u16) -> u16 { a ^ b }
pub fn shl_u16(a: u16, n: u32) -> u16 { a << n }
pub fn shr_u16(a: u16, n: u32) -> u16 { a >> n }
pub fn not_u16(a: u16) -> u16 { !a }
pub fn lt_u16(a: u16, b: u16) -> bool { a < b }
pub fn ge_u16(a: u16, b: u16) -> bool { a >= b }
pub fn compound_u16(a: u16, b: u16) -> u16 { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_u16(a: u16, b: u16) -> u16 { a.wrapping_add(b) }
pub fn checked_add_or_u16(a: u16, b: u16) -> u16 { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_u16(a: u16, b: u16) -> u16 { a.checked_add(b).unwrap() }
pub fn checked_add_some_u16(a: u16, b: u16) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_u16(a: u16, b: u16) -> u16 { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_u16(a: u16, b: u16) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_u16(a: u16, b: u16) -> u16 { a.wrapping_sub(b) }
pub fn checked_sub_or_u16(a: u16, b: u16) -> u16 { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_u16(a: u16, b: u16) -> u16 { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_u16(a: u16, b: u16) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_u16(a: u16, b: u16) -> u16 { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_u16(a: u16, b: u16) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_u16(a: u16, b: u16) -> u16 { a.wrapping_mul(b) }
pub fn checked_mul_or_u16(a: u16, b: u16) -> u16 { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_u16(a: u16, b: u16) -> u16 { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_u16(a: u16, b: u16) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_u16(a: u16, b: u16) -> u16 { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_u16(a: u16, b: u16) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_u16(a: u16, b: u16) -> u16 { a.wrapping_div(b) }
pub fn checked_div_or_u16(a: u16, b: u16) -> u16 { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_u16(a: u16, b: u16) -> u16 { a.checked_div(b).unwrap() }
pub fn checked_div_some_u16(a: u16, b: u16) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_u16(a: u16, b: u16) -> u16 { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_u16(a: u16, b: u16) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_u16(a: u16, b: u16) -> u16 { a.wrapping_rem(b) }
pub fn checked_rem_or_u16(a: u16, b: u16) -> u16 { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_u16(a: u16, b: u16) -> u16 { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_u16(a: u16, b: u16) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_u16(a: u16, b: u16) -> u16 { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_u16(a: u16, b: u16) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_u16(a: u16) -> u16 { a.wrapping_neg() }
pub fn checked_neg_or_u16(a: u16) -> u16 { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_u16(a: u16) -> u16 { a.checked_neg().unwrap() }
pub fn checked_neg_some_u16(a: u16) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_u16(a: u16) -> u16 { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_u16(a: u16) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_u16(a: u16, b: u32) -> u16 { a.wrapping_shl(b) }
pub fn checked_shl_or_u16(a: u16, b: u32) -> u16 { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_u16(a: u16, b: u32) -> u16 { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_u16(a: u16, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_u16(a: u16, b: u32) -> u16 { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_u16(a: u16, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_u16(a: u16, b: u32) -> u16 { a.wrapping_shr(b) }
pub fn checked_shr_or_u16(a: u16, b: u32) -> u16 { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_u16(a: u16, b: u32) -> u16 { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_u16(a: u16, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_u16(a: u16, b: u32) -> u16 { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_u16(a: u16, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_u16(a: u16, b: u32) -> u16 { a.wrapping_pow(b) }
pub fn checked_pow_or_u16(a: u16, b: u32) -> u16 { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_u16(a: u16, b: u32) -> u16 { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_u16(a: u16, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_u16(a: u16, b: u32) -> u16 { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_u16(a: u16, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn saturating_add_u16(a: u16, b: u16) -> u16 { a.saturating_add(b) }
pub fn saturating_sub_u16(a: u16, b: u16) -> u16 { a.saturating_sub(b) }
pub fn saturating_mul_u16(a: u16, b: u16) -> u16 { a.saturating_mul(b) }
pub fn saturating_div_u16(a: u16, b: u16) -> u16 { a.saturating_div(b) }
pub fn saturating_pow_u16(a: u16, b: u32) -> u16 { a.saturating_pow(b) }
pub fn pow_u16(a: u16, b: u32) -> u16 { a.pow(b) }
pub fn abs_diff_u16(a: u16, b: u16) -> u16 { a.abs_diff(b) }
pub fn min_max_u16(a: u16, b: u16) -> u16 { a.min(b) ^ a.max(b) }
pub fn clamp_u16(a: u16, b: u16) -> u16 { a.clamp(b, 5) }
pub fn div_euclid_u16(a: u16, b: u16) -> u16 { a.div_euclid(b) }
pub fn rem_euclid_u16(a: u16, b: u16) -> u16 { a.rem_euclid(b) }
pub fn rotate_left_u16(a: u16, b: u32) -> u16 { a.rotate_left(b) }
pub fn rotate_right_u16(a: u16, b: u32) -> u16 { a.rotate_right(b) }
pub fn swap_bytes_u16(a: u16) -> u16 { a.swap_bytes() }
pub fn reverse_bits_u16(a: u16) -> u16 { a.reverse_bits() }
pub fn counts_u16(a: u16) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_u16() -> u16 { u16::MAX ^ u16::MIN ^ (u16::BITS as u16) }
pub fn cast_u16_i8(a: u16) -> i8 { a as i8 }
pub fn cast_u16_i16(a: u16) -> i16 { a as i16 }
pub fn cast_u16_i32(a: u16) -> i32 { a as i32 }
pub fn cast_u16_i64(a: u16) -> i64 { a as i64 }
pub fn cast_u16_isize(a: u16) -> isize { a as isize }
pub fn cast_u16_u8(a: u16) -> u8 { a as u8 }
pub fn cast_u16_u32(a: u16) -> u32 { a as u32 }
pub fn cast_u16_u64(a: u16) -> u64 { a as u64 }
pub fn cast_u16_usize(a: u16) -> usize { a as usize }
pub fn cast_u16_f32(a: u16) -> f32 { a as f32 }
pub fn cast_u16_f64(a: u16) -> f64 { a as f64 }

// ---- u32
pub fn add_u32(a: u32, b: u32) -> u32 { a + b }
pub fn sub_u32(a: u32, b: u32) -> u32 { a - b }
pub fn mul_u32(a: u32, b: u32) -> u32 { a * b }
pub fn div_u32(a: u32, b: u32) -> u32 { a / b }
pub fn rem_u32(a: u32, b: u32) -> u32 { a % b }
pub fn and_u32(a: u32, b: u32) -> u32 { a & b }
pub fn or_u32(a: u32, b: u32) -> u32 { a | b }
pub fn xor_u32(a: u32, b: u32) -> u32 { a ^ b }
pub fn shl_u32(a: u32, n: u32) -> u32 { a << n }
pub fn shr_u32(a: u32, n: u32) -> u32 { a >> n }
pub fn not_u32(a: u32) -> u32 { !a }
pub fn lt_u32(a: u32, b: u32) -> bool { a < b }
pub fn ge_u32(a: u32, b: u32) -> bool { a >= b }
pub fn compound_u32(a: u32, b: u32) -> u32 { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_u32(a: u32, b: u32) -> u32 { a.wrapping_add(b) }
pub fn checked_add_or_u32(a: u32, b: u32) -> u32 { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_u32(a: u32, b: u32) -> u32 { a.checked_add(b).unwrap() }
pub fn checked_add_some_u32(a: u32, b: u32) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_u32(a: u32, b: u32) -> u32 { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_u32(a: u32, b: u32) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_u32(a: u32, b: u32) -> u32 { a.wrapping_sub(b) }
pub fn checked_sub_or_u32(a: u32, b: u32) -> u32 { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_u32(a: u32, b: u32) -> u32 { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_u32(a: u32, b: u32) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_u32(a: u32, b: u32) -> u32 { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_u32(a: u32, b: u32) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_u32(a: u32, b: u32) -> u32 { a.wrapping_mul(b) }
pub fn checked_mul_or_u32(a: u32, b: u32) -> u32 { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_u32(a: u32, b: u32) -> u32 { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_u32(a: u32, b: u32) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_u32(a: u32, b: u32) -> u32 { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_u32(a: u32, b: u32) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_u32(a: u32, b: u32) -> u32 { a.wrapping_div(b) }
pub fn checked_div_or_u32(a: u32, b: u32) -> u32 { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_u32(a: u32, b: u32) -> u32 { a.checked_div(b).unwrap() }
pub fn checked_div_some_u32(a: u32, b: u32) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_u32(a: u32, b: u32) -> u32 { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_u32(a: u32, b: u32) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_u32(a: u32, b: u32) -> u32 { a.wrapping_rem(b) }
pub fn checked_rem_or_u32(a: u32, b: u32) -> u32 { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_u32(a: u32, b: u32) -> u32 { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_u32(a: u32, b: u32) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_u32(a: u32, b: u32) -> u32 { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_u32(a: u32, b: u32) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_u32(a: u32) -> u32 { a.wrapping_neg() }
pub fn checked_neg_or_u32(a: u32) -> u32 { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_u32(a: u32) -> u32 { a.checked_neg().unwrap() }
pub fn checked_neg_some_u32(a: u32) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_u32(a: u32) -> u32 { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_u32(a: u32) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_u32(a: u32, b: u32) -> u32 { a.wrapping_shl(b) }
pub fn checked_shl_or_u32(a: u32, b: u32) -> u32 { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_u32(a: u32, b: u32) -> u32 { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_u32(a: u32, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_u32(a: u32, b: u32) -> u32 { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_u32(a: u32, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_u32(a: u32, b: u32) -> u32 { a.wrapping_shr(b) }
pub fn checked_shr_or_u32(a: u32, b: u32) -> u32 { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_u32(a: u32, b: u32) -> u32 { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_u32(a: u32, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_u32(a: u32, b: u32) -> u32 { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_u32(a: u32, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_u32(a: u32, b: u32) -> u32 { a.wrapping_pow(b) }
pub fn checked_pow_or_u32(a: u32, b: u32) -> u32 { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_u32(a: u32, b: u32) -> u32 { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_u32(a: u32, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_u32(a: u32, b: u32) -> u32 { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_u32(a: u32, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn saturating_add_u32(a: u32, b: u32) -> u32 { a.saturating_add(b) }
pub fn saturating_sub_u32(a: u32, b: u32) -> u32 { a.saturating_sub(b) }
pub fn saturating_mul_u32(a: u32, b: u32) -> u32 { a.saturating_mul(b) }
pub fn saturating_div_u32(a: u32, b: u32) -> u32 { a.saturating_div(b) }
pub fn saturating_pow_u32(a: u32, b: u32) -> u32 { a.saturating_pow(b) }
pub fn pow_u32(a: u32, b: u32) -> u32 { a.pow(b) }
pub fn abs_diff_u32(a: u32, b: u32) -> u32 { a.abs_diff(b) }
pub fn min_max_u32(a: u32, b: u32) -> u32 { a.min(b) ^ a.max(b) }
pub fn clamp_u32(a: u32, b: u32) -> u32 { a.clamp(b, 5) }
pub fn div_euclid_u32(a: u32, b: u32) -> u32 { a.div_euclid(b) }
pub fn rem_euclid_u32(a: u32, b: u32) -> u32 { a.rem_euclid(b) }
pub fn rotate_left_u32(a: u32, b: u32) -> u32 { a.rotate_left(b) }
pub fn rotate_right_u32(a: u32, b: u32) -> u32 { a.rotate_right(b) }
pub fn swap_bytes_u32(a: u32) -> u32 { a.swap_bytes() }
pub fn reverse_bits_u32(a: u32) -> u32 { a.reverse_bits() }
pub fn counts_u32(a: u32) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_u32() -> u32 { u32::MAX ^ u32::MIN ^ (u32::BITS as u32) }
pub fn cast_u32_i8(a: u32) -> i8 { a as i8 }
pub fn cast_u32_i16(a: u32) -> i16 { a as i16 }
pub fn cast_u32_i32(a: u32) -> i32 { a as i32 }
pub fn cast_u32_i64(a: u32) -> i64 { a as i64 }
pub fn cast_u32_isize(a: u32) -> isize { a as isize }
pub fn cast_u32_u8(a: u32) -> u8 { a as u8 }
pub fn cast_u32_u16(a: u32) -> u16 { a as u16 }
pub fn cast_u32_u64(a: u32) -> u64 { a as u64 }
pub fn cast_u32_usize(a: u32) -> usize { a as usize }
pub fn cast_u32_f32(a: u32) -> f32 { a as f32 }
pub fn cast_u32_f64(a: u32) -> f64 { a as f64 }

// ---- u64
pub fn add_u64(a: u64, b: u64) -> u64 { a + b }
pub fn sub_u64(a: u64, b: u64) -> u64 { a - b }
pub fn mul_u64(a: u64, b: u64) -> u64 { a * b }
pub fn div_u64(a: u64, b: u64) -> u64 { a / b }
pub fn rem_u64(a: u64, b: u64) -> u64 { a % b }
pub fn and_u64(a: u64, b: u64) -> u64 { a & b }
pub fn or_u64(a: u64, b: u64) -> u64 { a | b }
pub fn xor_u64(a: u64, b: u64) -> u64 { a ^ b }
pub fn shl_u64(a: u64, n: u32) -> u64 { a << n }
pub fn shr_u64(a: u64, n: u32) -> u64 { a >> n }
pub fn not_u64(a: u64) -> u64 { !a }
pub fn lt_u64(a: u64, b: u64) -> bool { a < b }
pub fn ge_u64(a: u64, b: u64) -> bool { a >= b }
pub fn compound_u64(a: u64, b: u64) -> u64 { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_u64(a: u64, b: u64) -> u64 { a.wrapping_add(b) }
pub fn checked_add_or_u64(a: u64, b: u64) -> u64 { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_u64(a: u64, b: u64) -> u64 { a.checked_add(b).unwrap() }
pub fn checked_add_some_u64(a: u64, b: u64) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_u64(a: u64, b: u64) -> u64 { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_u64(a: u64, b: u64) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_u64(a: u64, b: u64) -> u64 { a.wrapping_sub(b) }
pub fn checked_sub_or_u64(a: u64, b: u64) -> u64 { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_u64(a: u64, b: u64) -> u64 { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_u64(a: u64, b: u64) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_u64(a: u64, b: u64) -> u64 { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_u64(a: u64, b: u64) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_u64(a: u64, b: u64) -> u64 { a.wrapping_mul(b) }
pub fn checked_mul_or_u64(a: u64, b: u64) -> u64 { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_u64(a: u64, b: u64) -> u64 { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_u64(a: u64, b: u64) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_u64(a: u64, b: u64) -> u64 { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_u64(a: u64, b: u64) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_u64(a: u64, b: u64) -> u64 { a.wrapping_div(b) }
pub fn checked_div_or_u64(a: u64, b: u64) -> u64 { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_u64(a: u64, b: u64) -> u64 { a.checked_div(b).unwrap() }
pub fn checked_div_some_u64(a: u64, b: u64) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_u64(a: u64, b: u64) -> u64 { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_u64(a: u64, b: u64) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_u64(a: u64, b: u64) -> u64 { a.wrapping_rem(b) }
pub fn checked_rem_or_u64(a: u64, b: u64) -> u64 { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_u64(a: u64, b: u64) -> u64 { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_u64(a: u64, b: u64) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_u64(a: u64, b: u64) -> u64 { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_u64(a: u64, b: u64) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_u64(a: u64) -> u64 { a.wrapping_neg() }
pub fn checked_neg_or_u64(a: u64) -> u64 { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_u64(a: u64) -> u64 { a.checked_neg().unwrap() }
pub fn checked_neg_some_u64(a: u64) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_u64(a: u64) -> u64 { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_u64(a: u64) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_u64(a: u64, b: u32) -> u64 { a.wrapping_shl(b) }
pub fn checked_shl_or_u64(a: u64, b: u32) -> u64 { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_u64(a: u64, b: u32) -> u64 { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_u64(a: u64, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_u64(a: u64, b: u32) -> u64 { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_u64(a: u64, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_u64(a: u64, b: u32) -> u64 { a.wrapping_shr(b) }
pub fn checked_shr_or_u64(a: u64, b: u32) -> u64 { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_u64(a: u64, b: u32) -> u64 { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_u64(a: u64, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_u64(a: u64, b: u32) -> u64 { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_u64(a: u64, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_u64(a: u64, b: u32) -> u64 { a.wrapping_pow(b) }
pub fn checked_pow_or_u64(a: u64, b: u32) -> u64 { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_u64(a: u64, b: u32) -> u64 { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_u64(a: u64, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_u64(a: u64, b: u32) -> u64 { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_u64(a: u64, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn saturating_add_u64(a: u64, b: u64) -> u64 { a.saturating_add(b) }
pub fn saturating_sub_u64(a: u64, b: u64) -> u64 { a.saturating_sub(b) }
pub fn saturating_mul_u64(a: u64, b: u64) -> u64 { a.saturating_mul(b) }
pub fn saturating_div_u64(a: u64, b: u64) -> u64 { a.saturating_div(b) }
pub fn saturating_pow_u64(a: u64, b: u32) -> u64 { a.saturating_pow(b) }
pub fn pow_u64(a: u64, b: u32) -> u64 { a.pow(b) }
pub fn abs_diff_u64(a: u64, b: u64) -> u64 { a.abs_diff(b) }
pub fn min_max_u64(a: u64, b: u64) -> u64 { a.min(b) ^ a.max(b) }
pub fn clamp_u64(a: u64, b: u64) -> u64 { a.clamp(b, 5) }
pub fn div_euclid_u64(a: u64, b: u64) -> u64 { a.div_euclid(b) }
pub fn rem_euclid_u64(a: u64, b: u64) -> u64 { a.rem_euclid(b) }
pub fn rotate_left_u64(a: u64, b: u32) -> u64 { a.rotate_left(b) }
pub fn rotate_right_u64(a: u64, b: u32) -> u64 { a.rotate_right(b) }
pub fn swap_bytes_u64(a: u64) -> u64 { a.swap_bytes() }
pub fn reverse_bits_u64(a: u64) -> u64 { a.reverse_bits() }
pub fn counts_u64(a: u64) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_u64() -> u64 { u64::MAX ^ u64::MIN ^ (u64::BITS as u64) }
pub fn cast_u64_i8(a: u64) -> i8 { a as i8 }
pub fn cast_u64_i16(a: u64) -> i16 { a as i16 }
pub fn cast_u64_i32(a: u64) -> i32 { a as i32 }
pub fn cast_u64_i64(a: u64) -> i64 { a as i64 }
pub fn cast_u64_isize(a: u64) -> isize { a as isize }
pub fn cast_u64_u8(a: u64) -> u8 { a as u8 }
pub fn cast_u64_u16(a: u64) -> u16 { a as u16 }
pub fn cast_u64_u32(a: u64) -> u32 { a as u32 }
pub fn cast_u64_usize(a: u64) -> usize { a as usize }
pub fn cast_u64_f32(a: u64) -> f32 { a as f32 }
pub fn cast_u64_f64(a: u64) -> f64 { a as f64 }

// ---- usize
pub fn add_usize(a: usize, b: usize) -> usize { a + b }
pub fn sub_usize(a: usize, b: usize) -> usize { a - b }
pub fn mul_usize(a: usize, b: usize) -> usize { a * b }
pub fn div_usize(a: usize, b: usize) -> usize { a / b }
pub fn rem_usize(a: usize, b: usize) -> usize { a % b }
pub fn and_usize(a: usize, b: usize) -> usize { a & b }
pub fn or_usize(a: usize, b: usize) -> usize { a | b }
pub fn xor_usize(a: usize, b: usize) -> usize { a ^ b }
pub fn shl_usize(a: usize, n: u32) -> usize { a << n }
pub fn shr_usize(a: usize, n: u32) -> usize { a >> n }
pub fn not_usize(a: usize) -> usize { !a }
pub fn lt_usize(a: usize, b: usize) -> bool { a < b }
pub fn ge_usize(a: usize, b: usize) -> bool { a >= b }
pub fn compound_usize(a: usize, b: usize) -> usize { let mut x = a; x += b; x *= 3; x -= 1; x ^= b; x }
pub fn wrapping_add_usize(a: usize, b: usize) -> usize { a.wrapping_add(b) }
pub fn checked_add_or_usize(a: usize, b: usize) -> usize { a.checked_add(b).unwrap_or(7) }
pub fn checked_add_unwrap_usize(a: usize, b: usize) -> usize { a.checked_add(b).unwrap() }
pub fn checked_add_some_usize(a: usize, b: usize) -> bool { a.checked_add(b).is_some() }
pub fn overflowing_add_usize(a: usize, b: usize) -> usize { let (v, o) = a.overflowing_add(b); if o { v ^ 1 } else { v } }
pub fn overflowing_add_flag_usize(a: usize, b: usize) -> bool { a.overflowing_add(b).1 }
pub fn wrapping_sub_usize(a: usize, b: usize) -> usize { a.wrapping_sub(b) }
pub fn checked_sub_or_usize(a: usize, b: usize) -> usize { a.checked_sub(b).unwrap_or(7) }
pub fn checked_sub_unwrap_usize(a: usize, b: usize) -> usize { a.checked_sub(b).unwrap() }
pub fn checked_sub_some_usize(a: usize, b: usize) -> bool { a.checked_sub(b).is_some() }
pub fn overflowing_sub_usize(a: usize, b: usize) -> usize { let (v, o) = a.overflowing_sub(b); if o { v ^ 1 } else { v } }
pub fn overflowing_sub_flag_usize(a: usize, b: usize) -> bool { a.overflowing_sub(b).1 }
pub fn wrapping_mul_usize(a: usize, b: usize) -> usize { a.wrapping_mul(b) }
pub fn checked_mul_or_usize(a: usize, b: usize) -> usize { a.checked_mul(b).unwrap_or(7) }
pub fn checked_mul_unwrap_usize(a: usize, b: usize) -> usize { a.checked_mul(b).unwrap() }
pub fn checked_mul_some_usize(a: usize, b: usize) -> bool { a.checked_mul(b).is_some() }
pub fn overflowing_mul_usize(a: usize, b: usize) -> usize { let (v, o) = a.overflowing_mul(b); if o { v ^ 1 } else { v } }
pub fn overflowing_mul_flag_usize(a: usize, b: usize) -> bool { a.overflowing_mul(b).1 }
pub fn wrapping_div_usize(a: usize, b: usize) -> usize { a.wrapping_div(b) }
pub fn checked_div_or_usize(a: usize, b: usize) -> usize { a.checked_div(b).unwrap_or(7) }
pub fn checked_div_unwrap_usize(a: usize, b: usize) -> usize { a.checked_div(b).unwrap() }
pub fn checked_div_some_usize(a: usize, b: usize) -> bool { a.checked_div(b).is_some() }
pub fn overflowing_div_usize(a: usize, b: usize) -> usize { let (v, o) = a.overflowing_div(b); if o { v ^ 1 } else { v } }
pub fn overflowing_div_flag_usize(a: usize, b: usize) -> bool { a.overflowing_div(b).1 }
pub fn wrapping_rem_usize(a: usize, b: usize) -> usize { a.wrapping_rem(b) }
pub fn checked_rem_or_usize(a: usize, b: usize) -> usize { a.checked_rem(b).unwrap_or(7) }
pub fn checked_rem_unwrap_usize(a: usize, b: usize) -> usize { a.checked_rem(b).unwrap() }
pub fn checked_rem_some_usize(a: usize, b: usize) -> bool { a.checked_rem(b).is_some() }
pub fn overflowing_rem_usize(a: usize, b: usize) -> usize { let (v, o) = a.overflowing_rem(b); if o { v ^ 1 } else { v } }
pub fn overflowing_rem_flag_usize(a: usize, b: usize) -> bool { a.overflowing_rem(b).1 }
pub fn wrapping_neg_usize(a: usize) -> usize { a.wrapping_neg() }
pub fn checked_neg_or_usize(a: usize) -> usize { a.checked_neg().unwrap_or(7) }
pub fn checked_neg_unwrap_usize(a: usize) -> usize { a.checked_neg().unwrap() }
pub fn checked_neg_some_usize(a: usize) -> bool { a.checked_neg().is_some() }
pub fn overflowing_neg_usize(a: usize) -> usize { let (v, o) = a.overflowing_neg(); if o { v ^ 1 } else { v } }
pub fn overflowing_neg_flag_usize(a: usize) -> bool { a.overflowing_neg().1 }
pub fn wrapping_shl_usize(a: usize, b: u32) -> usize { a.wrapping_shl(b) }
pub fn checked_shl_or_usize(a: usize, b: u32) -> usize { a.checked_shl(b).unwrap_or(7) }
pub fn checked_shl_unwrap_usize(a: usize, b: u32) -> usize { a.checked_shl(b).unwrap() }
pub fn checked_shl_some_usize(a: usize, b: u32) -> bool { a.checked_shl(b).is_some() }
pub fn overflowing_shl_usize(a: usize, b: u32) -> usize { let (v, o) = a.overflowing_shl(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shl_flag_usize(a: usize, b: u32) -> bool { a.overflowing_shl(b).1 }
pub fn wrapping_shr_usize(a: usize, b: u32) -> usize { a.wrapping_shr(b) }
pub fn checked_shr_or_usize(a: usize, b: u32) -> usize { a.checked_shr(b).unwrap_or(7) }
pub fn checked_shr_unwrap_usize(a: usize, b: u32) -> usize { a.checked_shr(b).unwrap() }
pub fn checked_shr_some_usize(a: usize, b: u32) -> bool { a.checked_shr(b).is_some() }
pub fn overflowing_shr_usize(a: usize, b: u32) -> usize { let (v, o) = a.overflowing_shr(b); if o { v ^ 1 } else { v } }
pub fn overflowing_shr_flag_usize(a: usize, b: u32) -> bool { a.overflowing_shr(b).1 }
pub fn wrapping_pow_usize(a: usize, b: u32) -> usize { a.wrapping_pow(b) }
pub fn checked_pow_or_usize(a: usize, b: u32) -> usize { a.checked_pow(b).unwrap_or(7) }
pub fn checked_pow_unwrap_usize(a: usize, b: u32) -> usize { a.checked_pow(b).unwrap() }
pub fn checked_pow_some_usize(a: usize, b: u32) -> bool { a.checked_pow(b).is_some() }
pub fn overflowing_pow_usize(a: usize, b: u32) -> usize { let (v, o) = a.overflowing_pow(b); if o { v ^ 1 } else { v } }
pub fn overflowing_pow_flag_usize(a: usize, b: u32) -> bool { a.overflowing_pow(b).1 }
pub fn saturating_add_usize(a: usize, b: usize) -> usize { a.saturating_add(b) }
pub fn saturating_sub_usize(a: usize, b: usize) -> usize { a.saturating_sub(b) }
pub fn saturating_mul_usize(a: usize, b: usize) -> usize { a.saturating_mul(b) }
pub fn saturating_div_usize(a: usize, b: usize) -> usize { a.saturating_div(b) }
pub fn saturating_pow_usize(a: usize, b: u32) -> usize { a.saturating_pow(b) }
pub fn pow_usize(a: usize, b: u32) -> usize { a.pow(b) }
pub fn abs_diff_usize(a: usize, b: usize) -> usize { a.abs_diff(b) }
pub fn min_max_usize(a: usize, b: usize) -> usize { a.min(b) ^ a.max(b) }
pub fn clamp_usize(a: usize, b: usize) -> usize { a.clamp(b, 5) }
pub fn div_euclid_usize(a: usize, b: usize) -> usize { a.div_euclid(b) }
pub fn rem_euclid_usize(a: usize, b: usize) -> usize { a.rem_euclid(b) }
pub fn rotate_left_usize(a: usize, b: u32) -> usize { a.rotate_left(b) }
pub fn rotate_right_usize(a: usize, b: u32) -> usize { a.rotate_right(b) }
pub fn swap_bytes_usize(a: usize) -> usize { a.swap_bytes() }
pub fn reverse_bits_usize(a: usize) -> usize { a.reverse_bits() }
pub fn counts_usize(a: usize) -> u32 { a.count_ones() * 1000000 + a.count_zeros() * 10000 + a.leading_zeros() * 100 + a.trailing_zeros() }
pub fn limits_usize() -> usize { usize::MAX ^ usize::MIN ^ (usize::BITS as usize) }
pub fn cast_usize_i8(a: usize) -> i8 { a as i8 }
pub fn cast_usize_i16(a: usize) -> i16 { a as i16 }
pub fn cast_usize_i32(a: usize) -> i32 { a as i32 }
pub fn cast_usize_i64(a: usize) -> i64 { a as i64 }
pub fn cast_usize_isize(a: usize) -> isize { a as isize }
pub fn cast_usize_u8(a: usize) -> u8 { a as u8 }
pub fn cast_usize_u16(a: usize) -> u16 { a as u16 }
pub fn cast_usize_u32(a: usize) -> u32 { a as u32 }
pub fn cast_usize_u64(a: usize) -> u64 { a as u64 }
pub fn cast_usize_f32(a: usize) -> f32 { a as f32 }
pub fn cast_usize_f64(a: usize) -> f64 { a as f64 }

// ---- f32
pub fn add_f32(a: f32, b: f32) -> f32 { a + b }
pub fn sub_f32(a: f32, b: f32) -> f32 { a - b }
pub fn mul_f32(a: f32, b: f32) -> f32 { a * b }
pub fn div_f32(a: f32, b: f32) -> f32 { a / b }
pub fn rem_f32(a: f32, b: f32) -> f32 { a % b }
pub fn neg_f32(a: f32) -> f32 { -a }
pub fn eq_f32(a: f32, b: f32) -> bool { a == b }
pub fn ne_f32(a: f32, b: f32) -> bool { a != b }
pub fn lt_f32(a: f32, b: f32) -> bool { a < b }
pub fn le_f32(a: f32, b: f32) -> bool { a <= b }
pub fn fused_f32(a: f32, b: f32) -> f32 { a * b + a }
pub fn abs_f32(a: f32) -> f32 { a.abs() }
pub fn sqrt_f32(a: f32) -> f32 { a.sqrt() }
pub fn to_bits_f32(a: f32) -> u32 { a.to_bits() }
pub fn from_bits_f32(a: u32) -> f32 { f32::from_bits(a) }
pub fn is_nan_f32(a: f32) -> bool { a.is_nan() }
pub fn limits_f32() -> f32 { f32::MAX + f32::MIN + f32::EPSILON + f32::MIN_POSITIVE }
pub fn cast_f32_i8(a: f32) -> i8 { a as i8 }
pub fn cast_f32_i16(a: f32) -> i16 { a as i16 }
pub fn cast_f32_i32(a: f32) -> i32 { a as i32 }
pub fn cast_f32_i64(a: f32) -> i64 { a as i64 }
pub fn cast_f32_isize(a: f32) -> isize { a as isize }
pub fn cast_f32_u8(a: f32) -> u8 { a as u8 }
pub fn cast_f32_u16(a: f32) -> u16 { a as u16 }
pub fn cast_f32_u32(a: f32) -> u32 { a as u32 }
pub fn cast_f32_u64(a: f32) -> u64 { a as u64 }
pub fn cast_f32_usize(a: f32) -> usize { a as usize }
pub fn cast_f32_f64(a: f32) -> f64 { a as f64 }

// ---- f64
pub fn add_f64(a: f64, b: f64) -> f64 { a + b }
pub fn sub_f64(a: f64, b: f64) -> f64 { a - b }
pub fn mul_f64(a: f64, b: f64) -> f64 { a * b }
pub fn div_f64(a: f64, b: f64) -> f64 { a / b }
pub fn rem_f64(a: f64, b: f64) -> f64 { a % b }
pub fn neg_f64(a: f64) -> f64 { -a }
pub fn eq_f64(a: f64, b: f64) -> bool { a == b }
pub fn ne_f64(a: f64, b: f64) -> bool { a != b }
pub fn lt_f64(a: f64, b: f64) -> bool { a < b }
pub fn le_f64(a: f64, b: f64) -> bool { a <= b }
pub fn fused_f64(a: f64, b: f64) -> f64 { a * b + a }
pub fn abs_f64(a: f64) -> f64 { a.abs() }
pub fn sqrt_f64(a: f64) -> f64 { a.sqrt() }
pub fn to_bits_f64(a: f64) -> u64 { a.to_bits() }
pub fn from_bits_f64(a: u64) -> f64 { f64::from_bits(a) }
pub fn is_nan_f64(a: f64) -> bool { a.is_nan() }
pub fn limits_f64() -> f64 { f64::MAX + f64::MIN + f64::EPSILON + f64::MIN_POSITIVE }
pub fn cast_f64_i8(a: f64) -> i8 { a as i8 }
pub fn cast_f64_i16(a: f64) -> i16 { a as i16 }
pub fn cast_f64_i32(a: f64) -> i32 { a as i32 }
pub fn cast_f64_i64(a: f64) -> i64 { a as i64 }
pub fn cast_f64_isize(a: f64) -> isize { a as isize }
pub fn cast_f64_u8(a: f64) -> u8 { a as u8 }
pub fn cast_f64_u16(a: f64) -> u16 { a as u16 }
pub fn cast_f64_u32(a: f64) -> u32 { a as u32 }
pub fn cast_f64_u64(a: f64) -> u64 { a as u64 }
pub fn cast_f64_usize(a: f64) -> usize { a as usize }
pub fn cast_f64_f32(a: f64) -> f32 { a as f32 }

// ---- bool
pub fn bool_ops(a: bool, b: bool) -> u8 { ((a & b) as u8) | (((a | b) as u8) << 1) | (((a ^ b) as u8) << 2) | ((!a as u8) << 3) | (((a < b) as u8) << 4) }
pub fn bool_to_i64(a: bool) -> i64 { a as i64 }
pub fn short_circuit(a: u32, b: u32) -> bool { b != 0 && a / b > 1 || a == 7 }

// ---- literals and inference
pub fn literals() -> u64 { 0xFF_u64 + 0o17 + 0b1010_1010 + 1_000_000 + b'A' as u64 }
pub fn float_literals() -> f64 { 1.5e3 + 2. + 0.1 + 1e-2 + 3f64 }
pub fn single_literal() -> f32 { 0.1 + 16777217.0 }
pub fn inferred(a: u16) -> u16 { let mut s = 0; let k = 3; s += a; s * k }
pub fn shadowing(a: i32) -> i64 { let a = a as i64; let a = a * a; a }
pub fn negative_literal() -> i8 { -128 }
pub fn local_const(a: u32) -> u32 { const K: u32 = 0x9E37_79B9; a.wrapping_mul(K) }
pub fn block_value(a: i32) -> i32 { let b = { let c = a * 2; c + 1 }; b }
pub fn fnv1a_step(h: u32, b: u32) -> u32 { (h ^ b).wrapping_mul(16777619) }
pub fn expect_add(a: u8, b: u8) -> u8 { a.checked_add(b).expect("boom") }
pub fn order(a: i32, b: i32, c: i32, d: i32) -> i32 { (a / b) + (c / d) }
pub fn shift_signed_amount(a: u32, n: i32) -> u32 { a << n }
pub fn shift_byte_amount(a: i64, n: u8) -> i64 { a >> n }
pub fn sign_extension(a: u8) -> u32 { a as i8 as i32 as u32 }
pub fn compound_shift(a: u16, n: u64) -> u16 { let mut x = a; x <<= n; x >>= 1; x }
