using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// #1424: a global of an enum type is the enum itself behind a <c>Mutex</c>, as a floating-point
/// global is; and a reference to a class that may be null, converted to a base class that is a
/// trait, is mapped to the trait's <c>Rc</c>, which an <c>Option</c> is not by itself.
/// </summary>
[TestFixture]
public sealed class EnumGlobalEmitTests {

  private const string Color = "public enum Color : byte { Red = 1, Green = 2, Blue = 4 } ";

  private static string Emitted(string source, RustProfile? profile = null) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", source), profile));

  private static string[] Problems(string source, RustProfile? profile = null)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", source), profile).Select(problem => problem.Message)];

  [Test]
  public void EnumGlobal_IsTheEnumBehindAMutex() {
    var text = Emitted(Color + "public static class S { static Color current = Color.Green; "
      + "public static int F(int c) { var old = current; current = (Color)c; return (int)old * 10 + (int)current; } }");
    Assert.That(text, Does.Contain("static CURRENT: Mutex<Color> = Mutex::new(Color::GREEN);"));
    Assert.That(text, Does.Contain("ply_with(&CURRENT, |current| *current)"));
    Assert.That(text, Does.Contain("ply_with(&CURRENT, |current| *current = Color(c as u8));"));
  }

  [Test]
  public void EnumGlobal_WithoutAFirstValue_HoldsZero() {
    var text = Emitted(Color + "public static class S { static Color current; public static int F(int c) { current = (Color)c; return (int)current; } }");
    Assert.That(text, Does.Contain("static CURRENT: Mutex<Color> = Mutex::new(Color(0));"));
  }

  [Test]
  public void EnumGlobal_OfAClosedEnum_HoldsAVariant() {
    var text = Emitted("public enum Mode { Off, On, Auto } public static class S { static Mode mode = Mode.On; "
      + "public static int F(bool on) { mode = on ? Mode.Auto : Mode.Off; return mode == Mode.Auto ? 1 : 0; } }");
    Assert.That(text, Does.Contain("pub enum Mode {"));
    Assert.That(text, Does.Contain("static MODE: Mutex<Mode> = Mutex::new(Mode::On);"));
  }

  [Test]
  public void EnumGlobal_WithoutAFirstValue_OpensAnEnumWithoutAZeroMember() {
    var text = Emitted("public enum Level { Low = 1, High = 2 } public static class S { static Level level; "
      + "public static int F(bool high) { var was = level == Level.Low ? 1 : 0; level = high ? Level.High : Level.Low; return was; } }");
    Assert.That(text, Does.Contain("pub struct Level(pub i32);"));
    Assert.That(text, Does.Contain("static LEVEL: Mutex<Level> = Mutex::new(Level(0));"));
  }

  [Test]
  public void EnumGlobal_BeforeConstMutex_IsRefused()
    => Assert.That(Problems(Color + "public static class S { static Color current = Color.Red; public static int F() => (int)current; }", RustProfile.Rust2021),
      Has.Some.Contains("a Mutex in a static, Rust 1.63"));

  private const string Hierarchy = "public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } ";

  [Test]
  public void NullableDerived_ConvertedToItsBase_IsMappedToTheTrait() {
    var text = Emitted(Hierarchy + "public static class S { static int G(A a) => a == null ? 0 : a.M(); "
      + "public static int F(bool make) { B b = make ? new B() : null; A a = b; return G(b) + (a == null ? 10 : 20); } }");
    Assert.That(text, Does.Contain("b.clone().map(|o| o as Rc<dyn A>)"));
    Assert.That(text, Does.Contain("let a: Option<Rc<dyn A>> = b.clone().map(|o| o as Rc<dyn A>);"));
  }

  [Test]
  public void NullableDerived_ReturnedAsItsBase_IsMappedToTheTrait() {
    var text = Emitted(Hierarchy + "public static class S { static A Base(B b) => b; public static int F(bool make) { B b = make ? new B() : null; A a = null; a = b; return (Base(b) == null ? 1 : 2) + (a == null ? 10 : 20); } }");
    Assert.That(text, Does.Contain("fn base(b: Option<Rc<B>>) -> Option<Rc<dyn A>> {\n    b.clone().map(|o| o as Rc<dyn A>)\n}"));
    Assert.That(text, Does.Contain("a = b.clone().map(|o| o as Rc<dyn A>);"));
  }

  [Test]
  public void NeverNullDerived_ConvertedToItsBase_IsCoercedAsItIs() {
    var text = Emitted(Hierarchy + "public static class S { static int G(A a) => a.M(); public static int F() { B b = new B(); return G(b); } }");
    Assert.That(text, Does.Not.Contain(".map(|"));
  }

}
