// Evaluates the ADR 0005 input set with the Rust standard library.
use std::io::{self, BufRead, Write};

fn main() {
  let stdin = io::stdin();
  let stdout = io::stdout();
  let mut out = io::BufWriter::new(stdout.lock());
  for line in stdin.lock().lines() {
    let line = line.unwrap();
    let parts: Vec<&str> = line.split(' ').collect();
    let v: Vec<f64> = parts[2..].iter().map(|h| f64::from_bits(u64::from_str_radix(h, 16).unwrap())).collect();
    let x = std::hint::black_box(v[0]);
    let y = std::hint::black_box(if v.len() > 1 { v[1] } else { 0.0 });
    let z = std::hint::black_box(if v.len() > 2 { v[2] } else { 0.0 });
    let r = match parts[0] {
      "sin" => x.sin(),
      "cos" => x.cos(),
      "tan" => x.tan(),
      "exp" => x.exp(),
      "log" => x.ln(),
      "log2" => x.log2(),
      "log10" => x.log10(),
      "pow" => x.powf(y),
      "atan2" => x.atan2(y),
      "sinh" => x.sinh(),
      "cosh" => x.cosh(),
      "tanh" => x.tanh(),
      "cbrt" => x.cbrt(),
      "hypot" => x.hypot(y),
      "add" => x + y,
      "sub" => x - y,
      "mul" => x * y,
      "div" => x / y,
      "sqrt" => x.sqrt(),
      "fma" => x.mul_add(y, z),
      _ => panic!("unknown function"),
    };
    writeln!(out, "{:016x}", r.to_bits()).unwrap();
  }
}
