using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Optional values and arrays that may be null as Rust writes them: <c>Option&lt;T&gt;</c> of a value,
/// <c>Option&lt;&amp;[T]&gt;</c> and <c>Option&lt;Vec&lt;T&gt;&gt;</c> of an array the module tests for null,
/// the operand a frontend evaluates once bound before its statement, a choice of truth values as the
/// lazy operator it is, and the lints such code needs.
/// </summary>
[TestFixture]
public sealed class OptionalEmitTests {

  private static string Methods(string methods) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", "#nullable enable\n" + PrototypeFixture.Wrap(methods))));

  private static string Lib(string methods) => RustCrates.Text(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", "#nullable enable\n" + PrototypeFixture.Wrap(methods))), "src/lib.rs");

  private static string[] Problems(string methods)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", "#nullable enable\n" + PrototypeFixture.Wrap(methods))).Select(problem => $"{problem.Code} {problem.Message}")];

  [Test]
  public void OptionalValue_IsAnOption() {
    var text = Methods("public static int? F(int a) => a > 0 ? a : null; public static bool G(int? a) => a == null; public static int H(int? a) => a!.Value;");
    Assert.That(text, Does.Contain("pub fn f(a: i32) -> Option<i32> {\n    if a > 0 { Some(a) } else { None }\n}"));
    Assert.That(text, Does.Contain("pub fn g(a: Option<i32>) -> bool {\n    a.is_none()\n}"));
    Assert.That(text, Does.Contain("pub fn h(a: Option<i32>) -> i32 {\n    ply_value(&a)\n}"));
    Assert.That(text, Does.Contain("fn ply_value<T: Copy>(value: &Option<T>) -> T {\n    value.expect(\"MissingValue\")\n}"));
  }

  [Test]
  public void LiftedOperator_ReadsTheTestedValueWithoutATrap() {
    var text = Methods("public static int F(int? a) => (a + 1) ?? -1; public static bool G(int? a) => a < 1; public static bool H(int? a) => a != null;");
    Assert.That(text, Does.Contain("Some(a.unwrap_or_default().wrapping_add(1))"));
    Assert.That(text, Does.Contain(".unwrap_or(-1)"));
    Assert.That(text, Does.Contain("a.is_some() && a.unwrap_or_default() < 1"));
    Assert.That(text, Does.Contain("pub fn h(a: Option<i32>) -> bool {\n    a.is_some()\n}"));
  }

  [Test]
  public void Coalesce_EvaluatesAFallbackWithAnEffectOnlyWhenNeeded() {
    var text = Methods("static int Other(int[] c) { c[0]++; return -1; } public static int F(int? a, int[] c) => a ?? Other(c); "
      + "public static int? G(int? a, int? b) => a ?? b; public static int? H(int? a, int[] c) => a ?? (int?)Other(c);");
    Assert.That(text, Does.Contain("a.unwrap_or_else(|| other(c))"));
    Assert.That(text, Does.Contain("a.or(b)"));
    Assert.That(text, Does.Contain("a.or_else(|| Some(other(c)))"));
  }

  [Test]
  public void OperandEvaluatedOnce_IsBoundBeforeItsStatement() {
    var text = Methods("static int? Next(int[] c) { c[0]++; return c[0]; } public static int F(int[] c) => (Next(c) + Next(c)) ?? -1;");
    Assert.That(text, Does.Contain("let t0: Option<i32> = next(c);\n    let t1: Option<i32> = next(c);"));
    Assert.That(text, Does.Contain("if t0.is_none() || t1.is_none() {"));
  }

  [Test]
  public void AssignmentInAFallback_StaysWhereItIs() {
    var text = Methods("public static int F(int? q) => (q ??= 4) + 10 * (q ?? 0);");
    Assert.That(text, Does.Contain("q.unwrap_or_else(|| {\n        q = Some(4);\n        q.unwrap_or_default()\n    })"));
  }

  [Test]
  public void ArrayTestedForNull_IsAnOption() {
    var text = Methods("public static int F(int[]? a) => a?.Length ?? -1; public static int G(int[]? a, int[] b) => (a ?? b).Length; public static int H(int[]? a) { a ??= new int[3]; return a.Length; }");
    Assert.That(text, Does.Contain("pub fn f(a: Option<&[i32]>) -> i32 {"));
    Assert.That(text, Does.Contain("Some(ply_non_null(&a).len() as i32)"));
    Assert.That(text, Does.Contain("(if a.is_none() { b } else { *ply_non_null(&a) }).len() as i32"));
    Assert.That(text, Does.Contain("let mut a: Option<Vec<i32>> = a.map(<[i32]>::to_vec);"));
    Assert.That(text, Does.Contain("a = Some(vec![0; 3]);"));
    Assert.That(text, Does.Contain("ply_non_null(&a).len() as i32"));
  }

  [Test]
  public void NullArray_FlowsAsAnOption() {
    var text = Methods("static int[]? Make(bool p) => p ? new int[2] : null; static int Count(int[]? a) => a == null ? -1 : a.Length; "
      + "public static int F(bool p, int[] b) { var made = Make(p); var other = made ?? b; return Count(made) + Count(b) + other.Length + made![0]; }");
    Assert.That(text, Does.Contain("fn make(p: bool) -> Option<Vec<i32>> {\n    if p { Some(vec![0; 2]) } else { None }\n}"));
    Assert.That(text, Does.Contain("let made: Option<Vec<i32>> = make(p);"));
    Assert.That(text, Does.Contain("count(made.as_deref())"));
    Assert.That(text, Does.Contain("count(Some(b))"));
    Assert.That(text, Does.Contain("ply_non_null(&made)[0]"));
  }

  [Test]
  public void ArrayThatMayBeNull_AndIsWritten_IsRefused()
    => Assert.That(Problems("public static int F(int[]? a) { if (a != null) a[0] = 1; return a == null ? 0 : 1; }"), Has.Some.Contain("'an array that may be null and is written'"));

  [Test]
  public void SearchLoopAndALiteralUnwrapped_AllowTheirLints() {
    var lib = Lib("public static int? F(int[] a, int v) { for (var i = 0; i < a.Length; ++i) if (a[i] == v) return i; return null; } "
      + "public static bool G(double x) { double? n = x; return n == n; }");
    Assert.That(lib, Does.Contain("clippy::manual_find"));
    Assert.That(lib, Does.Contain("clippy::unnecessary_literal_unwrap"));
  }

  [Test]
  public void ChoiceOfTruthValues_IsTheLazyOperator() {
    var text = Methods("public static bool F(bool a, bool b) => a ? true : b; public static bool G(bool a, bool b) => a ? false : b; "
      + "public static bool H(bool a, bool b) => a ? b : true; public static bool K(bool a, bool b) => a ? b : false; public static bool M(bool a, bool b, bool c) => !(a && (b || c));");
    Assert.That(text, Does.Contain("pub fn f(a: bool, b: bool) -> bool {\n    a || b\n}"));
    Assert.That(text, Does.Contain("pub fn g(a: bool, b: bool) -> bool {\n    !a && b\n}"));
    Assert.That(text, Does.Contain("pub fn h(a: bool, b: bool) -> bool {\n    !a || b\n}"));
    Assert.That(text, Does.Contain("pub fn k(a: bool, b: bool) -> bool {\n    a && b\n}"));
    Assert.That(text, Does.Contain("pub fn m(a: bool, b: bool, c: bool) -> bool {\n    !a || !b && !c\n}"));
  }

  [Test]
  public void TakesNull_NamesTheParametersThatAreOptions() {
    var module = RustCrates.Prepared(PrototypeFixture.Import("Snippet.cs", "#nullable enable\n" + PrototypeFixture.Wrap("public static int F(int? a, int[]? b, int[] c) => (a ?? 0) + (b?.Length ?? 0) + c.Length;")));
    Assert.That(Enumerable.Range(0, 3).Select(index => RustEmitter.TakesNull(module, "F", index)), Is.EqualTo(new[] { true, true, false }));
  }

}
