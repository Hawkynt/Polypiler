using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The hierarchies the C# frontend imports: every kind of dispatch, declaration and type test against
/// what .NET computes of the same source, and the constructs it refuses by name.
/// </summary>
[TestFixture]
public sealed class CSharpDispatchImportTests {

  /// <summary>The source imported, verified and run by the interpreter, against the C# compiler's build of it, for each call of F.</summary>
  private static void AgreesWithDotNet(string source, params object[][] calls) {
    var text = "#nullable disable\n" + source;
    var module = PrototypeFixture.Import("Snippet.cs", text);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    // A fresh load per call: the static fields of the build start anew, as the interpreter's do.
    foreach (var arguments in calls.Length == 0 ? [[]] : calls) {
      var call = new PrototypeCall("F", arguments);
      Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(GeneratedCSharp.Compile(text, false).Outcome(call)), call.ToString());
    }
  }

  private static string Refusal(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\n" + source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics[0].Code, Is.EqualTo(CSharpFrontend.Unsupported));
    return result.Diagnostics[0].Message;
  }

  /// <summary>Test design 2: direct, virtual, interface, base and sealed-override calls select what .NET selects.</summary>
  [TestCase("public class A { public int M() => 1; } public sealed class B : A { } public static class C { public static int F() => new B().M(); }")]
  [TestCase("public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } "
    + "public static class C { public static int F() { A a = new B(); return a.M() * 10 + new A().M(); } }")]
  [TestCase("public interface I { int M(); } public class A : I { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } "
    + "public static class C { public static int F() { I i = new B(); I j = new A(); return i.M() * 10 + j.M(); } }")]
  [TestCase("public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => base.M() + 5; } public static class C { public static int F() => new B().M(); }")]
  [TestCase("public class A { public virtual int M() => 1; } public class B : A { public sealed override int M() => 2; } public sealed class D : B { } "
    + "public static class C { public static int F() { A a = new D(); return a.M(); } }")]
  [TestCase("public abstract class A { protected int Twice(int x) => 2 * x; public abstract int M(); } public sealed class B : A { public override int M() => this.Twice(21); } "
    + "public static class C { public static int F() { A a = new B(); return a.M(); } }")]
  public void CallKinds_SelectWhatDotNetSelects(string source) => AgreesWithDotNet(source);

  /// <summary>Test design 3: abstract classes and members, interfaces implemented implicitly, explicitly and through an inherited interface, with properties.</summary>
  [Test]
  public void Declarations_BecomeTheirHierarchy() {
    const string source = """
      public interface INamed { int Id { get; } }
      public interface IShape : INamed { double Area(); }
      public abstract class Shape : IShape {
        public abstract double Area();
        public int Id { get { return 7; } }
      }
      public sealed class Square : Shape, System.IComparable {
        public double Side;
        public Square(double side) { Side = side; }
        public override double Area() => Side * Side;
        int System.IComparable.CompareTo(object other) => 0;
      }
      public static class C { public static int F() => 0; }
      """;
    Assert.That(Refusal(source), Is.EqualTo("'the interface IComparable, which the file does not declare' is not supported yet"));
    AgreesWithDotNet("""
      public interface INamed { int Id { get; } }
      public interface IShape : INamed { double Area(); }
      public interface ICounted { int Count(); }
      public abstract class Shape : IShape {
        public abstract double Area();
        public int Id { get { return 7; } }
      }
      public sealed class Square : Shape, ICounted {
        private readonly double _side;
        public Square(double side) { _side = side; }
        public override double Area() => _side * _side;
        int ICounted.Count() => 4;
      }
      public static class C {
        public static double F(double side) {
          IShape shape = new Square(side);
          INamed named = shape;
          ICounted counted = new Square(side);
          return shape.Area() + named.Id + counted.Count();
        }
      }
      """, [3.0], [0.5]);
    var module = PrototypeFixture.Import("Snippet.cs", "public interface I : J { int M(); } public interface J { int P { get; set; } } public abstract class A : I { public abstract int M(); "
      + "public int P { get => 1; set { } } } public static class C { public static int F() => 0; }");
    var (contract, inherited) = (module.Types.OfType<IrInterfaceType>().Single(type => type.Name == "I"), module.Types.OfType<IrInterfaceType>().Single(type => type.Name == "J"));
    Assert.That((contract.BaseInterfaces.Single(), contract.Closure.Count(), inherited.Properties.Single().Name), Is.EqualTo((inherited, 2, "P")));
    var owner = module.Types.OfType<IrClassType>().Single();
    Assert.That((owner.IsAbstract, owner.Methods.Single(method => method.Name == "M").Dispatch), Is.EqualTo((true, IrDispatch.Abstract)));
    Assert.That(owner.Methods.Where(method => method.Kind != IrMethodKind.Method).SelectMany(method => method.Implements), Is.EquivalentTo(inherited.Methods));
  }

  /// <summary>Test design 5: is, as and a cast of an instance of the type, of a subclass, of an unrelated class and of null, to a class and to an interface.</summary>
  [TestCase(0), TestCase(1), TestCase(2), TestCase(3)]
  public void TypeTests_AgreeForEveryOperand(int which) => AgreesWithDotNet("""
    public interface I { int M(); }
    public class T : I { public virtual int M() => 1; }
    public sealed class S : T { public override int M() => 2; }
    public sealed class U { }
    public class O { }
    public sealed class Q : O, I { public int M() => 3; }
    public static class C {
      private static O Pick(int which) {
        if (which == 0) return new O();
        if (which == 1) return new Q();
        return null;
      }
      private static T Of(int which) {
        if (which == 0) return new T();
        if (which == 1) return new S();
        return null;
      }
      public static int F(int which, int kind) {
        T t = Of(which);
        O o = Pick(which);
        if (kind == 0) return (t is S ? 1 : 0) + (t is T ? 2 : 0) + (t is I ? 4 : 0) + (o is Q ? 8 : 0) + (o is I ? 16 : 0) + (o is not Q ? 32 : 0);
        if (kind == 1) return (t as S == null ? 1 : 0) + (o as Q == null ? 2 : 0) + (o as I == null ? 4 : 0);
        if (kind == 2) return ((S)t) == null ? -1 : ((S)t).M();
        if (kind == 3) return ((I)o) == null ? -1 : ((I)o).M();
        return ((Q)o) == null ? -1 : ((Q)o).M();
      }
    }
    """, [which, 0], [which, 1], [which, 2], [which, 3], [which, 4]);

  /// <summary>Test design 6: hierarchies of depth 1, 2 and 5, overridden at every level and only at the bottom.</summary>
  [TestCase(1, true), TestCase(2, true), TestCase(5, true), TestCase(2, false), TestCase(5, false)]
  public void DeepHierarchies_SelectTheNearestOverride(int depth, bool everyLevel) {
    var classes = "public class L0 { public virtual int M() => 0; public int Call() => M(); }";
    for (var level = 1; level <= depth; ++level)
      classes += $" public class L{level} : L{level - 1} {{ {(everyLevel || level == depth ? $"public override int M() => {level} + base.M() * 10;" : "")} }}";

    var calls = string.Join(" + ", Enumerable.Range(0, depth + 1).Select(level => $"new L{level}().Call() * {(int)System.Math.Pow(100, level)}"));
    AgreesWithDotNet(classes + $" public static class C {{ public static int F() => {calls}; }}");
  }

  /// <summary>Test design 8: a virtual call on 'this' during construction is refused; a direct call, a call on another object and one after construction are not.</summary>
  [TestCase("public class A { public A() { Init(); } public virtual void Init() { } } public sealed class B : A { public override void Init() { } }", true)]
  [TestCase("public class A { public virtual void Init() { } } public sealed class B : A { public B() { Init(); } public override void Init() { } }", true)]
  [TestCase("public interface I { void Init(); } public sealed class B : I { public B() { Help(this); } private static void Help(I i) => i.Init(); public void Init() { } }", true)]
  [TestCase("public class A { public A() { Init(); } public void Init() { } } public sealed class B : A { }", false)]
  [TestCase("public class A { public virtual int M() => 1; } public sealed class B : A { public int V; public B() { V = new A().M(); } public override int M() => 2; }", false)]
  [TestCase("public class A { public virtual int M() => 1; public int Call() => M(); } public sealed class B : A { public override int M() => 2; }", false)]
  public void VirtualCallDuringConstruction_IsRefused(string classes, bool refused) {
    var source = "#nullable disable\n" + classes + " public static class C { public static int F() { new B(); return 1; } }";
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Module is null, Is.EqualTo(refused), string.Join("; ", result.Diagnostics));
    if (refused)
      Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("'a virtual call on 'this' during construction' is not supported yet"));
  }

  /// <summary>Test design 9 and the construct table: what is refused, by name, with nothing written.</summary>
  [TestCase("public class A { public int M() => 1; } public sealed class B : A { public new int M() => 2; }", "new member hiding")]
  [TestCase("public class A { public int M() => 1; } public sealed class B : A { public int M() => 2; }", "new member hiding")]
  [TestCase("public class A { public int P => 1; } public sealed class B : A { public new int P => 2; }", "new member hiding")]
  [TestCase("public interface I { int M() => 1; }", "a default interface method")]
  [TestCase("public interface I { int P { get => 1; } }", "a default interface method")]
  [TestCase("public interface I { static abstract int M(); }", "a static abstract member")]
  [TestCase("public interface I<T> { T M(); }", "a generic interface")]
  [TestCase("public interface I { int M<T>(); }", "a generic method")]
  [TestCase("public interface I { event System.Action E; }", "an event")]
  [TestCase("public interface I { public int M(); }", "a public interface member")]
  [TestCase("public partial interface I { }", "a partial interface")]
  [TestCase("[System.Obsolete] public interface I { }", "the attribute System.Obsolete")]
  [TestCase("public interface I : System.IDisposable { }", "the interface IDisposable, which the file does not declare")]
  [TestCase("public interface I : System.IEquatable<int> { }", "a generic interface")]
  [TestCase("public sealed class D : System.IEquatable<D> { public bool Equals(D other) => true; }", "a generic interface")]
  [TestCase("public sealed class D { public override string ToString() => \"d\"; }", "overriding the object member ToString")]
  [TestCase("public class A { public virtual int P => 1; }", "a virtual, abstract or overriding property")]
  [TestCase("public interface I { int P { get; } } public sealed class D : I { int I.P => 1; }", "an explicit implementation of a property")]
  [TestCase("public interface I { int P { get; } } public sealed class D : I { public int P { get; } }", "an auto-property that implements a property of an interface")]
  [TestCase("public record A(int X); public sealed record B(int X) : A(X);", "a record in a hierarchy")]
  [TestCase("public class A { public A(int x) { } } public sealed class B : A { public B() : base(x: 1) { } }", "a named argument")]
  [TestCase("public class A { } public sealed class B : A { public int V = Next(); private static int Next() => 1; }", "an initializer of a field of a derived class that is not a constant")]
  [TestCase("public class A { } public static class C { public static int F(object o) => o is A ? 1 : 0; }", "object")]
  [TestCase("public class A { } public sealed class B : A { } public sealed class D : A { } public static class C { public static int F(bool b) { A a = b ? (A)new B() : new D(); return 0; } }",
    "a conditional expression whose branches are objects of two classes")]
  public void Construct_IsRefusedByName(string declarations, string what) {
    var source = declarations.Contains("static class C") ? declarations : declarations + " public static class C { public static int F() => 0; }";
    Assert.That(Refusal(source), Is.EqualTo($"'{what}' is not supported yet"));
  }

  /// <summary>A pattern variable used outside its if, and a pattern elsewhere than the condition of an if, are refused.</summary>
  [TestCase("public static int F(A a) { if (!(a is B b)) return 0; return b.V; }", "a pattern declaration outside the condition of an if")]
  [TestCase("public static int F(A a) { if (a is B b) { } else { return 0; } return b.V; }", "a pattern variable used outside the statement its if guards")]
  [TestCase("public static int F(int x) { if (x is int y) return y; return 0; }", "a pattern declaration of a type other than a class or an interface")]
  [TestCase("public static int F(int x) { return x is A ? 1 : 0; }", "a type test or a cast of a value of the type i32")]
  public void Pattern_IsRefusedWhereItCannotStand(string method, string what)
    => Assert.That(Refusal("public class A { } public sealed class B : A { public int V; } public static class C { " + method + " }"), Is.EqualTo($"'{what}' is not supported yet"));

  /// <summary>A pattern on an expression that is no variable tests a temporary that holds it, evaluated once.</summary>
  [Test]
  public void Pattern_OnAnExpression_EvaluatesItOnce() => AgreesWithDotNet("""
    public class A { public static int Made; public A() { ++Made; } }
    public sealed class B : A { public int V = 4; }
    public static class C {
      private static A Make(bool b) {
        if (b)
          return new B();

        return new A();
      }
      public static int F(bool b) {
        if (Make(b) is B made)
          return made.V * 10 + A.Made;
        return A.Made;
      }
    }
    """, [true], [false]);

}
