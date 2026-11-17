using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Parallel loops, moves of a vector and copies between views as safe Rust (#460): a loop over the
/// shorter of two arrays is <c>zip</c> with <c>iter</c>/<c>iter_mut</c>, a vector the source never names
/// again changes owner, a view copy is <c>copy_from_slice</c> or, within one array, <c>copy_within</c>,
/// and two halves of one array are <c>split_at_mut</c>. The acceptance project builds the same shapes
/// and runs them against rustc's and .NET's results.
/// </summary>
[TestFixture]
public sealed class ZipAndViewEmitTests {

  private static string Emitted(string methods) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: OptimizationLevel.None));

  private static string Rust(string source) => RustCrates.ModuleText(RustCrates.Emit(RustFixture.Import("snippet.rs", source, OverflowChecks.Panic), level: OptimizationLevel.None));

  private static string[] Problems(string methods) => [.. RustCrates.Check(RustCrates.Import(methods)).Select(problem => $"{problem.Code} {problem.Message}")];

  [Test]
  public void ZipOfAReadAndAWrittenSlice_IsIterAndIterMut() {
    var text = Rust("pub fn xor_transform(src: &[u8], dst: &mut [u8]) { for (s, d) in src.iter().zip(dst.iter_mut()) { *d = *s ^ 0x5A; } }");
    Assert.That(text, Does.Contain("for (&s, item) in src.iter().zip(dst.iter_mut()) {"));
    Assert.That(text, Does.Contain("*item = assigned;"));
    Assert.That(text, Does.Not.Contain("count2"));
  }

  [Test]
  public void ZipOfTwoReadSlices_CopiesBothElements() {
    var text = Rust("pub fn dot(a: &[u32], b: &[u32]) -> u32 { let mut s = 0u32; for (x, y) in a.iter().zip(b.iter()) { s = s.wrapping_add(x.wrapping_mul(*y)); } s }");
    Assert.That(text, Does.Contain("for (&x, &y) in a.iter().zip(b.iter()) {"));
  }

  [Test]
  public void ZipWhoseElementsAreNotUsed_BindsNothing() {
    var text = Rust("pub fn zip_count(a: &[u8], b: &[u8]) -> u32 { let mut n = 0; for _ in a.iter().zip(b) { n += 1; } n }");
    Assert.That(text, Does.Contain("for _ in a.iter().zip(b.iter()) {"));
  }

  [Test]
  public void LoopToTheMinimumOfTwoLengths_InCSharp_IsAZip() {
    var text = Emitted("public static void X(byte[] a, byte[] b) { for (int i = 0; i < System.Math.Min(a.Length, b.Length); i++) b[i] = (byte)(a[i] ^ 0x5A); }");
    Assert.That(text, Does.Contain("for (&element, item) in a.iter().zip(b.iter_mut()) {"));
    Assert.That(text, Does.Contain("*item = (i32::from(element) ^ 90) as u8;"));
  }

  [Test]
  public void ElementReadAndWrittenThroughOneArray_IsDereferenced() {
    var text = Emitted("public static void X(int[] a, int[] b) { for (int i = 0; i < System.Math.Min(a.Length, b.Length); i++) b[i] = b[i] + a[i]; }");
    Assert.That(text, Does.Contain("for (&element, item) in a.iter().zip(b.iter_mut()) {"));
    Assert.That(text, Does.Contain("*item = (*item).wrapping_add(element);").Or.Contain("*item = item.wrapping_add(element);"));
  }

  [TestCase("public static int X(int[] a, int[] b) { int s = 0; for (int i = 0; i < System.Math.Min(a.Length, b.Length); i++) s += a[i] * i; return s; }", TestName = "CounterUsedOtherwise_StaysAnIndexLoop")]
  [TestCase("public static int X(int[] a, int[] b) { int s = 0; for (int i = 0; i < System.Math.Min(a.Length, b.Length); i++) s += a[i] + b[0]; return s; }", TestName = "ArrayIndexedOtherwise_StaysAnIndexLoop")]
  [TestCase("public static int X(int[] a, int[] b) { int s = 0; for (int i = 0; i < System.Math.Min(a.Length, b.Length); i++) s += a[i] + b.Length; return s; }", TestName = "ArrayMeasuredInTheBody_StaysAnIndexLoop")]
  [TestCase("public static int X(int[] a) { int s = 0; for (int i = 0; i < System.Math.Min(a.Length, a.Length); i++) s += a[i]; return s; }", TestName = "OneArrayTwice_StaysAnIndexLoop")]
  [TestCase("public static int X(int[] a, int[] b) { int s = 0; for (int i = 0; i < System.Math.Min(a.Length, 3); i++) s += a[i]; return s; }", TestName = "MinimumWithAConstant_StaysAnIndexLoop")]
  [TestCase("public static int X(int[] a, int[] b) { int s = 0; for (int i = 1; i < System.Math.Min(a.Length, b.Length); i++) s += a[i]; return s; }", TestName = "LoopFromOne_StaysAnIndexLoop")]
  public void LoopThatIsNoParallelLoop_IsNoZip(string methods) => Assert.That(Emitted(methods), Does.Not.Contain(".zip("));

  [Test]
  public void BoundReadAfterTheLoop_StaysAnIndexLoop() {
    var text = Rust("pub fn f(a: &[u8], b: &[u8]) -> u32 { let n = a.len().min(b.len()); let mut s = 0u32; let mut i = 0; while i < n { s += a[i] as u32 + b[i] as u32; i += 1; } s + n as u32 }");
    Assert.That(text, Does.Not.Contain(".zip("));
  }

  [Test]
  public void ArrayAssignedInTheBody_StaysAnIndexLoop() {
    var text = Emitted("public static int X(int[] a, int[] b) { int s = 0; for (int i = 0; i < System.Math.Min(a.Length, b.Length); i++) { s += a[i] + b[i]; } return s; }");
    Assert.That(text, Does.Contain(".zip("));
    Assert.That(Emitted("public static int X(int[] a, int[] b) { int s = 0; int n = System.Math.Min(a.Length, b.Length); for (int i = 0; i < n; i++) { s += a[i] + b[i]; a = b; } return s; }"), Does.Not.Contain(".zip("));
  }

  [Test]
  public void VectorTheSourceNeverNamesAgain_IsMoved() {
    var text = Emitted("public static int X() { var a = new int[3]; a[1] = 2; var b = a; b[0] = 1; return b[0] + b[1]; }");
    Assert.That(text, Does.Contain("let mut b: Vec<i32> = a;"));
    Assert.That(Emitted("public static int X(bool c) { var a = new int[3]; var b = new int[1]; if (c) b = a; b[0] = 1; return b[0]; }"), Does.Contain("b = a;"));
  }

  [TestCase("public static int X() { var a = new int[3]; var b = a; b[0] = 1; a[1] = 2; return b[0]; }", TestName = "SourceReadAfterwards_IsStillShared")]
  [TestCase("public static int X() { var a = new int[3]; int[] b = null; for (int i = 0; i < 2; i++) { b = a; } b[0] = 1; return b[0]; }", TestName = "MoveInALoop_IsStillShared")]
  public void ArrayTheSourceStillNeeds_IsRefused(string methods)
    => Assert.That(Problems(methods), Has.Some.Contain("one array shared by the variables").Or.Some.Contain("PLY1550"));

  [Test]
  public void ParameterGivenToALocal_IsNoMove() {
    var text = Emitted("public static int X(int[] p) { var b = p; b[0] = 1; return b[0]; }");
    Assert.That(text, Does.Contain("let b: &mut [i32] = p;"));
  }

  [Test]
  public void ViewCopiedIntoAnother_IsCopyFromSliceAfterTheLengthCheck() {
    var text = Emitted("public static void X(byte[] a, byte[] b) { new System.ReadOnlySpan<byte>(a).CopyTo(b); }");
    Assert.That(text, Does.Contain("pub fn x(a: &[u8], b: &mut [u8]) {"));
    Assert.That(text, Does.Contain("ply_view_mut(b, 0, a.len() as i32).copy_from_slice(a);"));
  }

  [Test]
  public void ViewThatIsMade_IsBoundBeforeTheTarget() {
    var text = Emitted("public static void X(byte[] a, byte[] b) { new System.ReadOnlySpan<byte>(a, 1, 2).CopyTo(b); }");
    Assert.That(text, Does.Contain("let source = ply_view(a, 1, 2);"));
    Assert.That(text, Does.Contain("ply_view_mut(b, 0, source.len() as i32).copy_from_slice(source)"));
  }

  [Test]
  public void CopyBetweenTwoPartsOfOneArray_IsCopyWithin() {
    var text = Emitted("public static void X(int[] a, int n) { new System.Span<int>(a, 0, n).CopyTo(new System.Span<int>(a).Slice(2)); }");
    Assert.That(text, Does.Contain("let source = ply_view(a, 0, n).len();"));
    Assert.That(text, Does.Contain("let target = ply_view_from(a, 2).len();"));
    Assert.That(text, Does.Contain("panic!(\"ArgumentOutOfRange\");"));
    Assert.That(text, Does.Contain("a.copy_within(..source, 2);"));
  }

  [Test]
  public void CopyWithinWithBoundsThatAreComputed_BindsThemInOrder() {
    var text = Emitted("public static void X(int[] a, int s, int n, int d) { new System.Span<int>(a, s + 1, n * 2).CopyTo(new System.Span<int>(a, d - 1, n)); }");
    Assert.That(text, Does.Contain("let source_start = s.wrapping_add(1);"));
    Assert.That(text, Does.Contain("let source_length = n.wrapping_mul(2);"));
    Assert.That(text, Does.Contain("let target_start = d.wrapping_sub(1);"));
    Assert.That(text, Does.Contain("let target = ply_view(a, target_start, n).len();"));
    Assert.That(text, Does.Contain("source_start as usize..source_start as usize + source,"));
    Assert.That(text, Does.Contain("target_start as usize,"));
  }

  [Test]
  public void CopyOfAWholeVectorIntoItsOwnTail_BorrowsTheVector() {
    var text = Emitted("public static int X() { var a = new int[4]; a[0] = 7; new System.Span<int>(a, 0, 2).CopyTo(new System.Span<int>(a).Slice(2)); return a[2]; }");
    Assert.That(text, Does.Contain("ply_view(&a, 0, 2).len()"));
    Assert.That(text, Does.Contain("a.copy_within(..source, 2);"));
  }

  [Test]
  public void CopyBetweenAViewVariableAndItsArray_IsRefused() {
    var problems = Problems("public static void X(int[] a) { System.Span<int> s = new System.Span<int>(a).Slice(1); new System.Span<int>(a, 0, 2).CopyTo(s); }");
    Assert.That(problems, Has.Some.Contain("'a view copied onto a view of the same array'"));
  }

  [Test]
  public void ViewOfAViewWritten_MakesTheParameterExclusive() {
    var text = Rust("pub fn store_le32(x: u32, d: &mut [u8]) { d[..4].copy_from_slice(&x.to_le_bytes()); }");
    Assert.That(text, Does.Contain("pub fn store_le32(x: u32, d: &mut [u8]) {"));
  }

  [Test]
  public void TwoHalvesOfOneSlice_AreSplitAtMut() {
    var text = Rust("pub fn split_and_write(a: &mut [u32], m: usize) -> u32 { let (l, r) = a.split_at_mut(m); if !l.is_empty() && r.len() > 1 { l[0] = r[1]; r[0] = 9; } l.len() as u32 * 10 + r.len() as u32 }");
    Assert.That(text, Does.Contain("let (l, r) = a.split_at_mut(ply_view(a, 0, m as i32).len());"));
  }

  [TestCase("public static int X(int[] a, int m) { System.Span<int> l = new System.Span<int>(a, 0, m); System.Span<int> r = new System.Span<int>(a).Slice(m); return l[0] + r[0]; }", TestName = "HalvesOnlyRead_AreTwoSharedViews")]
  [TestCase("public static int X(int[] a, int m) { System.Span<int> l = new System.Span<int>(a, 0, m); System.Span<int> r = new System.Span<int>(a).Slice(m + 1); l[0] = 1; return r[0]; }", TestName = "HalvesThatDoNotMeet_AreNoSplit")]
  [TestCase("public static int X(int[] a, int m) { System.Span<int> l = new System.Span<int>(a, 1, m); System.Span<int> r = new System.Span<int>(a).Slice(m); l[0] = 1; return r[0]; }", TestName = "FirstPartNotFromTheStart_IsNoSplit")]
  [TestCase("public static int X(int[] a, int m) { System.Span<int> l = new System.Span<int>(a, 0, m); System.Span<int> r = new System.Span<int>(a).Slice(m); l[0] = 1; l = r; return r[0]; }", TestName = "HalfAssignedLater_IsNoSplit")]
  public void PartsThatAreNoSplit_AreNotSplitAtMut(string methods) {
    var module = RustCrates.Import(methods);
    var files = RustCrates.Check(module).Count == 0 ? RustCrates.ModuleText(RustCrates.Emit(module, level: OptimizationLevel.None)) : "";
    Assert.That(files, Does.Not.Contain("split_at_mut"));
  }

  [Test]
  public void SplitAtAConstant_IsSplitAtMut() {
    var text = Emitted("public static int X(int[] a) { System.Span<int> l = new System.Span<int>(a, 0, 2); System.Span<int> r = new System.Span<int>(a).Slice(2); l[0] = r[0]; return l[1]; }");
    Assert.That(text, Does.Contain("let (l, r) = a.split_at_mut(ply_view(a, 0, 2).len());"));
  }

  [Test]
  public void ValuesSwappedThroughAThird_AllowManualSwap() {
    var files = RustCrates.Emit(RustCrates.Import("public static int X(int a, int b) { int t = a; a = b; b = t; return a * 10 + b; }"), level: OptimizationLevel.None);
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("clippy::manual_swap"));
    Assert.That(RustEmitter.Lints.Closed, Does.Contain("clippy::manual_swap"));
  }

  [Test]
  public void ListMadeOfAnArray_CallsToVecOnTheArray() {
    var text = Emitted("public static int X(int[] a) { var l = new System.Collections.Generic.List<int>(a); l.Add(1); return l.Count; }");
    Assert.That(text, Does.Contain("a.to_vec()"));
    Assert.That(text, Does.Not.Contain("(&a)"));
  }

  [Test]
  public void ParallelLoopWithABreak_KeepsItsLabelOnlyWhereNamed() {
    var text = Emitted("public static int X(int[] a, int[] b) { int s = 0; for (int i = 0; i < System.Math.Min(a.Length, b.Length); i++) { if (a[i] == 0) break; s += b[i]; } return s; }");
    Assert.That(text, Does.Contain("for (&element, &element1) in a.iter().zip(b.iter()) {").Or.Contain("in a.iter().zip(b.iter()) {"));
    Assert.That(text, Does.Contain("break;"));
  }

  [TestCase("public static int X(int[] a, int[] b) { int s = 0; int n = System.Math.Min(a.Length, b.Length); for (int i = 0; i < n; i++) s += a[i] + b[i] + n; return s; }", TestName = "BoundReadInTheBody_StaysAnIndexLoop")]
  [TestCase("public static int X(string[] a, string[] b) { int s = 0; for (int i = 0; i < System.Math.Min(a.Length, b.Length); i++) s += a[i].Length + b[i].Length; return s; }", TestName = "ElementsThatAreNoValues_StayAnIndexLoop")]
  public void MoreLoopsThatAreNoParallelLoops_AreNoZip(string methods) => Assert.That(Emitted(methods), Does.Not.Contain(".zip("));

  [Test]
  public void ViewStepped_MakesTheParameterExclusive() {
    var text = Emitted("public static void X(int[] a) { System.Span<int> s = a; s[0]++; }");
    Assert.That(text, Does.Contain("pub fn x(a: &mut [i32]) {"));
  }

  [Test]
  public void CopyIntoAWholeViewParameter_MeasuresTheParameter() {
    var text = Emitted("public static void X(System.Span<int> a) { a.Slice(1, 2).CopyTo(a); }");
    Assert.That(text, Does.Contain("let target = a.len();"));
    Assert.That(text, Does.Contain("a.copy_within(1..1 + source, 0);"));
  }

  [Test]
  public void SplitOfAVector_SplitsTheVectorItself() {
    var text = Emitted("public static int X(int m) { var a = new int[4]; System.Span<int> l = new System.Span<int>(a, 0, m); System.Span<int> r = new System.Span<int>(a).Slice(m); l[0] = 1; r[0] = 2; return a[0] + a[m]; }");
    Assert.That(text, Does.Contain("let (l, r) = a.split_at_mut(ply_view(&a, 0, m).len());"));
  }

}
