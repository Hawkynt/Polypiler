using NUnit.Framework;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Objects and function values: the conversions between the traits of a hierarchy where the value
/// may be null, the identity and the value equality of objects that may be null, the receivers of
/// a record's and a class's own methods, the methods made function values, and the closures whose
/// captures Rust cannot hold.
/// </summary>
public sealed partial class RustCoverageEmitTests {

  private const string Hierarchy = "public interface I { int M(); } public class A : I { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } ";

  [Test]
  public void Cast_OfAnInterfaceThatMayBeNull_ToAClass_IsTheCastOfTheOption()
    => Assert.That(EmittedFile(Hierarchy + "public static class S { public static int F(bool c) { I i = c ? new A() : null; A a = (A)i; return a.M(); } }"),
      Does.Contain("    let i: Option<Rc<dyn I>> = if c { Some(AObject::new()) } else { None };\n    let a: Option<Rc<dyn A>> = ply_cast(i.clone(), PlyObject::into_a);\n    A::m(&**ply_non_null(&a))\n"));

  [Test]
  public void TypeTest_OfAnInterfaceThatMayBeNull_AsksTheObjectItHolds() {
    var text = EmittedFile(Hierarchy + "public static class S { public static bool F(bool c) { I i = c ? new A() : null; return i is A; } public static bool G() { I i = new B(); return i is A; } }");
    Assert.That(text, Does.Contain("    i.as_deref().is_some_and(PlyObject::is_a)\n"));
    Assert.That(text, Does.Contain("    let i: Rc<dyn I> = B::new();\n    i.is_a()\n"));
  }

  [Test]
  public void Argument_OfAClassThatMayBeNull_ForAnInterfaceParameter_IsConvertedInTheOption()
    => Assert.That(EmittedFile(Hierarchy + "public static class S { static int Use(I i) => i == null ? 0 : i.M(); public static int F(bool c) { A a = c ? new B() : null; return Use(a) + Use(new B()) + Use(null); } }"),
      Does.Contain("    r#use(a.clone().and_then(PlyObject::into_i))\n        .wrapping_add(r#use(Some(B::new())))\n        .wrapping_add(r#use(None))\n"));

  [Test]
  public void Argument_OfADerivedClass_ForABaseParameter_IsConvertedToTheBasesTrait()
    => Assert.That(EmittedFile("public class A { public virtual int M() => 1; } public class B : A { public override int M() => 2; } public sealed class C : B { public override int M() => 3; } "
      + "public static class S { public static int F(A a) => a.M(); public static int G() => F(new C()) + F(new B()); }"),
      Does.Contain("    f(Some(C::new())).wrapping_add(f(Some(BObject::new()).and_then(PlyObject::into_a)))\n"));

  [Test]
  public void Identity_WithNull_IsATestOfTheOption_OrFalseOfANewObject() {
    var text = EmittedFile("public sealed class C { public int N; } public static class S { public static bool F(C a) => ReferenceEquals(a, null); public static bool G(C a) => a == null; public static bool H() => new C() == null; }");
    Assert.That(text, Does.Contain("pub fn f(a: Option<Rc<C>>) -> bool {\n    a.is_none()\n}"));
    Assert.That(text, Does.Contain("pub fn g(a: Option<Rc<C>>) -> bool {\n    a.is_none()\n}"));
    Assert.That(text, Does.Contain("pub fn h() -> bool {\n    false\n}"));
  }

  [Test]
  public void Identity_OfObjectsACallMayGiveAsNull_ComparesTheirAddresses() {
    var text = EmittedFile("public sealed class C { public int N; } public static class S { static C Make(bool c) => c ? new C() : null; "
      + "public static bool F(bool c) => Make(c) == null; public static bool G(bool c) => Make(c) == Make(!c); public static int H(bool c) => Make(c).N; }");
    Assert.That(text, Does.Contain("    make(c).is_none()\n"));
    Assert.That(text, Does.Contain("    make(c).as_ref().map(Rc::as_ptr) == make(!c).as_ref().map(Rc::as_ptr)\n"));
    Assert.That(text, Does.Contain("    make(c).expect(\"NullReference\").n.get()\n"));
  }

  [Test]
  public void RecordEquality_WithNullOrAValueThatMayBeNull_ComparesTheOptions() {
    var text = EmittedFile("public sealed record R(int X); public static class S { public static bool G(R a) => a == null; public static bool H(R a) => null == a; "
      + "public static bool I(bool c) { R a = c ? new R(1) : null; return a == new R(1); } }");
    Assert.That(text, Does.Contain("pub fn g(a: Option<Rc<R>>) -> bool {\n    a.is_none()\n}"));
    Assert.That(text, Does.Contain("pub fn h(a: Option<Rc<R>>) -> bool {\n    a.is_none()\n}"));
    Assert.That(text, Does.Contain("    a.as_ref() == Some(&R::new(1))\n"));
  }

  [Test]
  public void OwnMethod_ComparingItsObject_UsesTheSharedSelf() {
    Assert.That(EmittedFile("public sealed class C { public int N; public bool Same(C o) => ReferenceEquals(this, o); } public static class S { public static bool F() { var c = new C(); return c.Same(c); } }"),
      Does.Contain("    pub fn same(self: &Rc<Self>, o: &Rc<C>) -> bool {\n        Rc::ptr_eq(self, o)\n    }"));
    Assert.That(EmittedFile("public sealed record R(int X) { public bool Same(R o) => this == o; } public static class S { public static bool F() { var r = new R(1); return r.Same(r); } }"),
      Does.Contain("    pub fn same(self: &Rc<Self>, o: &Rc<R>) -> bool {\n        *self == *o\n    }"));
  }

  [Test]
  public void ObjectThatMayBeNull_HandedOnOrRead_IsTheOptionOrTraps() {
    Assert.That(EmittedFile("public sealed class C { public int N; } public static class S { static int G(C c) => c.N; public static int F() => G(null); }"),
      Does.Contain("pub fn f() -> i32 {\n    g(None)\n}"));
    var text = EmittedFile("public sealed class C { public int N; public int Get() => N; } public static class S { public static int F(bool c) { C a = c ? new C() : null; C b = c ? a : new C(); return b.Get(); } }");
    Assert.That(text, Does.Contain("    let b: Option<Rc<C>> = if c { a.clone() } else { Some(C::new()) };\n    ply_non_null(&b).get()\n"));
  }

  [Test]
  public void StaticMethod_AsAFunctionValue_IsItsPath()
    => Assert.That(EmittedFile("public sealed class C { public static int Twice(int x) => x * 2; } public static class S { public static int F() { System.Func<int, int> g = C.Twice; return g(3); } }"),
      Does.Contain("    let g: Rc<dyn Fn(i32) -> i32> = Rc::new(C::twice);\n    g(3)\n"));

  [Test]
  public void VirtualMethod_BoundToItsObject_IsRefused()
    => Assert.That(ProblemsOfFile("public class C { public virtual int Twice(int x) => x * 2; } public static class S { public static int F() { var c = new C(); System.Func<int, int> g = c.Twice; return g(3); } }"),
      Has.Some.StartWith("PLY1550 'a virtual method bound to its object' cannot be expressed"));

  [TestCase("public static int F(int[] a) { System.Func<int, int> g = i => a[i]; return g(0); }",
    "a closure that captures an array and may outlive the call it is made for", TestName = "Closure_ThatCapturesAnArray_IsRefused")]
  [TestCase("public static int F(int[] a) { System.Action<int> g = i => a[i] = 1; g(0); return a[0]; }",
    "a closure that captures an array that is written", TestName = "Closure_ThatWritesACapturedArray_IsRefused")]
  [TestCase("static int[] G(int[] a) => a; public static int F() { System.Func<int[], int[]> g = G; return g(null).Length; }",
    "a function that takes or gives more than values as a function value", TestName = "FunctionValue_OfAFunctionOfArrays_IsRefused")]
  public void FunctionValue_RustCannotHold_IsRefused(string methods, string what)
    => Assert.That(Problems(methods), Has.Some.StartWith($"PLY1550 '{what}' cannot be expressed"));

  [Test]
  public void CapturedVariable_AssignedOnBothSides_IsOneSharedCell()
    => Assert.That(Emitted("public static int F() { int x; System.Action g = () => x = 1; x = 2; g(); return x; }"),
      Does.Contain("    let x = Rc::new(Cell::new(0i32));\n    let g: Rc<dyn Fn()> = Rc::new({\n        let x = Rc::clone(&x);\n        move || {\n            x.set(1);\n        }\n    });\n    x.set(2);\n    g();\n    x.get()\n"));

}
