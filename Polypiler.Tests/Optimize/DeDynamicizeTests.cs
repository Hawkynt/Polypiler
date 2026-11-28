using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;
using Polypiler.Tests.Frontends.VisualBasic;

namespace Polypiler.Tests.Optimize;

/// <summary>
/// The de-dynamicization (ADR 0300): Visual Basic modules lowered with every <c>Variant</c> dynamic
/// (<see cref="VisualBasicInference.Framework"/>) and typed by the shared framework with the Visual
/// Basic table - within a function (#1585), on tag tests (#1586) and across the module by clones
/// (#1587) - each result verified and computed as the frontend's own typing computes it.
/// </summary>
[TestFixture]
public sealed class DeDynamicizeTests {

  private static object? Run(IrModule module, string function, params object[] arguments) => IrInterpreter.Call(module, function, arguments);

  private static IrModule Import(string source, VisualBasicInference inference = VisualBasicInference.Framework) {
    var result = VisualBasicFrontend.Import("Test.bas", source, new VisualBasicImportOptions { Inference = inference });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  private static DeDynamicizeResult Typed(string source, DeDynamicizeOptions? options = null) {
    var module = Import(source);
    Assert.That(module.DynamicSemantics, Is.EqualTo("visualbasic"));
    var result = DeDynamicize.Analyse(module, VisualBasicFrontend.Semantics, options ?? DeDynamicizeOptions.Default);
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    return result;
  }

  private static IrType TypeOf(IrModule module, string function, string local)
    => module.Functions.Single(candidate => candidate.Name == function).Locals.Concat(module.Functions.Single(candidate => candidate.Name == function).Parameters).Single(candidate => candidate.Name == local).Type;

  private static string Signature(IrFunction function) => $"{function.Name}({string.Join(", ", function.Parameters.Select(parameter => parameter.Type))}) -> {function.ReturnType}";

  /// <summary>The source and every function of it returns the same in the frontend's typing and in the framework's.</summary>
  private static void SameResults(string source, IrModule typed, params (string Function, object[] Arguments)[] calls) {
    var frontend = Import(source, VisualBasicInference.Frontend);
    foreach (var (function, arguments) in calls)
      Assert.That(Run(typed, function, arguments), Is.EqualTo(Run(frontend, function, arguments)), function);
  }

  #region Within a function (#1585)

  [Test]
  public void ALocalOfOneKind_GetsItsType() {
    const string source = "Public Function F()\nDim s, i\ns = CDbl(0)\nFor i = 1 To 10\ns = s + CDbl(i)\nNext\nF = s\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(TypeOf(typed, "F", "s"), Is.EqualTo(IrType.F64));
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.F64, 55.0)));
    var calls = IrTree.Nodes(typed.Functions.Single(function => function.Name == "F").Body).OfType<IrCall>().Select(call => call.Function).ToList();
    Assert.That(calls, Has.Member("PlyCheckD"), "the addition is typed");
    Assert.That(calls.Count(call => call == "PlyVarArith"), Is.EqualTo(1), "only the step of the Variant counter stays dynamic");
    SameResults(source, typed, ("F", []));
  }

  [Test]
  public void ALongAccumulator_StaysAVariant_ForItsSumMayPromoteToDouble() {
    const string source = "Public Function F()\nDim s, i\ns = CLng(0)\nFor i = 1 To 10\ns = s + CLng(i)\nNext\nF = s\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(TypeOf(typed, "F", "s"), Is.EqualTo(IrType.Variant));
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.I32, 55)));
  }

  [Test]
  public void ALocalOfTwoKinds_StaysAVariant() {
    const string source = "Public Function F(ByVal c As Boolean)\nDim v\nIf c Then\nv = 1\nElse\nv = \"a\"\nEnd If\nF = v\nEnd Function\n";
    var result = Typed(source);
    Assert.That(TypeOf(result.Module, "F", "v"), Is.EqualTo(IrType.Variant));
    Assert.That(result.Places.Single(place => place.Name == "v").Fact.Type.ToString(), Is.EqualTo("union<i16, str>"));
    Assert.That(result.Places.Single(place => place.Name == "v").Reason, Is.EqualTo("assigned i16 and str"));
    SameResults(source, result.Module, ("F", [true]), ("F", [false]));
  }

  [Test]
  public void ALoopThatGrowsTheFacts_Converges() {
    const string source = "Public Function F()\nDim x\nx = CInt(1)\nDo While x < 100000\nx = x * 2\nLoop\nF = x\nEnd Function\n";
    var result = Typed(source);
    Assert.That(TypeOf(result.Module, "F", "x"), Is.EqualTo(IrType.Variant));
    Assert.That(result.Places.Single(place => place.Name == "x").Fact.Type.ToString(), Is.EqualTo("union<f64, i16, i32>"), "Long doubled may promote to Double");
    Assert.That(Run(result.Module, "F"), Is.EqualTo(new IrVariant(IrVariantTag.I32, 131072)));
  }

  [Test]
  public void ALoopBeyondTheRounds_LeavesWhatItChangesDynamic() {
    const string source = "Public Function F()\nDim x\nx = CInt(1)\nDo While x < 100000\nx = x * 2\nLoop\nF = x\nEnd Function\n";
    var result = Typed(source, new DeDynamicizeOptions { MostRounds = 0 });
    Assert.That(result.Places.Single(place => place.Name == "x").Fact.Type, Is.EqualTo(DynamicType.Variant));
    Assert.That(Run(result.Module, "F"), Is.EqualTo(new IrVariant(IrVariantTag.I32, 131072)));
  }

  [Test]
  public void EarlyExits_JoinTheirFactsAfterTheLoop() {
    const string mixed = "Public Function F(ByVal n As Long)\nDim v, i\nv = CDbl(1)\nFor i = 1 To 3\nIf i = n Then\nv = \"a\"\nExit For\nEnd If\nNext\nF = v\nEnd Function\n";
    var typed = Typed(mixed).Module;
    Assert.That(TypeOf(typed, "F", "v"), Is.EqualTo(IrType.Variant));
    SameResults(mixed, typed, ("F", [2]), ("F", [9]));
    const string same = "Public Function F(ByVal n As Long)\nDim v, i\nv = CDbl(1)\nDo\nIf n > 3 Then Exit Do\nv = v * 2\nn = n + 1\nLoop\nF = v\nEnd Function\n";
    var kept = Typed(same).Module;
    Assert.That(TypeOf(kept, "F", "v"), Is.EqualTo(IrType.F64));
    SameResults(same, kept, ("F", [1]), ("F", [7]));
  }

  [Test]
  public void AnAssignmentThatMayBeSkipped_AddsToWhatTheLocalHolds() {
    const string source = "Public Function F(ByVal d As Double)\nDim v\nv = CLng(1)\nOn Error Resume Next\nv = 1 / d\nF = v\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(TypeOf(typed, "F", "v"), Is.EqualTo(IrType.Variant));
    SameResults(source, typed, ("F", [2.0]), ("F", [0.0]));
  }

  [Test]
  public void AModuleWithoutVariants_IsReturnedAsItIs() {
    var module = Import("Public Function F(ByVal n As Long) As Long\nF = n * 2\nEnd Function\n");
    var result = DeDynamicize.Analyse(module, VisualBasicFrontend.Semantics, DeDynamicizeOptions.Default);
    Assert.That(result.Module, Is.SameAs(module));
    Assert.That(result.Places, Is.Empty);
    Assert.That(DeDynamicize.Run(module, VisualBasicFrontend.Semantics, DeDynamicizeOptions.Default), Is.SameAs(module));
    Assert.That(() => DeDynamicize.Run(null!, VisualBasicFrontend.Semantics, DeDynamicizeOptions.Default), Throws.ArgumentNullException);
    Assert.That(() => DeDynamicize.Run(module, null!, DeDynamicizeOptions.Default), Throws.ArgumentNullException);
    Assert.That(() => DeDynamicize.Run(module, VisualBasicFrontend.Semantics, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void AReadWhereTheLocalMayStillBeEmpty_KeepsItAVariant() {
    const string source = "Public Function F(ByVal c As Boolean)\nDim v\nIf c Then v = CDbl(2)\nF = v\nEnd Function\n";
    var result = Typed(source);
    Assert.That(TypeOf(result.Module, "F", "v"), Is.EqualTo(IrType.Variant));
    Assert.That(result.Places.Single(place => place.Name == "v").Reason, Is.EqualTo("read where it may still be missing"));
    SameResults(source, result.Module, ("F", [true]), ("F", [false]));
  }

  /// <summary>The fixtures of #1556 and #1564 lowered dynamic and typed by the framework: every case computes what LibreOffice printed, and every local the frontend types the framework types the same, but for the counters of For loops, which need the range of the bounds (#1591).</summary>
  [TestCase("Variants", "Accumulate.i:i16;Joined.i:i16;Counted.ply1:variant;Counted.ply2:variant;Counted.ply3:variant")]
  [TestCase("Inference", "")]
  [TestCase("Numeric", "")]
  [TestCase("Bitwise", "")]
  [TestCase("CurrencyDate", "")]
  [TestCase("Locale", "")]
  public void Fixtures_ComputeTheirCases_AndTypeAsTheFrontendDoes(string fixture, string differences) {
    var cases = ClassicCases.Lines(fixture + ".cases.txt");
    var expected = ClassicCases.Lines(fixture + ".expected.txt");
    var source = ClassicCases.Source(fixture + ".bas", cases);
    var frontend = VisualBasicFrontend.Import(fixture + ".bas", source).Module!;
    var raw = VisualBasicFrontend.Import(fixture + ".bas", source, new VisualBasicImportOptions { Inference = VisualBasicInference.Framework }).Module!;
    var typed = DeDynamicize.Run(raw, VisualBasicFrontend.Semantics, DeDynamicizeOptions.Default);
    Assert.That(IrVerifier.Verify(typed), Is.Empty);
    for (var index = 0; index < cases.Count; ++index)
      Assert.That(ClassicCases.Interpret(typed, cases.Count)[index], Is.EqualTo(expected[index]), cases[index]);

    var found = new List<string>();
    foreach (var function in frontend.Functions.Where(function => !VisualBasicFrontend.Semantics.IsHelper(function.Name) && !function.Name.StartsWith("Probe", StringComparison.Ordinal))) {
      var other = typed.Functions.Single(candidate => candidate.Name == function.Name);
      Assert.That(Signature(other), Is.EqualTo(Signature(function)), "the signature, clones included");
      foreach (var local in function.Locals.Where(local => other.Locals.Single(candidate => candidate.Name == local.Name).Type != local.Type))
        found.Add($"{function.Name}.{local.Name}:{local.Type}");
    }

    Assert.That(string.Join(";", found), Is.EqualTo(differences));
    Assert.That(typed.Functions.Select(function => function.Name).Where(name => !VisualBasicFrontend.Semantics.IsHelper(name)).Except(frontend.Functions.Select(function => function.Name)), Is.Empty, "no clone the frontend does not make");
  }

  #endregion

  #region Refinement (#1586)

  private static IrFunction Function(IrModule module, string name) => module.Functions.Single(function => function.Name == name);

  [Test]
  public void TheTrueBranchOfATagTest_SeesTheTestedTag() {
    const string source = "Public Function F(v)\nIf VarType(v) = 5 Then\nF = v + 1\nElse\nF = 0\nEnd If\nEnd Function\n";
    var typed = Typed(source).Module;
    var nodes = IrTree.Nodes(Function(typed, "F").Body).ToList();
    Assert.That(nodes.OfType<IrVariantValue>().Where(value => value.Tag == IrVariantTag.F64), Has.Some.Matches<IrVariantValue>(value => value.IsTested));
    Assert.That(nodes.OfType<IrCall>().Select(call => call.Function), Has.Member("PlyCheckD").And.No.Member("PlyVarArith"));
    SameResults(source, typed, ("F", [new IrVariant(IrVariantTag.F64, 2.5)]), ("F", [new IrVariant(IrVariantTag.I16, (short)2)]));
  }

  [Test]
  public void ATestOfAnotherValue_DoesNotNarrow() {
    const string source = "Public Function F(v, w)\nIf VarType(w) = 5 Then\nF = v + 1\nEnd If\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(IrTree.Nodes(Function(typed, "F").Body).OfType<IrCall>().Select(call => call.Function), Has.Member("PlyVarArith"));
  }

  [Test]
  public void TheElseBranch_HasTheTagRemoved_AndAnInequalityNarrowsItsOtherBranch() {
    const string source = "Public Function F(v)\nDim d\nd = CDbl(1)\nIf VarType(d) <> 5 Then\nF = \"never\"\nElse\nF = d\nEnd If\nIf VarType(v) <> 5 Then\nF = 0\nElse\nF = v * 2\nEnd If\nEnd Function\n";
    var typed = Typed(source).Module;
    var calls = IrTree.Nodes(Function(typed, "F").Body).OfType<IrCall>().Select(call => call.Function).ToList();
    Assert.That(calls, Has.Member("PlyCheckD").And.No.Member("PlyVarArith"));
    Assert.That(IrTree.Nodes(Function(typed, "F").Body).OfType<IrStrConst>().Select(text => text.Value), Has.No.Member("never"), "the branch no value reaches is folded");
    SameResults(source, typed, ("F", [new IrVariant(IrVariantTag.F64, 2.0)]), ("F", [new IrVariant(IrVariantTag.String, "x")]));
  }

  [Test]
  public void ASelectOnTheTag_ComputesWhatTheFrontendComputes() {
    // Select Case lowers to tests of a copy of the tag, which the framework does not follow (yet).
    const string source = "Public Function F(v)\nSelect Case VarType(v)\nCase 5\nF = v + 1\nCase 8, 2\nF = v & \"!\"\nCase Else\nF = Null\nEnd Select\nEnd Function\n";
    var typed = Typed(source).Module;
    SameResults(source, typed, ("F", [new IrVariant(IrVariantTag.F64, 1.5)]), ("F", [new IrVariant(IrVariantTag.String, "a")]), ("F", [IrVariant.Empty]));
  }

  /// <summary>A switch on the tag narrows each section to its labels, and the default section to the other tags; built by hand.</summary>
  [Test]
  public void ASwitchOnTheTag_NarrowsEachSection() {
    var helpers = Import("Public Function G(a, b)\nG = a + b\nEnd Function\nPublic Function H(ByVal x As Double) As Double\nH = x + x\nEnd Function\n");
    var v = new IrLocal("v", IrType.Variant);
    var b = new IrLocal("b", IrType.Bool);
    var one = new IrVariantOf(IrVariantTag.F64, new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(1.0)));
    IrExpr Plus() => new IrCall("PlyVarArith", IrType.Variant, [new IrConst(IrType.I16, 0), new IrLocalRef(v), one]);
    var body = new IrBlock([
      new IrDeclare(v, new IrConditional(new IrLocalRef(b), one, new IrVariantOf(IrVariantTag.I32, new IrConst(IrType.I32, 4)))),
      new IrSwitch(new IrVariantTagOf(new IrLocalRef(v)), [
        new IrSwitchSection([5], false, new IrBlock([new IrReturn(Plus())])),
        new IrSwitchSection([8, 2], false, new IrBlock([new IrBreak()])),
        new IrSwitchSection([], true, new IrBlock([new IrIf(new IrBinary(IrBinaryOp.Equal, new IrVariantTagOf(new IrLocalRef(v)), new IrConst(IrType.I32, 5)),
          new IrBlock([new IrReturn(new IrVariantOf(IrVariantTag.String, new IrStrConst("impossible")))]), null)]))
      ]),
      new IrReturn(Plus())
    ]);
    var module = helpers with { Functions = [.. helpers.Functions, new IrFunction("S", true, IrType.Variant, [b], [v], body)] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var typed = DeDynamicize.Run(module, VisualBasicFrontend.Semantics, DeDynamicizeOptions.Default);
    Assert.That(IrVerifier.Verify(typed), Is.Empty);
    var sections = ((IrSwitch)Function(typed, "S").Body.Statements[1]).Sections;
    Assert.That(IrTree.Nodes(sections[0].Body).OfType<IrCall>().Select(call => call.Function), Is.EqualTo(new[] { "PlyCheckD" }), "the Double section adds typed");
    Assert.That(IrTree.Nodes(sections[2].Body).OfType<IrStrConst>(), Is.Empty, "the default section holds no Double");
    Assert.That(IrTree.Nodes(Function(typed, "S").Body.Statements[2]).OfType<IrCall>().Select(call => call.Function), Is.EqualTo(new[] { "PlyCheckD" }), "only the Long falls out of the switch, and Long + Double is a Double");
    foreach (var argument in new[] { true, false })
      Assert.That(Run(typed, "S", argument), Is.EqualTo(Run(module, "S", argument)), argument.ToString());
  }
  [Test]
  public void ATestOfTheOnlyPossibleTag_FoldsTheOtherBranch() {
    const string source = "Public Function F()\nDim v\nv = CDbl(1)\nIf VarType(v) = 5 Then\nF = \"double\"\nElse\nF = \"other\"\nEnd If\nIf IsNull(v) Then F = \"null\"\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(IrTree.Nodes(Function(typed, "F").Body).OfType<IrStrConst>().Select(text => text.Value), Has.Member("double").And.No.Member("other").And.No.Member("null"));
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.String, "double")));
  }

  [Test]
  public void AnAssignmentInTheBranch_EndsTheNarrowing() {
    const string source = "Public Function F(v)\nIf VarType(v) = 5 Then\nv = \"a\"\nF = v + 1\nEnd If\nEnd Function\n";
    var module = Import("Public Function F(w)\nDim v\nv = w\nIf VarType(v) = 5 Then\nv = \"2\"\nF = v + 1\nEnd If\nEnd Function\n");
    var typed = DeDynamicize.Run(module, VisualBasicFrontend.Semantics, DeDynamicizeOptions.Default);
    Assert.That(IrTree.Nodes(Function(typed, "F").Body).OfType<IrCall>().Select(call => call.Function), Has.Member("PlyVarArith"));
    Assert.That(Run(typed, "F", new IrVariant(IrVariantTag.F64, 1.0)), Is.EqualTo(new IrVariant(IrVariantTag.F64, 3.0)));
    Assert.That(source, Is.Not.Empty);
  }

  [Test]
  public void TestsCombine_AndNegate() {
    const string source = "Public Function F(v, w)\nIf VarType(v) = 5 And Not IsNull(w) Then\nF = v * 3\nEnd If\nIf IsNull(w) Or VarType(v) <> 5 Then\nF = 0\nElse\nF = v * 2\nEnd If\nEnd Function\n";
    var typed = Typed(source).Module;
    SameResults(source, typed, ("F", [new IrVariant(IrVariantTag.F64, 1.5), IrVariant.Empty]), ("F", [new IrVariant(IrVariantTag.F64, 1.5), IrVariant.Null]), ("F", [IrVariant.Empty, IrVariant.Empty]));
  }

  #endregion

  #region Across the module (#1587)

  [Test]
  public void APrivateFunctionCalledWithLongs_IsTyped() {
    const string source = "Private Function Mul(a, b)\nMul = a * b\nEnd Function\nPublic Function F()\nF = Mul(CLng(3), CLng(4))\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(Signature(Function(typed, "Mul")), Is.EqualTo("Mul(i32, i32) -> variant"));
    Assert.That(Function(typed, "Mul").SpecialisedFrom, Is.EqualTo("Mul"));
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.I32, 12)));
  }

  [Test]
  public void APublicFunction_KeepsItsSignature_AndGetsAClone() {
    const string source = "Public Function Mul(a, b)\nMul = a * b\nEnd Function\nPublic Function F()\nF = Mul(CDbl(3), CDbl(4))\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(Signature(Function(typed, "Mul")), Is.EqualTo("Mul(variant, variant) -> variant"));
    Assert.That(Signature(Function(typed, "Mul_Double_Double")), Is.EqualTo("Mul_Double_Double(f64, f64) -> f64"));
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.F64, 12.0)));
    SameResults(source, typed, ("F", []), ("Mul", [new IrVariant(IrVariantTag.I16, (short)2), new IrVariant(IrVariantTag.String, "3")]));
  }

  [Test]
  public void AFifthSignature_CallsTheDynamicFunction() {
    const string source = "Private Function Id(a)\nId = a\nEnd Function\nPublic Function F()\nF = Id(CInt(1)) & Id(CLng(2)) & Id(CDbl(3)) & Id(\"4\") & Id(CSng(5))\nEnd Function\n";
    var typed = Typed(source).Module;
    var clones = typed.Functions.Where(function => function.SpecialisedFrom == "Id").ToList();
    Assert.That(clones, Has.Count.EqualTo(DeDynamicizeOptions.Default.MostClones));
    Assert.That(typed.Functions.Select(function => function.Name), Has.Member("Id"), "the dynamic function stays for the fifth call");
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.String, "12345")));
  }

  [Test]
  public void RoundsBeyondTheBudget_LeaveEveryFunctionDynamic() {
    var module = ClassicCases.Import("Inference.bas", "Inference.cases.txt");
    var raw = VisualBasicFrontend.Import("Inference.bas", ClassicCases.Source("Inference.bas", ClassicCases.Lines("Inference.cases.txt")), new VisualBasicImportOptions { Inference = VisualBasicInference.Framework }).Module!;
    var typed = DeDynamicize.Run(raw, VisualBasicFrontend.Semantics, new DeDynamicizeOptions { MostRounds = 1 });
    Assert.That(typed.Functions.Where(function => function.SpecialisedFrom is not null), Is.Empty);
    Assert.That(IrVerifier.Verify(typed), Is.Empty);
    Assert.That(ClassicCases.Interpret(typed, 7), Is.EqualTo(ClassicCases.Interpret(module, 7)));
  }

  [Test]
  public void Recursion_ReachesAFixedPoint() {
    const string source = "Private Function Fact(n)\nIf n <= 1 Then\nFact = CDbl(1)\nElse\nFact = n * Fact(n - 1)\nEnd If\nEnd Function\nPublic Function F()\nF = Fact(CDbl(10))\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(Signature(Function(typed, "Fact")), Is.EqualTo("Fact(f64) -> f64"));
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.F64, 3628800.0)));
  }

  [Test]
  public void AResultThatMayBeEmpty_StaysAVariant() {
    const string source = "Private Function G(c)\nIf c Then G = CDbl(1)\nEnd Function\nPublic Function F(ByVal b As Boolean)\nF = G(b)\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(Signature(Function(typed, "G")), Is.EqualTo("G(bool) -> variant"));
    SameResults(source, typed, ("F", [true]), ("F", [false]));
  }

  [Test]
  public void ATakenName_GetsANumber() {
    const string source = "Public Function Mul(a, b)\nMul = a * b\nEnd Function\nPublic Function Mul_Double_Double()\nMul_Double_Double = 0\nEnd Function\nPublic Function F()\nF = Mul(CDbl(3), CDbl(4))\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(typed.Functions.Select(function => function.Name), Has.Member("Mul_Double_Double_2"));
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.F64, 12.0)));
  }

  [Test]
  public void AParameterTheBodyWrites_IsNotSpecialised() {
    const string source = "Private Function G(ByVal a)\na = a & \"!\"\nG = a\nEnd Function\nPublic Function F()\nF = G(CDbl(1))\nEnd Function\n";
    var typed = Typed(source).Module;
    Assert.That(typed.Functions.Where(function => function.SpecialisedFrom is not null), Is.Empty);
    // The frontend's own clone types the parameter Double and then assigns it a String (#1564), which
    // fails with a type mismatch; Visual Basic returns "1!".
    Assert.That(Run(typed, "F"), Is.EqualTo(new IrVariant(IrVariantTag.String, "1!")));
  }

  #endregion

  [Test]
  public void TheModuleFormat_KeepsTheLanguageOfTheTable() {
    var module = Import("Public Function F(v)\nF = v\nEnd Function\n");
    Assert.That(IrModuleFormat.Read(IrModuleFormat.Write(module)).DynamicSemantics, Is.EqualTo("visualbasic"));
    var plain = module with { DynamicSemantics = null };
    Assert.That(IrModuleFormat.Read(IrModuleFormat.Write(plain)).DynamicSemantics, Is.Null);
    var sourced = module with { Sources = [new SourceArtifact("r", "0000000000000000000000000000000000000000", "a.bas", new string('0', 64), "vba7")] };
    var read = IrModuleFormat.Read(IrModuleFormat.Write(sourced));
    Assert.That((read.DynamicSemantics, read.Sources.Count), Is.EqualTo(("visualbasic", 1)));
  }

  [Test]
  public void Places_SayWhatEveryPlaceHolds() {
    const string source = "Public Function F(v, ByVal n As Long)\nDim a, b\na = CDbl(n)\nb = v\nF = a\nEnd Function\n";
    var places = Typed(source).Places.Where(place => place.Function == "F").ToList();
    Assert.That(places.Select(place => (place.Name, place.Kind, place.Fact.Type.ToString())), Is.EquivalentTo(new[] {
      ("v", DynamicPlaceKind.Parameter, "variant"), ("n", DynamicPlaceKind.Parameter, "i32"),
      ("a", DynamicPlaceKind.Local, "f64"), ("b", DynamicPlaceKind.Local, "variant"), ("F", DynamicPlaceKind.Local, "f64"),
      ("F", DynamicPlaceKind.Result, "variant")
    }));
    Assert.That(places.Single(place => place.Name == "v").Reason, Does.Contain("public function"));
    Assert.That(places.Single(place => place.Kind == DynamicPlaceKind.Result).Reason, Does.Contain("host"));
    Assert.That(places.Single(place => place.Name == "b").Reason, Does.Contain("any type"));
    Assert.That(places.Single(place => place.Name == "a").Reason, Is.Null);
  }

}
