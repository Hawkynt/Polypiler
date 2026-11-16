using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Pointers as safe Rust (#460): a pointer is a position in a region the backend knows statically - the
/// pinned array or view, the array of a <c>stackalloc</c>, the local whose address is taken, the slice a
/// pointer parameter brings - so a walk is index arithmetic and a load an index, never a raw pointer or
/// <c>unsafe</c>. The acceptance project runs the pointer fixtures against .NET and the interpreter.
/// </summary>
[TestFixture]
public sealed class PointerEmitTests {

  private static string Emitted(string methods) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: OptimizationLevel.None));

  private static string[] Problems(string methods) => [.. RustCrates.Check(RustCrates.Import(methods)).Select(problem => $"{problem.Code} {problem.Message}")];

  [Test]
  public void PointerWalk_IsAnIndexIntoThePinnedSlice() {
    var text = RustCrates.ModuleText(RustCrates.Emit(PointerFixtures.Imported("Fnv1aPointer"), level: OptimizationLevel.None));
    Assert.That(text, Does.Contain("pub fn hash(data: &[u8]) -> u32 {"));
    Assert.That(text, Does.Contain("let mut p: i64 = start;"));
    Assert.That(text, Does.Contain("data[ply_pointer(p, 1, data.len(), true).start]"));
    Assert.That(text, Does.Contain("p = p.wrapping_add(1);"));
    Assert.That(text, Does.Not.Contain("unsafe").And.Not.Contain("*const").And.Not.Contain("*mut"));
  }

  [Test]
  public void LoadOfAWiderNumberFromBytes_ReadsItsLittleEndianBytes() {
    var text = Emitted("public static unsafe uint W(byte[] d, int at) { fixed (byte* p = d) { return *(uint*)(p + at); } }");
    Assert.That(text, Does.Contain("u32::from_le_bytes("));
    Assert.That(text, Does.Contain("d[ply_pointer(p.wrapping_add(i64::from(at)), 4, d.len(), true)]"));
    Assert.That(text, Does.Contain(".expect(\"4 bytes\")"));
  }

  [Test]
  public void StoreOfAWiderNumberIntoBytes_BindsItsPlaceBeforeTheSliceIsWritten() {
    var text = Emitted("public static unsafe void S(byte[] d, int at, long v) { fixed (byte* p = d) { *(long*)(p + at) = v; } }");
    Assert.That(text, Does.Contain("pub fn s(d: &mut [u8], at: i32, v: i64) {"));
    Assert.That(text, Does.Contain("let value = v;"));
    Assert.That(text, Does.Contain("let at1 = ply_pointer(p.wrapping_add(i64::from(at)), 8, d.len(), true);"));
    Assert.That(text, Does.Contain("d[at1].copy_from_slice(&value.to_le_bytes());"));
  }

  [Test]
  public void ArithmeticOfAWiderPointerOverBytes_ScalesByItsWidth() {
    var text = Emitted("public static unsafe int D(byte[] d) { fixed (byte* p = d) { uint* x = (uint*)p; uint* y = x + 2; return (int)(y - x); } }");
    Assert.That(text, Does.Contain("x.wrapping_add(2i64.wrapping_mul(4))").Or.Contain("x.wrapping_add(2.wrapping_mul(4))"));
    Assert.That(text, Does.Contain("y.wrapping_sub(x) / 4"));
  }

  [Test]
  public void PointerOfTheSameWidth_ReinterpretsTheElement() {
    var text = Emitted("public static unsafe uint R(int[] a) { fixed (int* p = a) { uint* q = (uint*)p; *q = 5; float* f = (float*)p; return *q + (uint)*f; } }");
    Assert.That(text, Does.Contain("as u32"));
    Assert.That(text, Does.Contain("f32::from_bits("));
    Assert.That(text, Does.Contain("pub fn r(a: &mut [i32]) -> u32 {"));
  }

  [Test]
  public void StackAlloc_IsAnArrayOfZerosOrAVector() {
    var text = Emitted("public static unsafe int S(int n) { int* p = stackalloc int[8]; p[7] = n; int* q = stackalloc int[n]; return p[7] + (n > 0 ? q[0] : 0); }");
    Assert.That(text, Does.Contain("let mut p_memory = [0i32; 8];"));
    Assert.That(text, Does.Contain("let q_memory = vec![0i32; usize::try_from(n).expect(\"IntegerOverflow\")];"));
    Assert.That(text, Does.Contain("p_memory[p.wrapping_add(7) as usize] = n;"));
  }

  [Test]
  public void AddressOfALocal_IsTheLocalItself() {
    var text = Emitted("public static unsafe int L() { int x = 6; int* p = &x; *p += 1; return x; }");
    Assert.That(text, Does.Contain("let mut x: i32 = 6;"));
    Assert.That(text, Does.Contain("x = x.wrapping_add(1);"));
    Assert.That(text, Does.Not.Contain("let p"));
  }

  [Test]
  public void AddressOfADoubleWrittenAsALong_IsItsBits() {
    var text = Emitted("public static unsafe double B() { double d = 0; *(long*)&d = 0x3FF8000000000000; return d; }");
    Assert.That(text, Does.Contain("d = f64::from_bits(4609434218613702656);"));
    Assert.That(Emitted("public static unsafe long B(double d) { return *(long*)&d; }"), Does.Contain("d.to_bits() as i64"));
  }

  [Test]
  public void AddressOfAParameterWritten_BindsTheParameterAgain() {
    var text = Emitted("public static unsafe int P(int x) { int* p = &x; *p = *p * 2; return x; }");
    Assert.That(text, Does.Contain("let mut x = x;"));
  }

  [Test]
  public void PinOfOneElement_ChecksTheElement() {
    var text = Emitted("public static unsafe int E(byte[] d) { fixed (byte* e = &d[3]) { return e[-1] + e[0]; } }");
    Assert.That(text, Does.Contain("let e: i64 = ply_pointer(3, 1, d.len(), false).start as i64;"));
    Assert.That(text, Does.Contain("d[e.wrapping_add(-1) as usize]"));
  }

  [Test]
  public void PinOfAView_IndexesTheView() {
    var text = Emitted("public static unsafe int V(byte[] d) { System.Span<byte> w = new System.Span<byte>(d, 4, 4); fixed (byte* v = w) { v[3] = 1; return v[0]; } }");
    Assert.That(text, Does.Contain("let w: &mut [u8] = ply_view_mut(d, 4, 4);"));
    Assert.That(text, Does.Contain("w[ply_pointer(v.wrapping_add(3), 1, w.len(), true).start] = 1;"));
    Assert.That(text, Does.Contain("pub fn v(d: &mut [u8]) -> i32 {"));
  }

  [Test]
  public void PointerParameter_BringsItsRegionAsASlice() {
    var text = Emitted("public static unsafe int P(int[] a) { fixed (int* p = a) { return Sum(p, a.Length); } } private static unsafe int Sum(int* p, int n) { int t = 0; for (int i = 0; i < n; i++) t += p[i]; return t; }");
    Assert.That(text, Does.Contain("fn sum(p_region: &[i32], p: i64, n: i32) -> i32 {"));
    Assert.That(text, Does.Contain("sum(a, p, a.len() as i32)"));
    Assert.That(text, Does.Contain("p_region[ply_pointer(p.wrapping_add(i64::from(i)), 1, p_region.len(), true).start]"));
  }

  [Test]
  public void PointerParameterWrittenThrough_IsExclusiveInEveryCaller() {
    var text = Emitted("public static unsafe int P(int[] a) { fixed (int* p = a) { Outer(p); } return a[0]; } private static unsafe void Outer(int* p) { Inner(p + 1); } private static unsafe void Inner(int* q) { *q = 9; }"
      + " public static unsafe int S() { int* s = stackalloc int[2]; Inner(s); return s[1]; }");
    Assert.That(text, Does.Contain("fn inner(q_region: &mut [i32], q: i64) {"));
    Assert.That(text, Does.Contain("fn outer(p_region: &mut [i32], p: i64) {"));
    Assert.That(text, Does.Contain("inner(p_region, p.wrapping_add(1));"));
    Assert.That(text, Does.Contain("pub fn p(a: &mut [i32]) -> i32 {"));
    Assert.That(text, Does.Contain("inner(&mut s_memory, s);"));
  }

  [Test]
  public void ComparisonWithNull_IsTheEmptinessOfAWholePin() {
    var text = Emitted("public static unsafe bool N(byte[] d) { fixed (byte* p = d) { return p == null; } } public static unsafe bool M(byte[] d) { fixed (byte* p = d) { return p != null; } }");
    Assert.That(text, Does.Contain("d.is_empty()"));
    Assert.That(text, Does.Contain("!d.is_empty()"));
    Assert.That(text, Does.Contain("let _p: i64 = 0;"));
  }

  [TestCase("public static unsafe bool N(byte[] d) { fixed (byte* p = &d[0]) { return p == null; } }", "false", TestName = "ElementPinComparedWithNull_IsNeverNull")]
  [TestCase("public static unsafe bool N() { fixed (byte* p = new byte[0]) { return p == null; } }", "true", TestName = "PinOfAnEmptyNewArray_IsNull")]
  [TestCase("public static unsafe bool N() { fixed (byte* p = new byte[2]) { return p != null; } }", "true", TestName = "PinOfANewArrayWithElements_IsNotNull")]
  public void ComparisonWithNull_OfAKnownRegion_IsAConstant(string methods, string value)
    => Assert.That(Emitted(methods), Does.Contain($"    {value}\n"));

  [Test]
  public void PinOfANewArrayThatIsRead_HoldsItsMemory() {
    var text = Emitted("public static unsafe int N(int n) { fixed (int* p = new int[] { 4, 5 }) { fixed (int* q = new int[n]) { return p[1] + (q == null ? 1 : 0); } } }");
    Assert.That(text, Does.Contain("let p_memory = [4i32, 5i32];"));
    Assert.That(text, Does.Contain("let q_memory: Vec<i32> = vec![0; usize::try_from(n).expect(\"IntegerOverflow\")];").Or.Contain("let q_memory = vec![0; usize::try_from(n).expect(\"IntegerOverflow\")];"));
    Assert.That(text, Does.Contain("q_memory.is_empty()"));
  }

  [Test]
  public void ComparisonOfPointersIntoOneRegion_ComparesPositions() {
    var text = Emitted("public static unsafe bool I(int[] a, int i) { fixed (int* p = a) { int* q = p + i; return q >= p && q < p + a.Length && q != p && q <= p && q > p; } }");
    Assert.That(text, Does.Contain("q >= p && q < p.wrapping_add(i64::from(a.len() as i32))"));
  }

  [TestCase("public static unsafe bool A(int[] a, int[] b) { fixed (int* p = a) { fixed (int* q = b) { return p < q; } } }", "'a pointer comparison across two regions'", TestName = "OrderAcrossTwoArrays_IsRefused")]
  [TestCase("public static unsafe long A(int[] a, int[] b) { fixed (int* p = a) { fixed (int* q = b) { return q - p; } } }", "'a pointer difference across two regions'", TestName = "DifferenceAcrossTwoArrays_IsRefused")]
  [TestCase("public static unsafe int A(int x) { int* p = &x; p++; return *p; }", "'arithmetic on the address of a local'", TestName = "AddressMoved_IsRefused")]
  [TestCase("public static unsafe int A(int x) { int* p = &x; int* q = p + 1; return *q; }", "'arithmetic on the address of a local'", TestName = "AddressAdded_IsRefused")]
  [TestCase("public static unsafe int A(int x) { return G(&x); } private static unsafe int G(int* p) => *p;", "'the address of a local passed to a function'", TestName = "AddressPassed_IsRefused")]
  [TestCase("public static unsafe int A(int[] a, int[] b, bool c) { fixed (int* p = a) { fixed (int* q = b) { int* r = p; if (c) r = q; return *r; } } }", "'the pointer 'r' given pointers into two regions'", TestName = "PointerIntoTwoRegions_IsRefused")]
  [TestCase("public static unsafe int A(int[] a) { fixed (int* p = new System.Span<int>(a, 1, 2)) { return *p; } }", "'a pin of something that is no variable or new array'", TestName = "PinOfAViewThatIsMade_IsRefused")]
  [TestCase("public static unsafe int A(short[] a) { fixed (short* p = a) { return *(int*)p; } }", "'a pointer of i32 into the memory of i16'", TestName = "WiderPointerIntoNoBytes_IsRefused")]
  [TestCase("public static unsafe int A(int[] a) { if (a == null) return 0; fixed (int* p = a) { return *p; } }", "'a pin of an array that may be null'", TestName = "PinOfAnArrayThatMayBeNull_IsRefused")]
  [TestCase("public static unsafe bool A(int[] a) { fixed (int* p = a) { return p < null; } }", "'an ordering of a pointer and the null pointer'", TestName = "OrderingWithNull_IsRefused")]
  public void PointerWithNoSafeForm_IsRefusedByName(string methods, string construct)
    => Assert.That(Problems(methods), Has.Some.Contain(construct));

  [Test]
  public void NullPointerPassed_IsRefused()
    => Assert.That(Problems("public static unsafe int A() { return G(null); } private static unsafe int G(int* p) => p == null ? 0 : *p;"), Has.Some.Contain("'the null pointer passed to a function'"));

  /// <summary>The parse-back sweep over the pointer fixtures: every generated file reads back into a tree that prints the same text.</summary>
  [TestCase("Fnv1aPointer")]
  [TestCase("Pointers")]
  public void GeneratedFileOfAPointerFixture_ReadsBackToItsText(string fixture) {
    var module = PointerFixtures.Imported(fixture);
    module = module with { Functions = [.. module.Functions.Where(function => function.Name is not ("Apart" or "Before"))] };
    foreach (var file in RustCrates.Emit(module).Where(file => file.Path.EndsWith(".rs", StringComparison.Ordinal)))
      Assert.That(RustPrinter.Print(RustSyntaxReader.Read(file.Content)), Is.EqualTo(file.Content), file.Path);
  }

  [Test]
  public void NumberOfTheSameWidth_IsItsBitsBothWays() {
    var text = Emitted("public static unsafe void F(int[] a) { fixed (int* p = a) { *(float*)p = 1.5f; } } public static unsafe ulong U(double d) => *(ulong*)&d;"
      + " public static unsafe double L(long l) => *(double*)&l; public static unsafe byte B(sbyte s) => *(byte*)&s;");
    Assert.That(text, Does.Contain("= 1.5f32.to_bits() as i32;").Or.Contain("= (1.5f32).to_bits() as i32;").Or.Contain("= 1.5.to_bits() as i32;"));
    Assert.That(text, Does.Contain("d.to_bits()\n"));
    Assert.That(text, Does.Contain("f64::from_bits(l as u64)"));
    Assert.That(text, Does.Contain("s as u8"));
  }

  [Test]
  public void OffsetOfAnotherIntegerType_IsWidened() {
    var text = Emitted("public static unsafe int O(int[] a, long i, uint j) { fixed (int* p = a) { return *(p + i) + *(p + j); } }");
    Assert.That(text, Does.Contain("pub fn o(a: &[i32], i: i64, j: u32) -> i32 {"));
  }

  [Test]
  public void PinOfAnArrayLiteral_IsNeverNull() {
    var text = Emitted("public static unsafe bool N() { fixed (int* p = new int[] { 1 }) { return p == null; } }");
    Assert.That(text, Does.Contain("    false\n"));
  }

  [TestCase("public static unsafe int A(short[] a) { fixed (short* p = a) { *(int*)p = 1; return a[0]; } }", "'a pointer of i32 into the memory of i16'", TestName = "WiderStoreIntoNoBytes_IsRefused")]
  [TestCase("public static unsafe long A(short[] a) { fixed (short* p = a) { int* q = (int*)p; return (q + 1) - q; } }", "'a pointer of i32 into the memory of i16'", TestName = "WiderArithmeticIntoNoBytes_IsRefused")]
  [TestCase("public static unsafe int A() { int* p = null; *p = 1; return 0; }", "'a store through the null pointer'", TestName = "StoreThroughNull_IsRefused")]
  [TestCase("public static unsafe int A() { int* p = null; return *p; }", "'a load through the null pointer'", TestName = "LoadThroughNull_IsRefused")]
  [TestCase("public static unsafe int A(int[] a) { fixed (int* q = a) { int* p; p = q; return *p; } }", "'the pointer 'p' declared without a value'", TestName = "PointerDeclaredWithoutAValue_IsRefused")]
  [TestCase("public static unsafe int A(int[] a) { fixed (int* q = a) { return G(q); } } private static unsafe int G(void* v) => *(int*)v;", "'the void* parameter 'v''", TestName = "VoidPointerParameter_IsRefused")]
  [TestCase("public static unsafe int A(int[] a) { fixed (int* q = a) { int* r = q; r = null; return r == null ? 1 : 0; } }", "'a pointer of the form Null'", TestName = "PointerAssignedNull_IsRefused")]
  public void MorePointersWithNoSafeForm_AreRefusedByName(string methods, string construct)
    => Assert.That(Problems(methods), Has.Some.Contain(construct));

}
