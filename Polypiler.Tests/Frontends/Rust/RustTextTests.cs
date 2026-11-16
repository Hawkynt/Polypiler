using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// Text beside the <c>strings</c> fixture: chars compared, cast and matched, <c>String</c> in structs,
/// vectors and options, deref coercion, <c>&amp;mut String</c> copied in and out, the format specs one
/// by one, and the forms refused - each value the one rustc 1.99 printed for the same source.
/// </summary>
[TestFixture]
public sealed class RustTextTests {

  private static ImportResult Import(string source) => RustFrontend.Import("lib.rs", source, OverflowChecks.Panic);

  [TestCase("pub fn f() -> u32 { let c = 'x'; if c > 'a' && c != 'y' { c as u32 } else { 0 } }", "120")]
  [TestCase("pub fn f() -> u8 { let c = 200u8 as char; (c as u32 - 100) as u8 }", "100")]
  [TestCase("pub fn f() -> usize { let v: Vec<String> = vec![\"a\".to_string(), \"bc\".to_string()]; let mut n = 0; for s in v.iter() { n += s.len(); } n + v.len() }", "5")]
  [TestCase("fn g(s: &str) -> usize { s.len() } pub fn f() -> usize { let s = String::from(\"héllo\"); g(&s) }", "6")]
  [TestCase("pub fn f() -> String { let mut s = String::from(\"hi\"); let r = &mut s; r.push_str(\"!\"); r.push('?'); s }", "hi!?")]
  [TestCase("pub fn f() -> String { format!(\"{:>5}|{:<5}|{:^5}|{:*^7}|{:05}|{:+}|{:#x}|{:#010b}|{0}-{0}|{n}\", 42, true, 'c', \"ab\", -7, 3u8, 255, 5, n = 9) }", "   42|true |  c  |**ab***|-0007|+3|0xff|0b00000101|42-42|9")]
  [TestCase("pub fn f() -> u8 { let s = String::from(\"let\"); match s.as_str() { \"let\" => 1, _ => 2 } }", "1")]
  [TestCase("pub fn f() -> String { false.to_string() + &7i8.to_string() + &'é'.to_string() }", "false7é")]
  [TestCase("pub fn f() -> u32 { let mut s = String::new(); s += \"ab\"; s += &String::from(\"c\"); let t = s.clone(); (t.len() * 10 + s.chars().count()) as u32 }", "33")]
  [TestCase("pub fn f() -> u32 { let mut n = 0; for (i, c) in \"abc\".chars().enumerate() { n += i as u32 * c as u32; } n }", "296")]
  [TestCase("pub fn f() -> u32 { \"a1b2\".chars().filter(|c| c.is_ascii_digit()).map(|c| c.to_digit(10).unwrap()).sum() }", "3")]
  [TestCase("pub fn f() -> bool { let a = String::from(\"b\"); let b = \"b\"; a == b && b == a && a == *b && \"a\" < \"b\" }", "true")]
  [TestCase("pub fn f() -> String { let mut s = String::from(\"x\"); s.clear(); s.push('\U0001F600'); s }", "\U0001F600")]
  [TestCase("pub fn f() -> u8 { b'a' + \"z\".as_bytes()[0] - b'z' }", "97")]
  [TestCase("pub fn f() -> String { format!(\"{:X}|{:o}|{:x}|{:?}|{:?}|{:5}|{:<3}|{:e^4}\", -1i8, 8u8, u64::MAX, -3i32, true, \"é\", 'q', 1) }", "FF|10|ffffffffffffffff|-3|true|é    |q  |e1ee")]
  [TestCase("pub fn f() -> String { format!(\"{{{}}}{}\", 1, \"}\") }", "{1}}")]
  [TestCase("pub fn f() -> u32 { let x = 'b'; match x { 'a' | 'b' => 1, 'c'..='z' => 2, _ => 3 } }", "1")]
  [TestCase("pub fn f() -> u64 { \"x\".parse::<u64>().is_err() as u64 + \"12\".parse::<u64>().unwrap() }", "13")]
  [TestCase("pub fn f() -> i8 { let r = \"-128\".parse::<i8>(); r.unwrap() }", "-128")]
  [TestCase("pub fn f() -> String { let s = \"  x \"; s.trim_end().to_string() + \"|\" + s.trim_start() }", "  x|x ")]
  [TestCase("pub fn f() -> u32 { char::from_digit(7, 10).unwrap() as u32 + char::from(65u8) as u32 }", "120")]
  [TestCase("pub fn f() -> bool { 'Z'.is_ascii_uppercase() && 'z'.to_ascii_uppercase() == 'Z' && 'Q'.to_ascii_lowercase() == 'q' && ' '.is_whitespace() && 'f'.is_digit(16) }", "true")]
  [TestCase("pub fn f() -> u8 { 'a'.is_ascii_alphanumeric() as u8 + '#'.is_ascii_punctuation() as u8 + 'b'.is_ascii_hexdigit() as u8 + '~'.is_ascii_graphic() as u8 + '\\x07'.is_ascii_control() as u8 + 'A'.is_ascii_alphabetic() as u8 + 'x'.is_ascii_lowercase() as u8 + 'é'.is_ascii() as u8 }", "7")]
  [TestCase("pub fn f() -> String { let s = String::from(\"Ab\"); s.to_ascii_lowercase() + &s.to_owned() + s.as_str() }", "abAbAb")]
  [TestCase("pub fn f() -> bool { \"abc\".is_empty() || String::new().is_empty() }", "true")]
  [TestCase("pub fn f() -> bool { let a = \"a\"; a != \"b\" && a <= \"a\" && \"b\" >= a }", "true")]
  [TestCase("pub fn f() -> String { String::from('é') + &String::with_capacity(3) + &std::string::String::new() }", "é")]
  [TestCase("pub fn f() -> u32 { std::char::from_u32(66).unwrap() as u32 + core::char::from_digit(11, 16).unwrap() as u32 + 'a'.clone() as u32 }", "261")]
  [TestCase("pub fn f() -> String { let s = \"hello\"; s[1..=2].to_string() }", "el")]
  [TestCase("pub fn f() -> usize { let s = \"ab\"; let m = usize::MAX; s[..=m].len() }", RustFixture.Panic)]
  [TestCase("pub fn f() -> String { format!(\"{:+x}|{:#}|{:^4}|{}|{:-}\", 255u8, 5, 7, \"\", 5) }", "+ff|5| 7  ||5")]
  [TestCase("pub fn f() -> String { format!(\"\") }", "")]
  [TestCase("pub fn f() -> u8 { let r = \"5\".parse(); let v: u8 = r.unwrap(); v }", "5")]
  public void Text_HasTheValueRustGives(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo(expected));
  }

  [TestCase("pub fn f() -> String { let w = 6; let v = 3; format!(\"{v:>w$}\") }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f() -> String { format!(\"{}\", 1.5) }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f() -> String { format!(\"{:?}\", \"x\") }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f() -> String { format!(\"{:.2}\", 1) }", RustFrontend.UnsupportedMacro)]
  [TestCase("pub fn f() -> f64 { \"1.5\".parse::<f64>().unwrap() }", "parse into 'f64'")]
  [TestCase("pub fn f() -> bool { 'a'.is_alphabetic() }", "char::is_alphabetic")]
  [TestCase("pub fn f() -> usize { \"a b\".split_whitespace().count() }", "str::split_whitespace")]
  [TestCase("pub fn f() -> usize { \"ab\".split(\"\").count() }", "split at an empty pattern")]
  [TestCase("pub fn f() -> usize { \"ab\".split(1u8).count() }", "text pattern of type")]
  [TestCase("pub fn f(s: &mut str) -> usize { s.len() }", RustFrontend.TextType)]
  [TestCase("pub fn f() -> u8 { let s = \"ab\"; s[0] }", "E0277: the type `str` cannot be indexed")]
  [TestCase("pub fn f() -> String { String::from(\"a\") + String::from(\"b\") }", "E0308")]
  [TestCase("pub fn f() -> String { \"a\" + \"b\" }", "E0369: cannot add")]
  [TestCase("pub fn f() -> bool { String::from(\"a\") < \"b\" }", "E0308")]
  [TestCase("pub fn f() -> bool { \"a\" * \"b\" }", "E0369")]
  [TestCase("pub fn f() -> char { 'a' + 1 }", "E0369")]
  [TestCase("struct P { name: String } pub fn f() -> u8 { 1 }", "field 'name' of type 'String'")]
  [TestCase("fn add(s: &mut String) { s.push('a'); } pub fn f() -> u8 { 1 }", "'&mut String'")]
  [TestCase("pub fn f() -> u8 { let o: Option<&str> = None; 1 }", "Option<&str>")]
  [TestCase("pub fn f() -> u8 { let o = Some(\"x\"); 1 }", "a value holding text")]
  [TestCase("pub fn f() -> u8 { let t = (1u8, \"x\"); 1 }", "a value holding text")]
  [TestCase("pub fn f(t: (u8, String)) -> u8 { 1 }", "tuple holding String")]
  [TestCase("pub fn f() -> Result<String, u8> { Ok(String::new()) }", "Result<String, u8>")]
  [TestCase("pub fn f() -> char { 65u32 as char }", "E0054")]
  [TestCase("pub fn f() -> String { let s = String::new(); s.push('a'); s }", "E0596")]
  [TestCase("pub fn f() -> String { String::from(1u8) }", "E0277")]
  [TestCase("pub fn f() -> String { format!(\"{}\") }", "invalid reference to positional argument 0")]
  [TestCase("pub fn f() -> String { format!(\"{1}\", 1) }", "invalid reference to positional argument 1")]
  [TestCase("pub fn f() -> String { format!(\"{}\", 1, 2) }", "argument never used")]
  [TestCase("pub fn f() -> String { format!(\"{\", 1) }", "expected `}`")]
  [TestCase("pub fn f() -> String { format!(\"}\") }", "unmatched `}`")]
  [TestCase("pub fn f() -> String { format!(1) }", "format argument must be a string literal")]
  [TestCase("pub fn f() -> String { format!(\"{a}\", a = 1, 2) }", "positional arguments cannot follow named arguments")]
  [TestCase("pub fn f() -> String { format!(\"{:x}\", true) }", "E0277: `bool` doesn't implement `std::fmt::LowerHex`")]
  [TestCase("pub fn f() -> String { format!(\"{}\", (1, 2)) }", "E0277")]
  [TestCase("pub fn f() -> String { format!(\"{:?}\", [1u8]) }", "E0277: `[u8; 1]` doesn't implement `Debug`")]
  [TestCase("pub fn f() -> String { String::new(1) }", "E0061")]
  [TestCase("pub fn f() -> String { 'a'.to_string(1) }", "E0061")]
  [TestCase("pub fn f() -> usize { \"a\".len(1) }", "E0061")]
  [TestCase("pub fn f() -> u8 { \"a\".nope(1) }", "str::nope")]
  [TestCase("pub fn f() -> u8 { 'a'.nope(1) }", "char::nope")]
  [TestCase("pub fn f() -> bool { \"a\".contains(missing) }", "E0425")]
  [TestCase("pub fn f() -> String { format!(\"{}\", missing) }", "E0425")]
  [TestCase("pub fn f() -> String { format!(\"{:o}\", true) }", "`bool` doesn't implement `std::fmt::Octal`")]
  [TestCase("pub fn f() -> String { format!(\"{:b}\", true) }", "`bool` doesn't implement `std::fmt::Binary`")]
  [TestCase("pub fn f() -> String { format!(\"{:X}\", 'a') }", "`char` doesn't implement `std::fmt::UpperHex`")]
  public void Text_IsRefused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

  /// <summary>A function of the crate whose name starts as the helpers' do moves the helpers' names out of its way.</summary>
  [Test]
  public void Helpers_KeepClearOfTheCratesNames() {
    var result = Import("fn rust_str_len(x: u8) -> u8 { x } pub fn f() -> usize { \"ab\".len() + rust_str_len(1) as usize }");
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Functions.Select(function => function.Name), Does.Contain("_rust_str_len"));
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo("3"));
  }

  /// <summary>The functions the helpers are written with are no functions of any other source.</summary>
  [Test]
  public void Intrinsics_AreNoFunctionsOfTheCrate()
    => Assert.That(Import("pub fn f() -> usize { __units(\"a\") }").Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains("E0425"));

}
