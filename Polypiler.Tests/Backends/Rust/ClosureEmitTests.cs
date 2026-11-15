using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Function values as Rust writes them: a lambda as a closure behind an <c>Rc</c>, a variable it
/// shares with its function as an <c>Rc&lt;Cell&lt;T&gt;&gt;</c>, a value it only reads copied by
/// <c>move</c>, a local function that captures nothing as a <c>fn</c> of its own, a function type the
/// module holds <c>null</c> of as an <c>Option</c>, and the constructs that stay refused.
/// </summary>
[TestFixture]
public sealed class ClosureEmitTests {

  private static string Emitted(string source) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", "using System; " + source)));

  private static string Methods(string methods) => Emitted(PrototypeFixture.Wrap(methods));

  private static string[] Problems(string source)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", "using System; " + source)).Select(problem => $"{problem.Code} {problem.Message}")];

  private const string Fold = "static double Fold(double[] a, Func<double, double, double> step) { var r = 0.0; foreach (var v in a) r = step(r, v); return r; } ";

  [Test]
  public void Lambda_WithoutCaptures_IsAClosureInAnRc() {
    var text = Methods(Fold + "public static double F(double[] a) => Fold(a, (s, v) => s + v);");
    Assert.That(text, Does.Contain("fn fold(a: &[f64], step: Rc<dyn Fn(f64, f64) -> f64>) -> f64 {"));
    Assert.That(text, Does.Contain("fold(a, Rc::new(|s: f64, v: f64| s + v))"));
    Assert.That(text, Does.Contain("result = step(result, v);").Or.Contain("= step(r, v);"));
    Assert.That(text, Does.StartWith("use std::rc::Rc;"));
  }

  [Test]
  public void CapturedValue_IsMovedIntoTheClosure() {
    var text = Methods(Fold + "public static double F(double[] a, double k) => Fold(a, (s, v) => s + v * k);");
    Assert.That(text, Does.Contain("Rc::new(move |s: f64, v: f64| s + v * k)"));
  }

  [Test]
  public void VariableAssignedAfterItIsCaptured_IsSharedInACell() {
    var text = Methods(Fold + "public static int F(double[] a) { var calls = 0; Fold(a, (s, v) => { calls++; return s * v; }); return calls; }");
    Assert.That(text, Does.Contain("let calls = Rc::new(Cell::new(0i32));"));
    Assert.That(text, Does.Contain("let calls = Rc::clone(&calls);\n            move |s: f64, v: f64| {\n                calls.set(calls.get().wrapping_add(1));\n                s * v\n            }"));
    Assert.That(text, Does.Contain("calls.get()\n}"));
    Assert.That(text, Does.Contain("use std::cell::Cell;"));
  }

  [Test]
  public void ParameterAssignedAfterItIsCaptured_IsSharedFromTheStart() {
    var text = Methods("public static int F(int x) { Func<int> g = () => x; x = x * 5; return g(); }");
    Assert.That(text, Does.Contain("let x = Rc::new(Cell::new(x));"));
    Assert.That(text, Does.Contain("x.set(x.get().wrapping_mul(5));"));
    Assert.That(text, Does.Contain("g()"));
  }

  [Test]
  public void SharedStep_AsAValue_IsABlock() {
    var text = Methods("static Func<int> Make() { var n = 0; return () => ++n; } static Func<int> Late() { var n = 0; return () => n++; } "
      + "public static int F() => Make()() + Late()();");
    Assert.That(text, Does.Contain("move || {\n            n.set(n.get().wrapping_add(1));\n            n.get()\n        }"));
    Assert.That(text, Does.Contain("let old = n.get();\n            n.set(old.wrapping_add(1));\n            old"));
  }

  [Test]
  public void LocalFunction_ThatCapturesNothing_IsANestedFn() {
    var text = Methods("public static int F(int n) { return G(n); int G(int m) => m <= 1 ? 1 : m * G(m - 1); }");
    Assert.That(text, Does.Contain("pub fn f(n: i32) -> i32 {\n    fn g(m: i32) -> i32 {\n        if m <= 1 {\n            1\n        } else {\n            m.wrapping_mul(g(m.wrapping_sub(1)))\n        }\n    }\n    g(n)\n}"));
  }

  [Test]
  public void LocalFunction_AsAValue_IsTheFnInAnRc() {
    var text = Methods("static int Apply(Func<int, int> f, int v) => f(v); public static int F(int n) { int Twice(int v) => v * 2; return Apply(Twice, n); }");
    Assert.That(text, Does.Contain("apply(Rc::new(twice), n)"));
  }

  [Test]
  public void FunctionTypeWithNull_IsAnOption() {
    var text = Methods("public static int F() { var fs = new Func<int>[2]; fs[0] = () => 1; Func<int> g = null; var none = g == null; g = fs[0]; return none && g != null ? fs[0]() + g() : 0; }");
    Assert.That(text, Does.Contain("let mut fs: Vec<Option<Rc<dyn Fn() -> i32>>> = vec![None; 2];"));
    Assert.That(text, Does.Contain("fs[0] = Some(Rc::new(|| 1));"));
    Assert.That(text, Does.Contain("g.is_none()"));
    Assert.That(text, Does.Contain("g = fs[0].clone();"));
    Assert.That(text, Does.Contain("g.is_some()"));
    Assert.That(text, Does.Contain("ply_non_null(&fs[0])().wrapping_add(ply_non_null(&g)())"));
    Assert.That(text, Does.Contain("fn ply_non_null<T>(value: &Option<T>) -> &T {"));
  }

  [Test]
  public void NullableCallee_WithArgumentsThatAreMade_BindsThemFirst() {
    var text = Methods("public static int F(int a) { Func<int, int> f = null; return f(a + 1); }");
    Assert.That(text, Does.Contain("let callee = &f;\n        let argument = a.wrapping_add(1);\n        callee.as_ref().expect(\"NullReference\")(argument)"));
  }

  [Test]
  public void FunctionValue_IsSharedWhereAPlaceTakesIt() {
    var text = Methods("static Func<int, int> Compose(Func<int, int> f, Func<int, int> g) => v => g(f(v)); "
      + "public static int F(int x, bool b, int k) { Func<int, int> a = v => v + k; var c = b ? a : v => v; return Compose(a, c)(x); }");
    Assert.That(text, Does.Contain("let g = Rc::clone(&g);\n        let f = Rc::clone(&f);\n        move |v: i32| g(f(v))"));
    Assert.That(text, Does.Contain("if b {\n        Rc::clone(&a)\n    } else {\n        Rc::new(|v: i32| v)\n    };"));
    Assert.That(text, Does.Contain("compose(Rc::clone(&a), Rc::clone(&c))(x)"));
  }

  [Test]
  public void MethodGroup_IsTheFunctionInAnRc() {
    var text = Methods("static int Square(int v) => v * v; public static int F(int x) { Func<int, int> f = Square; return f(x); }");
    Assert.That(text, Does.Contain("let f: Rc<dyn Fn(i32) -> i32> = Rc::new(square);"));
  }

  [Test]
  public void StaticMethodOfAClass_IsItsPath() {
    var text = Emitted("public sealed class Box { public int V; public static int Twice(int v) => v * 2; } "
      + "public static class S { public static int F(int x) { var b = new Box(); b.V = x; Func<int, int> f = Box.Twice; return f(b.V); } }");
    Assert.That(text, Does.Contain("Rc::new(Box::twice)"));
  }

  [Test]
  public void BoundMethod_HoldsItsObject() {
    var text = Emitted("public sealed class Scale { private readonly int _k; public Scale(int k) { _k = k; } public int Apply(int v) => v * _k; } "
      + "public static class S { public static int F(int v) { var s = new Scale(3); Func<int, int> f = s.Apply; s = null; return f(v); } }");
    Assert.That(text, Does.Contain("let receiver = s.clone().expect(\"NullReference\");\n        move |v1: i32| receiver.apply(v1)"));
  }

  [Test]
  public void LambdaThatAssignsItsParameter_BindsItAgain() {
    var text = Methods("public static int F(int x) { Func<int, int> f = v => { v = v * 2; return v + 1; }; return f(x); }");
    Assert.That(text, Does.Contain("let mut v = v;"));
  }

  [Test]
  public void CapturedArrayOrStringNothingWrites_IsCopied() {
    var text = Methods("public static int F(int[] a, string s) { Func<int, int> f = i => a[i] + s.Length; return f(0); }");
    Assert.That(text, Does.Contain("let a = a.to_vec();"));
    Assert.That(text, Does.Contain("let s = s.to_vec();"));
  }

  [Test]
  public void FunctionValueThatTakesAFunction_AllowsTheComplexType() {
    var files = RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", "using System; " + PrototypeFixture.Wrap("public static int F(int x) { Func<Func<int>> outer = () => () => x + 1; return outer()(); }")));
    Assert.That(RustCrates.ModuleText(files), Does.Contain("let outer: Rc<dyn Fn() -> Rc<dyn Fn() -> i32>> =\n        Rc::new(move || Rc::new(move || x.wrapping_add(1)));"));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("clippy::type_complexity"));
  }

  [TestCase("public static int F() { var a = new int[2]; Func<int> f = () => a[0]; a[0] = 1; return f(); }", "'a closure that captures an array that is written'")]
  [TestCase("public static int F() { Func<int[], int> f = a => a.Length; return f(new int[3]); }", "'a function value that takes or gives i32[]'")]
  [TestCase("public static int F() { var t = \"\"; Func<int> f = () => t.Length; t = \"x\"; return f(); }", "'a captured variable of the type str that is assigned'")]
  public void FunctionValueOutsideTheForm_IsRefused(string methods, string construct)
    => Assert.That(Problems(PrototypeFixture.Wrap(methods)), Has.Some.Contain(construct));

  [Test]
  public void ClosureOverALoopVariableCopiedForEachPass_IsRefused() {
    var imported = Polypiler.Core.Frontends.JavaScript.JavaScriptFrontend.Import("loop.mjs", "export function g(n) { let s = 0; for (let i = 0; i < n; i++) { const f = () => i; s += f(); } return s; }");
    Assert.That(RustCrates.Check(imported.Module!).Select(problem => problem.Message), Has.Some.Contain("'a closure that captures the variable of a loop copied for each pass'"));
  }

  [Test]
  public void ClosureThatCapturesTheObjectOfAMethod_IsRefused()
    => Assert.That(Problems("public sealed class C { public int V; public int F() { Func<int> g = () => V; return g(); } } public static class S { public static int G() => new C().F(); }"),
      Has.Some.Contain("'a closure that captures the object of a method'"));

}
