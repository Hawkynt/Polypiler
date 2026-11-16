using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Arrays as safe Rust (#460): who owns an array decides its type - a parameter is borrowed shared or
/// exclusive, an array the function makes is a vector, a table a <c>static</c> - and every access
/// keeps the IR's bounds traps. The acceptance project runs the same shapes against .NET.
/// </summary>
[TestFixture]
public sealed class MemoryEmitTests {

  private static string Emitted(string methods, OptimizationLevel level = OptimizationLevel.None)
    => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: level));

  private static string[] Problems(string methods) => [.. RustCrates.Check(RustCrates.Import(methods)).Select(problem => $"{problem.Code} {problem.Message}")];

  private static readonly IrType _ints = IrType.Array(IrType.I32);

  /// <summary>A module of functions written as IR: what the C# frontend does not import.</summary>
  private static IrModule Module(params IrFunction[] functions) => new("Snippet", functions);

  private static IrFunction Function(string name, IrType result, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] body)
    => new(name, true, result, parameters, locals, new IrBlock(body));

  private static string EmittedIr(IrModule module) => RustCrates.ModuleText(RustCrates.Emit(module, level: OptimizationLevel.None));

  private static string[] ProblemsIr(IrModule module) => [.. RustEmitter.Check(module, RustProfile.Default).Select(problem => problem.Message)];

  [Test]
  public void ReadOnlyParameter_IsASharedSlice_AndItsLoopAnElementLoop() {
    var text = Emitted("public static uint Sum(byte[] data) { uint s = 0; for (int i = 0; i < data.Length; i++) s += data[i]; return s; }");
    Assert.That(text, Does.Contain("pub fn sum(data: &[u8]) -> u32 {"));
    Assert.That(text, Does.Contain("for &element in data {\n        s = s.wrapping_add(u32::from(element));\n    }"));
  }

  [Test]
  public void WrittenParameter_IsAnExclusiveSlice() {
    var text = Emitted("public static void Fill(int[] data, int v) { for (int i = 0; i < data.Length; i++) data[i] = v; }");
    Assert.That(text, Does.Contain("pub fn fill(data: &mut [i32], v: i32) {"));
    Assert.That(text, Does.Contain("for i in 0..data.len() as i32 {\n        data[i as usize] = v;\n    }"));
  }

  [Test]
  public void ParameterWrittenByACallee_IsExclusiveInTheCaller() {
    var text = Emitted("public static void Outer(int[] a) { Inner(a); } private static void Inner(int[] b) { b[0] = 1; }");
    Assert.That(text, Does.Contain("pub fn outer(a: &mut [i32]) {\n    inner(a);\n}"));
    Assert.That(text, Does.Contain("fn inner(b: &mut [i32]) {"));
  }

  [Test]
  public void NewArray_IsAVector_AndANegativeLengthTraps() {
    var text = Emitted("public static int[] Make(int n) { var a = new int[n]; a[0] = 1; return a; }");
    Assert.That(text, Does.Contain("pub fn make(n: i32) -> Vec<i32> {"));
    Assert.That(text, Does.Contain("let mut a: Vec<i32> = vec![0; usize::try_from(n).expect(\"IntegerOverflow\")];"));
  }

  [Test]
  public void NewArrayOfConstantOrUnsignedOrMeasuredLength_NeedsNoCheck() {
    Assert.That(Emitted("public static double[] Make() => new double[3];"), Does.Contain("vec![0.0; 3]"));
    Assert.That(Emitted("public static uint[] Make(uint n) => new uint[n];"), Does.Contain("vec![0; n as usize]"));
    Assert.That(Emitted("public static long[] Copy(long[] a) => new long[a.Length];"), Does.Contain("vec![0; a.len()]"));
  }

  [Test]
  public void ArrayLiteral_IsAVector_TheEmptyOneVecNew_AndATemporaryOneAnArray() {
    Assert.That(Emitted("public static int[] Pair(int a) => new[] { a, -a };"), Does.Contain("vec![a, a.wrapping_neg()]"));
    Assert.That(Emitted("public static ulong[] None() => new ulong[] { };"), Does.Contain("Vec::new()"));
    Assert.That(Emitted("public static int First() => new[] { 'x' }[0];"), Does.Contain("[120u16][0]"));
  }

  [Test]
  public void BorrowedResult_TakesTheLifetimeOfItsParameters() {
    var text = Emitted("public static long[] Choose(bool first, long[] a, long[] b) => first ? a : b;");
    Assert.That(text, Does.Contain("pub fn choose<'a>(first: bool, a: &'a [i64], b: &'a [i64]) -> &'a [i64] {"));
    Assert.That(Emitted("public static int[] Same(int[] a) => a;"), Does.Contain("pub fn same(a: &[i32]) -> &[i32] {"));
  }

  [Test]
  public void BorrowedResultWrittenByTheCaller_IsExclusive() {
    var text = Emitted("public static void Touch(int[] a) { Same(a)[0] = 1; } private static int[] Same(int[] a) => a;");
    Assert.That(text, Does.Contain("fn same(a: &mut [i32]) -> &mut [i32] {"));
    Assert.That(text, Does.Contain("pub fn touch(a: &mut [i32]) {"));
    Assert.That(text, Does.Contain("same(a)[0] = 1;"));
  }

  [Test]
  public void LocalOfAParameter_Borrows() {
    var text = Emitted("public static int F(int[] a, bool b) { int[] x = a; return x[0]; }");
    Assert.That(text, Does.Contain("let x: &[i32] = a;"));
    Assert.That(Emitted("public static int F(int[] a) { int[] x = a; x[0] = 2; return x[1]; }"), Does.Contain("pub fn f(a: &mut [i32]) -> i32 {\n    let x: &mut [i32] = a;"));
  }

  [Test]
  public void ArrayOfAnUnknownForm_IsRefused() {
    var local = new IrLocal("x", _ints);
    var maker = new IrLocal("make", IrType.Function(_ints));
    var other = Function("Other", IrType.Void, [maker], [local], new IrDeclare(local, new IrInvoke(_ints, new IrLocalRef(maker), [])));
    Assert.That(ProblemsIr(Module(other)), Has.Some.Contain("an array of the form Invoke"));
  }

  [Test]
  public void VectorPassed_IsBorrowed_SharedOrExclusive() {
    var text = Emitted("public static int F() { var a = new int[2]; Set(a); return Get(a); } private static void Set(int[] a) { a[1] = 5; } private static int Get(int[] a) => a[1];");
    Assert.That(text, Does.Contain("set(&mut a);"));
    Assert.That(text, Does.Contain("get(&a)"));
  }

  [Test]
  public void TemporaryArrays_AreBorrowedForTheCall() {
    var text = Emitted("public static int F(int n) => Get(new int[n]) + Get(new[] { n }) + Set(new int[2]); private static int Get(int[] a) => a.Length; private static int Set(int[] a) { a[0] = 1; return a[0]; }");
    Assert.That(text, Does.Contain("get(&vec![0; usize::try_from(n).expect(\"IntegerOverflow\")])"));
    Assert.That(text, Does.Contain("get(&[n])"));
    Assert.That(text, Does.Contain("set(&mut vec![0; 2])"));
  }

  [Test]
  public void OneArrayPassedTwiceWhereItIsWritten_CallsACopyOfTheCalleeWithOneParameter() {
    var text = Emitted("public static void F(int[] a) { Copy(a, a); } private static void Copy(int[] s, int[] d) { d[0] = s[1]; }");
    Assert.That(text, Does.Contain("pub fn f(a: &mut [i32]) {\n    copy_aliased01(a);\n}"));
    Assert.That(text, Does.Contain("fn copy_aliased01(s: &mut [i32]) {\n    s[0] = s[1];\n}"));
    Assert.That(text, Does.Not.Contain("fn copy("));
  }

  [Test]
  public void OneArrayPassedTwiceToACalleeThatSwapsItsParameters_IsRefusedAsAliasing() {
    var problems = Problems("public static void F(int[] a) { Copy(a, a); } private static void Copy(int[] s, int[] d) { if (s.Length > 9) { var t = s; s = d; d = t; } d[0] = s[1]; }");
    Assert.That(problems, Has.Some.StartsWith(RustEmitter.MayAlias).And.Contain("views 'a' and 'a' may alias and 'a' is written"));
  }

  [Test]
  public void OneArrayPassedTwiceToBeRead_IsBorrowedTwice()
    => Assert.That(Emitted("public static int F(int[] a) => Sum(a, a); private static int Sum(int[] s, int[] d) => s[0] + d[0];"), Does.Contain("sum(a, a)"));

  [Test]
  public void ArrayNothingWrites_IsCopiedWhereItChangesHands() {
    var text = Emitted("public static int[] F(int[] a, int n) { if (n > 0) a = new int[n]; var b = a; return b; }");
    Assert.That(text, Does.Contain("pub fn f(a: &[i32], n: i32) -> Vec<i32> {\n    let mut a: Vec<i32> = a.to_vec();"));
    Assert.That(text, Does.Contain("    if n > 0 {\n        a = vec![0; usize::try_from(n).expect(\"IntegerOverflow\")];\n    }"));
    Assert.That(Emitted("public static int[] F(int[] a, bool c) => c ? a : new int[1];"), Does.Contain("if c { a.to_vec() } else { vec![0; 1] }"));
  }

  [Test]
  public void ArraysWithoutASafeOwner_AreRefusedByName() {
    Assert.That(Problems("public static int F(int[] a) { a = new int[1]; a[0] = 2; return a[0]; }"), Has.Some.Contain("the parameter 'a' and a new array in one variable"));
    Assert.That(Problems("public static int F() { var a = new int[1]; var b = a; b[0] = 1; return a[0]; }"), Has.Some.Contain("one array shared by the variables 'a' and 'b'"));
  }

  [Test]
  public void ElementStore_EvaluatesAnIndexWithAnEffectFirst() {
    var text = Emitted("public static void F(int[] a, int[] c) { a[Next(c)] = Next(c); } private static int Next(int[] c) => c[0]++;");
    Assert.That(text, Does.Contain("let index: usize = next(c) as usize;").Or.Contain("let index = next(c) as usize;"));
    Assert.That(text, Does.Contain("a[index] = next(c);"));
  }

  [Test]
  public void ElementStoreAtAStepOfTheIndex_StepsBeforeOrAfter() {
    var post = Emitted("public static int F(int[] a, int v) { int w = 0; a[w++] = v; a[++w] = v; return w; }");
    Assert.That(post, Does.Contain("let index = w as usize;\n    w = w.wrapping_add(1);\n    a[index] = v;"));
    Assert.That(post, Does.Contain("w = w.wrapping_add(1);\n    let index1 = w as usize;\n    a[index1] = v;"));
  }

  [Test]
  public void CompoundElementStore_IsACompoundAssignment() {
    var text = Emitted("public static void F(uint[] a, float[] b, int i) { a[i] |= 4; b[i] *= 2; a[i] += 1; }");
    Assert.That(text, Does.Contain("a[i as usize] |= 4;"));
    Assert.That(text, Does.Contain("b[i as usize] *= 2.0;"));
    Assert.That(text, Does.Contain("a[i as usize] = a[i as usize].wrapping_add(1);"));
  }

  [Test]
  public void ElementSteps_EvaluateTheIndexOnce() {
    var text = Emitted("public static int F(int[] a, int i) => a[i]++ + ++a[i]; public static void G(float[] b, int[] c, int i) { b[i]++; c[i + 1]--; }");
    Assert.That(text, Does.Contain("let old = a[i as usize];\n        a[i as usize] = old.wrapping_add(1);\n        old"));
    Assert.That(text, Does.Contain("b[i as usize] += 1.0;"));
    Assert.That(text, Does.Contain("let t0: i32 = i.wrapping_add(1);\n    c[t0 as usize] = c[t0 as usize].wrapping_sub(1);"));
  }

  [Test]
  public void ManualSwap_IsSwap_ButNotWhereAnIndexMayPanic() {
    Assert.That(Emitted("public static void F(int[] a, int i, int j) { int t = a[i]; a[i] = a[j]; a[j] = t; }"), Does.Contain("a.swap(i as usize, j as usize);"));
    Assert.That(Emitted("public static void F(int[] a, int i, int j) { checked { int t = a[i + 1]; a[i + 1] = a[j]; a[j] = t; } }"), Does.Not.Contain("swap"));
    Assert.That(Emitted("public static int F(int[] a, int i, int j) { int t = a[i]; a[i] = a[j]; a[j] = t; return t; }"), Does.Not.Contain("swap"));
  }

  [Test]
  public void CopiesAndFills_CheckEveryRangeFirst() {
    var text = Emitted("public static void F(int[] s, int[] d, short[] h, float[] f) { System.Array.Copy(s, 1, d, 0, 2); System.Array.Copy(d, 0, d, 1, 2); System.Array.Fill(h, (short)3); System.Array.Clear(f); }");
    Assert.That(text, Does.Contain("ply_copy(s, 1, d, 0, 2);"));
    Assert.That(text, Does.Contain("ply_copy_within(d, 0, 1, 2);"));
    Assert.That(text, Does.Contain("h.fill(3);"));
    Assert.That(text, Does.Contain("f.fill(0.0);"));
    Assert.That(text, Does.Contain("fn ply_copy<T: Copy>(source: &[T], from: i32, target: &mut [T], to: i32, length: i32) {"));
    Assert.That(text, Does.Contain("fn ply_copy_within<T: Copy>(data: &mut [T], from: i32, to: i32, length: i32) {"));
    Assert.That(text, Does.Contain("fn ply_range(start: i32, length: i32, size: usize) -> std::ops::Range<usize> {\n    let start = usize::try_from(start).expect(\"IndexOutOfRange\");"));
  }

  [Test]
  public void EndianAccess_ConvertsBytesOfARangeCheckedFirst() {
    var text = Emitted("public static uint F(byte[] b, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(System.MemoryExtensions.AsSpan(b, i)); public static void G(byte[] b, int i, double x) => System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(System.MemoryExtensions.AsSpan(b, i, 8), x * 2); public static void H(byte[] b, float x) => System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(System.MemoryExtensions.AsSpan(b, 0), x);");
    Assert.That(text, Does.Contain("u32::from_le_bytes(b[ply_range(i, 4, b.len())].try_into().expect(\"4 bytes\"))"));
    Assert.That(text, Does.Contain("pub fn g(b: &mut [u8], i: i32, x: f64) {\n    let value = x * 2.0;\n    let at = ply_range(i, 8, b.len());\n    b[at].copy_from_slice(&value.to_be_bytes());\n}"));
    Assert.That(text, Does.Contain("let at = ply_range(0, 4, b.len());\n    b[at].copy_from_slice(&x.to_le_bytes());"));
  }

  [Test]
  public void ElementLoops_OverVectorsTablesAndExclusiveSlices() {
    Assert.That(Emitted("static readonly int[] T = { 1, 2 }; public static int F() { int s = 0; foreach (var x in T) s += x; return s; }"), Does.Contain("for &x in &T {"));
    Assert.That(Emitted("public static int F(int n) { var a = new int[n]; int s = 0; foreach (var x in a) s += x; return s; }"), Does.Contain("for &x in &a {"));
    Assert.That(Emitted("public static int F(int[] a) { a[0] = 1; int s = 0; foreach (var x in a) s += x; return s; }"), Does.Contain("for &x in a.iter() {"));
  }

  [Test]
  public void LoopsThatWriteOrReadOtherElements_KeepTheirIndex() {
    Assert.That(Emitted("public static void F(int[] a) { for (int i = 0; i < a.Length; i++) a[i] = a[i] + 1; }"), Does.Not.Contain("for &"));
    Assert.That(Emitted("public static int F(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) s += a[i] + i; return s; }"), Does.Not.Contain("for &"));
    Assert.That(Emitted("public static int F(int[] a, int[] b) { int s = 0; for (int i = 0; i < a.Length; i++) s += b[i]; return s; }"), Does.Not.Contain("for &"));
    Assert.That(Emitted("public static void F(int[] a) { for (int i = 0; i < a.Length; i++) G(a, a[i]); } private static void G(int[] a, int v) { a[0] = v; }"), Does.Not.Contain("for &"));
  }

  [Test]
  public void ConstantTable_IsAStaticFilledShortElementsPerLine() {
    var values = string.Join(", ", Enumerable.Range(0, 40).Select(value => (value * 100003).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    var text = Emitted($"static readonly int[] T = {{ {values} }}; public static int F(int i) => T[i];");
    Assert.That(text, Does.StartWith("static T: [i32; 40] = [\n    0, 100003, 200006,"));
    Assert.That(text.Split('\n').Max(line => line.Length), Is.LessThan(RustPrinter.MaxWidth));
  }

  [Test]
  public void NewSyntax_PrintsAndReadsBack() {
    RsExpression P(string name) => new RsPath(name);
    var body = new RsBlock([
      new RsLet("a", true, "Vec<u8>", new RsMacro("vec", [new RsArray([new RsLiteral("0")]) { Count = P("n") }]) { Bracketed = true }),
      new RsLet("b", false, "&mut [u8]", new RsReference(true, new RsIndex(P("a"), new RsRange(null, P("n"), false)))),
      new RsLet("c", false, null, new RsReference(false, new RsIndex(P("a"), new RsRange(P("n"), null, false)))),
      new RsLet("d", false, null, new RsField(new RsCall(P("g"), []), "start")),
      new RsExpressionStatement(new RsFor(null, "&x", P("c"), RsBlock.Empty))
    ], new RsIndex(new RsArray([new RsLiteral("1u8")]), new RsLiteral("0")));
    var file = new RsFile([], [], [
      new RsConst("T", "", "[u8; 2]", new RsArray([new RsLiteral("1"), new RsLiteral("2")])) { IsStatic = true },
      new RsFunction("f", "pub", [new RsParameter("x", "&'a [i64]"), new RsParameter("n", "usize")], "&'a [i64]", body) { Generics = "<'a, T: Copy>" }
    ]);
    var text = RustPrinter.Print(file);
    Assert.That(text, Does.Contain("static T: [u8; 2] = [1, 2];"));
    Assert.That(text, Does.Contain("pub fn f<'a, T: Copy>(x: &'a [i64], n: usize) -> &'a [i64] {"));
    Assert.That(text, Does.Contain("let mut a: Vec<u8> = vec![0; n];\n    let b: &mut [u8] = &mut a[..n];\n    let c = &a[n..];\n    let d = g().start;"));
    Assert.That(text, Does.Contain("for &x in c {}\n    [1u8][0]\n"));
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
    var renamed = RustSyntaxRewriter.Rewrite(file, node => node is RsPath { Name: "n" } ? P("m") : node);
    Assert.That(RustPrinter.Print(renamed), Does.Contain("vec![0; m]").And.Contain("&mut a[..m]").And.Contain("&a[m..]"));
  }

  [Test]
  public void BlockStatement_EndsAtItsBlock() {
    const string text = "fn f(a: i32) -> i32 {\n    if a > 0 {\n        return 1;\n    }\n    -1\n}\n";
    var read = RustSyntaxReader.Read(text);
    Assert.That(((RsFunction)read.Items[0]).Body.Tail, Is.Not.TypeOf<RsBinary>());
    Assert.That(RustPrinter.Print(read), Is.EqualTo(text));
  }

}
