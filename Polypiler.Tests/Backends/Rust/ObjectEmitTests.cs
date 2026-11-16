using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Classes, hierarchies, generics, module state and views as Rust writes them (#462): the shared
/// form of an object with a cell per field, the nullness of its references, traits for interfaces and
/// base classes, one function per generic instantiation, atomics for globals and slices for views.
/// </summary>
[TestFixture]
public sealed class ObjectEmitTests {

  private static string Emitted(string source) => RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", source)));

  private static string[] Problems(string source)
    => [.. RustCrates.Check(PrototypeFixture.Import("Snippet.cs", source)).Select(problem => $"{problem.Code} {problem.Message}")];

  private const string Node = "public sealed class Node { public int Value; public Node Next; public int[] Items = new int[2]; "
    + "public Node(int value) { Value = value; } public int Twice() => Value * 2; public Node Self() => this; } ";

  [Test]
  public void Class_IsAnRcOfCells() {
    var text = Emitted(Node + "public static class S { public static int F(int v) { var n = new Node(v); n.Value = v + 1; return n.Twice() + n.Items.Length; } }");
    // Next, which nothing here reads or writes, is left out.
    Assert.That(text, Does.Contain("#[derive(Default)]\npub struct Node {\n    value: Cell<i32>,\n    items: RefCell<Vec<i32>>,\n}"));
    Assert.That(text, Does.Contain("pub fn new(value: i32) -> Rc<Self> {\n        let this = Rc::new(Self::default());\n        this.init(value);\n        this\n    }"));
    Assert.That(text, Does.Contain("pub fn twice(&self) -> i32"));
    Assert.That(text, Does.Contain("pub fn self_(self: &Rc<Self>) -> Rc<Node>").Or.Contain("self: &Rc<Self>"));
    Assert.That(text, Does.Contain("n.value.set(v.wrapping_add(1));"));
    Assert.That(text, Does.Contain("n.items.borrow().len() as i32"));
    Assert.That(text, Does.Contain("use std::cell::Cell;\nuse std::cell::RefCell;\nuse std::rc::Rc;"));
  }

  [Test]
  public void FieldsOfOtherTypes_AreWrittenWithTheValueFirst() {
    var text = Emitted(Node + "public static class S { public static int F(int v) { var n = new Node(v); n.Next = new Node(v); n.Next.Next = n.Next; n.Items[n.Items.Length - 1] = n.Value; return n.Items[1]; } }");
    Assert.That(text, Does.Contain("let value = Some(Node::new(v));\n    *n.next.borrow_mut() = value;"));
    Assert.That(text, Does.Contain("let value_2 = n.next.borrow().clone();"));
    Assert.That(text, Does.Contain("n.items.borrow_mut()[index] = value1;"));
  }

  [Test]
  public void NullableReference_IsAnOption() {
    var text = Emitted(Node + "public static class S { public static int F(bool b) { Node n = b ? new Node(1) : null; Node m = null; return n == null || m != null ? 0 : n.Twice(); } }");
    Assert.That(text, Does.Contain("let n: Option<Rc<Node>> = if b { Some(Node::new(1)) } else { None };"));
    Assert.That(text, Does.Contain("n.is_none()"));
    Assert.That(text, Does.Contain("ply_non_null(&n).twice()"));
  }

  [Test]
  public void ReferenceEquality_ComparesTheRcs() {
    var text = Emitted(Node + "public static class S { public static bool F(Node a, int v) { var b = new Node(v); var c = b; return ReferenceEquals(b, c) && ReferenceEquals(a, b); } }");
    Assert.That(text, Does.Contain("Rc::ptr_eq(&b, &c)"));
    Assert.That(text, Does.Contain("a.as_ref().map(Rc::as_ptr) == Some(&b).map(Rc::as_ptr)"));
  }

  [Test]
  public void Record_DerivesItsEquality_ExceptOfFloats() {
    var text = Emitted("public sealed record R(int A); public static class S { public static bool F(int a) => new R(a) == new R(a); }");
    Assert.That(text, Does.Contain("#[derive(Default, PartialEq)]\npub struct R {"));
    Assert.That(Problems("public sealed record R(double A); public static class S { public static bool F(double a) => new R(a) == new R(a); }"),
      Has.Some.Contain("'a record with a floating-point field'"));
  }

  private const string Shapes = "public interface IShape { double Area(); } public abstract class Shape : IShape { public double Scale = 1; public abstract double Area(); public virtual int Sides() => 0; } "
    + "public sealed class Rect : Shape { private readonly double _w; public Rect(double w) { _w = w; } public override double Area() => _w * Scale; public override int Sides() => base.Sides() + 4; } "
    + "public sealed class Circle : Shape { public override double Area() => 3 * Scale; } ";

  [Test]
  public void Hierarchy_IsTraits() {
    var text = Emitted(Shapes + "public static class S { public static double F(double w) { Shape s = new Rect(w); IShape i = s; s.Scale = 2; return s.Area() + i.Area() + s.Sides(); } }");
    Assert.That(text, Does.Contain("pub trait IShape {\n    fn area(&self) -> f64;\n}"));
    Assert.That(text, Does.Contain("pub trait Shape: PlyObject + IShape {").Or.Contain("pub trait Shape: IShape {"));
    Assert.That(text, Does.Contain("fn field_scale(&self) -> &Cell<f64>;"));
    Assert.That(text, Does.Contain("fn sides(&self) -> i32 {\n        shape_sides(self)\n    }"));
    Assert.That(text, Does.Contain("impl Shape for Rect {"));
    Assert.That(text, Does.Contain("impl IShape for Rect {\n    fn area(&self) -> f64 {\n        Shape::area(self)\n    }\n}"));
    Assert.That(text, Does.Contain("pub fn rect_sides(this: &Rect) -> i32 {\n    shape_sides(this).wrapping_add(4)\n}"));
    Assert.That(text, Does.Contain("Shape::field_scale(&*s).set(2.0);"));
    Assert.That(text, Does.Contain("Shape::area(&*s)"));
    Assert.That(text, Does.Contain("IShape::area(&*i)"));
  }

  [Test]
  public void TypeTestsAndCasts_GoThroughPlyObject() {
    var text = Emitted(Shapes + "public static class S { public static double F(double w, bool b) { Shape s = new Rect(w); if (b) s = new Circle(); Shape n = null; var r = (Rect)n; "
      + "return s is Rect ? ((Rect)s).Area() : (s as Circle == null ? (r == null ? 1 : 2) : 0); } }");
    Assert.That(text, Does.Contain("pub trait PlyObject {"));
    Assert.That(text, Does.Contain("fn is_rect(&self) -> bool;"));
    Assert.That(text, Does.Contain("fn into_rect(self: Rc<Self>) -> Option<Rc<Rect>>;"));
    Assert.That(text, Does.Contain("s.is_rect()"));
    Assert.That(text, Does.Contain("Rc::clone(&s).into_rect().expect(\"InvalidCast\")"));
    Assert.That(text, Does.Contain("ply_cast(n.clone(), PlyObject::into_rect)"));
  }

  [Test]
  public void AbstractClass_HasNoMaker_AndAClassWithObjectsOfItsOwnIsAnObjectStruct() {
    var text = Emitted("public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } "
      + "public static class S { public static int F() { A a = new A(); A b = new B(); return a.M() + b.M(); } }");
    Assert.That(text, Does.Contain("pub struct AObject;"));
    Assert.That(text, Does.Contain("let this = Rc::new(Self);"));
    Assert.That(text, Does.Contain("a_ctor(&*this);"));
    Assert.That(text, Does.Contain("let a: Rc<dyn A> = AObject::new();"));
  }

  [Test]
  public void GenericMethod_IsWrittenPerInstantiation() {
    var text = Emitted("public static class S { static T Max<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b) >= 0 ? a : b; "
      + "static bool Eq<T>(T a, T b) where T : System.IEquatable<T> => a.Equals(b); "
      + "public static double F(double a, double b) => Max(a, b); public static int G(short a, short b) => Max(a, b); public static long H(long a, long b) => Max(a, b); "
      + "public static bool E(double a, double b) => Eq(a, b); public static bool I(int a, int b) => Eq(a, b); }");
    Assert.That(text, Does.Contain("fn max_f64(a: f64, b: f64) -> f64"));
    Assert.That(text, Does.Contain("ply_compare_f64(a, b)"));
    Assert.That(text, Does.Contain("i32::from(a) - i32::from(b)").Or.Contain("i32::from(a).wrapping_sub(i32::from(b))"));
    Assert.That(text, Does.Contain("a.cmp(&b) as i32"));
    Assert.That(text, Does.Contain("a == b || (a.is_nan() && b.is_nan())"));
    Assert.That(text, Does.Contain("a == b"));
  }

  [Test]
  public void GlobalStep_ThatTraps_ChecksBeforeItStores() {
    var text = Emitted(PrototypeFixture.Wrap("static int counter; public static int F() => checked(counter++); public static int G() => --counter;"));
    Assert.That(text, Does.Contain("let old = COUNTER.load(Ordering::Relaxed);"));
    Assert.That(text, Does.Contain("let new = old.checked_add(1).expect(\"IntegerOverflow\");"));
    Assert.That(text, Does.Contain("COUNTER.fetch_sub(1, Ordering::Relaxed).wrapping_sub(1)"));
  }

  [Test]
  public void View_IsASlice() {
    var text = Emitted("using System; public static class S { static int Sum(System.ReadOnlySpan<byte> s) { var t = 0; for (var i = 0; i < s.Length; ++i) t += s[i]; return t; } "
      + "static void Clear(System.Span<byte> s) => s.Fill(0); "
      + "public static int F(byte[] d, int a, int n) { Clear(d.AsSpan(a + n)); return Sum(d.AsSpan(a, n)) + Sum(d) + (d.AsSpan().SequenceEqual(d) ? 1 : 0) + d.AsSpan(1).ToArray().Length; } }");
    Assert.That(text, Does.Contain("fn sum(s: &[u8]) -> i32"));
    Assert.That(text, Does.Contain("fn clear(s: &mut [u8])"));
    Assert.That(text, Does.Contain("s.fill(0);"));
    Assert.That(text, Does.Contain("ply_view(d, a, n)"));
    Assert.That(text, Does.Contain("ply_view_from_mut(d, a.wrapping_add(n))"));
    Assert.That(text, Does.Contain("ply_view_from(d, 1).to_vec()"));
  }

  [Test]
  public void FieldNothingTouches_IsLeftOut_AndOneOnlyWritten_Stays() {
    var text = Emitted("public sealed class T { private int _count; private int _last; private int _never; public int Pub; public int Add(int v) { _count++; _last = v; return _count; } } "
      + "public sealed class E { private int _unused; } public static class S { public static int F(int v) { var e = new E(); var t = new T(); var u = t; return u.Add(v); } }");
    Assert.That(text, Does.Contain("pub struct T {\n    count: Cell<i32>,\n    last: Cell<i32>,\n}"));
    Assert.That(text, Does.Contain("self.last.set(v);"));
    // A single-owner E (#1393) keeps no field nothing reads: a unit struct.
    Assert.That(text, Does.Contain("pub struct E;"));
  }

  [Test]
  public void UnsupportedForms_AreRefusedByName() {
    Assert.That(Problems("using System; " + PrototypeFixture.Wrap("public static bool F(double[] a, double[] b) => a.AsSpan().SequenceEqual(b);")), Has.Some.Contain("'ViewEqual'"));
  }

}
