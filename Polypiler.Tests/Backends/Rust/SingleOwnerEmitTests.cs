using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Single-owner classes as plain structs (#1393): the ownership analysis that finds an object no two
/// places hold, the plain form with <c>&amp;self</c> and <c>&amp;mut self</c> methods, objects borrowed
/// where they are passed and moved where they are returned - and every use that keeps a class shared.
/// </summary>
[TestFixture]
public sealed class SingleOwnerEmitTests {

  private static string Emitted(string source) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", source)));

  private const string Xorshift = "public sealed class Xorshift32 { private uint _state; public Xorshift32(uint seed) { this._state = seed; } "
    + "public uint Next() { var x = this._state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; this._state = x; return x; } } ";

  private const string Counter = "public sealed class C { private int _n; private int[] _a = new int[2]; "
    + "public int Get() => _n + _a[0]; public void Set(int v) { _n = v; } public void Poke(int v) { _a[1] = v; } public void Twice(int v) { Set(v); Set(v); } } ";

  private static string Single(string body) => Emitted(Counter + "public static class S { " + body + " }");

  [Test]
  public void StateObject_IsAPlainStructWithAMutatingMethod() {
    var text = Emitted(Xorshift + "public static class S { public static uint First(uint seed) => new Xorshift32(seed).Next(); "
      + "public static uint Second(uint seed) { var g = new Xorshift32(seed); g.Next(); return g.Next(); } }");
    Assert.That(text, Does.Contain("#[derive(Default)]\npub struct Xorshift32 {\n    state: u32,\n}"));
    Assert.That(text, Does.Contain("pub fn new(seed: u32) -> Self {\n        let mut this = Self::default();\n        this.init(seed);\n        this\n    }"));
    Assert.That(text, Does.Contain("pub fn next(&mut self) -> u32 {"));
    Assert.That(text, Does.Contain("Xorshift32::new(seed).next()"));
    Assert.That(text, Does.Contain("let mut g: Xorshift32 = Xorshift32::new(seed);"));
    Assert.That(text, Does.Not.Contain("Rc<").And.Not.Contain("Cell<"));
  }

  [Test]
  public void Receivers_AreSharedOrExclusiveByWhatTheyWrite() {
    var text = Single("public static int F(int v) { var c = new C(); c.Twice(v); c.Poke(v); return c.Get(); }");
    Assert.That(text, Does.Contain("pub fn get(&self) -> i32"));
    Assert.That(text, Does.Contain("pub fn set(&mut self, v: i32)"));
    Assert.That(text, Does.Contain("pub fn poke(&mut self, v: i32)"));
    Assert.That(text, Does.Contain("pub fn twice(&mut self, v: i32)"));
    Assert.That(text, Does.Contain("pub struct C {\n    n: i32,\n    a: Vec<i32>,\n}"));
    Assert.That(text, Does.Contain("self.a[1] = v;"));
  }

  [Test]
  public void Parameters_AreBorrowedSharedOrExclusive() {
    var text = Single("public static int F(int v) { var c = new C(); Write(c, v); return Read(c); } static void Write(C c, int v) => c.Set(v); static int Read(C c) => c.Get();");
    Assert.That(text, Does.Contain("fn write(c: &mut C, v: i32)"));
    Assert.That(text, Does.Contain("fn read(c: &C) -> i32"));
    Assert.That(text, Does.Contain("write(&mut c, v);"));
    Assert.That(text, Does.Contain("read(&c)"));
    Assert.That(text, Does.Contain("let mut c: C = C::new();"));
  }

  [Test]
  public void ObjectOnlyRead_IsNoMutableVariable() {
    var text = Single("public static int F() { var c = new C(); return c.Get(); }");
    Assert.That(text, Does.Contain("let c: C = C::new();"));
  }

  [Test]
  public void Factory_MovesTheObjectOut() {
    var text = Single("public static int F(int v) { var c = Make(v); return c.Get(); } static C Make(int v) { var c = new C(); c.Set(v); return c; } static C Fresh() => new C();");
    Assert.That(text, Does.Contain("fn make(v: i32) -> C {\n    let mut c: C = C::new();\n    c.set(v);\n    c\n}"));
    Assert.That(text, Does.Contain("fn fresh() -> C {\n    C::new()\n}"));
  }

  [Test]
  public void NewObjectPassedOn_IsBorrowedInPlace() {
    var text = Single("public static int F(int v) => Read(new C()); static int Read(C c) => c.Get();");
    Assert.That(text, Does.Contain("read(&C::new())"));
  }

  [Test]
  public void FieldOnlyWritten_IsLeftOut_KeepingWhatItsValueDoes() {
    var text = Emitted("public sealed class W { private int _seen; private int _last; public int Add(int v) { _last = v; _seen = v + 1; return _seen; } public void Odd(int[] a) { _last = a[3]; } } "
      + "public static class S { public static int F(int v, int[] a) { var w = new W(); w.Odd(a); return w.Add(v); } }");
    Assert.That(text, Does.Contain("pub struct W {\n    seen: i32,\n}"));
    Assert.That(text, Does.Not.Contain("self.last"));
    // An index that may trap is still evaluated.
    Assert.That(text, Does.Contain("let _ = a[3];"));
  }

  [Test]
  public void ClassWithoutFields_IsAUnitStruct() {
    var text = Emitted("public sealed class U { public int One() => 1; } public static class S { public static int F() => new U().One(); }");
    Assert.That(text, Does.Contain("#[derive(Default)]\npub struct U;"));
    Assert.That(text, Does.Contain("pub fn new() -> Self {\n        let this = Self;\n        this.init();\n        this\n    }"));
  }

  [TestCase("public static int F() { var a = new C(); var b = a; b.Set(1); return a.Get(); }", TestName = "SecondVariable_KeepsTheClassShared")]
  [TestCase("public static bool F() { var a = new C(); var b = new C(); return ReferenceEquals(a, b); }", TestName = "ReferenceEquality_KeepsTheClassShared")]
  [TestCase("public static int F(bool b) { C c = null; if (b) c = new C(); return c == null ? 0 : c.Get(); }", TestName = "Null_KeepsTheClassShared")]
  [TestCase("public static int F() { var h = new Holder(); h.Kept = new C(); return h.Kept.Get(); }", "public sealed class Holder { public C Kept; } ", TestName = "FieldOfAnotherClass_KeepsTheClassShared")]
  [TestCase("public static int F() => Getter()(); static System.Func<int> Getter() { var c = new C(); return () => Read(c); } static int Read(C c) => c.Get();", TestName = "LambdaCapture_KeepsTheClassShared")]
  [TestCase("public static int F() => Same(new C()).Get(); static C Same(C c) => c;", TestName = "ReturnedParameter_KeepsTheClassShared")]
  [TestCase("public static int F() { var c = new C(); return Both(c, c); } static int Both(C a, C b) => a.Get() + b.Get();", TestName = "SameObjectTwiceInOneCall_KeepsTheClassShared")]
  [TestCase("public static int F(bool b) { var c = b ? new C() : new C(); return c.Get(); }", TestName = "ConditionalChoice_KeepsTheClassShared")]
  [TestCase("public static int F() { var c = new C(); c = Other(); return c.Get(); } static C Other() { var a = new C(); var b = a; return b; }", TestName = "SharedFactory_KeepsTheClassShared")]
  [TestCase("public static int F(C c) { c = new C(); return c.Get(); }", TestName = "AssignedParameter_KeepsTheClassShared")]
  public void Shared(string body, string other = "") => Assert.That(Emitted(other + Counter + "public static class S { " + body + " }"), Does.Contain("Rc<C>"));

  [Test]
  public void ListOfObjects_KeepsTheClassShared()
    => Assert.That(RustCrates.Check(PrototypeFixture.Import("Snippet.cs", "using System.Collections.Generic; " + Counter + "public static class S { public static int F() { var l = new List<C>(); l.Add(new C()); return l.Count; } }")).Select(problem => problem.Message),
      Has.Some.Contain("'a list of C'"));

  [Test]
  public void ObjectOnceInOneCall_StaysPlain() {
    var text = Single("public static int F() { var c = new C(); var d = new C(); return Both(c, d); } static int Both(C a, C b) => a.Get() + b.Get();");
    Assert.That(text, Does.Contain("fn both(a: &C, b: &C) -> i32"));
    Assert.That(text, Does.Not.Contain("Rc<"));
  }

  [Test]
  public void TraitMethodName_AllowsShouldImplementTrait() {
    var files = RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", Xorshift + "public static class S { public static uint F(uint s) => new Xorshift32(s).Next(); }"));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("#![allow(clippy::should_implement_trait)]"));
  }

  /// <summary>The share of the prototype's <c>classes</c> family in the plain form: Adler32 and Rc4 have one owner per object; Counter aliases and compares, Range is a record, null holds null.</summary>
  [TestCase("Adler32", true)]
  [TestCase("Rc4", true)]
  [TestCase("Counter", false)]
  [TestCase("Range", false)]
  [TestCase("null", false)]
  public void ClassesFixture_IsPlainExactlyWhereItsObjectsHaveOneOwner(string fixture, bool plain) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "classes", fixture + ".cs");
    var text = RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import(fixture + ".cs", File.ReadAllText(path))));
    Assert.That(text.Contains("Rc<", System.StringComparison.Ordinal), Is.EqualTo(!plain));
  }

}
