using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Batch;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.Go;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.VBScript;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Backends.VisualBasic;
using Polypiler.Tests.Frontends.VisualBasic;

namespace Polypiler.Tests.Backends;

/// <summary>
/// The IR's dynamic type through the backends: Visual Basic writes a <c>Variant</c>, C# an
/// <c>object</c>, JavaScript a pair and Python a tuple, each computing what the interpreter computes;
/// every other backend refuses it with its diagnostic instead of writing it wrongly.
/// </summary>
[TestFixture]
public sealed class VariantBackendTests {

  private static readonly string _classic = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "classic");

  private static IrModule Fixture() => VisualBasicFrontend.Import("Variants.bas", File.ReadAllText(Path.Combine(_classic, "Variants.bas"))).Module!;

  private static readonly IrLocal V = new("v", IrType.Variant);

  /// <summary>A module of every node of the type: a function that boxes, reads the tag and unboxes with and without a test.</summary>
  private static IrModule Nodes(IrVariantTag tag = IrVariantTag.I16) {
    var payload = IrVariants.PayloadType(tag)!;
    var x = new IrLocal("x", payload);
    var y = new IrLocal("y", payload);
    var body = new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrVariantTagOf(new IrLocalRef(V)), new IrConst(IrType.I32, 0)),
        new IrBlock([new IrReturn(new IrVariantOf(tag, new IrLocalRef(x)))]), null),
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrVariantTagOf(new IrLocalRef(V)), new IrConst(IrType.I32, 1)),
        new IrBlock([new IrReturn(new IrVariantOf(IrVariantTag.Null, null))]), null),
      new IrDeclare(y, new IrVariantValue(new IrLocalRef(V), tag)),
      new IrReturn(new IrVariantOf(tag, new IrVariantValue(new IrVariantOf(tag, new IrLocalRef(y)), tag) { IsTested = true }))
    ]);
    return new IrModule("Nodes", [new IrFunction("Pass", true, IrType.Variant, [V, x], [y], body)]);
  }

  [Test]
  public void VisualBasic_WritesVariants() {
    var translation = VisualBasicFixtures.Translate(Fixture());
    Assert.That(translation.Problems, Is.Empty);
    Assert.That(translation.Source, Does.Contain("Dim v As Variant").And.Contain("v = Empty").And.Contain("v = Null").And.Contain("VarType(").And.Contain("Public Function Identity(ByVal x As Variant) As Variant"));
    Assert.That(translation.Source, Does.Contain("Dim v As Long"), "the local of one type");
    var nodes = VisualBasicFixtures.Translate(Nodes());
    Assert.That(nodes.Problems, Is.Empty);
    Assert.That(nodes.Source, Does.Contain("If VarType(v) <> 2 Then").And.Contain("Err.Raise 13").And.Contain("CInt("));
  }

  [Test]
  public void VisualBasic_WritesError94ForInvalidOperation() {
    var module = VisualBasicFrontend.Import("T.bas", "Option Explicit\nPublic Sub S()\nError 94\nEnd Sub\n").Module!;
    Assert.That(VisualBasicFixtures.Translate(module).Source, Does.Contain("Err.Raise 94"));
  }

  [Test]
  public void VisualBasic_WritesCurrencyAndDate() {
    var date = VisualBasicFixtures.Translate(Nodes(IrVariantTag.Date), VisualBasicProfile.Vba7);
    Assert.That(date.Problems, Is.Empty);
    Assert.That(date.Source, Does.Contain("CDate(x)").And.Contain("CDbl("));
    var currency = VisualBasicFixtures.Translate(Nodes(IrVariantTag.Currency), VisualBasicProfile.Vba7x64);
    Assert.That(currency.Problems, Is.Empty);
    Assert.That(currency.Source, Does.Contain("CCur(CDec(x) / 10000)").And.Contain("CLngLng(CDec("));
    Assert.That(VisualBasicEmitter.Check(Nodes(IrVariantTag.Currency), VisualBasicProfile.Vba7).Select(problem => problem.Message), Has.Some.Contains("LongLong"));
  }


  [Test]
  public void CSharp_WritesObjects_AndComputesWhatTheInterpreterComputes() {
    var module = Fixture();
    var legalized = CSharpEmitter.Legalize(module, () => PrecomputedImplementations.Module, CSharpProfile.Default, FloatMode.Accurate);
    Assert.That(CSharpEmitter.Check(legalized, CSharpProfile.Default), Is.Empty);
    var text = CSharpEmitter.Emit(legalized, CSharpProfile.Default).Single().Content;
    Assert.That(text, Does.Contain("global::System.DBNull.Value").And.Contain("(object)null").And.Contain("VariantTag("));
    var nodes = CSharpEmitter.Emit(Nodes(), CSharpProfile.Default).Single().Content;
    Assert.That(nodes, Does.Contain("VariantPayload(v)").And.Contain("(object)(short)"));
    Assert.That(CSharpEmitter.Check(Nodes(IrVariantTag.Currency), CSharpProfile.Default), Is.Empty);
    Assert.That(CSharpEmitter.Emit(Nodes(IrVariantTag.Currency), CSharpProfile.Default).Single().Content, Does.Contain("(decimal)x / (decimal)10000").And.Contain("* (decimal)10000"));
    Assert.That(CSharpEmitter.Emit(Nodes(IrVariantTag.Date), CSharpProfile.Default).Single().Content, Does.Contain("new VariantDate(x)").And.Contain("public sealed class VariantDate").And.Contain(".Value"));
  }

  [Test, Category("Slow")]
  public void CSharp_RunsTheFixture() {
    var module = Fixture();
    var legalized = CSharpEmitter.Legalize(module, () => PrecomputedImplementations.Module, CSharpProfile.Default, FloatMode.Accurate);
    var build = GeneratedCSharp.Compile(CSharpEmitter.Emit(legalized, CSharpProfile.Default).Single().Content, false);
    foreach (var call in Calls())
      Assert.That(build.Outcome(call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)), call.Method);
    var nodes = GeneratedCSharp.Compile(CSharpEmitter.Emit(Nodes(), CSharpProfile.Default).Single().Content, false);
    Assert.That(nodes.Outcome(new PrototypeCall("Pass", [(object)(int)7, (short)1])), Does.Contain("InvalidCast").Or.Contain("cast"));
  }

  [TestCase("f64", IrBinaryOp.NotEqual, "double.IsNaN(x)")]
  [TestCase("f32", IrBinaryOp.NotEqual, "float.IsNaN(x)")]
  [TestCase("f64", IrBinaryOp.Equal, "!double.IsNaN(x)")]
  public void CSharp_WritesTheSelfComparisonOfAFloatAsANaNTest(string type, IrBinaryOp op, string expected) {
    var x = new IrLocal("x", type == "f32" ? IrType.F32 : IrType.F64);
    var y = new IrLocal("y", x.Type);
    var module = new IrModule("M", [
      new IrFunction("F", true, IrType.Bool, [x, y], [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.LogicalAnd, new IrBinary(op, new IrLocalRef(x), new IrLocalRef(x)), new IrBinary(op, new IrLocalRef(x), new IrLocalRef(y))))]))
    ]);
    var text = CSharpEmitter.Emit(module, CSharpProfile.Default).Single().Content;
    Assert.That(text, Does.Contain(expected).And.Contain(op == IrBinaryOp.Equal ? "x == y" : "x != y"));
  }

  [Test, Category("Slow")]
  public void CSharpHelpers_AreCSharp2ForNetFramework20() {
    var text = $"public static class Helpers {{\n\n  {string.Join("\n  ", CSharpVariantHelpers.Tag.Lines("VariantTag"))}\n\n  {string.Join("\n  ", CSharpVariantHelpers.Payload.Lines("VariantPayload"))}\n\n}}\n";
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net2.0", "2"), Is.Empty, text);
  }

  /// <summary>The calls of the fixture with arguments of plain types, whose results every runner prints alike.</summary>
  internal static IReadOnlyList<PrototypeCall> Calls() => [.. VisualBasicVariantTests.Calls()
    .Where(call => call.Function is not ("NullToInteger" or "Identity" or "KindOf"))
    .Select(call => new PrototypeCall(call.Function, call.Arguments))];

  [Test]
  public void JavaScript_WritesPairs() {
    var legalized = JavaScriptEmitter.Legalize(Fixture(), () => PrecomputedImplementations.Module, JavaScriptProfile.Default, FloatMode.Accurate);
    var text = JavaScriptEmitter.Emit(legalized, JavaScriptProfile.Default).Single().Content;
    Assert.That(text, Does.Contain("[0]").And.Contain("[1]").And.Contain("[tag, value]"));
    var nodes = JavaScriptEmitter.Emit(Nodes(), JavaScriptProfile.Default).Single().Content;
    Assert.That(nodes, Does.Contain("function $variant(value, tag)").And.Contain("$variant(v, 2)").And.Contain("[2, "));
    Assert.That(JavaScriptEmitter.Emit(Nodes(IrVariantTag.Date), JavaScriptProfile.Default).Single().Content, Does.Contain("[7, "));
    foreach (var tag in new[] { IrVariantTag.I64, IrVariantTag.Currency })
      Assert.That(() => JavaScriptEmitter.Emit(Nodes(tag), JavaScriptProfile.Default), Throws.ArgumentException.With.Message.Contains($"a variant of the tag {tag}"));
  }

  [Test]
  public void Python_WritesTuples() {
    var legalized = PythonEmitter.Legalize(Fixture(), () => PrecomputedImplementations.Module, PythonProfile.Default, FloatMode.Accurate);
    var text = PythonEmitter.Emit(legalized, PythonProfile.Default).Single().Content;
    Assert.That(text, Does.Contain("(0,)").And.Contain("(1,)").And.Contain("-> tuple"));
    var nodes = PythonEmitter.Emit(Nodes(), PythonProfile.Default).Single().Content;
    Assert.That(nodes, Does.Contain("def _ply_variant(value: tuple, tag: int) -> object:").And.Contain("_ply_variant(v, 2)").And.Contain("[1]"));
    Assert.That(PythonEmitter.Emit(Nodes(IrVariantTag.Currency), PythonProfile.Default).Single().Content, Does.Contain("(6, "));
    Assert.That(PythonEmitter.Emit(Nodes(IrVariantTag.Date), PythonProfile.Default).Single().Content, Does.Contain("(7, "));
  }

  private static IEnumerable<TestCaseData> Others() {
    yield return new TestCaseData("rust", (System.Func<IrModule, IReadOnlyList<Diagnostic>>)(module => RustEmitter.Check(module, RustProfile.Default)));
    yield return new TestCaseData("go", (System.Func<IrModule, IReadOnlyList<Diagnostic>>)(module => GoEmitter.Check(module, GoProfile.Default)));
    yield return new TestCaseData("cpp", (System.Func<IrModule, IReadOnlyList<Diagnostic>>)(module => CppEmitter.Check(module, CppProfile.Default)));
    yield return new TestCaseData("jvm", (System.Func<IrModule, IReadOnlyList<Diagnostic>>)(module => JvmEmitter.Check(module, JvmProfile.Default)));
    yield return new TestCaseData("msil", (System.Func<IrModule, IReadOnlyList<Diagnostic>>)(module => MsilEmitter.Check(module, MsilProfile.Default)));
    yield return new TestCaseData("perl", (System.Func<IrModule, IReadOnlyList<Diagnostic>>)(module => PerlEmitter.Check(module, PerlProfile.Default)));
    yield return new TestCaseData("batch", (System.Func<IrModule, IReadOnlyList<Diagnostic>>)(module => BatchEmitter.Check(module)));
    yield return new TestCaseData("vbscript", (System.Func<IrModule, IReadOnlyList<Diagnostic>>)(module => VBScriptEmitter.Check(module, VBScriptProfile.Default)));
  }

  [TestCaseSource(nameof(Others))]
  public void BackendWithoutTheType_RefusesIt(string backend, System.Func<IrModule, IReadOnlyList<Diagnostic>> check)
    => Assert.That(check(Nodes()), Is.Not.Empty, backend);

  /// <summary>A module that writes a Double as Visual Basic's CStr writes it.</summary>
  private static IrModule Formatted() {
    var x = new IrLocal("x", IrType.F64);
    return new IrModule("Text", [new IrFunction("F", true, IrType.String, [x], [], new IrBlock([new IrReturn(NumberTextOperations.VisualBasic(new IrLocalRef(x), ','))]))]);
  }

  [TestCaseSource(nameof(Others))]
  public void BackendWithoutVisualBasicText_RefusesIt(string backend, System.Func<IrModule, IReadOnlyList<Diagnostic>> check)
    => Assert.That(check(Formatted()), Is.Not.Empty, backend);

}
