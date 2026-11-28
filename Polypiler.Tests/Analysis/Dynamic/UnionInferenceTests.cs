using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;

namespace Polypiler.Tests.Analysis.Dynamic;

/// <summary>
/// Small sets of proven types become closed unions (#1589, ADR 0300 level 5): a local every read of
/// which sees one of two to four exact types, and every value assigned it one of them, is the union of
/// #1582; its assignments make the member, its reads box the member they find by the tag. Visual Basic
/// modules lowered dynamic are typed by the rewrite with unions on, and compute what the default
/// import computes.
/// </summary>
[TestFixture]
public sealed class UnionInferenceTests {

  private static readonly DeDynamicizeOptions Unions = DeDynamicizeOptions.Default with { Unions = true };

  private static IrModule Import(string source, VisualBasicInference inference = VisualBasicInference.Dynamic) {
    var result = VisualBasicFrontend.Import("Test.bas", source, new VisualBasicImportOptions { Inference = inference });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  private static DeDynamicizeResult Typed(string source, DeDynamicizeOptions? options = null) {
    var result = DeDynamicize.Analyse(Import(source), VisualBasicFrontend.Semantics, options ?? Unions);
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    return result;
  }

  private static IrType TypeOf(IrModule module, string local) => module.Functions.Single(function => function.Name == "F").Locals.Single(candidate => candidate.Name == local).Type;

  private static void SameResults(string source, IrModule typed, params object[][] calls) {
    var plain = Import(source, VisualBasicInference.Framework);
    foreach (var arguments in calls)
      Assert.That(IrInterpreter.Call(typed, "F", arguments), Is.EqualTo(IrInterpreter.Call(plain, "F", arguments)), string.Join(", ", arguments));
  }

  private const string TwoKinds = "Public Function F(ByVal c As Boolean)\nDim v\nIf c Then\nv = 1\nElse\nv = \"a\"\nEnd If\nF = v\nEnd Function\n";

  [Test]
  public void ANumberOrAString_IsAUnion() {
    var result = Typed(TwoKinds);
    Assert.That(TypeOf(result.Module, "v").ToString(), Is.EqualTo("union<i16, str>"));
    var body = IrTree.Nodes(result.Module.Functions.Single(function => function.Name == "F").Body).ToList();
    Assert.That(body.OfType<IrUnionOf>().Select(made => made.Member), Is.EquivalentTo(new[] { 0, 1 }));
    Assert.That(body.OfType<IrUnionValue>(), Has.All.Matches<IrUnionValue>(value => value.IsTested));
    var place = result.Places.Single(candidate => candidate.Name == "v");
    Assert.That((place.Fact.IsStatic, place.Reason), Is.EqualTo((true, (string?)null)));
    SameResults(TwoKinds, result.Module, [true], [false]);
  }

  [Test]
  public void WithoutUnions_TheLocalStaysAVariant() {
    Assert.That(TypeOf(Typed(TwoKinds, DeDynamicizeOptions.Default).Module, "v"), Is.EqualTo(IrType.Variant));
    Assert.That(DeDynamicizeOptions.Default.Unions, Is.False);
  }

  [Test]
  public void FourKinds_AreAUnion_AFifthIsAVariant() {
    const string four = "Public Function F(ByVal n As Integer)\nDim v\nSelect Case n\nCase 1\nv = CInt(1)\nCase 2\nv = CLng(2)\nCase 3\nv = \"c\"\nCase Else\nv = True\nEnd Select\nF = v\nEnd Function\n";
    var typed = Typed(four).Module;
    Assert.That(TypeOf(typed, "v").ToString(), Is.EqualTo("union<bool, i16, i32, str>"));
    SameResults(four, typed, [(short)1], [(short)2], [(short)3], [(short)4]);
    var five = four.Replace("Case Else\nv = True", "Case 4\nv = True\nCase Else\nv = CDbl(5)");
    Assert.That(TypeOf(Typed(five).Module, "v"), Is.EqualTo(IrType.Variant));
  }

  [Test]
  public void TwoKindsOfOneType_AreNoUnionOfTheIr() {
    const string source = "Public Function F(ByVal c As Boolean)\nDim v\nIf c Then\nv = CDbl(1)\nElse\nv = CDate(1)\nEnd If\nF = v\nEnd Function\n";
    Assert.That(TypeOf(Typed(source).Module, "v"), Is.EqualTo(IrType.Variant), "a Double and a Date are both f64");
  }

  [Test]
  public void AValueThatIsItselfOfSeveralKinds_KeepsTheLocalAVariant() {
    const string source = "Public Function F(ByVal c As Boolean)\nDim v, w\nIf c Then\nv = 1\nElse\nv = \"a\"\nEnd If\nw = v\nF = w\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(TypeOf(typed, "v").ToString(), Is.EqualTo("union<i16, str>"));
    Assert.That(TypeOf(typed, "w"), Is.EqualTo(IrType.Variant), "assigned the union, no member of it");
    SameResults(source, typed, [true], [false]);
  }

  [Test]
  public void AStoreNoReadSees_KeepsOnlyItsEffects() {
    const string source = "Public Function F(ByVal c As Boolean)\nDim v\nv = CDbl(2)\nIf c Then\nv = 1\nElse\nv = \"a\"\nEnd If\nF = v\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(TypeOf(typed, "v").ToString(), Is.EqualTo("union<i16, str>"));
    SameResults(source, typed, [true], [false]);
  }

  [Test]
  public void AUnionReadWhereItMayBeEmpty_StaysAVariant() {
    const string source = "Public Function F(ByVal n As Integer)\nDim v\nIf n = 1 Then v = 1\nIf n = 2 Then v = \"a\"\nF = v\nEnd Function\n";
    Assert.That(TypeOf(Typed(source).Module, "v"), Is.EqualTo(IrType.Variant));
  }

}
