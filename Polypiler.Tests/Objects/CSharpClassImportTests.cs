using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The classes the C# frontend imports beside the static class - their members as the IR has them and
/// what the interpreter computes of them - and the constructs of the construct table it refuses by name.
/// </summary>
[TestFixture]
public sealed class CSharpClassImportTests {

  private const string Counter = """
    public sealed class Counter {
      public static int Created;
      private int _count;
      public Counter(int start) { _count = start; ++Created; }
      public Counter() : this(0) { }
      public int Step { get; set; } = 1;
      public int Count => _count;
      public int Next() { _count += Step; return _count; }
    }
    """;

  private static IrModule Import(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static object? Run(string source, params object[] arguments) => IrInterpreter.Call(Import(source), "F", arguments);

  private static Diagnostic Refusal(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Is.Not.Empty);
    return result.Diagnostics[0];
  }

  private static IrClassType ClassOf(IrModule module, string name) => module.Types.OfType<IrClassType>().Single(type => type.Name == name);

  /// <summary>Test design 2: every member kind of a class becomes its part of the IR class.</summary>
  [Test]
  public void MemberKinds_BecomeTheirPartsOfTheClass() {
    var module = Import(Counter + """
      public sealed class Box {
        private readonly int[] _cells;
        public const int Size = 4;
        public Box() { _cells = new int[Size]; }
        public int Fixed { get; } = 7;
        public long Wide { get; init; }
        public int Twice { get => Fixed * 2; set => _cells[0] = value; }
        public static Box Make() => new Box();
        private int Hidden() => 1;
      }
      public static class C { public static int F() => 0; }
      """);
    var counter = ClassOf(module, "Counter");
    Assert.That(counter.Fields.Select(field => (field.Name, field.IsReadOnly, field.Visibility)),
      Is.EqualTo(new[] { ("_count", false, IrVisibility.Private), ("Step", false, IrVisibility.Public) }));
    Assert.That(counter.Constructors, Has.Count.EqualTo(2));
    Assert.That(counter.Methods.Select(method => (method.Name, method.Kind, method.IsStatic)),
      Is.EqualTo(new[] { ("Count", IrMethodKind.Getter, false), ("Next", IrMethodKind.Method, false) }));
    Assert.That(counter.Properties.Select(property => (property.Name, property.BackingField?.Name, property.HasSetter)),
      Is.EqualTo(new[] { ("Step", (string?)"Step", true), ("Count", null, false) }));
    Assert.That(counter.StaticFields.Select(global => global.Name), Is.EqualTo(new[] { "Counter.Created" }));
    Assert.That((counter.IsSealed, counter.IsAbstract, counter.Base, counter.Interfaces, counter.IsRecord), Is.EqualTo((true, false, (IrClassType?)null, Enumerable.Empty<IrInterfaceType>(), false)));

    var box = ClassOf(module, "Box");
    Assert.That(box.Fields.Select(field => (field.Name, field.IsReadOnly)), Is.EqualTo(new[] { ("_cells", true), ("Fixed", true), ("Wide", false) }));
    Assert.That(box.Properties.Select(property => (property.Name, property.IsInitOnly, property.Getter is null, property.Setter is null)),
      Is.EqualTo(new[] { ("Fixed", false, true, true), ("Wide", true, true, true), ("Twice", false, false, false) }));
    Assert.That(box.Methods.Single(method => method.Name == "Make").IsStatic, Is.True);
    Assert.That(box.Methods.Single(method => method.Name == "Hidden").Visibility, Is.EqualTo(IrVisibility.Private));
  }

  /// <summary>The scenario values: a counter started at 10 with a step of 3 is at 16 after two steps, and references alias.</summary>
  [TestCase("var c = new Counter(10); c.Step = 3; c.Next(); return c.Next();", 16)]
  [TestCase("var c = new Counter(10) { Step = 3 }; c.Next(); c.Next(); return c.Count;", 16)]
  [TestCase("var a = new Counter(); var b = a; b.Next(); return a.Count;", 1)]
  [TestCase("var before = Counter.Created; new Counter(); new Counter(4).Next(); return Counter.Created - before;", 2)]
  [TestCase("var c = new Counter(1); c.Step += 4; c.Next(); return c.Count;", 6)]
  public void Counter_ComputesWhatDotNetComputes(string body, int expected)
    => Assert.That(Run(Counter + " public static class C { public static int F() { " + body + " } }"), Is.EqualTo(expected));

  /// <summary>Test design 4: identity for classes, value equality for records, identity again by ReferenceEquals.</summary>
  [TestCase("var a = new Counter(); var b = a; return a == b && ReferenceEquals(a, b) && !(a != b);", true)]
  [TestCase("return new Counter() == new Counter();", false)]
  [TestCase("return new R(2, 9) == new R(2, 9) && new R(2, 9).Equals(new R(2, 9)) && new R(2, 9) != new R(2, 8);", true)]
  [TestCase("return ReferenceEquals(new R(2, 9), new R(2, 9));", false)]
  [TestCase("R r = null; return r == null && r is null && new R(1, 1) is not null && null != new R(1, 1);", true)]
  [TestCase("Counter c = null; return c == null && ReferenceEquals(null, c);", true)]
  [TestCase("return new R(2, 9).Length == 7;", true)]
  public void Equality_IsIdentityForClassesAndValueForRecords(string body, bool expected)
    => Assert.That(Run("#nullable disable\n" + Counter + " public sealed record R(int Lo, int Hi) { public int Length => Hi - Lo; } public static class C { public static bool F() { " + body + " } }"), Is.EqualTo(expected));

  /// <summary>Test design 5: the fields of a new object hold their defaults before the constructor body runs.</summary>
  [Test]
  public void Fields_HoldTheirDefaultsBeforeTheConstructorRuns() {
    const string source = """
      public struct P { public int X; }
      public sealed class D {
        public sbyte A; public byte B; public short C2; public ushort E; public int F2; public uint G; public long H; public ulong I;
        public float J; public double K; public bool L; public P M; public int[] N; public D O;
        public D() { Seen = A + B + C2 + E + F2 + (int)G + (int)H + (int)I + (int)J + (int)K + (L ? 1 : 0) + M.X + (O == null ? 0 : 1); }
        public int Seen;
      }
      public static class C { public static int F() => new D().Seen; }
      """;
    Assert.That(Run("#nullable disable\n" + source), Is.EqualTo(0));
  }

  /// <summary>Test design 9: every member reached through null traps with NullReference.</summary>
  [TestCase("Counter c = null; return c.Next();")]
  [TestCase("Counter c = null; return c.Count;")]
  [TestCase("Counter c = null; return c.Step;")]
  [TestCase("Counter c = null; c.Step = 2; return 0;")]
  public void NullReceiver_Traps(string body) {
    var module = Import("#nullable disable\n" + Counter + " public static class C { public static int F() { " + body + " } }");
    Assert.That(() => IrInterpreter.Call(module, "F", []), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.NullReference));
  }

  /// <summary>Test design 8: a class without fields, with one, and a constructor of eight parameters.</summary>
  [Test]
  public void Boundaries_OfFieldsAndParameters() {
    Assert.That(Run("public sealed class E { public int One() => 1; } public static class C { public static int F() => new E().One(); }"), Is.EqualTo(1));
    var fields = string.Join(" ", Enumerable.Range(0, 32).Select(index => $"public int F{index} = {index};"));
    var sum = string.Join(" + ", Enumerable.Range(0, 32).Select(index => $"w.F{index}"));
    Assert.That(Run($"public sealed class W {{ {fields} }} public static class C {{ public static int F() {{ var w = new W(); return {sum}; }} }}"), Is.EqualTo(496));
    Assert.That(Run("""
      public sealed class Eight { public int S; public Eight(int a, int b, int c, int d, int e, int f, int g, int h) { S = a + b + c + d + e + f + g + h; } }
      public static class C { public static int F() => new Eight(1, 2, 3, 4, 5, 6, 7, 8).S; }
      """), Is.EqualTo(36));
  }

  /// <summary>Static methods through the class name, overloads, struct fields of objects and fields of other objects of the class.</summary>
  [Test]
  public void Members_ThroughEveryForm() {
    const string source = """
      public struct P { public int X; }
      public sealed class V {
        public P Point;
        public int Value;
        public static V Of(int value) => new V { Value = value };
        public int Add(int x) => Value + x;
        public int Add(int x, int y) => Value + x + y;
        public int Plus(V other) => this.Value + other.Value;
      }
      public static class C {
        public static int F() {
          var v = V.Of(3);
          v.Point.X = 5;
          v.Point.X += 1;
          return v.Add(1) + v.Add(1, 2) + v.Plus(V.Of(10)) + v.Point.X;
        }
      }
      """;
    Assert.That(Run(source), Is.EqualTo(4 + 6 + 13 + 6));
  }

  /// <summary>Test design 10: what the construct table refuses, by name, with nothing written.</summary>
  [TestCase("public sealed class D : System.Exception { }", "inheritance from Exception, which the file does not declare")]
  [TestCase("public sealed class D : System.IDisposable { public void Dispose() { } }", "the interface IDisposable, which the file does not declare")]
  [TestCase("public sealed class G<T> : System.IDisposable { public void Dispose() { } }", "a generic class in a hierarchy")]
  [TestCase("public sealed class D { public event System.Action E; }", "an event")]
  [TestCase("public sealed class D { public int this[int i] => i; }", "an indexer")]
  [TestCase("public sealed class D { public static D operator +(D a, D b) => a; }", "an operator")]
  [TestCase("public sealed class D { ~D() { } }", "a finalizer")]
  [TestCase("public sealed class D { static D() { } }", "a static constructor")]
  [TestCase("public partial class D { }", "a partial class")]
  [TestCase("public class D { public extern int M(); }", "a extern method")]
  [TestCase("public sealed class D { public static int P { get; set; } }", "a static property")]
  [TestCase("public sealed class D { public int P { get => 1; private set { } } }", "an accessor with an accessibility of its own")]
  [TestCase("public sealed class D { public string S = \"\"; }", "a field of the type str")]
  [TestCase("public sealed class D { public int M<T>() => 1; }", "a generic method")]
  [TestCase("public sealed class D { public class N { } }", "a type nested in a class")]
  [TestCase("public sealed record D(int A) { } public static class X { public static int F() => (new D(1) with { A = 2 }).A; }", "a with expression")]
  public void Construct_IsRefusedByName(string declaration, string what) {
    var source = declaration.Contains("static class X") ? declaration : declaration + " public static class C { public static int F() => 0; }";
    var diagnostic = Refusal("#nullable disable\n" + source);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo((CSharpFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

  /// <summary>A file of classes without the static class still says what it lacks.</summary>
  [Test]
  public void OnlyClasses_AreRefusedAsNotStatic()
    => Assert.That(Refusal("public sealed class D { }").Message, Is.EqualTo("'a class that is not static' is not supported yet"));
}
