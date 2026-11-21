using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Generic classes of C# as Rust generic structs (#1421): <c>pub struct Ring&lt;T&gt;</c> with
/// <c>impl&lt;T: Default + Copy&gt; Ring&lt;T&gt;</c> in the place of one struct per instantiation, the
/// bounds of the members, the paths their callers name, and every class and instantiation that keeps
/// a struct of its own.
/// </summary>
[TestFixture]
public sealed class GenericClassEmitTests {

  private static string Emitted(IrModule module, OptimizationLevel level = OptimizationLevel.Default) {
    Assert.That(RustCrates.Check(module), Is.Empty);
    return RustCrates.ModuleText(RustCrates.Emit(module, level: level));
  }

  private static string Emitted(string source, OptimizationLevel level = OptimizationLevel.Default) => Emitted(PrototypeFixture.Import("Snippet.cs", source), level);

  private const string Cell = """
    public sealed class Cell<T> {
      private T _v;
      public Cell(T v) { this._v = v; }
      public T Get() => this._v;
    }
    """;

  [TestCase(OptimizationLevel.Default)]
  [TestCase(OptimizationLevel.None)]
  public void Ring_IsOneGenericStruct(OptimizationLevel level) {
    var text = Emitted(GenericsFixtures.Imported("Ring"), level);
    Assert.That(text, Does.Contain("#[derive(Default)]\npub struct Ring<T> {\n    items: Vec<T>,\n    count: i32,\n}"));
    Assert.That(text, Does.Contain("impl<T: Default + Copy> Ring<T> {\n    pub fn new(capacity: i32) -> Self {"));
    Assert.That(text, Does.Contain("self.items = vec![T::default(); usize::try_from(capacity).expect(\"IntegerOverflow\")];"));
    Assert.That(text, Does.Contain("pub fn last(&self) -> T {"));
    Assert.That(text, Does.Contain("pub fn push(&mut self, value: T) {"));
    Assert.That(text, Does.Contain("fn longs() -> Ring<i64> {\n    let mut ring: Ring<i64> = Ring::<i64>::new(3);"));
    Assert.That(text, Does.Contain("let mut ring: Ring<f64> = Ring::<f64>::new(2);"));
    Assert.That(text, Does.Not.Contain("RingI64").And.Not.Contain("RingF64"));
  }

  [Test]
  public void Equals_IsEq_AndAFloatingPointInstantiationKeepsItsOwnStruct() {
    var text = Emitted("""
      public sealed class Box<T> where T : System.IEquatable<T> {
        private T _v;
        public Box(T v) { this._v = v; }
        public bool Is(T v) => this._v.Equals(v);
      }
      public static class Use {
        public static bool Int(int a, int b) { var box = new Box<int>(a); return box.Is(b); }
        public static bool Double(double a, double b) { var box = new Box<double>(a); return box.Is(b); }
      }
      """);
    Assert.That(text, Does.Contain("pub struct Box<T> {\n    v: T,\n}"));
    Assert.That(text, Does.Contain("impl<T: Eq + Default + Copy> Box<T> {"));
    Assert.That(text, Does.Contain("self.v == v"));
    Assert.That(text, Does.Contain("Box::<i32>::new(a)"));
    Assert.That(text, Does.Contain("pub struct BoxF64 {\n    v: f64,\n}"));
    Assert.That(text, Does.Contain("ply_same_value_f64(self.v, v)"));
    Assert.That(text, Does.Contain("BoxF64::new(a)"));
  }

  [Test]
  public void TheSignOfCompareTo_IsOrd() {
    var text = Emitted("""
      public sealed class Top<T> where T : System.IComparable<T> {
        private T _v;
        public Top(T v) { this._v = v; }
        public void Offer(T v) { if (v.CompareTo(this._v) > 0) this._v = v; }
        public T Get() => this._v;
      }
      public static class Use {
        public static long F(long a, long b) { var top = new Top<long>(a); top.Offer(b); return top.Get(); }
      }
      """);
    Assert.That(text, Does.Contain("impl<T: Ord + Default + Copy> Top<T> {"));
    Assert.That(text, Does.Contain("if v > self.v {"));
  }

  [Test]
  public void TwoParameters_StaticMembers_AndCallsOfMembers() {
    var text = Emitted("""
      public sealed class Pair<A, B> {
        private A _a;
        private B _b;
        public Pair(A a, B b) { this.Set(a, b); }
        public void Set(A a, B b) { this._a = a; this._b = b; }
        public A First() => Keep(this._a);
        public static A Keep(A a) => a;
        public B Second() => this._b;
        public static int Size() => 2;
        public int Twice() => Size() * 2;
      }
      public static class Use {
        public static int F(int a, double b) { var p = new Pair<int, double>(a, b); return p.First() + Pair<int, double>.Size() + p.Twice(); }
        public static double G(long a, double b) { var p = new Pair<long, double>(a, b); return p.Second(); }
      }
      """);
    Assert.That(text, Does.Contain("pub struct Pair<A, B> {\n    a: A,\n    b: B,\n}"));
    Assert.That(text, Does.Contain("impl<A: Default + Copy, B: Default + Copy> Pair<A, B> {"));
    Assert.That(text, Does.Contain("Self::size().wrapping_mul(2)"));
    Assert.That(text, Does.Contain("Pair::<i32, f64>::new(a, b)"));
    Assert.That(text, Does.Contain("Pair::<i32, f64>::size()"));
    Assert.That(text, Does.Contain("Pair::<i64, f64>::new(a, b)"));
  }

  // The classes and instantiations that keep a struct of their own.

  [TestCase("public string Show() => \"v\" + this._v;", TestName = "A class whose template the frontend could not state")]
  [TestCase("public T[] Both() => new[] { this._v, this._v };", TestName = "A member that gives an array")]
  [TestCase("public void Fill(T[] a) { a[0] = this._v; }", TestName = "A member that takes an array")]
  public void AClassTheBackendCannotWriteGeneric_KeepsOneStructPerInstantiation(string member) {
    var text = Emitted(Cell.Replace("public T Get()", member + " public T Get()", StringComparison.Ordinal) + """
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); return c.Get(); }
        public static long G(long a) { var c = new Cell<long>(a); return c.Get(); }
      }
      """);
    Assert.That(text, Does.Contain("pub struct CellI32").And.Contain("pub struct CellI64"));
    Assert.That(text, Does.Not.Contain("<T"));
  }

  [Test]
  public void AClassOfSharedObjects_KeepsOneStructPerInstantiation() {
    var text = Emitted(Cell + """
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); var d = c; return c.Get() + d.Get(); }
      }
      """);
    Assert.That(text, Does.Contain("pub struct CellI32"));
    Assert.That(text, Does.Not.Contain("<T"));
  }

  [Test]
  public void AParameterNoReadFieldHas_KeepsOneStructPerInstantiation() {
    var text = Emitted("""
      public sealed class Sink<T> {
        private T _v;
        private int _n;
        public void Put(T v) { this._v = v; this._n++; }
        public int Count() => this._n;
      }
      public static class Use {
        public static int F(int a) { var s = new Sink<int>(); s.Put(a); return s.Count(); }
      }
      """);
    Assert.That(text, Does.Contain("pub struct SinkI32"));
    Assert.That(text, Does.Not.Contain("<T"));
  }

  [Test]
  public void AClassAndAGenericClassOfOneName_KeepOneStructPerInstantiation() {
    var text = Emitted(Cell + """
      public sealed class Cell {
        public int V;
      }
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); var o = new Cell(); o.V = 1; return c.Get() + o.V; }
      }
      """);
    Assert.That(text, Does.Contain("pub struct CellI32"));
    Assert.That(text, Does.Not.Contain("<T"));
  }

  [Test]
  public void AClassAsTypeArgument_KeepsItsOwnStruct_BesideTheGenericOne() {
    var text = Emitted(Cell + """
      public sealed class Thing {
        public int V;
      }
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); return c.Get(); }
        public static bool G() { var c = new Cell<Thing>(null); return c.Get() == null; }
      }
      """);
    Assert.That(text, Does.Contain("pub struct Cell<T> {"));
    Assert.That(text, Does.Contain("Cell::<i32>::new(a)"));
    Assert.That(text, Does.Contain("CellThing"));
  }

  [Test]
  public void EveryIntegerTypeUsed_LeavesNoStandIn() {
    var text = Emitted(Cell.Replace("public T Get()", "public long Mix(sbyte b, byte c, short d, ushort e, int f, uint g, long h, ulong i) => b + c + d + e + f + g + h + (long)i; public T Get()", StringComparison.Ordinal) + """
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); return c.Get() + (int)c.Mix(1, 2, 3, 4, 5, 6, 7, 8); }
      }
      """);
    Assert.That(text, Does.Contain("pub struct CellI32"));
  }

  [Test]
  public void AnInstantiationWhoseFunctionsAreNotTheTemplates_KeepsItsOwnStruct() {
    var module = PrototypeFixture.Import("Snippet.cs", Cell + """
      public static class Use {
        public static int F(int a) { var c = new Cell<int>(a); return c.Get(); }
        public static long G(long a) { var c = new Cell<long>(a); return c.Get(); }
      }
      """);
    // The getter of Cell<i64> rewritten, as a pass might have done, is no longer the template instantiated.
    var get = module.Functions.Single(function => function.Name == "Cell<i64>.Get");
    var changed = get with { Body = new IrBlock([new IrReturn(new IrConst(IrType.I64, 7))]) };
    var text = Emitted(module with { Functions = [.. module.Functions.Select(function => ReferenceEquals(function, get) ? changed : function)] }, OptimizationLevel.None);
    Assert.That(text, Does.Contain("pub struct Cell<T> {"));
    Assert.That(text, Does.Contain("pub struct CellI64"));
    Assert.That(text, Does.Contain("Cell::<i32>::new(a)"));
  }

}
