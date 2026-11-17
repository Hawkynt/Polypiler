using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Generic methods of C# as Rust generics with trait bounds (#1394): the bounds the body needs, the
/// instantiations a bound gives another meaning kept as functions of their own, the stand-in types,
/// <c>where</c> clauses and the type arguments a call names where Rust cannot infer them.
/// </summary>
[TestFixture]
public sealed class GenericEmitTests {

  private static string Emitted(string methods, OptimizationLevel level = OptimizationLevel.None)
    => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: level));

  private const string Max = "public static T Max<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b) >= 0 ? a : b; ";

  private const string IndexOf = "static int IndexOf<T>(T[] a, T v) where T : System.IEquatable<T> { for (var i = 0; i < a.Length; ++i) if (a[i].Equals(v)) return i; return -1; } ";

  [Test]
  public void Max_IsOneGenericFunction_CalledWithU8AndI64() {
    var text = Emitted(Max + "public static byte MaxByte(byte a, byte b) => Max(a, b); public static long MaxLong(long a, long b) => Max(a, b);");
    Assert.That(text, Does.Contain("pub fn max<T: Ord + Copy>(a: T, b: T) -> T {\n    if a >= b { a } else { b }\n}"));
    Assert.That(text, Does.Contain("pub fn max_byte(a: u8, b: u8) -> u8 {\n    max(a, b)\n}"));
    Assert.That(text, Does.Contain("pub fn max_long(a: i64, b: i64) -> i64 {\n    max(a, b)\n}"));
    Assert.That(text, Does.Not.Contain("max_u8").And.Not.Contain("max_i64").And.Not.Contain("cmp("));
  }

  [Test]
  public void Max_OverDouble_KeepsItsOwnFunction_WithDotNetsOrder() {
    var text = Emitted(Max + "public static double F(double a, double b) => Max(a, b); public static int G(int a, int b) => Max(a, b); public static bool H(bool a, bool b) => Max(a, b);");
    Assert.That(text, Does.Contain("pub fn max<T: Ord + Copy>(a: T, b: T) -> T"));
    Assert.That(text, Does.Contain("pub fn max_f64(a: f64, b: f64) -> f64 {\n    if ply_compare_f64(a, b) >= 0 { a } else { b }\n}"));
    Assert.That(text, Does.Contain("max_f64(a, b)"));
    Assert.That(text, Does.Contain("pub fn g(a: i32, b: i32) -> i32 {\n    max(a, b)\n}"));
    Assert.That(text, Does.Contain("pub fn h(a: bool, b: bool) -> bool {\n    max(a, b)\n}"));
    // The generic definition stands where the source's method stands, before the copy for doubles.
    Assert.That(text.IndexOf("pub fn max<", System.StringComparison.Ordinal), Is.LessThan(text.IndexOf("pub fn max_f64", System.StringComparison.Ordinal)));
  }

  [TestCase("a.CompareTo(b) < 0", "a < b")]
  [TestCase("a.CompareTo(b) <= 0", "a <= b")]
  [TestCase("a.CompareTo(b) == 0", "a == b")]
  [TestCase("a.CompareTo(b) != 0", "a != b")]
  [TestCase("0 < a.CompareTo(b)", "a > b")]
  [TestCase("0 >= a.CompareTo(b)", "a <= b")]
  [TestCase("0 > a.CompareTo(b)", "a < b")]
  [TestCase("0 <= a.CompareTo(b)", "a >= b")]
  [TestCase("0 == a.CompareTo(b)", "a == b")]
  public void SignOfCompareTo_IsTheComparisonOfTheValues(string test, string comparison) {
    var text = Emitted($"static bool Test<T>(T a, T b) where T : System.IComparable<T> => {test}; public static bool F(short a, short b) => Test(a, b);");
    Assert.That(text, Does.Contain($"fn test<T: Ord + Copy>(a: T, b: T) -> bool {{\n    {comparison}\n}}"));
  }

  [Test]
  public void Equals_IsEq_AndAFloatingPointInstantiationKeepsItsOwnFunction() {
    var text = Emitted(IndexOf + "public static int F(int[] a, int v) => IndexOf(a, v); public static int G(double[] a, double v) => IndexOf(a, v); public static int H(ulong[] a, ulong v) => IndexOf(a, v);");
    Assert.That(text, Does.Contain("fn index_of<T: Eq + Copy>(a: &[T], v: T) -> i32 {"));
    Assert.That(text, Does.Contain("if a[i as usize] == v {"));
    Assert.That(text, Does.Contain("fn index_of_f64(a: &[f64], v: f64) -> i32 {"));
    Assert.That(text, Does.Contain("ply_same_value_f64(a[i as usize], v)"));
    Assert.That(text, Does.Contain("pub fn h(a: &[u64], v: u64) -> i32 {\n    index_of(a, v)\n}"));
  }

  [Test]
  public void Default_IsTheDefaultOfTheParameter_AndACallNamesItsTypeArgument() {
    var text = Emitted("static T Def<T>() => default(T); static T Make<T>() where T : new() => new T(); "
      + "public static int F() => Def<int>(); public static double G() => Def<double>() + Make<double>(); public static bool H() => Def<bool>();");
    Assert.That(text, Does.Contain("fn def<T: Default + Copy>() -> T {\n    T::default()\n}"));
    Assert.That(text, Does.Contain("fn make<T: Default + Copy>() -> T {\n    T::default()\n}"));
    Assert.That(text, Does.Contain("def::<i32>()"));
    Assert.That(text, Does.Contain("def::<f64>() + make::<f64>()"));
    Assert.That(text, Does.Contain("def::<bool>()"));
  }

  [Test]
  public void ValuesOnlyHeld_NeedCopy_AndAnArrayWrittenIsAMutableSlice() {
    var text = Emitted("static void Swap<T>(T[] a, int i, int j) { var t = a[i]; a[i] = a[j]; a[j] = t; } "
      + "static T Last<T>(T[] a) { var t = a[0]; for (var i = 1; i < a.Length; ++i) t = a[i]; return t; } "
      + "public static int F() { var a = new[] { 1, 2 }; Swap(a, 0, 1); return a[0] + Last(a); } public static double G(double[] a) { Swap(a, 0, 1); return a[0] + Last(a); }");
    Assert.That(text, Does.Contain("fn swap<T: Copy>(a: &mut [T], i: i32, j: i32) {\n    a.swap(i as usize, j as usize);\n}"));
    Assert.That(text, Does.Contain("fn last<T: Copy>(a: &[T]) -> T {"));
    Assert.That(text, Does.Contain("let mut t: T = a[0];"));
    Assert.That(text, Does.Not.Contain("swap_i32").And.Not.Contain("swap_f64").And.Not.Contain("last_f64"));
  }

  [Test]
  public void TwoParameters_HaveAWhereClause() {
    var text = Emitted("static T First<T, U>(T a, U b) => a; public static int F(int a, double b) => First(a, b); public static long G(long a, bool b) => First(a, b);");
    Assert.That(text, Does.Contain("fn first<T, U>(a: T, _b: U) -> T\nwhere\n    T: Copy,\n    U: Copy,\n{\n    a\n}"));
  }

  [Test]
  public void ThreeBounds_HaveAWhereClause() {
    var text = Emitted("static T Floor<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b) > 0 ? a : default(T); public static int F(int a, int b) => Floor(a, b);");
    Assert.That(text, Does.Contain("fn floor<T>(a: T, b: T) -> T\nwhere\n    T: Ord + Default + Copy,\n{\n    if a > b { a } else { T::default() }\n}"));
  }

  [Test]
  public void EmptyBodyWithAWhereClause_HasItsBracesOnTwoLines() {
    var text = Emitted("static void Nop<T, U>(T a, U b) { } public static void F(int a, double b) => Nop(a, b);");
    Assert.That(text, Does.Contain("fn nop<T, U>(_a: T, _b: U)\nwhere\n    T: Copy,\n    U: Copy,\n{\n}"));
  }

  [Test]
  public void PrivateDefinition_IsPrivate_AndOptimizedModulesKeepIt() {
    var text = Emitted("static T Id<T>(T a) => a; public static int F(int a) => Id(a) + Id(a + 1); public static long G(long a) => Id(a);", OptimizationLevel.Default);
    Assert.That(text, Does.Not.Contain("pub fn id"));
  }

  // The instantiations and the definitions that stay one function per instantiation.

  [TestCase("static int Cmp<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b); public static int F(byte a, byte b) => Cmp(a, b); public static int G(long a, long b) => Cmp(a, b);",
    "fn cmp_u8(", "fn cmp_i64(", TestName = "The value of CompareTo, the difference of two bytes in .NET")]
  [TestCase("static bool Big<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b) > 1; public static bool F(byte a, byte b) => Big(a, b); public static bool G(int a, int b) => Big(a, b);",
    "fn big_u8(", "fn big_i32(", TestName = "CompareTo against another number than zero")]
  [TestCase("public sealed class Box { public int V; } public static class S2 { static T Def<T>() => default(T); public static bool F() => Def<Box>() == null; public static int G() => Def<int>(); }",
    "fn def_box(", "fn def<T: Default + Copy>()", TestName = "A class as type argument")]
  [TestCase("static int Apply(System.Func<int, int> f, int v) => f(v); static T Twice<T>(T a) => Apply(x => x + 1, 1) > 0 ? a : a; public static int F(int a) => Twice(a); public static long G(long a) => Twice(a);",
    "fn twice_i32(", "fn twice_i64(", TestName = "A lambda in the body")]
  [TestCase("static bool Same<T>(T a, T b, int c) where T : System.IComparable<T> => a.CompareTo(b) == c; public static bool F(byte a, byte b) => Same(a, b, 0); public static bool G(int a, int b) => Same(a, b, 0);",
    "fn same_u8(", "fn same_i32(", TestName = "CompareTo against a variable")]
  [TestCase("static int Count<T>(T a) => new System.Collections.Generic.List<T>().IndexOf(a); public static int F(int a) => Count(a); public static int G(long a) => Count(a);",
    "fn count_i32(", "fn count_i64(", TestName = "A list of the parameter")]
  [TestCase("static int Count<T>(T a) => new System.Collections.Generic.Dictionary<T, int>().ContainsKey(a) ? 1 : 0; public static int F(int a) => Count(a); public static int G(long a) => Count(a);",
    "fn count_i32(", "fn count_i64(", TestName = "A map keyed by the parameter")]
  [TestCase("static bool Has<T>(T a) where T : struct => ((T?)a).HasValue; public static bool F(int a) => Has(a); public static bool G(long a) => Has(a);",
    "fn has_i32(", "fn has_i64(", TestName = "An optional value of the parameter")]
  [TestCase("static T Max<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b) >= 0 ? a : b; public static double F(double a, double b) => Max(a, b); public static float G(float a, float b) => Max(a, b);",
    "fn max_f64(", "fn max_f32(", TestName = "Only floating-point instantiations")]
  [TestCase("static int Size<T>() { var x = default(T); return 4; } public static int F() => Size<int>(); public static int G() => Size<long>();",
    "fn size_i32(", "fn size_i64(", TestName = "A parameter the signature does not name")]
  [TestCase("static T Inner<T>(T a) => a; static T Outer<T>(T a) => Inner(a); public static int F(int a) => Outer(a); public static long G(long a) => Outer(a);",
    "fn outer_i32(", "fn inner<T: Copy>", TestName = "A call of another generic method")]
  [TestCase("static T Pick<T>(T a, sbyte b, byte c, short d, ushort e, int f, uint g, long h, ulong i) => a; public static int F(int a) => Pick(a, 1, 2, 3, 4, 5, 6, 7, 8); public static bool G(bool a) => Pick(a, 1, 2, 3, 4, 5, 6, 7, 8);",
    "fn pick_i32(", "fn pick_bool(", TestName = "Every integer type used, no stand-in left")]
  [TestCase("static string Name<T>(T a) => \"x\"; public static string F(int a) => Name(a); public static string G(long a) => Name(a);",
    "fn name_i32(", "fn name_i64(", TestName = "A string in the body")]
  public void Definition_StaysOneFunctionPerInstantiation(string methods, string first, string second) {
    var module = methods.Contains("class S2", System.StringComparison.Ordinal) ? PrototypeFixture.Import("Snippet.cs", methods) : RustCrates.Import(methods);
    Assert.That(RustCrates.Check(module), Is.Empty);
    var text = RustCrates.ModuleText(RustCrates.Emit(module, level: OptimizationLevel.None));
    Assert.That(text, Does.Contain(first));
    Assert.That(text, Does.Contain(second));
  }

  [Test]
  public void BorrowedArrayResult_HasItsLifetimeBeforeTheParameters() {
    var text = Emitted("static T[] Longer<T>(T[] a, T[] b) => a.Length >= b.Length ? a : b; public static int F(int[] a, int[] b) => Longer(a, b)[0]; public static double G(double[] a, double[] b) => Longer(a, b)[0];");
    Assert.That(text, Does.Contain("fn longer<'a, T: Copy>(a: &'a [T], b: &'a [T]) -> &'a [T] {"));
  }

  [Test]
  public void SevenIntegerTypesUsed_LeaveTheLastOneAsStandIn() {
    var text = Emitted("static T Pick<T>(T a, byte c, short d, ushort e, int f, uint g, long h, ulong i) => a; public static int F(int a) => Pick(a, 2, 3, 4, 5, 6, 7, 8); public static bool G(bool a) => Pick(a, 2, 3, 4, 5, 6, 7, 8);");
    Assert.That(text, Does.Contain("fn pick<T: Copy>(a: T, _c: u8, _d: i16, _e: u16, _f: i32, _g: u32, _h: i64, _i: u64) -> T {\n    a\n}"));
  }

  [Test]
  public void ArrayThatMayBeNull_KeepsItsInstantiationApart() {
    var text = Emitted(IndexOf + "static int[] None() { int[] a = null; return a; } public static int F() { var a = None(); return IndexOf(a, 1); } public static int G(long[] a) => IndexOf(a, 2L);");
    Assert.That(text, Does.Contain("fn index_of_i32(a: Option<&[i32]>, v: i32) -> i32"));
    Assert.That(text, Does.Contain("fn index_of<T: Eq + Copy>(a: &[T], v: T) -> i32"));
  }

  [Test]
  public void Exports_NameTheGenericFunction_WithTheTypeArgumentsWhereRustCannotInferThem() {
    var module = RustCrates.Prepared(RustCrates.Import(Max + "public static T Def<T>() => default(T); public static byte F(byte a, byte b) => Max(a, b); public static long G() => Def<long>() + Max(1L, 2L);"));
    var exports = RustEmitter.Exports(module, RustProfile.Default);
    Assert.That(exports["Max<u8>"], Is.EqualTo("snippet::max"));
    Assert.That(exports["Max<i64>"], Is.EqualTo("snippet::max"));
    Assert.That(exports["Def<i64>"], Is.EqualTo("snippet::def::<i64>"));
    Assert.That(exports["F"], Is.EqualTo("snippet::f"));
  }

  // The syntax: a where clause and the type arguments of a call read back as they are printed.

  [TestCase("fn f<T, U>(a: T, b: U) -> T\nwhere\n    T: Ord + Copy,\n    U: Copy,\n{\n    a\n}\n")]
  [TestCase("fn f<T, U>(a: T, b: U)\nwhere\n    T: Copy,\n    U: Copy,\n{\n}\n")]
  [TestCase("fn f<'a, T: Copy>(a: &'a [T], b: &'a [T]) -> &'a [T] {\n    a\n}\n")]
  [TestCase("fn f() -> i32 {\n    def::<i32>() + pair::<u8, i64>(1, 2)\n}\n")]
  public void Reader_ReadsWhatThePrinterWritesBack(string text) => Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));

  [Test]
  public void Printer_BreaksTheParametersBeforeTheWhereClause_OfALongSignature() {
    var parameters = Enumerable.Range(0, 8).Select(index => new RsParameter($"parameter{index}", "T")).ToList();
    var function = new RsFunction("f", "pub", parameters, "T", new RsBlock([], new RsPath("parameter0"))) { Generics = "<T, U>", Where = ["T: Copy", "U: Copy"] };
    Assert.That(RustPrinter.Print(new RsFile([], [], [function])), Does.StartWith("pub fn f<T, U>(\n    parameter0: T,\n").And.Contain("    parameter7: T,\n) -> T\nwhere\n    T: Copy,\n    U: Copy,\n{\n    parameter0\n}\n"));
  }

}
