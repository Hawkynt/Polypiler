using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Records;

/// <summary>
/// The structs and record structs the C# frontend imports, and the constructs of the construct table
/// it refuses by name.
/// </summary>
[TestFixture]
public sealed class CSharpRecordImportTests {

  private const string Vec = "public struct Vec { public double X; public double Y; public Vec(double x, double y) { X = x; this.Y = y; } }";

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

  /// <summary>Test design 4: every declaration form, with the read-only flags of its fields.</summary>
  [TestCase("public struct S { public int A; private long B; }", false, false)]
  [TestCase("public struct S { public readonly int A; internal readonly long B; }", true, false)]
  [TestCase("public readonly struct S { public readonly int A; public readonly long B; }", true, false)]
  [TestCase("public record struct S(int A, long B);", false, true)]
  [TestCase("public readonly record struct S(int A, long B);", true, true)]
  public void DeclarationForm_GivesTheFieldsAndTheirFlags(string declaration, bool readOnly, bool equatable) {
    var module = Import(declaration + " public static class C { public static int F() => 0; }");
    var type = (IrStructType)module.Types.Single();
    Assert.That(type.Name, Is.EqualTo("S"));
    Assert.That(type.Fields.Select(field => (field.Name, field.Type, field.IsReadOnly)), Is.EqualTo(new[] { ("A", IrType.I32, readOnly), ("B", IrType.I64, readOnly) }));
    Assert.That(type.IsEquatable, Is.EqualTo(equatable));
  }

  /// <summary>A struct nested in the static class, one in a namespace, one in another file of the class: all are types of the module.</summary>
  [Test]
  public void Structs_AreFoundBesideAndInsideTheClass() {
    var module = Import("namespace N; public struct A { public int X; } public static class C { public struct B { public A Inner; } public static int F(B b) => b.Inner.X; }");
    Assert.That(module.Types.Select(type => type.Name), Is.EqualTo(new[] { "A", "B" }));
    var parts = CSharpFrontend.Import([
      new CSharpSource("One.cs", "public static partial class C { public struct P { public int X; } public static int F(Q q) => q.Y; }"),
      new CSharpSource("Two.cs", "public struct Q { public int Y; } public static partial class C { public static int G(P p) => p.X; }")
    ]);
    Assert.That(parts.Diagnostics, Is.Empty, string.Join("; ", parts.Diagnostics));
    Assert.That(parts.Module!.Types.Select(type => type.Name), Is.EqualTo(new[] { "P", "Q" }));
  }

  /// <summary>Constructors, the parameterless forms, default values and object initializers build the values C# builds.</summary>
  [TestCase("public static double F() { var v = new Vec(1, 2); return v.X * 10 + v.Y; }", 12.0)]
  [TestCase("public static double F() { var v = new Vec(); return v.X + v.Y; }", 0.0)]
  [TestCase("public static double F() { Vec v = new(3, 4); return v.Y; }", 4.0)]
  [TestCase("public static double F() { Vec v = default; Vec w = default(Vec); return v.X + w.Y; }", 0.0)]
  [TestCase("public static double F() { var v = new Vec { Y = 5 }; return v.X + v.Y; }", 5.0)]
  [TestCase("public static double F() { var v = new Vec(1, 1); v = new Vec { X = v.Y + 1, Y = v.X + 2 }; return v.X * 10 + v.Y; }", 23.0)]
  [TestCase("public static double F() => G(new Vec { Y = 2, X = 1 }); private static double G(Vec v) => v.X * 10 + v.Y;", 12.0)]
  [TestCase("public static double F() => H().Y; private static Vec H() { return new Vec { X = 7, Y = 8 }; }", 8.0)]
  [TestCase("public static double F() { var a = new Vec[] { new Vec(1, 2), new Vec(3, 4) }; a[1].X += 10; a[0].Y++; return a[1].X + a[0].Y; }", 16.0)]
  [TestCase("public static double F() { var v = new Vec(1, 2); for (var i = 0; i < 3; v.X++) ++i; return v.X; }", 4.0)]
  [TestCase("public static bool F() { var v = new Vec(1, 2); var w = v; return v.X == w.X; }", true)]
  public void Construction_ComputesWhatCSharpComputes(string methods, object expected)
    => Assert.That(Run(Vec + " public static class C { " + methods + " }"), Is.EqualTo(expected));

  /// <summary>A constructor may give a field a constant, take its parameters in another order, or convert them; arguments without effects can then be moved.</summary>
  [Test]
  public void Constructor_GivesConstantsAndReorders() {
    const string Pair = "public struct P { public long A; public int B; public double C; public P(int b, long a) { this.C = 1.5; A = a; B = b; } }";
    Assert.That(Run(Pair + " public static class C { public static double F(int x) => new P(x, 2).A * 100 + new P(x, 2).B + new P(x, 2).C; }", 3), Is.EqualTo(204.5));
    Assert.That(Refusal(Pair + " public static class C { public static double F(int[] x) => new P(x[0]++, 2).A; }").Message,
      Is.EqualTo("'a constructor call whose arguments the constructor does not take in field order' is not supported yet"));
    var shorter = "public struct P { public long A; public P(int a) => A = a; } public static class C { public static long F() => new P(4).A; }";
    Assert.That(Run(shorter), Is.EqualTo(4L));
  }

  /// <summary>An object initializer inside an expression builds all fields at once; where its order is not the fields' order only values without effects can stand.</summary>
  [Test]
  public void ObjectInitializer_InsideAnExpression() {
    Assert.That(Run(Vec + " public static class C { public static double F(double[] a) => new Vec { X = a[0], Y = a[1] }.Y; }", new[] { 1.0, 2.0 }), Is.EqualTo(2.0));
    Assert.That(Refusal(Vec + " public static class C { public static double F(double[] a) => new Vec { Y = a[0], X = a[1] }.Y; }").Message,
      Is.EqualTo("'an object initializer with effects in an order other than that of the fields' is not supported yet"));
    Assert.That(Refusal(Vec + " public static class C { public static double F() => new Vec(1, 2) { X = 3 }.Y; }").Message,
      Is.EqualTo("'a constructor call with an object initializer' is not supported yet"));
  }

  /// <summary>Test design 10: what the construct table refuses, by name and with nothing written.</summary>
  [TestCase("public struct S { public int X; public int Get() => X; }", "the method Get of a struct")]
  [TestCase("public struct S { public int X { get; set; } }", "the property X of a struct")]
  [TestCase("public struct S { public int X = 1; public S() { } }", "a field initializer of a struct")]
  [TestCase("public struct S { public int X; public S(int x) { X = x * 2; } }", "a constructor with logic beyond giving each field a parameter or a constant")]
  [TestCase("public struct S { public int X; public S(int x) { X = x; X = x; } }", "a constructor that assigns the field X twice")]
  [TestCase("public struct S { public int X; public int Y; public S(int x) { X = x; Y = 0; } public S(long x) { X = 0; } }", "a constructor that does not assign every field")]
  [TestCase("public struct S { public int X; public S(int x) : this() { X = x; } }", "a constructor that calls another")]
  [TestCase("public struct S { public int X; static S() { } }", "a static constructor")]
  [TestCase("public struct S { public int X; public S(ref int x) { X = x; } }", "a constructor parameter with a modifier or a default value")]
  [TestCase("public struct S { public int X; public S(int x) { int y = x; X = y; } }", "a constructor with logic beyond giving each field a parameter or a constant")]
  [TestCase("public struct S { public int X; public S(int x) { X = (x); } public static int operator +(S a, S b) => 0; }", "an operator of a struct")]
  [TestCase("public struct S { public static int X; }", "a static field of a struct")]
  [TestCase("public struct S { public const int X = 1; }", "a const field of a struct")]
  [TestCase("public struct S { public struct T { } }", "a type nested in a struct")]
  [TestCase("public struct S { public int X; public event System.Action E; }", "a EventFieldDeclaration of a struct")]
  [TestCase("public ref struct S { public int X; }", "ref struct")]
  [TestCase("public partial struct S { public int X; }", "a partial struct")]
  [TestCase("public struct S<T> { public int X; }", "a generic struct")]
  [TestCase("public struct S : System.IComparable { public int X; public int CompareTo(object o) => 0; }", "a struct that implements an interface")]
  [TestCase("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] public struct S { [System.Runtime.InteropServices.FieldOffset(0)] public int X; }",
    "the attribute System.Runtime.InteropServices.StructLayout")]
  [TestCase("public record struct S(int X = 1);", "a positional parameter with a modifier, an attribute or a default value")]
  [TestCase("public record struct S(int X) { public int Twice() => X * 2; }", "the method Twice of a struct")]
  [TestCase("public struct S { public object O; }", "object")]
  [TestCase("public struct S { public S[] Many; }", "an array of S as a field")]
  public void Declaration_IsRefusedByName(string declaration, string what)
    => Assert.That(Refusal(declaration + " public static class C { public static int F() => 0; }").Message, Is.EqualTo($"'{what}' is not supported yet"));

  /// <summary>Test design 10: the uses of a struct the construct table refuses.</summary>
  [TestCase("public static Vec F(Vec v) => v with { X = 1 };", "a with expression", "Vec")]
  [TestCase("public static void F(Vec v) { v.ToString(); }", "ValueType.ToString", "Vec")]
  [TestCase("public static void F(R r) { r.ToString(); }", "R.ToString", "R")]
  [TestCase("public static int F(Vec v) => v.GetHashCode();", "ValueType.GetHashCode", "Vec")]
  [TestCase("public static int F(R r) => r.GetHashCode();", "R.GetHashCode", "R")]
  [TestCase("public static bool F(R r) => r.Equals((object)r);", "R.Equals", "R")]
  [TestCase("public static bool F(Vec v) => v.Equals(v);", "ValueType.Equals", "Vec")]
  [TestCase("public static double F(ref Vec v) => v.X;", "ref", "Vec")]
  [TestCase("public static double F(in Vec v) => v.X;", "in", "Vec")]
  [TestCase("public static double F(Vec v) => v.X++;", "SimpleMemberAccessExpression", "Vec")]
  [TestCase("public static Vec F() => new Vec { X = 1, Y = { } };", "a nested object initializer", "Vec")]
  [TestCase("private static readonly Vec Origin = new Vec(); public static double F() => Origin.X;", "a constant of the type Vec", "Vec")]
  [TestCase("public static double F() => new B().D.Length;", "the default value of B, which has a field of an array type", "B")]
  [TestCase("public static int F() => new B[1].Length;", "the default value of B, which has a field of an array type", "B")]
  [TestCase("public static int F() => new B { }.D.Length;", "an object initializer that leaves an array field without a value", "B")]
  [TestCase("public static int F() { B b = new B { }; return b.D.Length; }", "the default value of B, which has a field of an array type", "B")]
  [TestCase("public static int F() => G(new B { }); private static int G(B b) => 0;", "an object initializer that leaves an array field without a value", "B")]
  [TestCase("public static bool F(R r) => r == r && r.N < 0 || r != r;", "", "R")]
  public void Use_IsRefusedByName(string methods, string what, string type) {
    const string Types = "public struct Vec { public double X; public double Y; } public record struct R(int N); public struct B { public int[] D; }";
    var source = Types + " public static class C { " + methods + " }";
    if (what.Length == 0) {
      Assert.That(CSharpFrontend.Import("Snippet.cs", source).Diagnostics, Is.Empty);
      return;
    }

    Assert.That(Refusal(source).Message, Is.EqualTo($"'{what}' is not supported yet"), type);
  }

  /// <summary>C# has no == for a plain struct: the compiler's own error is reported.</summary>
  [Test]
  public void EqualityOfAPlainStruct_IsTheCompilersError() {
    var refusal = Refusal(Vec + " public static class C { public static bool F(Vec a, Vec b) => a == b; }");
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((CSharpFrontend.CompilerError, refusal.Message)));
    Assert.That(refusal.Message, Does.StartWith("CS0019"));
  }

  /// <summary>A file of two structs of one name, or of nothing but structs, is refused.</summary>
  [Test]
  public void File_OfTwoStructsOfOneNameOrWithoutAClass_IsRefused() {
    Assert.That(Refusal("public struct S { public int X; } public static class C { public struct S { public int Y; } public static int F() => 0; }").Message,
      Is.EqualTo("'a second struct named S' is not supported yet"));
    Assert.That(Refusal("public struct S { public int X; }").Message, Is.EqualTo("'a file that is not exactly one static class' is not supported yet"));
  }

  /// <summary>Test design 3: a field of every type a field can have; equality of a record struct is IrStructEquals, negated for !=.</summary>
  [Test]
  public void RecordStruct_EqualityIsFieldWise() {
    const string Source = "public record struct R(bool B, sbyte I8, byte U8, short I16, ushort U16, char C, int I32, uint U32, long I64, ulong U64, float F32, double F64, int[] A); "
      + "public static class C { public static bool F(int[] a, double x) => new R(true, 1, 2, 3, 4, 'c', 5, 6, 7, 8, 9, x, a) == new R(true, 1, 2, 3, 4, 'c', 5, 6, 7, 8, 9, x, a) "
      + "&& new R(true, 1, 2, 3, 4, 'c', 5, 6, 7, 8, 9, x, a) != new R(false, 1, 2, 3, 4, 'c', 5, 6, 7, 8, 9, x, a); }";
    var module = Import(Source);
    Assert.That(((IrStructType)module.Types[0]).Fields.Select(field => field.Type.ToString()),
      Is.EqualTo(new[] { "bool", "i8", "u8", "i16", "u16", "u16", "i32", "u32", "i64", "u64", "f32", "f64", "i32[]" }));
    Assert.That(IrInterpreter.Call(module, "F", [new[] { 1 }, double.NaN]), Is.True);
  }

}
