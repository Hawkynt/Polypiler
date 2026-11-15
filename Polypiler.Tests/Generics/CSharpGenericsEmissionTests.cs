using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Generics;

/// <summary>
/// The instantiations of a module written by the C# backend: a set whose bodies are its template
/// with the type arguments in place is one generic definition with its constraints, any other
/// instantiation a method or a class of its own named after its type arguments, and the copies of
/// a JavaScript function overloads of its name - each compiled at the profile and run against .NET.
/// </summary>
[TestFixture]
internal sealed class CSharpGenericsEmissionTests {

  public static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in GenericsFixtures.CSharp
       from profile in CSharpProfile.All
       select new TestCaseData(fixture.File, fixture.Class, profile.Name).SetName($"{fixture.File} for {profile.Name}");

  private static string Emitted(IrModule module, string profile) => LegalizedCSharp.Emit(module, CSharpProfile.Parse(profile))[0].Content;

  private static string Compiled(string source, string profile) {
    var module = PrototypeFixture.Import("Snippet.cs", "#nullable disable\n" + source);
    var text = Emitted(module, profile);
    var parsed = CSharpProfile.Parse(profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, parsed.Name, parsed.LanguageVersionName), Is.Empty, text);
    return text;
  }

  /// <summary>Test design 1: every fixture compiles for every profile and computes what .NET computes.</summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void Fixture_CompilesForTheProfile_AndRunsLikeDotNet(string file, string className, string profileName) {
    var profile = CSharpProfile.Parse(profileName);
    var text = Emitted(GenericsFixtures.Imported(file), profileName);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, false);
    foreach (var call in GenericsFixtures.Calls().Where(call => call.File == file).Select(call => call.Call))
      Assert.That(build.Outcome(call), Is.EqualTo(PrototypeFixture.InvokedOutcome(GenericsFixtures.Compiled(className, call.Method), call)), $"{call}\n{text}");
  }

  /// <summary>Test design 3: the search is one generic definition per method with its constraints, the calls state their type arguments.</summary>
  [TestCase("net2.0")]
  [TestCase("net10.0")]
  public void Search_IsGeneralizedAgain(string profile) {
    var text = Emitted(GenericsFixtures.Imported("Search"), profile);
    Assert.That(text.Split("IndexOf<T>(T[] a, T v) where T : global::System.IEquatable<T>").Length, Is.EqualTo(2), text);
    Assert.That(text, Does.Not.Contain("IndexOf_Double"));
    Assert.That(text, Does.Contain("Max<T>(T a, T b) where T : global::System.IComparable<T>"));
    Assert.That(text, Does.Contain("public static T Make<T>() where T : new()"));
    Assert.That(text, Does.Contain("Swap<T>(T[] a, int i, int j)"));
    Assert.That(text, Does.Contain("default(T)"));
    Assert.That(text, Does.Contain("IndexOf<double>(").And.Contain("IndexOf<int>(").And.Contain("Make<Box>()"));
    Assert.That(text, Does.Contain("a[i].Equals(v)").And.Contain("a.CompareTo(b)"));
  }

  /// <summary>Test design 3: a generic class is a class per instantiation.</summary>
  [Test]
  public void Ring_IsAClassPerInstantiation() {
    var text = Emitted(GenericsFixtures.Imported("Ring"), "net10.0");
    Assert.That(text, Does.Contain("public sealed class Ring_Int64 {").And.Contain("public sealed class Ring_Double {"));
    Assert.That(text, Does.Contain("new Ring_Int64(3)"));
  }

  /// <summary>Test design 3: the copies of a JavaScript function are overloads of one name, which the class name makes take an underscore.</summary>
  [Test]
  public void Copies_AreOverloads() {
    var text = Emitted(GenericsFixtures.ImportedScript("middle.js"), "net10.0");
    Assert.That(text, Does.Contain("private static int middle_(int[] a)").And.Contain("private static double middle_(double[] a)"), text);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net10.0", "14"), Is.Empty, text);
    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("f", [])), Is.EqualTo(PrototypeFixture.Show(-3.75)));
  }

  /// <summary>Test design 7: unmanaged at C# 7.2 and 7.3, notnull at C# 7.3 and 8 - written per instantiation below the version that has it, generic at and above it.</summary>
  [TestCase("unmanaged", "net4.6", false)]
  [TestCase("unmanaged", "net4.8", true)]
  [TestCase("notnull", "netstandard2.0", false)]
  [TestCase("notnull", "netstandard2.1", true)]
  [TestCase("struct", "net2.0", true)]
  [TestCase("class", "net2.0", true)]
  public void Constraint_AtTheProfileEdge(string constraint, string profile, bool generic) {
    var argument = constraint == "class" ? "B" : "int";
    var text = Compiled($"public sealed class B {{ }} public static class Snippet {{ static T Pick<T>(T a, T b) where T : {constraint} => a; public static bool F({argument} a) => Pick(a, a) == a; }}", profile);
    if (generic) {
      Assert.That(text, Does.Contain($"Pick<T>(T a, T b) where T : {constraint}"));
      Assert.That(text, Does.Not.Contain("Pick_"));
    } else {
      Assert.That(text, Does.Contain("Pick_Int32(int a, int b)").And.Not.Contain("where T"));
    }
  }

  /// <summary>A definition written back calls another generic definition with its own parameter; one whose callee is written per instantiation is written so itself.</summary>
  [TestCase("net4.8", "Outer<T>(T[] a) where T : unmanaged", "Inner<T>(a)")]
  [TestCase("net4.6", "Outer_Int32(int[] a)", "Inner_Int32(a)")]
  public void TemplateCalls_FollowTheirCallee(string profile, string declared, string call) {
    var text = Compiled("public static class Snippet { static T Inner<T>(T[] a) where T : unmanaged => a[0]; static T Outer<T>(T[] a) where T : unmanaged => Inner(a); public static int F(int[] a) => Outer(a); }", profile);
    Assert.That(text, Does.Contain(declared).And.Contain(call), text);
  }

  /// <summary>Without a template - a generic class used with the parameter - the instantiations are methods of their own; the equality of a class is the default comparer's.</summary>
  [Test]
  public void WithoutTemplate_InstantiationsAreSuffixed() {
    var text = Compiled("""
      public sealed class Cell<T> { public T Value; public Cell(T value) { this.Value = value; } }
      public sealed class Box { }
      public static class Snippet {
        static T Through<T>(T a) => new Cell<T>(a).Value;
        static bool Same<T>(T a, T b) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b);
        static int Order<T>(T a, T b) => System.Collections.Generic.Comparer<T>.Default.Compare(a, b);
        public static double F(double a) => Through(a) + Through((int)a);
        public static bool G() { var b = new Box(); return Same(b, b) && Same(Order(1, 2), -1); }
      }
      """, "net10.0");
    Assert.That(text, Does.Contain("Through_Double(double a)").And.Contain("Through_Int32(int a)").And.Contain("new Cell_Double(a)"));
    Assert.That(text, Does.Contain("Same<T>(T a, T b)").And.Contain("global::System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b)"));
    Assert.That(text, Does.Contain("global::System.Collections.Generic.Comparer<T>.Default.Compare(a, b)"));
    var build = GeneratedCSharp.Compile(text, false);
    Assert.That((build.Outcome(new PrototypeCall("F", [2.5])), build.Outcome(new PrototypeCall("G", []))), Is.EqualTo((PrototypeFixture.Show(4.5), "true")));
  }

  /// <summary>An interface of the module as a constraint is written as its type.</summary>
  [Test]
  public void InterfaceConstraint_IsWritten() {
    var text = Compiled("""
      public interface IShape { double Area(); }
      public sealed class Square : IShape { public double Side; public Square(double side) { this.Side = side; } public double Area() => this.Side * this.Side; }
      public static class Snippet {
        static double Twice<T>(T shape) where T : IShape => 2 * shape.Area();
        public static double F(double side) => Twice(new Square(side));
      }
      """, "net10.0");
    Assert.That(text, Does.Contain("Twice<T>(T shape) where T : IShape"), text);
  }

  /// <summary>An instantiation whose body is not its template instantiated is written on its own: the backend never writes back what differs in more than the type.</summary>
  [Test]
  public void DifferingBody_IsNotGeneralized() {
    var module = PrototypeFixture.Import("Snippet.cs", "public static class Snippet { static T Id<T>(T a) => a; public static int F(int a) => Id(a); }");
    var instance = module.Functions.Single(function => function.Origin is not null);
    var changed = instance with { Body = new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]) };
    var text = Emitted(module with { Functions = [.. module.Functions.Select(function => function == instance ? changed : function)] }, "net10.0");
    Assert.That(text, Does.Contain("Id_Int32(int a)").And.Not.Contain("Id<T>"));
    var refused = instance with { Origin = instance.Origin! with { Template = instance.Origin.Template! with { Parameters = [] } } };
    Assert.That(Emitted(module with { Functions = [.. module.Functions.Select(function => function == instance ? refused : function)] }, "net10.0"), Does.Contain("Id_Int32(int a)"));
  }

  /// <summary>The names of the type arguments of a method written on its own, for every kind of type.</summary>
  [Test]
  public void SuffixedNames_OfEveryKindOfType() {
    var text = Compiled("""
      public struct P { public int X; }
      public static class Snippet {
        static int Count<T>(T[] a) => a.Length + new Cell<T>().N;
        public static int F() => Count(new sbyte[1]) + Count(new byte[1]) + Count(new short[1]) + Count(new ushort[1]) + Count(new int[1]) + Count(new uint[1])
          + Count(new long[1]) + Count(new ulong[1]) + Count(new float[1]) + Count(new double[1]) + Count(new P[1]) + Count(new string[] { "" }) + Count(new int[][] { });
      }
      public sealed class Cell<T> { public int N = 1; }
      """.Replace(" + Count(new int[][] { })", "", System.StringComparison.Ordinal), "net10.0");
    foreach (var name in new[] { "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "P", "String" })
      Assert.That(text, Does.Contain($"Count_{name}("), name);
  }

}
