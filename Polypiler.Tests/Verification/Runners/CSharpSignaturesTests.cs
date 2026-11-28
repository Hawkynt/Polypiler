using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;

namespace Polypiler.Tests.Verification.Runners;

/// <summary>
/// The derivation of call signatures from C# declarations (#220): one row of the derivation table per
/// case, the types without a derived image, the lookup of an export and the fit of a supplied
/// signature to the declaration.
/// </summary>
[TestFixture]
public sealed class CSharpSignaturesTests {

  private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files(string source)
    => new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["Fixture.cs"] = Encoding.UTF8.GetBytes(source), ["notes.txt"] = "ignored"u8.ToArray() };

  private static string Derived(string declaration, string usings = "using System;")
    => CSharpSignatures.Derive(CSharpSignatures.Find(Files($"{usings}\npublic enum Small : byte {{ A }}\npublic enum Wide : long {{ B }}\n"
      + "public struct Point { public int X; public double Y { get; set; } public static int Count; public const int Limit = 3; }\n"
      + $"public static class T {{ {declaration} }}"), "T.F")).ToString();

  [TestCase("sbyte", "i8")]
  [TestCase("byte", "u8")]
  [TestCase("short", "i16")]
  [TestCase("ushort", "u16")]
  [TestCase("int", "i32")]
  [TestCase("uint", "u32")]
  [TestCase("long", "i64")]
  [TestCase("ulong", "u64")]
  [TestCase("bool", "bool")]
  [TestCase("char", "char16")]
  [TestCase("float", "f32")]
  [TestCase("double", "f64")]
  [TestCase("string", "utf16")]
  [TestCase("string?", "opt<utf16>")]
  [TestCase("byte[]", "bytes")]
  [TestCase("ReadOnlySpan<byte>", "bytes")]
  [TestCase("int[]", "seq<i32>")]
  [TestCase("ReadOnlySpan<ulong>", "seq<u64>")]
  [TestCase("int[]?", "opt<seq<i32>>")]
  [TestCase("string?[]", "seq<opt<utf16>>")]
  [TestCase("int[][]", "seq<seq<i32>>")]
  [TestCase("Small", "enum<u8>")]
  [TestCase("Wide", "enum<i64>")]
  [TestCase("(int, bool)", "tuple<i32,bool>")]
  [TestCase("(int Left, (byte, char) Right)", "tuple<i32,tuple<u8,char16>>")]
  [TestCase("Point", "record{X:i32,Y:f64}")]
  public void EachRowOfTheDerivationTable_IsItsImageType(string type, string image) {
    Assert.That(Derived($"public static {type} F({type} a) => a;"), Is.EqualTo($"(a: {image} in) -> {image}"));
  }

  [TestCase("ref uint a", "(a: u32 inout) -> void")]
  [TestCase("out int a", "(a: i32 out) -> void")]
  [TestCase("in long a", "(a: i64 in) -> void")]
  [TestCase("ref readonly long a", "(a: i64 in) -> void")]
  [TestCase("Span<int> a", "(a: seq<i32> inout) -> void")]
  [TestCase("Span<byte> a", "(a: bytes inout) -> void")]
  public void TheModifierOfAParameter_IsItsMode(string parameter, string signature) {
    Assert.That(Derived($"public static void F({parameter}) {{ {(parameter.StartsWith("out", StringComparison.Ordinal) ? "a = 0;" : "")} }}"), Is.EqualTo(signature));
  }

  [Test]
  public void TheScenarioOfTheWorkOrder_DerivesItsSignature() {
    var signature = Derived("public static int F(ReadOnlySpan<byte> data, Span<int> scratch, ref uint state, int[]? table) => 0;");

    Assert.That(signature, Is.EqualTo("(data: bytes in, scratch: seq<i32> inout, state: u32 inout, table: opt<seq<i32>> in) -> i32"));
  }

  [Test]
  public void AMethodWithoutParameters_HasAnEmptySignature() => Assert.That(Derived("public static void F() { }"), Is.EqualTo("() -> void"));

  [TestCase("public static decimal F(decimal a) => a;", "'decimal'")]
  [TestCase("public static nint F(nint a) => a;", "'nint'")]
  [TestCase("public static int? F(int? a) => a;", "'int?'")]
  [TestCase("public static object F(object a) => a;", "'object'")]
  [TestCase("public static int F(System.Numerics.Complex a) => 0;", "'System.Numerics.Complex'")]
  [TestCase("public static int F(Guid a) => 0;", "'System.Guid'")]
  [TestCase("public static int[,] F(int[,] a) => a;", "'int[*,*]'")]
  [TestCase("public static System.Collections.Generic.List<int> F(System.Collections.Generic.List<int> a) => a;", "List<int>")]
  [TestCase("public struct Empty { } public static int F(Empty a) => 0;", "without fields")]
  [TestCase("public struct Box<V> { public V Value; } public static int F(Box<int> a) => 0;", "Box<int>")]
  [TestCase("public static int F(ref Span<int> a) => 0;", "passes a span by reference")]
  [TestCase("private static int _x; public static ref int F() => ref _x;", "returns by reference")]
  [TestCase("public static int F<V>(V a) => 0;", "is generic")]
  [TestCase("public int F(int a) => a;", "instance method")]
  public void ATypeWithoutDerivedImage_IsRefused(string declaration, string reason) {
    var error = Assert.Throws<ArgumentException>(() => Derived(declaration));

    Assert.That(error!.Message, Does.Contain(reason));
  }

  [Test]
  public void AnInstanceMethodOfAClass_IsRefused() {
    var method = CSharpSignatures.Find(Files("public class C { public int F(int a) => a; }"), "C.F");

    Assert.That(Assert.Throws<ArgumentException>(() => CSharpSignatures.Derive(method))!.Message, Does.Contain("instance method"));
  }

  [Test]
  public void TypesNestedSeventeenLevelsDeep_AreRefused() {
    var type = "int" + string.Concat(Enumerable.Repeat("[]", 17));

    Assert.That(Assert.Throws<ArgumentException>(() => Derived($"public static void F({type} a) {{ }}"))!.Message, Does.Contain("deeper than 16"));
    Assert.That(Derived($"public static void F({"int" + string.Concat(Enumerable.Repeat("[]", 16))} a) {{ }}"), Does.StartWith("(a: seq<seq<"));
  }

  [Test]
  public void AnExportInANamespaceOrANestedType_IsFound() {
    var files = Files("namespace N.M { public static class Outer { public static class Inner { public static int F(int a) => a; } } }");

    Assert.That(CSharpSignatures.Bind(CSharpSignatures.Find(files, "N.M.Outer.Inner.F"), null).Method, Is.EqualTo("global::N.M.Outer.Inner.F"));
  }

  [Test]
  public void ABareMethodName_IsFoundInTheOneTypeThatDeclaresIt() {
    var files = Files("namespace N { public static class A { public static int add(int a) => a; } public static class B { public static class C { public static int div(int a) => a; } } }");

    Assert.That(CSharpSignatures.Find(files, "add").ContainingType.Name, Is.EqualTo("A"));
    Assert.That(CSharpSignatures.Find(files, "div").ContainingType.Name, Is.EqualTo("C"));
    Assert.That(CSharpSignatures.Bind(CSharpSignatures.Find(files, "add"), null, "add").Export, Is.EqualTo("add"));
    Assert.That(CSharpSignatures.Bind(CSharpSignatures.Find(files, "add"), null).Export, Is.EqualTo("N.A.add"));
  }

  [TestCase("T.G", null, "does not exist")]
  [TestCase("U.F", null, "there is no 'U'")]
  [TestCase("T.F", null, "names 2 overloads")]
  [TestCase("T.F", 3, "does not exist")]
  [TestCase("F", null, "names 3 overloads")]
  public void AnExportThatIsNotOneMethod_IsRefused(string export, int? count, string reason) {
    var files = Files("public static class T { public static int F(int a) => a; public static int F(int a, int b) => a; } public static class V { public static int F() => 0; }");

    Assert.That(Assert.Throws<ArgumentException>(() => CSharpSignatures.Find(files, export, count))!.Message, Does.Contain(reason));
  }

  [Test]
  public void TheParameterCount_PicksAnOverload() {
    var files = Files("public static class T { public static int F(int a) => a; public static int F(int a, int b) => a; }");

    Assert.That(CSharpSignatures.Find(files, "T.F", 2).Parameters, Has.Length.EqualTo(2));
  }

  [TestCase("")]
  [TestCase(" ")]
  public void AnEmptyExport_IsRefused(string export) => Assert.Throws<ArgumentException>(() => CSharpSignatures.Find(Files(""), export));

  private static readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _modes
    = Files("public static class T { public static int F(int[] table, ref int state) => 0; }");

  private static CallSignature Supplied(string table, string tableMode, string state = "i32", string stateMode = "inout", string? result = "i32")
    => new([new CallParameter("t", ImageType.Parse(table), Mode(tableMode)), new CallParameter("s", ImageType.Parse(state), Mode(stateMode))], result is null ? null : ImageType.Parse(result));

  private static ParameterMode Mode(string mode) => mode switch { "in" => ParameterMode.In, "inout" => ParameterMode.InOut, _ => ParameterMode.Out };

  [Test]
  public void ASuppliedSignature_MayDeclareAnArrayInOut_AndNamesTheParameters() {
    var binding = CSharpSignatures.Bind(CSharpSignatures.Find(_modes, "T.F"), Supplied("seq<i32>", "inout"));

    Assert.That(binding.Signature.ToString(), Is.EqualTo("(t: seq<i32> inout, s: i32 inout) -> i32"));
    Assert.That(binding.Parameters[0].Passing, Is.EqualTo(CSharpPassing.Value));
  }

  [TestCase("u32", "in", "i32", "inout", "i32", "'t' is u32 in the signature, seq<i32> in the declaration")]
  [TestCase("seq<i32>", "out", "i32", "inout", "i32", "'t' is out in the signature, in in the declaration")]
  [TestCase("seq<i32>", "in", "i32", "in", "i32", "'s' is in in the signature, inout in the declaration")]
  [TestCase("seq<i32>", "in", "i32", "inout", "u32", "returns u32, the declaration i32")]
  [TestCase("seq<i32>", "in", "i32", "inout", null, "returns void, the declaration i32")]
  public void ASuppliedSignatureThatDoesNotFit_IsRefused(string table, string tableMode, string state, string stateMode, string? result, string reason) {
    var error = Assert.Throws<ArgumentException>(() => CSharpSignatures.Bind(CSharpSignatures.Find(_modes, "T.F"), Supplied(table, tableMode, state, stateMode, result)));

    Assert.That(error!.Message, Does.Contain(reason));
  }

  [Test]
  public void ASuppliedSignatureWithAnotherCount_IsRefused() {
    var error = Assert.Throws<ArgumentException>(() => CSharpSignatures.Bind(CSharpSignatures.Find(_modes, "T.F"), new CallSignature([], ImageType.I32)));

    Assert.That(error!.Message, Does.Contain("0 parameter(s), the declaration 2"));
  }

  [Test]
  public void AVoidMethod_FitsAVoidSignature() {
    var method = CSharpSignatures.Find(Files("public static class T { public static void F() { } }"), "T.F");

    Assert.That(CSharpSignatures.Bind(method, new CallSignature([], null)).Result, Is.Null);
    Assert.Throws<ArgumentException>(() => CSharpSignatures.Bind(method, new CallSignature([], ImageType.I32)));
  }

  [Test]
  public void NullArguments_AreRefused() {
    Assert.Throws<ArgumentNullException>(() => CSharpSignatures.Derive(null!));
    Assert.Throws<ArgumentNullException>(() => CSharpSignatures.Find(null!, "T.F"));
  }

}
