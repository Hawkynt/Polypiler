using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Generics;

/// <summary>
/// Generic methods and classes of C#, monomorphized by the frontend: the instantiations the module
/// reaches, what each computes against .NET's build of the same source, the templates, the
/// instantiations <c>--instantiate</c> asks for, and every construct refused by name.
/// </summary>
[TestFixture]
internal sealed class CSharpGenericsImportTests {

  /// <summary>The calls of <see cref="GenericsFixtures.Calls"/> as test cases.</summary>
  public static IEnumerable<TestCaseData> Calls()
    => GenericsFixtures.Calls().Select(call => new TestCaseData(call.File, call.Call).SetName($"{call.Class}.{call.Call}"));

  private static string Refusal(string source, params CSharpInstantiation[] instantiations) {
    var result = CSharpFrontend.Import([new CSharpSource("Snippet.cs", "#nullable disable\n" + source)], false, instantiations);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics[0].Code, Is.EqualTo(CSharpFrontend.Unsupported));
    return result.Diagnostics[0].Message;
  }

  /// <summary>Test design 1: the fixtures import into IR the verifier accepts, one function or class per instantiation, each with its origin.</summary>
  [Test]
  public void Fixtures_ImportIntoWellTypedIr() {
    var search = GenericsFixtures.Imported("Search");
    Assert.That(IrVerifier.Verify(search), Is.Empty);
    var instances = search.Functions.Where(function => function.Origin is not null).ToList();
    Assert.That(instances.Select(function => function.Name), Is.EquivalentTo(new[] {
      "IndexOf<f64>", "IndexOf<i32>", "Swap<i32>", "Swap<f64>", "Max<f64>", "Max<i32>", "Def<i32>", "Def<f64>", "Def<bool>", "Def<Box>", "Make<Box>"
    }));
    Assert.That(instances.All(function => function.Origin!.Template is not null), Is.True);
    var indexOf = instances.First(function => function.Name == "IndexOf<f64>").Origin!;
    Assert.That((indexOf.Name, indexOf.Display, indexOf.TypeArguments.Single()), Is.EqualTo(("IndexOf", "IndexOf<T>", IrType.F64)));
    Assert.That(indexOf.Constraints, Is.EqualTo(new[] { new IrGenericConstraint("T", IrGenericConstraintKind.Equatable) }));
    Assert.That(instances.First(function => function.Name == "Make<Box>").Origin!.Constraints.Single().Kind, Is.EqualTo(IrGenericConstraintKind.New));
    Assert.That(indexOf.Template!.Function.Name, Is.EqualTo("IndexOf<T>"));

    var ring = GenericsFixtures.Imported("Ring");
    Assert.That(IrVerifier.Verify(ring), Is.Empty);
    var classes = ring.Types.OfType<IrClassType>().ToList();
    Assert.That(classes.Select(type => type.Name), Is.EqualTo(new[] { "Ring<i64>", "Ring<f64>" }));
    Assert.That(classes.Select(type => type.Origin!.Display).Distinct(), Is.EqualTo(new[] { "Ring<T>" }));
    Assert.That(classes[0].Fields.Select(field => field.Type), Is.EqualTo(new[] { IrType.Array(IrType.I64), IrType.I32 }));
  }

  /// <summary>Test design 1 and 5: the interpreter computes what .NET computes of the fixtures.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_AgreesWithDotNet(string file, PrototypeCall call) {
    var className = GenericsFixtures.CSharp.First(entry => entry.File == file).Class;
    var expected = PrototypeFixture.InvokedOutcome(GenericsFixtures.Compiled(className, call.Method), call);
    Assert.That(PrototypeFixture.InterpretedOutcome(GenericsFixtures.Imported(file), call), Is.EqualTo(expected));
  }

  /// <summary>The values the acceptance criteria name, as .NET computes them.</summary>
  [Test]
  public void DotNet_GivesTheValuesOfTheIssue() {
    object? Run(string className, string method) => GenericsFixtures.Compiled(className, method).Invoke(null, []);
    Assert.That(new[] { Run("SearchDemo", "IndexOfNaN"), Run("SearchDemo", "IndexOfZero"), Run("SearchDemo", "IndexOfSeven"), Run("SearchDemo", "IndexOfFour") }, Is.EqualTo(new object[] { 1, 0, 2, -1 }));
    Assert.That(new[] { Run("SearchDemo", "DefInt"), Run("SearchDemo", "DefDouble"), Run("SearchDemo", "DefBool"), Run("SearchDemo", "DefBoxIsNull") }, Is.EqualTo(new object[] { 0, 0.0, false, true }));
    Assert.That((Run("RingDemo", "LastLong"), Run("RingDemo", "CountLong"), Run("RingDemo", "ReciprocalOfLastDouble")), Is.EqualTo((9007199254740993L, 4, double.NegativeInfinity)));
    Assert.That(GenericsFixtures.Compiled("SearchDemo", "MakeBoxIsNew").Invoke(null, []), Is.EqualTo(true));
    var recursion = typeof(GenericsFixtures).Assembly.GetType(GenericsFixtures.Namespace + ".RecursionDemo", true)!.GetMethod("Run")!;
    Assert.That(recursion.Invoke(null, []), Is.EqualTo(3));
  }

  /// <summary>Test design 2: an instantiation over each scalar type computes what .NET's instantiation computes: search, maximum, order and equality.</summary>
  [TestCase("sbyte", new object[] { (sbyte)-128, (sbyte)0, (sbyte)127 })]
  [TestCase("byte", new object[] { (byte)0, (byte)100, (byte)255 })]
  [TestCase("short", new object[] { (short)-32768, (short)0, (short)32767 })]
  [TestCase("ushort", new object[] { (ushort)0, (ushort)7, (ushort)65535 })]
  [TestCase("int", new object[] { int.MinValue, 0, int.MaxValue })]
  [TestCase("uint", new object[] { 0u, 5u, uint.MaxValue })]
  [TestCase("long", new object[] { long.MinValue, 9007199254740993L, long.MaxValue })]
  [TestCase("ulong", new object[] { 0ul, 3ul, ulong.MaxValue })]
  [TestCase("float", new object[] { float.NaN, -0.0f, 0.0f, float.NegativeInfinity })]
  [TestCase("double", new object[] { double.NaN, -0.0, 0.0, double.NegativeInfinity })]
  [TestCase("bool", new object[] { false, true })]
  public void Instantiation_OverEveryScalarType_AgreesWithDotNet(string type, object[] values) {
    var text = "#nullable disable\n" + PrototypeFixture.Wrap($$"""
      static int IndexOf<T>(T[] a, T v) where T : System.IEquatable<T> { for (var i = 0; i < a.Length; ++i) if (a[i].Equals(v)) return i; return -1; }
      static T Max<T>(T a, T b) where T : System.IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
      static void Swap<T>(T[] a, int i, int j) { var t = a[i]; a[i] = a[j]; a[j] = t; }
      static bool Same<T>(T a, T b) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b);
      static int Order<T>(T a, T b) => System.Collections.Generic.Comparer<T>.Default.Compare(a, b);
      {{(type == "bool" ? "" : $"public static int Find({type}[] a, {type} v) => IndexOf(a, v);")}}
      public static {{type}} Larger({{type}} a, {{type}} b) => Max(a, b);
      {{(type == "bool" ? "" : $"public static {type} Swapped({type}[] a) {{ Swap(a, 0, a.Length - 1); return a[0]; }}")}}
      public static bool Equal({{type}} a, {{type}} b) => Same(a, b) && a.Equals(b);
      public static int Compared({{type}} a, {{type}} b) => Order(a, b) * 1000 + a.CompareTo(b);
      """);
    var module = PrototypeFixture.Import("Snippet.cs", text);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var compiled = GeneratedCSharp.Compile(text, false);
    var array = Array.CreateInstance(values[0].GetType(), values.Length);
    Array.Copy(values, array, values.Length);
    var calls = new List<PrototypeCall>();
    if (type != "bool")
      calls.Add(new("Swapped", [array]));

    foreach (var a in values) {
      if (type != "bool")
        calls.Add(new PrototypeCall("Find", [array, a]));

      foreach (var b in values)
        calls.AddRange([new("Larger", [a, b]), new("Equal", [a, b]), new("Compared", [a, b])]);
    }

    foreach (var call in calls)
      Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(compiled.Outcome(call)), call.ToString());
  }

  /// <summary>default and new() follow the type argument: a number, a truth value, a struct and a class.</summary>
  [Test]
  public void DefaultAndNew_FollowTheTypeArgument() {
    var text = "#nullable disable\n" + """
      public struct P { public int X; }
      public sealed class B { public int V = 3; }
      public static class Snippet {
        static T D<T>() => default;
        static T N<T>() where T : new() => new T();
        public static int F() => D<int>() + N<int>() + D<P>().X + N<P>().X + N<B>().V + (D<B>() == null ? 10 : 0) + (D<bool>() || N<bool>() ? 100 : 0);
      }
      """;
    var module = PrototypeFixture.Import("Snippet.cs", text);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("F", [])), Is.EqualTo(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("F", []))));
  }

  /// <summary>A generic body carries the statements and values of the other constructs: try, catch, finally, throw, using, optional values and a throw expression.</summary>
  [Test]
  public void GenericBody_CarriesEveryConstruct() {
    var text = "#nullable disable\n" + """
      public sealed class R : System.IDisposable { public int N; public void Dispose() { this.N = this.N + 1; } }
      public static class Snippet {
        static T At<T>(T[] a, int i) {
          try {
            if (i < 0) throw new System.ArgumentException();
            return a[i];
          } catch (System.IndexOutOfRangeException) when (i > 100) {
            return a[0];
          } finally {
            i = 0;
          }
        }
        static T First<T>(T[] a) => a.Length > 0 ? a[0] : throw new System.InvalidOperationException();
        static int? Index<T>(T[] a, T v) where T : System.IEquatable<T> {
          int? found = null;
          for (var i = 0; i < a.Length; ++i) if (found == null && a[i].Equals(v)) found = i;
          return found;
        }
        static int Count<T>(T[] a, R r) { using (var s = r) { return a?.Length ?? s.N; } }
        public static int F(int i) {
          var a = new[] { 4, 5, 6 };
          var r = new R();
          return At(a, i) + First(a) * 10 + (Index(a, 6) ?? -1) * 100 + (Index(a, 7).HasValue ? 1000 : 0) + Count(a, r) * 10000 + r.N * 100000;
        }
      }
      """;
    var module = PrototypeFixture.Import("Snippet.cs", text);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var compiled = GeneratedCSharp.Compile(text, false);
    foreach (var i in new[] { 0, 2, 3, 101 })
      Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("F", [i])), Is.EqualTo(compiled.Outcome(new PrototypeCall("F", [i]))), i.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // A template that instantiates to its instances is written back as a generic method; the others stay instances.
    var emitted = Polypiler.Core.Backends.CSharp.CSharpEmitter.Emit(module)[0].Content;
    Assert.That(emitted, Does.Contain("First<T>(").And.Contain("Index<T>(").And.Contain("Count<T>("), emitted);
    Assert.That(GeneratedCSharp.ComplaintsFor(emitted, "net10.0", "14"), Is.Empty, emitted);
  }

  /// <summary>Test design 8: a private generic nothing calls is dropped; a public one is refused unless --instantiate names its arguments, which also reaches what it calls.</summary>
  [Test]
  public void InstantiationCount_DecidesWhatIsLowered() {
    const string Source = "public static class C { static int Unused<T>(T a) => 1; public static T Id<T>(T a) => Pick(a); static T Pick<T>(T a) => a; }";
    Assert.That(Refusal(Source), Is.EqualTo("'Id' has no instantiation; name one with --instantiate"));
    var result = CSharpFrontend.Import([new CSharpSource("C.cs", Source)], false, [new CSharpInstantiation("Id", ["double"]), new CSharpInstantiation("Id", ["int[]"])]);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Functions.Select(function => function.Name), Is.EqualTo(new[] { "Id<f64>", "Id<i32[]>", "Pick<f64>", "Pick<i32[]>" }));
    Assert.That(result.Module.Functions.All(function => function.IsPublic == function.Name.StartsWith("Id", StringComparison.Ordinal)), Is.True);
    var once = CSharpFrontend.Import([new CSharpSource("C.cs", "public static class C { public static T Id<T>(T a) => a; public static int F() => Id(3); }")], false, [new CSharpInstantiation("Id", ["int"])]);
    Assert.That(once.Module!.Functions.Count(function => function.Name == "Id<i32>"), Is.EqualTo(1));
  }

  /// <summary>Test design 10: an instantiation that names no generic method, a type that is none or another number of arguments is a mistake of the command line.</summary>
  [TestCase("Nope", "int", "names 'Nope', which is no generic method of the class")]
  [TestCase("Id", "Nope", "names the type 'Nope', which neither C# nor the file declares")]
  [TestCase("Id", "int,int", "gives 'Id' 2 type arguments; it takes 1")]
  public void UnknownInstantiation_IsRefused(string method, string types, string message) {
    var exception = Assert.Throws<ArgumentException>(() => CSharpFrontend.Import([new CSharpSource("C.cs", "public static class C { public static T Id<T>(T a) => a; }")], false,
      [new CSharpInstantiation(method, types.Split(','))]));
    Assert.That(exception!.Message, Does.Contain(message));
    Assert.Throws<ArgumentNullException>(() => CSharpFrontend.Import([new CSharpSource("C.cs", "public static class C { }")], false, null!));
  }

  /// <summary>The form --instantiate takes: a name and type arguments in brackets.</summary>
  [TestCase("IndexOf[double]", "IndexOf", "double")]
  [TestCase("Pair[int, Box[]]", "Pair", "int|Box[]")]
  [TestCase("_x[System.Int32]", "_x", "System.Int32")]
  public void Instantiation_IsRead(string text, string method, string types) {
    Assert.That(CSharpInstantiation.TryParse(text, out var read), Is.True);
    Assert.That((read!.Method, string.Join('|', read.TypeArguments)), Is.EqualTo((method, types)));
  }

  /// <summary>Test design 10: what is no instantiation.</summary>
  [TestCase("IndexOf")]
  [TestCase("[double]")]
  [TestCase("IndexOf[]")]
  [TestCase("IndexOf[double")]
  [TestCase("1x[int]")]
  [TestCase("Index Of[int]")]
  [TestCase("IndexOf[int,]")]
  [TestCase("IndexOf[in-t]")]
  public void Instantiation_IsRefused(string text) => Assert.That(CSharpInstantiation.TryParse(text, out _), Is.False);

  /// <summary>Test design 9: what the construct table refuses, by name.</summary>
  [TestCase("static int G<T>(T a) where T : System.Numerics.INumber<T> => 1; public static int F() => G<int>(1);", "", "generic math (System.Numerics.INumber<T>)")]
  [TestCase("public static int F() => G<int>(); static int G<T>() => typeof(T) == null ? 1 : 0;", "", "typeof(T)")]
  [TestCase("public static bool F() => G<int>(1); static bool G<T>(int a) => a is T;", "", "is T")]
  [TestCase("public static bool F() => G<B>(new B()); static bool G<T>(B a) where T : class => a as T == null;", "", "as T")]
  [TestCase("public static bool F() => G<B>(new B()); static bool G<T>(B a) where T : class => a is T t;", "", "is T")]
  [TestCase("public static int F(System.Func<int> f) { f += f; return 1; }", "", "a multicast delegate")]
  [TestCase("public static int F() => Depth<int>(3); static int Depth<T>(int n) => n == 0 ? 0 : 1 + Depth<T[]>(n - 1);", "", "the instantiations of 'Depth<T>' do not end")]
  [TestCase("static int G<T>(T a) where T : A => 1; public static int F() => G<A>(new A());", "", "the constraint Polypiler.Snippet.A")]
  [TestCase("public static string F() => G<string>(); static T G<T>() => default;", "", "default of the type str, which is null")]
  [TestCase("public static int[] F() => G<int[]>(); static T G<T>() => default;", "", "default of the type i32[], which is null")]
  [TestCase("public static bool F() => System.Collections.Generic.EqualityComparer<string>.Default.Equals(\"a\", \"b\");", "", "EqualityComparer.Equals on str")]
  [TestCase("public static int F() => new R<int>().V;", "public sealed class R<T> { public int V; public static int S; }", "a static field of a generic class")]
  [TestCase("public static int F() => new R<int>().M<int>();", "public sealed class R<T> { public int M<U>() => 1; }", "a generic method")]
  [TestCase("public static int F() => 0;", "public interface I<out T> { }", "variance (in or out on a type parameter)")]
  [TestCase("public static int F() => 0;", "public interface I<T> { }", "a generic interface")]
  [TestCase("public static int F() => 0;", "public abstract class A<T> { }", "a generic class in a hierarchy")]
  [TestCase("public static int F() => G<int>(); extern static int G<T>();", "", "extern")]
  public void Construct_IsRefusedByName(string members, string declarations, string what) {
    var message = Refusal($"namespace Polypiler.Snippet {{ public class A {{ }} public sealed class B {{ }} {declarations} public static class C {{ {members} }} }}");
    Assert.That(message, Is.EqualTo($"'{what}' is not supported yet"));
  }

  /// <summary>Instantiations of instantiations that never repeat end at the limit: a generic class wraps the type argument ever anew.</summary>
  [Test]
  public void InstantiationsThatDoNotEnd_AreRefusedAtTheLimit() {
    const string Source = """
      public sealed class W<T> { public int V; }
      public static class C {
        public static int F() => G<int>(3);
        static int G<T>(int n) => n == 0 ? 0 : H<W<T>>(n - 1);
        static int H<T>(int n) => G<T>(n);
      }
      """;
    Assert.That(Refusal(Source), Does.Match("^'the instantiations of '(G|H)<T>' do not end' is not supported yet$"));
  }

  /// <summary>A generic method whose body instantiates a generic class with its parameter has no template; its instantiations still compute what .NET computes.</summary>
  [Test]
  public void GenericClassInAGenericMethod_HasNoTemplate() {
    var text = "#nullable disable\n" + """
      public sealed class Cell<T> { public T Value; public Cell(T value) { this.Value = value; } }
      public static class Snippet {
        static T Through<T>(T a) => new Cell<T>(a).Value;
        public static double F(double a) => Through(a) + Through((int)a);
      }
      """;
    var module = PrototypeFixture.Import("Snippet.cs", text);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Functions.Where(function => function.Origin is not null).Select(function => function.Origin!.Template), Is.All.Null);
    var call = new PrototypeCall("F", [2.5]);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(GeneratedCSharp.Compile(text, false).Outcome(call)));
  }

  /// <summary>Equals and CompareTo of a number with one of its own type are the equality and the order of a generic definition outside one as well.</summary>
  [Test]
  public void EqualsAndCompareTo_OfNumbers() {
    var text = PrototypeFixture.Wrap("public static int F(double a, double b) => (a.Equals(b) ? 10 : 0) + a.CompareTo(b);");
    var module = PrototypeFixture.Import("Snippet.cs", text);
    foreach (var (a, b) in new[] { (double.NaN, double.NaN), (-0.0, 0.0), (1.0, 2.0), (double.NaN, 1.0) }) {
      var call = new PrototypeCall("F", [a, b]);
      Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(GeneratedCSharp.Compile(text, false).Outcome(call)), call.ToString());
    }
  }

}
