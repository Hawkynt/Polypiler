// Text: String, &str and char over the IR's strings of UTF-16 code units, with Rust's UTF-8 byte
// lengths and byte offsets, the char-boundary panic of a byte range, and the text of numbers.
// Text results that may be empty are written in brackets: the recording has one line per call.

pub fn utf8_len(s: &str) -> usize {
  s.len()
}

pub fn char_count(s: &str) -> usize {
  s.chars().count()
}

pub fn byte_at(s: &str, i: usize) -> u8 {
  s.as_bytes()[i]
}

pub fn byte_sum(s: &str) -> u32 {
  s.bytes().map(|b| b as u32).sum()
}

pub fn code_point_sum(s: &str) -> u32 {
  s.chars().map(|c| c as u32).sum()
}

pub fn sliced(s: &str, from: usize, to: usize) -> String {
  format!("[{}]", &s[from..to])
}

pub fn tail_chars(s: &str, from: usize) -> usize {
  s[from..].chars().count()
}

pub fn reversed(s: &str) -> String {
  let mut r = String::new();
  for c in s.chars().rev() {
    r.push(c);
  }
  "[".to_string() + &r + "]"
}

pub fn count_char(s: &str, code: u32) -> usize {
  match char::from_u32(code) {
    Some(wanted) => s.chars().filter(|&c| c == wanted).count(),
    None => 0,
  }
}

pub fn shout(s: &str) -> String {
  s.to_ascii_uppercase() + "!"
}

pub fn joined(a: &str, b: &str) -> String {
  let mut s = String::from(a);
  s.push_str(", ");
  s += b;
  s.push('.');
  s
}

pub fn compare(a: &str, b: &str) -> i32 {
  if a < b {
    -1
  } else if a > b {
    1
  } else {
    0
  }
}

pub fn same(a: &str, b: &str) -> bool {
  a == b && a.to_string() == b
}

pub fn find_at(haystack: &str, needle: &str) -> i64 {
  match haystack.find(needle) {
    Some(at) => at as i64,
    None => -1,
  }
}

pub fn affixes(s: &str, part: &str) -> u8 {
  (s.starts_with(part) as u8) * 4 + (s.ends_with(part) as u8) * 2 + s.contains(part) as u8
}

pub fn numbers_text(x: i32, y: u8) -> String {
  format!("{}+{:x}={:>6}|{:<4}|{:08b}|{:X}|{{}}", x, y, x as i64 + y as i64, y, y, x)
}

pub fn named_text(width: u64) -> String {
  let half = width / 2;
  format!("{width}/{half:03}:{:+}", -(half as i64))
}

pub fn parse_u8(s: &str) -> i32 {
  match s.parse::<u8>() {
    Ok(v) => v as i32,
    Err(_) => -1,
  }
}

pub fn parse_i64(s: &str) -> i64 {
  s.parse::<i64>().unwrap_or(i64::MIN)
}

pub fn parse_sum(a: &str, b: &str) -> u32 {
  a.parse::<u32>().unwrap() + b.parse::<u32>().unwrap()
}

pub fn digit_sum(n: u64) -> u32 {
  n.to_string().chars().map(|c| c.to_digit(10).unwrap()).sum()
}

pub fn described(x: i64, flag: bool, code: u32) -> String {
  let c = match char::from_u32(code) {
    Some(c) => c,
    None => '?',
  };
  x.to_string() + " " + &flag.to_string() + " " + &c.to_string()
}

pub fn classify(code: u32) -> u8 {
  match char::from_u32(code) {
    Some('a'..='z') => 1,
    Some('A'..='Z') => 2,
    Some(c) if c.is_ascii_digit() => 3,
    Some(c) if c.is_ascii_whitespace() => 4,
    Some(_) => 5,
    None => 0,
  }
}

pub fn char_width(code: u32) -> usize {
  match char::from_u32(code) {
    Some(c) => c.len_utf8(),
    None => 0,
  }
}

pub fn keyword(s: &str) -> u8 {
  match s {
    "fn" => 1,
    "let" | "const" => 2,
    _ => 0,
  }
}

pub fn trimmed(s: &str) -> String {
  format!("[{}|{}|{}]", s.trim(), s.trim_start().len(), s.trim_end().len())
}

pub fn repeated(s: &str, n: usize) -> String {
  format!("[{:^7}]", s.repeat(n))
}

pub fn field_count(s: &str) -> usize {
  s.split(',').count()
}

pub fn longest_field(s: &str) -> usize {
  let mut best = 0;
  for field in s.split(", ") {
    if field.len() > best {
      best = field.len();
    }
  }
  best
}

pub fn is_empty_after_clear(s: &str) -> bool {
  let mut owned = s.to_owned();
  let before = owned.is_empty();
  owned.clear();
  !before && owned.is_empty()
}
