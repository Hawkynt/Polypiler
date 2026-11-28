using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// <c>Variant</c> parameters and results typed from the calls of the whole module (#1564): a clone
/// per signature of its callers, the public procedure kept as it is declared, the dynamic type only
/// where the callers' kinds disagree within one call.
/// </summary>
[TestFixture]
public sealed class VisualBasicInferenceTests {

  private static IrModule Import(string body) => VisualBasicBitwiseTests.Import("Option Explicit\n" + body);

  private static IrFunction Function(IrModule module, string name) => module.Functions.Single(function => function.Name == name);

  private static string Parameters(IrFunction function) => string.Join(", ", function.Parameters.Select(parameter => parameter.Type.ToString()));

  private static string Run(IrModule module, string name = "F") => VisualBasicBitwiseTests.Run(module, name);

  [Test]
  public void PrivateFunction_CalledWithLongs_IsTyped() {
    var module = Import("Private Function Add(a, b)\n    Add = a * b\nEnd Function\nPublic Function F() As Long\n    F = Add(CLng(3), CLng(4))\nEnd Function\n");
    Assert.That(Parameters(Function(module, "Add")), Is.EqualTo("i32, i32"));
    Assert.That(Function(module, "Add").IsPublic, Is.False);
    Assert.That(Run(module), Is.EqualTo("12"));
  }

  [Test]
  public void CallersThatDisagree_GetOneCloneEach() {
    var module = Import("Private Function Twice(x)\n    Twice = x & x\nEnd Function\nPublic Function F() As String\n    F = Twice(CInt(4)) & \"|\" & Twice(\"ab\")\nEnd Function\n");
    Assert.That(module.Functions.Select(function => function.Name), Does.Contain("Twice_Integer").And.Contain("Twice_String").And.Not.Contain("Twice"));
    Assert.That(Function(module, "Twice_String").ReturnType, Is.EqualTo(IrType.String), "a result of one kind");
    Assert.That(Run(module), Is.EqualTo("44|abab"));
  }

  [Test]
  public void PublicFunction_KeepsItsDynamicSignature() {
    var module = Import("Public Function Add(a, b)\n    Add = a + b\nEnd Function\nPublic Function F() As Long\n    F = Add(CLng(3), CLng(4))\nEnd Function\n");
    Assert.That(Parameters(Function(module, "Add")), Is.EqualTo("variant, variant"));
    Assert.That(Function(module, "Add").IsPublic, Is.True);
    Assert.That(Parameters(Function(module, "Add_Long_Long")), Is.EqualTo("i32, i32"));
    Assert.That(Run(module), Is.EqualTo("7"));
    Assert.That(IrInterpreter.Call(module, "Add", [new IrVariant(IrVariantTag.String, "a"), new IrVariant(IrVariantTag.String, "b")]), Is.EqualTo(new IrVariant(IrVariantTag.String, "ab")));
  }

  [Test]
  public void TypedClone_KeepsVariantPromotion() {
    var module = Import("Private Function Inc(x)\n    Inc = x + 1\nEnd Function\nPublic Function F() As String\n    F = TypeName(Inc(CInt(32767))) & \" \" & Inc(CInt(32767)) & \" \" & TypeName(Inc(CInt(1)))\nEnd Function\n");
    Assert.That(Parameters(Function(module, "Inc")), Is.EqualTo("i16"));
    Assert.That(Function(module, "Inc").ReturnType, Is.EqualTo(IrType.Variant), "Integer or Long");
    Assert.That(Run(module), Is.EqualTo("Long 32768 Integer"));
  }

  [Test]
  public void CallOfSeveralKinds_StaysDynamic() {
    var module = Import("Private Function Id(x)\n    Id = x\nEnd Function\nPublic Function F(ByVal n As Integer) As String\n    Dim v\n    v = 1\n    If n = 1 Then v = \"a\"\n    F = TypeName(Id(v))\nEnd Function\n");
    Assert.That(Parameters(Function(module, "Id")), Is.EqualTo("variant"));
    Assert.That(VisualBasicBitwiseTests.Run(module, "F", (short)1), Is.EqualTo("String"));
    Assert.That(VisualBasicBitwiseTests.Run(module, "F", (short)0), Is.EqualTo("Integer"));
  }

  [Test]
  public void Recursion_ReachesAFixedPoint() {
    var module = Import("Private Function Fact(n)\n    If n <= 1 Then\n        Fact = 1\n    Else\n        Fact = n * Fact(n - 1)\n    End If\nEnd Function\nPublic Function F() As Double\n    F = Fact(CLng(10))\nEnd Function\n");
    Assert.That(Function(module, "F").Name, Is.EqualTo("F"));
    Assert.That(module.Functions.Any(function => function.Name.StartsWith("Fact", System.StringComparison.Ordinal) && function.Parameters[0].Type == IrType.I32), Is.True);
    Assert.That(Run(module), Is.EqualTo("3628800"));
  }

  [Test]
  public void ResultThatMayBeEmpty_StaysDynamic() {
    var module = Import("Private Function Maybe(x)\n    If x > 0 Then Maybe = 1\nEnd Function\nPublic Function F(ByVal n As Integer) As String\n    F = TypeName(Maybe(CLng(n)))\nEnd Function\n");
    Assert.That(Function(module, "Maybe").ReturnType, Is.EqualTo(IrType.Variant));
    Assert.That(VisualBasicBitwiseTests.Run(module, "F", (short)0), Is.EqualTo("Empty"));
    Assert.That(VisualBasicBitwiseTests.Run(module, "F", (short)1), Is.EqualTo("Integer"));
  }

  [Test]
  public void ResultOfOneKind_IsTyped() {
    var module = Import("Private Function Big(x)\n    Big = x > 5\nEnd Function\nPublic Function F() As Boolean\n    F = Big(CLng(9))\nEnd Function\n");
    Assert.That(Function(module, "Big").ReturnType, Is.EqualTo(IrType.Bool));
    Assert.That(Run(module), Is.EqualTo("True"));
  }

  [Test]
  public void MoreSignaturesThanTheBound_CallTheDynamicProcedure() {
    var calls = string.Join(" & ", new[] { "CByte(1)", "CInt(1)", "CLng(1)", "CSng(1)", "CDbl(1)", "True" }.Select(argument => $"TypeName(K({argument}))"));
    var module = Import($"Private Function K(x)\n    K = x\nEnd Function\nPublic Function F() As String\n    F = {calls}\nEnd Function\n");
    Assert.That(module.Functions.Count(function => function.Name.StartsWith("K_", System.StringComparison.Ordinal)), Is.EqualTo(4));
    Assert.That(module.Functions.Select(function => function.Name), Does.Contain("K"));
    Assert.That(Run(module), Is.EqualTo("ByteIntegerLongSingleDoubleBoolean"));
  }

  [Test]
  public void CloneName_TakenByAProcedure_IsSuffixed() {
    var module = Import("Public Function Add_Long(x) As Long\n    Add_Long = 0\nEnd Function\nPublic Function Add(x)\n    Add = x\nEnd Function\nPublic Function F() As Long\n    F = Add(CLng(2))\nEnd Function\n");
    Assert.That(module.Functions.Select(function => function.Name), Does.Contain("Add_Long_2"));
    Assert.That(Run(module), Is.EqualTo("2"));
  }

  [Test]
  public void OptionalDefault_ChoosesTheClone() {
    var module = Import("Private Function Scale(x, Optional y = 2)\n    Scale = x * y\nEnd Function\nPublic Function F() As Long\n    F = Scale(CInt(5)) + Scale(CInt(5), CInt(3))\nEnd Function\n");
    Assert.That(Parameters(Function(module, "Scale")), Is.EqualTo("i16, i16"));
    Assert.That(Run(module), Is.EqualTo("25"));
  }

  [Test]
  public void CloneThatTheFrontendRefuses_FallsBackToTheDynamicProcedure() {
    // The text of a typed Date is refused; a dynamic Variant that holds one raises error 13 when it runs.
    var module = Import("Private Function Text(x)\n    Text = x & \"\"\nEnd Function\nPublic Function F() As String\n    F = Text(CLng(1)) & Text(#1/2/2000#)\nEnd Function\n");
    Assert.That(module.Functions.Select(function => function.Name), Does.Contain("Text").And.Contain("Text_Long").And.Not.Contain("Text_Date"));
    Assert.That(Run(module), Is.EqualTo("!13"));
  }

  [Test]
  public void Refusal_InTheDynamicProcedure_IsReported()
    => Assert.That(VisualBasicBitwiseTests.Refused("Option Explicit\nPrivate Function G(x)\n    G = x Like \"a\"\nEnd Function\nPublic Function F() As Boolean\n    F = G(\"a\")\nEnd Function\n"), Does.Contain("Like"));

}
