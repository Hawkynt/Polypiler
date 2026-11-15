using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The Rust frontend on arrays, slices, <c>Vec</c>, references and slice iterators: the scenarios of
/// <c>fe-rs-memory.feature</c> and the rows of the test design of #465, in the interpreter. That
/// <c>rustc</c> computes the same is the acceptance tests' part, over the fixture <c>memory.rs</c>.
/// </summary>
[TestFixture]
public sealed class RustMemoryTests {

  private static ImportResult Import(string source, OverflowChecks mode = OverflowChecks.Panic) => RustFrontend.Import("snippet.rs", source, mode);

  private static IrModule Module(string source, OverflowChecks mode = OverflowChecks.Panic) {
    var result = Import(source, mode);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Run(string source, string function, params object[] arguments) => Run(source, OverflowChecks.Panic, function, arguments);

  private static string Run(string source, OverflowChecks mode, string function, params object[] arguments)
    => RustFixture.Interpreted(Module(source, mode), new PrototypeCall(function, arguments));

  private static string[] Codes(string source) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.Code)];
  }

  private static byte[] Bytes(string text) => [.. text.Select(character => (byte)character)];

  // ---------------------------------------------------------------- fe-rs-memory.feature

  private const string Adler32 = "pub fn adler32(d: &[u8]) -> u32 { let (mut a, mut b) = (1u32, 0u32); for &x in d { a = (a + x as u32) % 65521; b = (b + a) % 65521; } (b << 16) + a }";

  /// <summary>Scenario outline: imported slice kernels compute what rustc computes.</summary>
  [Test]
  public void Adler32_HasItsPublishedValue() {
    Assert.That(Run(Adler32, "adler32", Bytes("Wikipedia")), Is.EqualTo($"{0x11E60398u} | [{string.Join(",", Bytes("Wikipedia"))}]"));
    Assert.That(Run(Adler32, "adler32", new byte[0]), Is.EqualTo("1 | []"));
  }

  [TestCase("pub fn f(d: &[u8]) -> u32 { u32::from_be_bytes([d[0], d[1], d[2], d[3]]) }", new byte[] { 0x12, 0x34, 0x56, 0x78 }, 0x12345678u)]
  [TestCase("pub fn f(d: &[u8]) -> u32 { let mut s = 0u32; for c in d.chunks_exact(3) { s = s * 10 + c[0] as u32; } s }", new byte[] { 1, 2, 3, 4, 5, 6, 7 }, 14u)]
  [TestCase("pub fn f(a: &[u8]) -> u32 { let s = &a[1..3]; s[0] as u32 + s[1] as u32 }", new byte[] { 10, 20, 30, 40 }, 50u)]
  public void SliceKernel_ComputesWhatRustcComputes(string source, byte[] data, uint expected)
    => Assert.That(Run(source, "f", data), Does.StartWith(expected + " |"));

  [Test]
  public void WeightedSum_EnumeratesThePositions()
    => Assert.That(Run("pub fn f(a: &[u32]) -> u32 { let mut s = 0u32; for (i, &v) in a.iter().enumerate() { s += i as u32 * v; } s }", "f", new uint[] { 3, 1, 4 }), Is.EqualTo("9 | [3,1,4]"));

  /// <summary>Scenario: a mutable slice is written through an exclusive view and zip stops at the shorter side.</summary>
  [Test]
  public void XorTransform_WritesThroughTheExclusiveView() {
    const string Source = "pub fn f(src: &[u8], dst: &mut [u8]) { for (s, d) in src.iter().zip(dst.iter_mut()) { *d = *s ^ 0x5A; } }";
    Assert.That(Run(Source, "f", new byte[] { 0x00, 0xFF, 0x5A, 0x01 }, new byte[5]), Is.EqualTo("void | [0,255,90,1] | [90,165,0,91,0]"));
    var function = Module(Source).Functions[0];
    Assert.That(function.Parameters.Select(parameter => parameter.Type.Kind), Is.EqualTo(new[] { IrTypeKind.ReadOnlySpan, IrTypeKind.Span }));
  }

  /// <summary>Scenario: an out-of-bounds index is a trap, not a wild read.</summary>
  [Test]
  public void OutOfBoundsIndex_Traps() {
    const string Source = "pub fn f(a: &[u8], i: usize) -> u8 { a[i] }";
    Assert.That(Run(Source, "f", new byte[] { 1, 2, 3 }, 3UL), Is.EqualTo(RustFixture.Panic));
    Assert.That(Run(Source, "f", new byte[] { 1, 2, 3 }, 2UL), Is.EqualTo("3 | [1,2,3]"));
    Assert.That(Run(Source, "f", new byte[] { 1, 2, 3 }, ulong.MaxValue), Is.EqualTo(RustFixture.Panic));
  }

  /// <summary>Scenario: range indexing accepts start == len and rejects start > end.</summary>
  [TestCase(3UL, 3UL, "0")]
  [TestCase(2UL, 1UL, RustFixture.Panic)]
  [TestCase(1UL, 5UL, RustFixture.Panic)]
  [TestCase(0UL, 3UL, "3")]
  public void RangeIndex_ChecksItsBounds(ulong i, ulong j, string expected)
    => Assert.That(Run("pub fn f(a: &[u8], i: usize, j: usize) -> usize { a[i..j].len() }", "f", new byte[] { 1, 2, 3 }, i, j).Split(" |")[0], Is.EqualTo(expected));

  /// <summary>Test design 9: the open and inclusive range forms at their bounds.</summary>
  [TestCase("a[..].len()", "3")]
  [TestCase("a[3..].len()", "0")]
  [TestCase("a[4..].len()", RustFixture.Panic)]
  [TestCase("a[..2].len()", "2")]
  [TestCase("a[1..=2].len()", "2")]
  [TestCase("a[1..=3].len()", RustFixture.Panic)]
  [TestCase("a[..=0].len()", "1")]
  public void RangeForm_HasItsLength(string expression, string expected)
    => Assert.That(Run($"pub fn f(a: &[u8]) -> usize {{ {expression} }}", "f", new byte[] { 1, 2, 3 }).Split(" |")[0], Is.EqualTo(expected));

  /// <summary>Scenario: copy_from_slice refuses unequal lengths.</summary>
  [Test]
  public void CopyFromSlice_RefusesUnequalLengths() {
    const string Source = "pub fn f(dst: &mut [u8], src: &[u8]) { dst.copy_from_slice(src); }";
    Assert.That(Run(Source, "f", new byte[3], new byte[] { 1, 2 }), Is.EqualTo(RustFixture.Panic));
    Assert.That(Run(Source, "f", new byte[2], new byte[] { 1, 2 }), Is.EqualTo("void | [1,2] | [1,2]"));
  }

  /// <summary>Scenarios: unsafe memory access and text types are rejected with a stable code.</summary>
  [Test]
  public void UnsafeAndText_AreRejected() {
    var result = Import("fn peek(p: *const u8) -> u8 { unsafe { *p } }");
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line)), Does.Contain((RustFrontend.UnsafeConstruct, 1)));
    var text = Import("fn n(s: &str) -> usize { s.len() }");
    Assert.That(text.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line, diagnostic.Column)), Does.Contain((RustFrontend.TextType, 1, 9)));
  }

  // ---------------------------------------------------------------- test design

  /// <summary>Test design 1: CRC-32 of "123456789" is 0xCBF43926.</summary>
  [Test]
  public void Crc32Bitwise_HasItsCheckValue() {
    const string Source = """
      pub fn crc32(d: &[u8]) -> u32 {
        let mut crc = 0xFFFF_FFFFu32;
        for &byte in d.iter() {
          crc ^= byte as u32;
          for _ in 0..8 {
            crc = if crc & 1 != 0 { (crc >> 1) ^ 0xEDB8_8320 } else { crc >> 1 };
          }
        }
        !crc
      }
      """;
    Assert.That(Run(Source, "crc32", Bytes("123456789")).Split(" |")[0], Is.EqualTo(0xCBF43926u.ToString(System.Globalization.CultureInfo.InvariantCulture)));
  }

  /// <summary>Test design 2: an in-place reversal through swap, and through reverse.</summary>
  [TestCase("let n = a.len(); let mut i = 0; while i < n / 2 { a.swap(i, n - 1 - i); i += 1; }")]
  [TestCase("a.reverse();")]
  public void ReverseInPlace_ReversesTheSlice(string body)
    => Assert.That(Run($"pub fn f(a: &mut [i32]) {{ {body} }}", "f", new[] { 1, 2, 3, 4 }), Is.EqualTo("void | [4,3,2,1]"));

  /// <summary>Test design 3: each container class - array, &amp;[T], &amp;mut [T], Vec, static table, b"..." - read, written where mutable, measured.</summary>
  [TestCase("let mut a = [1u32, 2, 3]; a[1] = 7; a[1] + a.len() as u32", "10")]
  [TestCase("let a = [5u8; 4]; a[3] as u32 + a.len() as u32", "9")]
  [TestCase("let mut v = Vec::new(); v.push(3u32); v.push(4); v[1] = 9; v[0] + v[1] + v.len() as u32", "14")]
  [TestCase("let v = vec![1u32, 2, 3]; v[2] * 10 + v.len() as u32", "33")]
  [TestCase("let v = vec![7u32; 3]; v[2] + v.len() as u32", "10")]
  [TestCase("let v: Vec<u32> = Vec::with_capacity(4); v.len() as u32", "0")]
  [TestCase("let b = b\"abc\"; b[1] as u32 + b.len() as u32", "101")]
  [TestCase("TABLE[2] + TABLE.len() as u32", "34")]
  [TestCase("let t = TABLE; t[0] + t[3]", "41")]
  [TestCase("sum(&TABLE)", "73")]
  [TestCase("let a = [1u32, 2, 3]; sum(&a[1..])", "5")]
  [TestCase("let v = vec![4u32, 5]; sum(&v)", "9")]
  [TestCase("let mut a = [1u32, 2, 3]; let s = &mut a[..2]; s[0] = 9; a[0]", "9")]
  [TestCase("let mut a = [0u32; 3]; bump(&mut a); a[0] + a[2]", "2")]
  public void Container_IsReadWrittenAndMeasured(string body, string expected)
    => Assert.That(Run($"static TABLE: [u32; 4] = [1, 2, 30, 40]; fn sum(s: &[u32]) -> u32 {{ let mut t = 0; for x in s {{ t += x; }} t }} fn bump(s: &mut [u32]) {{ for x in s.iter_mut() {{ *x += 1; }} }} pub fn f() -> u32 {{ {body} }}", "f"), Is.EqualTo(expected));

  /// <summary>Test design 4: each element type summed with wrapping_add over iter().</summary>
  [TestCase("u8", new byte[] { 200, 100 }, "44")]
  [TestCase("i8", new sbyte[] { 100, 100 }, "-56")]
  [TestCase("u16", new ushort[] { 65535, 2 }, "1")]
  [TestCase("i16", new short[] { -32768, -1 }, "32767")]
  [TestCase("u32", new uint[] { 4294967295, 2 }, "1")]
  [TestCase("i32", new[] { int.MaxValue, 1 }, "-2147483648")]
  [TestCase("u64", new ulong[] { ulong.MaxValue, 2 }, "1")]
  [TestCase("i64", new[] { long.MaxValue, 1L }, "-9223372036854775808")]
  [TestCase("usize", new ulong[] { 3, 4 }, "7")]
  [TestCase("isize", new[] { -3L, 4L }, "1")]
  public void ElementType_SumsWithWrappingAdd(string type, System.Array values, string expected)
    => Assert.That(Run($"pub fn f(a: &[{type}]) -> {type} {{ let mut s: {type} = 0; for x in a.iter() {{ s = s.wrapping_add(*x); }} s }}", "f", values).Split(" |")[0], Is.EqualTo(expected));

  [TestCase("f32", new[] { 1.5f, 2.25f }, "f:400e000000000000")]
  [TestCase("f64", new[] { 1.5, 2.25 }, "f:400e000000000000")]
  public void FloatElements_Sum(string type, System.Array values, string expected)
    => Assert.That(Run($"pub fn f(a: &[{type}]) -> {type} {{ let mut s: {type} = 0.0; for x in a {{ s += *x; }} s }}", "f", values).Split(" |")[0], Is.EqualTo(expected));

  /// <summary>Test design 5: each iterator source and adapter over 1 2 3 4 5 6 7.</summary>
  [TestCase("for x in a.iter() { s = s * 10 + *x; }", "1234567")]
  [TestCase("for x in &a { s = s * 10 + x; }", "1234567")]
  [TestCase("for &x in a.iter().rev() { s = s * 10 + x; }", "7654321")]
  [TestCase("for x in a.iter().copied().step_by(3) { s = s * 10 + x; }", "147")]
  [TestCase("for x in a.iter().rev().step_by(3) { s = s * 10 + x; }", "741")]
  [TestCase("for x in a.iter().step_by(3).rev() { s = s * 10 + x; }", "741")]
  [TestCase("for x in a.iter().step_by(2).rev() { s = s * 10 + x; }", "7531")]
  [TestCase("for (i, x) in a.iter().enumerate().rev() { s = s * 10 + i as u64 * x; }", "45212620")]
  [TestCase("for (i, x) in a.iter().rev().enumerate() { s = s * 10 + i as u64 + x; }", "7777777")]
  [TestCase("for (x, y) in a.iter().zip(a[2..].iter()) { s = s * 10 + (y - x); }", "22222")]
  [TestCase("for c in a.chunks_exact(2) { s = s * 10 + c[1]; }", "246")]
  [TestCase("for c in a.chunks_exact(7) { s = s * 10 + c.len() as u64; }", "7")]
  [TestCase("for c in a.chunks_exact(8) { s = s * 10 + c.len() as u64; }", "0")]
  [TestCase("for x in a { s = s * 10 + x; }", "1234567")]
  [TestCase("let mut b = a; for x in &mut b { *x += 1; } for x in b.iter_mut().rev() { *x *= 2; } for x in b { s = s * 100 + x; }", "4060810121416")]
  [TestCase("let mut b = a; for c in b.chunks_exact_mut(3) { c[0] = 0; } for x in b { s = s * 10 + x; }", "230567")]
  public void IteratorForm_VisitsWhatRustVisits(string body, string expected)
    => Assert.That(Run($"pub fn f() -> u64 {{ let a = [1u64, 2, 3, 4, 5, 6, 7]; let mut s = 0u64; {body} s }}", "f"), Is.EqualTo(expected));

  /// <summary>Test design 6: a Vec moves, an array copies.</summary>
  [Test]
  public void MoveAndCopy_AreKept() {
    Assert.That(Run("pub fn f() -> u32 { let mut v = Vec::new(); for i in 0..5u32 { v.push(i * i); } let w = v; w.len() as u32 * 100 + w[4] }", "f"), Is.EqualTo("516"));
    Assert.That(Run("pub fn f() -> u32 { let a = [1u32, 2]; let mut b = a; b[0] = 9; a[0] * 10 + b[0] }", "f"), Is.EqualTo("19"));
    Assert.That(Run("fn g(mut x: [u32; 2]) -> u32 { x[0] = 5; x[0] } pub fn f() -> u32 { let a = [1u32, 2]; g(a) * 10 + a[0] }", "f"), Is.EqualTo("51"));
  }

  /// <summary>Test design 7: byte order.</summary>
  [TestCase("let b = 0x0102u16.to_be_bytes(); b[0] as u64 * 256 + b[1] as u64", "258")]
  [TestCase("u16::from_le_bytes([1, 2]) as u64", "513")]
  [TestCase("let b = 0x12345678u32.to_le_bytes(); b[0] as u64 * 1000 + b[3] as u64", "120018")]
  [TestCase("u64::from_be_bytes(1u64.to_be_bytes())", "1")]
  [TestCase("i8::from_le_bytes((-3i8).to_le_bytes()) as u64", "18446744073709551613")]
  [TestCase("f64::from_le_bytes(1.5f64.to_le_bytes()) as u64", "1")]
  public void ByteOrder_IsExplicit(string expression, string expected)
    => Assert.That(Run($"pub fn f() -> u64 {{ {expression} }}", "f"), Is.EqualTo(expected));

  /// <summary>Test design 12 and 13: split_at at its bounds, split_at_mut writes, copy_within overlapping.</summary>
  [TestCase("let (l, r) = a.split_at(0); l.len() * 10 + r.len()", "4")]
  [TestCase("let (l, r) = a.split_at(4); l.len() * 10 + r.len()", "40")]
  [TestCase("let (l, r) = a.split_at(5); l.len() * 10 + r.len()", RustFixture.Panic)]
  [TestCase("let (l, r) = a.split_at_mut(2); l[0] = r[1]; r[0] = 9; a[0] * 1000 + a[1] * 100 + a[2] * 10 + a[3]", "4294")]
  [TestCase("a.copy_within(0..3, 1); a[0] * 1000 + a[1] * 100 + a[2] * 10 + a[3]", "1123")]
  [TestCase("a.copy_within(1..=3, 0); a[0] * 1000 + a[1] * 100 + a[2] * 10 + a[3]", "2344")]
  [TestCase("a.copy_within(2.., 3); a[3]", RustFixture.Panic)]
  [TestCase("a.fill(7); a[0] + a[3]", "14")]
  public void SliceMethod_KeepsRustsBounds(string body, string expected)
    => Assert.That(Run($"pub fn f() -> usize {{ let mut a = [1usize, 2, 3, 4]; {body} }}", "f"), Is.EqualTo(expected));

  /// <summary>Test design 10 and 11: chunks_exact counts and zip lengths at their bounds.</summary>
  [TestCase(0, 0)]
  [TestCase(1, 0)]
  [TestCase(6, 2)]
  [TestCase(7, 2)]
  public void ChunksExact_DropsTheRemainder(int length, int chunks)
    => Assert.That(Run("pub fn f(a: &[u8]) -> u32 { let mut n = 0; for _ in a.chunks_exact(3) { n += 1; } n }", "f", new byte[length]).Split(" |")[0], Is.EqualTo(chunks.ToString(System.Globalization.CultureInfo.InvariantCulture)));

  [TestCase(3, 3, 3)]
  [TestCase(2, 3, 2)]
  [TestCase(3, 2, 2)]
  [TestCase(0, 3, 0)]
  public void Zip_StopsAtTheShorterSide(int left, int right, int iterations)
    => Assert.That(Run("pub fn f(a: &[u8], b: &[u8]) -> u32 { let mut n = 0; for _ in a.iter().zip(b) { n += 1; } n }", "f", new byte[left], new byte[right]).Split(" |")[0], Is.EqualTo(iterations.ToString(System.Globalization.CultureInfo.InvariantCulture)));

  /// <summary>Test design 14 to 17: the bounds traps, element overflow by mode, len() - 1 of an empty slice, and the order of a[i] = e.</summary>
  [TestCase("pub fn f(a: &mut [u8]) -> u8 { a.swap(0, 3); 0 }", OverflowChecks.Panic, RustFixture.Panic)]
  [TestCase("pub fn f(a: &mut [u8]) -> u8 { for _ in a.iter().step_by(0) {} 0 }", OverflowChecks.Panic, RustFixture.Panic)]
  [TestCase("pub fn f(a: &mut [u8]) -> u8 { for _ in a.chunks_exact(0) {} 0 }", OverflowChecks.Panic, RustFixture.Panic)]
  [TestCase("pub fn f(a: &mut [u8]) -> u8 { let mut s = 0u8; for &v in a.iter() { s += v; } s }", OverflowChecks.Panic, RustFixture.Overflow)]
  [TestCase("pub fn f(a: &mut [u8]) -> u8 { let mut s = 0u8; for &v in a.iter() { s += v; } s }", OverflowChecks.Wrap, "44")]
  public void Trap_HasItsClass(string source, OverflowChecks mode, string expected)
    => Assert.That(Run(source, mode, "f", new byte[] { 200, 100, 0 }).Split(" |")[0], Is.EqualTo(expected));

  [TestCase(OverflowChecks.Panic, RustFixture.Overflow)]
  [TestCase(OverflowChecks.Wrap, RustFixture.Panic)]
  public void LengthMinusOne_OfAnEmptySlice(OverflowChecks mode, string expected)
    => Assert.That(Run("pub fn f(a: &[u8]) -> u8 { a[a.len() - 1] }", mode, "f", new byte[0]), Is.EqualTo(expected));

  [Test]
  public void ElementAssignment_EvaluatesTheValueFirst()
    => Assert.That(Run("pub fn f(a: &mut [u8], z: u8) { a[9] = 1 / z; }", "f", new byte[3], (byte)0), Is.EqualTo(RustFixture.DivideByZero));

  /// <summary>References to scalars: a local &amp;mut writes its place, a parameter one is copied back, a shared one is the value.</summary>
  [TestCase("let mut x = 1u32; { let r = &mut x; *r += 4; } x", "5")]
  [TestCase("let mut x = 1u32; inc(&mut x); inc(&mut x); x", "3")]
  [TestCase("let mut a = [1u32, 2]; inc(&mut a[1]); a[1]", "3")]
  [TestCase("let x = 7u32; read(&x)", "7")]
  [TestCase("let mut x = 1u32; let mut y = 2u32; std::mem::swap(&mut x, &mut y); x * 10 + y", "21")]
  [TestCase("std::cmp::min(4u32, 9) * 10 + core::cmp::max(4u32, 9)", "49")]
  public void Reference_ReachesItsPlace(string body, string expected)
    => Assert.That(Run($"fn inc(x: &mut u32) {{ *x += 1; }} fn read(x: &u32) -> u32 {{ *x }} pub fn f() -> u32 {{ {body} }}", "f"), Is.EqualTo(expected));

  /// <summary>Test design 18: text, unsafe, smart pointers, other methods, other collections, unsupported element types.</summary>
  [TestCase("pub fn f(s: &str) -> u32 { 1 }", RustFrontend.TextType)]
  [TestCase("pub fn f(s: String) -> u32 { 1 }", RustFrontend.TextType)]
  [TestCase("pub fn f(p: *const u8) -> u32 { 1 }", RustFrontend.UnsafeConstruct)]
  [TestCase("pub fn f() -> u32 { unsafe { 1 } }", RustFrontend.UnsafeConstruct)]
  [TestCase("static mut X: u32 = 1; pub fn f() -> u32 { 1 }", RustFrontend.UnsafeConstruct)]
  [TestCase("union U { a: u32 } pub fn f() -> u32 { 1 }", RustFrontend.UnsafeConstruct)]
  [TestCase("pub fn f(b: Box<[u8]>) -> u32 { 1 }", RustFrontend.SmartPointer)]
  [TestCase("pub fn f(b: std::cell::RefCell<u32>) -> u32 { 1 }", RustFrontend.SmartPointer)]
  [TestCase("pub fn f(a: &[u8]) -> usize { a.windows(2).count() }", RustFrontend.UnsupportedSliceMethod)]
  [TestCase("pub fn f(a: u32) -> u8 { a.to_ne_bytes()[0] }", RustFrontend.UnsupportedSliceMethod)]
  [TestCase("pub fn f(a: &mut [u8]) { a.sort(); }", RustFrontend.UnsupportedSliceMethod)]
  [TestCase("pub fn f(m: std::collections::HashMap<u32, u32>) -> u32 { 1 }", RustFrontend.UnsupportedCollection)]
  [TestCase("pub fn f(m: HashMap<u32, u32>) -> u32 { 1 }", RustFrontend.UnsupportedCollection)]
  [TestCase("pub fn f(a: &[bool]) -> u32 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f(a: [[u8; 2]; 2]) -> u32 { 1 }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f(a: &[u8]) -> u32 { match a { [x] => 1, _ => 0 } }", RustFrontend.UnsupportedElement)]
  [TestCase("pub fn f(a: &[u8]) -> u32 { let it = a.iter(); 1 }", RustFrontend.UnsupportedIteratorMethod)]
  public void Construct_IsRefusedWithItsCode(string source, string code)
    => Assert.That(Codes(source), Does.Contain(code));

  /// <summary>What rustc rejects about the memory constructs is rejected.</summary>
  [TestCase("pub fn f(a: &[u8]) { a[0] = 1; }", "E0594")]
  [TestCase("pub fn f() -> u32 { let a = [1u32, 2]; a[2] }", "unconditional_panic")]
  [TestCase("pub fn f(a: &[u8], i: u32) -> u8 { a[i] }", "E0308")]
  [TestCase("pub fn f(a: u32) -> u8 { a[0] }", "E0608")]
  [TestCase("pub fn f() -> u32 { let x = 1u32; let r = &mut x; *r }", "E0596")]
  [TestCase("pub fn f(a: &[u8]) -> u8 { a.iter_mut().count() as u8 }", "E0596")]
  public void CompilerError_IsRejected(string source, string text)
    => Assert.That(Import(source).Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(text));

}
