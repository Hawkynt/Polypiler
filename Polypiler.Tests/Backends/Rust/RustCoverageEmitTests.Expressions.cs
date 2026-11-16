using NUnit.Framework;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Expressions: the operators of enums on their integer, the generic equality and order at each kind
/// of type argument, the lazy operators an operand decides alone, the tests of function values and
/// arrays for null, the numbers as text, and the module state stepped in place.
/// </summary>
public sealed partial class RustCoverageEmitTests {

  private const string Enums = "public enum E : sbyte { A = -1, B = 2 } public enum F : short { A = -1, B = 2 } public enum G : long { A = -1, B = 2 } public enum H : ulong { A = 1 } ";

  [Test]
  public void EnumOperators_WorkOnTheInteger() {
    var text = EmittedFile("[System.Flags] public enum E { A = 1, B = 2 } public static class S { public static E F(E e) => ~e; public static E G(E a, E b) => a ^ b; public static E H(E a, E b) => a | b; }");
    Assert.That(text, Does.Contain("pub fn f(e: E) -> E {\n    E(!e.0)\n}"));
    Assert.That(text, Does.Contain("pub fn g(a: E, b: E) -> E {\n    E(a.0 ^ b.0)\n}"));
    Assert.That(text, Does.Contain("pub fn h(a: E, b: E) -> E {\n    E(a.0 | b.0)\n}"));
  }

  [Test]
  public void EnumText_OfASignedType_OrdersByTheUnsignedBits() {
    var text = EmittedFile(Enums + "public static class S { public static string F1(E e) => e.ToString(); public static string F2(F e) => e.ToString(); public static string F3(G e) => e.ToString(); public static bool F4(H e) => System.Enum.IsDefined(e); }");
    Assert.That(text, Does.Contain("static E_VALUES: [u64; 2] = [2, 255];"));
    Assert.That(text, Does.Contain("static G_VALUES: [u64; 2] = [2, 18446744073709551615];"));
    Assert.That(text, Does.Contain("e.0 as u8 as u64,"));
    Assert.That(text, Does.Contain("e.0 as u16 as u64,"));
    Assert.That(text, Does.Contain("ply_enum_text(e.0 as u64, e.0.to_string(), &G_VALUES, &G_NAMES, false)"));
    Assert.That(text, Does.Contain("pub fn f4(e: H) -> bool {\n    H_VALUES.contains(&e.0)\n}"));
  }

  [Test]
  public void EnumLists_OfAnEnumWithoutMembers_AreEmptyVectors() {
    var text = EmittedFile("public enum K { } public static class S { public static int F() { var n = System.Enum.GetNames<K>(); var v = System.Enum.GetValues<K>(); return n.Length + v.Length; } }");
    Assert.That(text, Does.Contain("Vec::new()"));
  }

  [Test]
  public void GenericEquality_AndOrder_AreThoseOfTheTypeArgument() {
    var text = EmittedFile("public static class S { static bool Eq<T>(T a, T b) where T : System.IEquatable<T> => a.Equals(b); static int Cmp<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b); "
      + "public static bool F(float a, float b) => Eq(a, b); public static int G(float a, float b) => Cmp(a, b); public static int I(bool a, bool b) => Cmp(a, b); "
      + "public static int J(ulong a, ulong b) => Cmp(a, b); public static bool K(bool a, bool b) => Eq(a, b); }");
    Assert.That(text, Does.Contain("fn eq_f32(a: f32, b: f32) -> bool {\n    a == b || (a.is_nan() && b.is_nan())\n}"));
    Assert.That(text, Does.Contain("fn cmp_f32(a: f32, b: f32) -> i32 {\n    ply_compare_f32(a, b)\n}"));
    Assert.That(text, Does.Contain("fn cmp_bool(a: bool, b: bool) -> i32 {\n    a.cmp(&b) as i32\n}"));
    Assert.That(text, Does.Contain("fn cmp_u64(a: u64, b: u64) -> i32 {\n    a.cmp(&b) as i32\n}"));
    Assert.That(text, Does.Contain("fn eq_bool(a: bool, b: bool) -> bool {\n    a == b\n}"));
  }

  [Test]
  public void GenericEquality_OfObjects_IsTheRecordsFieldsOrTheIdentity() {
    Assert.That(EmittedFile("public record R(int X); public static class S { static bool Eq<T>(T a, T b) where T : System.IEquatable<T> => a.Equals(b); public static bool H(R a, R b) => Eq(a, b); }"),
      Does.Contain("fn eq_r(a: Option<Rc<R>>, b: Option<Rc<R>>) -> bool {\n    a.as_ref() == b.as_ref()\n}"));
    Assert.That(EmittedFile("public sealed class C { public int X; } public static class S { static bool Eq<T>(T a, T b) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b); public static bool H(C a, C b) => Eq(a, b); }"),
      Does.Contain("fn eq_c(a: Option<Rc<C>>, b: Option<Rc<C>>) -> bool {\n    a.as_ref().map(Rc::as_ptr) == b.as_ref().map(Rc::as_ptr)\n}"));
  }

  [TestCase("public static bool F(bool a) => false && a;", "false", TestName = "LazyAnd_OfALeadingFalse_IsFalse")]
  [TestCase("public static bool F(bool a) => true || a;", "true", TestName = "LazyOr_OfALeadingTrue_IsTrue")]
  [TestCase("public static bool F(int a) => a > 1 && false;", "false", TestName = "LazyAnd_OfATrailingFalse_IsFalse")]
  [TestCase("public static bool F(int a) => a > 1 || true;", "true", TestName = "LazyOr_OfATrailingTrue_IsTrue")]
  public void LazyOperator_DecidedByAConstant_IsTheConstant(string method, string value)
    => Assert.That(Emitted(method, OptimizationLevel.None), Does.Contain($" -> bool {{\n    {value}\n}}"));

  [Test]
  public void NegatedFloat_OfANegatedOperand_KeepsBothSigns()
    => Assert.That(Emitted("public static double F(double x) => -(-x);", OptimizationLevel.None), Does.Contain("    -(-x)\n"));

  [TestCase("public static bool F(System.Func<int> f) => f is null;", "f.is_none()", TestName = "FunctionValue_IsNull_IsIsNone")]
  [TestCase("public static bool F(System.Func<int> f) => f is not null;", "f.is_some()", TestName = "FunctionValue_IsNotNull_IsIsSome")]
  [TestCase("public static bool F(System.Func<int> f) => !(f == null);", "f.is_some()", TestName = "FunctionValue_NegatedEqualityToNull_IsIsSome")]
  [TestCase("public static bool F(System.Func<int> f) => null == f;", "f.is_none()", TestName = "FunctionValue_NullOnTheLeft_IsIsNone")]
  public void FunctionValue_ComparedWithNull_IsATestOfTheOption(string method, string test)
    => Assert.That(Emitted(method), Does.Contain($"pub fn f(f: Option<Rc<dyn Fn() -> i32>>) -> bool {{\n    {test}\n}}"));

  [TestCase("return f == null ? 0 : f();", "    if false { 0 } else { f() }\n", TestName = "LocalFunction_EqualToNull_IsFalse")]
  [TestCase("return f is null ? 0 : f();", "    if false { 0 } else { f() }\n", TestName = "LocalFunction_IsNull_IsFalse")]
  [TestCase("if (f != null) return f(); return 0;", "    if true {\n        return f();\n    }\n    0\n", TestName = "LocalFunction_UnequalToNull_IsTrue")]
  public void LocalFunction_ComparedWithNull_IsNeverNull(string body, string expected)
    => Assert.That(Emitted($"public static int F() {{ System.Func<int> f = () => 1; {body} }}"),
      Does.Contain("    fn f() -> i32 {\n        1\n    }\n" + expected));

  // Arrays that may be null.

  [Test]
  public void NullableArray_CopiedIntoAVariable_StaysABorrow()
    => Assert.That(Emitted("public static int F(int[] a) { if (a == null) return 0; int[] b = a; return b.Length; }"),
      Does.Contain("    let b: Option<&[i32]> = a;\n    ply_non_null(&b).len() as i32\n"));

  [Test]
  public void NullableArray_MadeByACall_TrapsWhereItIsUsed() {
    const string Make = "static int[] Make(int n) => n > 0 ? new int[n] : null; ";
    Assert.That(Emitted(Make + "public static int F(int n) => Make(n).Length;"), Does.Contain("make(n).expect(\"NullReference\").len() as i32"));
    Assert.That(Emitted(Make + "public static int F(int n) => Make(n)[0];"), Does.Contain("make(n).expect(\"NullReference\")[0]"));
    Assert.That(Emitted(Make + "public static bool F(int n) => Make(n) == null;"), Does.Contain("make(n).is_none()"));
  }

  [Test]
  public void NullableArray_PassedOn_IsSomeOfTheBorrowOrNone() {
    const string Count = "static int Count(int[] a) => a == null ? 0 : a.Length; ";
    Assert.That(Emitted(Count + "public static int F(int[] b) { int[] c = b; return Count(c) + Count(null); }"), Does.Contain("count(Some(c)).wrapping_add(count(None))"));
    Assert.That(Emitted(Count + "public static int F(int n) => Count(new int[n]);"), Does.Contain("count(Some(&vec![0; usize::try_from(n).expect(\"IntegerOverflow\")]))"));
    Assert.That(Emitted(Count + "public static int F(int[] a, bool c) => Count(c ? a : null);"), Does.Contain("count(if c { Some(a) } else { None })"));
  }

  [TestCase("public static int[] F(int[] a, bool c) { int[] b = c ? a : null; return b; }", "a borrowed array result that may be null", TestName = "NullableArray_ReturnedBorrowed_IsRefused")]
  [TestCase("static int[] Make(int n) => n > 0 ? new int[n] : null; static int Count(int[] a) => a == null ? 0 : a.Length; public static int F(int n) => Count(Make(n));",
    "a borrow of an array that may be null and is made in place", TestName = "NullableArray_MadeInPlaceAndBorrowed_IsRefused")]
  [TestCase("public static bool F() => new int[3] == null;", "a test for null of an array that is made", TestName = "NullTest_OfAnArrayThatIsMade_IsRefused")]
  public void NullableArray_ThatRustCannotHold_IsRefused(string methods, string what)
    => Assert.That(Problems(methods), Has.Some.StartWith($"PLY1550 '{what}' cannot be expressed in Rust profile 'rust2024'"));

  // Numbers as text.

  [Test]
  public void NumberText_OfEachFormat_IsItsHelperOrFormat() {
    var text = Emitted("public static string F(int x) => x.ToString(\"X4\") + x.ToString(\"x\") + x.ToString(\"D3\") + System.Convert.ToString(x, 2) + System.Convert.ToString(x, 8);");
    Assert.That(text, Does.Contain("&ply_utf16(&format!(\"{:04X}\", x))[..],\n        &ply_utf16(&format!(\"{:x}\", x))[..],\n        &ply_signed_digits(i128::from(x), 3)[..],\n        &ply_utf16(&format!(\"{:b}\", x))[..],\n        &ply_utf16(&format!(\"{:o}\", x))[..],"));
    Assert.That(Emitted("public static string F(float x) => x.ToString();"), Does.Contain("pub fn f(x: f32) -> Vec<u16> {\n    ply_format_f32(x)\n}"));
    Assert.That(Emitted("public static string F(double x) => x.ToString(\"F2\");"), Does.Contain("pub fn f(x: f64) -> Vec<u16> {\n    ply_fixed(x, 2)\n}"));
  }

  // Module state.

  [Test]
  public void Globals_OfFloatsAndStrings_AreMutexes() {
    var floats = Emitted("static double d = 1.5; static float e = 2; static double z; public static double F() => d + e + z;");
    Assert.That(floats, Does.Contain("static D: Mutex<f64> = Mutex::new(1.5);\n\nstatic E: Mutex<f32> = Mutex::new(2.0);\n\nstatic Z: Mutex<f64> = Mutex::new(0.0);"));
    var strings = Emitted("static long t = 1L << 40; static string s = \"abc\"; static string u = \"\"; public static long F() => t + s.Length + u.Length;");
    Assert.That(strings, Does.Contain("static T: AtomicI64 = AtomicI64::new(1099511627776);"));
    Assert.That(strings, Does.Contain("static S: LazyLock<Mutex<Vec<u16>>> = LazyLock::new(|| Mutex::new(ply_utf16(\"abc\")));"));
    Assert.That(strings, Does.Contain("static U: Mutex<Vec<u16>> = Mutex::new(Vec::new());"));
    var arrays = Emitted("static int[] list = { 1, 2, 3 }; static string[] names = { \"x\", \"\" }; public static int F() => list[0] + names[0].Length;");
    Assert.That(arrays, Does.Contain("static NAMES: LazyLock<Mutex<[Vec<u16>; 2]>> =\n    LazyLock::new(|| Mutex::new([ply_utf16(\"x\"), Vec::new()]));"));
  }

  [Test]
  public void GlobalSteps_AsStatementsAndValues_LoadAndStore() {
    var wrapping = Emitted("static int c; public static int F() { c++; ++c; c--; return c++ + --c; }");
    Assert.That(wrapping, Does.Contain("    C.store(C.load(Ordering::Relaxed).wrapping_sub(1), Ordering::Relaxed);"));
    Assert.That(wrapping, Does.Contain("C.fetch_add(1, Ordering::Relaxed)\n        .wrapping_add(C.fetch_sub(1, Ordering::Relaxed).wrapping_sub(1))"));
    var trapping = Emitted("static int c; public static int F() { checked { c++; return --c + c--; } }");
    Assert.That(trapping, Does.Contain("let new = old.checked_sub(1).expect(\"IntegerOverflow\");\n        C.store(new, Ordering::Relaxed);\n        new\n    })"));
    Assert.That(trapping, Does.Contain("C.store(new, Ordering::Relaxed);\n        old\n    })"));
  }

}
