using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;

namespace Polypiler.Tests.IR;

[TestFixture]
public sealed class StandardOutputTests {

  [TestCase(false, new[] { "first", "second" })]
  [TestCase(true, new[] { "first", "", "second" })]
  public void TheOutputEffectSurvivesRoundTrippingAndOptimization(bool choose, string[] expected) {
    foreach (var module in Variants(FromOracle())) {
      Assert.That(IrVerifier.Verify(module), Is.Empty);
      var output = new List<string>();
      var instance = IrInterpreter.Instantiate(module, output.Add);
      Assert.That(instance.Call("Run", choose), Is.EqualTo(42));
      Assert.That(output, Is.EqualTo(expected));
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  public void AStringWithNoValueWritesAnEmptyLine(bool fromCall) {
    var missing = new IrNull(IrType.String);
    IrExpr value = fromCall ? new IrCall("Missing", IrType.String, []) : missing;
    var module = new IrModule("NullableOutput", [
      Function("Run", true, IrType.I32, [], First(), new IrWriteLine(value),
        new IrWriteLine(new IrStrConst("")), new IrReturn(Int(42))),
      Function("Missing", false, IrType.String, [], new IrReturn(missing))
    ]);
    foreach (var candidate in Variants(module)) {
      Assert.That(IrVerifier.Verify(candidate), Is.Empty);
      var output = new List<string>();
      Assert.That(IrInterpreter.Instantiate(candidate, output.Add).Call("Run"), Is.EqualTo(42));
      Assert.That(output, Is.EqualTo(new[] { "first", "", "" }));
    }
  }

  [Test]
  public void AWriteOfNullStillRequiresAnOutputSink() {
    var module = new IrModule("NullableOutput", [Function("Run", true, IrType.Void, [],
      new IrWriteLine(new IrNull(IrType.String)), new IrReturn(null))]);
    Assert.That(() => IrInterpreter.Call(module, "Run", []),
      Throws.InvalidOperationException.With.Message.Contains("output sink"));
  }

  [Test]
  public void EarlierOutputSurvivesAnOperandThatTraps() {
    foreach (var module in Variants(FromOracle())) {
      var output = new List<string>();
      var instance = IrInterpreter.Instantiate(module, output.Add);
      Assert.That(instance.Call("Trap", 2), Is.EqualTo(42));
      Assert.That(output, Is.EqualTo(new[] { "first", "done" }));

      output.Clear();
      Assert.That(() => instance.Call("Trap", 0),
        Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.DivideByZero));
      Assert.That(output, Is.EqualTo(new[] { "first" }));
    }
  }

  [Test]
  public void TheInitializerAndCallsWriteToTheirOwnInstances() {
    var main = FromOracle();
    var module = main with {
      Initializer = "Start",
      Functions = [.. main.Functions, Function("Start", false, IrType.Void, [],
        new IrWriteLine(new IrStrConst("\uD800")), new IrReturn(null))]
    };
    var first = new List<string>();
    var second = new List<string>();
    var firstInstance = IrInterpreter.Instantiate(module, first.Add);
    var secondInstance = IrInterpreter.Instantiate(module, second.Add);
    firstInstance.Call("Run", true);
    secondInstance.Call("Trap", 2);
    firstInstance.Call("Run", false);
    Assert.That(first, Is.EqualTo(new[] { "\uD800", "first", "", "second", "first", "second" }));
    Assert.That(second, Is.EqualTo(new[] { "\uD800", "first", "done" }));
  }

  [Test]
  public void TheConvenienceCallReportsAnInitializerWriteWithoutASink() {
    var module = new IrModule("InitializerOutput", [
      Function("Run", true, IrType.I32, [], new IrReturn(Int(42))),
      Function("Start", false, IrType.Void, [], First(), new IrReturn(null))
    ]) { Initializer = "Start" };

    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(() => IrInterpreter.Call(module, "Run", []), Throws.InvalidOperationException.With.Message.Contains("output"));
  }

  [Test]
  public void ItEvaluatesTheWriteOperandExactlyOnce() {
    var counter = new IrGlobal("counter", IrType.I32, Int(0));
    var read = new IrGlobalRef(counter);
    var module = new IrModule("CountedOutput", [
      Function("Main", true, IrType.I32, [], new IrWriteLine(new IrCall("Next", IrType.String, [])), new IrReturn(read)),
      Function("Next", false, IrType.String, [],
        new IrGlobalAssign(counter, new IrBinary(IrBinaryOp.Add, read, Int(1))), new IrReturn(new IrStrConst("next")))
    ]) { Globals = [counter] };
    foreach (var candidate in Variants(module)) {
      var output = new List<string>();
      Assert.That(IrInterpreter.Instantiate(candidate, output.Add).Call("Main"), Is.EqualTo(1));
      Assert.That(output, Is.EqualTo(new[] { "next" }));
    }
  }

  [Test]
  public void TheVerifierRejectsANonStringOperand() {
    var module = new IrModule("WrongType", [Function("Run", true, IrType.Void, [], new IrWriteLine(Int(42)), new IrReturn(null))]);
    Assert.That(IrVerifier.Verify(module), Has.One.Matches<Diagnostic>(problem => problem.Code == IrVerifier.Code));
  }

  [Test]
  public void AWriteRequiresAnOutputSink() {
    Assert.That(() => IrInterpreter.Call(FromOracle(), "Run", [false]), Throws.InvalidOperationException.With.Message.Contains("output"));
    Assert.That(() => IrInterpreter.Instantiate(FromOracle(), null!), Throws.ArgumentNullException);
  }

  [Test]
  public void BackendsWithoutAnOutputMappingDiagnoseTheStatement() {
    var module = FromOracle();
    AssertRefusesOutput(CSharpEmitter.Check(module));
    AssertRefusesOutput(JavaScriptEmitter.Check(module, JavaScriptProfile.Parse("es2022")));
    AssertRefusesOutput(PerlEmitter.Check(module));
    AssertRefusesOutput(PythonEmitter.Check(module, PythonProfile.Parse("py3.14")));
    AssertRefusesOutput(RustEmitter.Check(module, RustProfile.Rust2024));
  }

  private static void AssertRefusesOutput(IReadOnlyList<Diagnostic> diagnostics)
    => Assert.That(diagnostics.Select(problem => problem.Message), Has.Some.Contains("WriteLine"));

  private static IrModule[] Variants(IrModule module) => [
    module,
    IrModuleFormat.Read(IrModuleFormat.Write(module)),
    IrPassPipeline.Default.Run(Optimizer.Run(module, new OptimizerOptions { Verify = true }).Module,
      new OptimizerOptions { Verify = true }).Module
  ];

  // The expected traces come from tests/fixtures/batch/OutputEffect.cs, compiled and run independently.
  private static IrModule FromOracle() {
    var choose = new IrLocal("choose", IrType.Bool);
    var divisor = new IrLocal("divisor", IrType.I32);
    var text = new IrLocal("text", IrType.String);
    var emitText = new IrLocal("divisor", IrType.I32);
    return new("OutputEffect", [
      Function("Run", true, IrType.I32, [choose], First(),
        new IrIf(new IrLocalRef(choose), new IrBlock([Emit(new IrStrConst(""))]), null),
        Emit(new IrStrConst("second")), new IrReturn(Int(42))),
      Function("Trap", true, IrType.I32, [divisor], First(),
        Emit(new IrCall("Text", IrType.String, [new IrLocalRef(divisor)])), new IrReturn(Int(42))),
      Function("Emit", false, IrType.Void, [text], new IrWriteLine(new IrLocalRef(text)), new IrReturn(null)),
      Function("Text", false, IrType.String, [emitText], new IrReturn(new IrConditional(
        new IrBinary(IrBinaryOp.NotEqual, new IrBinary(IrBinaryOp.Divide, Int(42), new IrLocalRef(emitText)), Int(0)),
        new IrStrConst("done"), new IrStrConst("zero"))))
    ]);
  }

  private static IrWriteLine First() => new(new IrStrConst("first"));

  private static IrExprStmt Emit(IrExpr text) => new(new IrCall("Emit", IrType.Void, [text]));

  private static IrConst Int(int value) => new(IrType.I32, value);

  private static IrFunction Function(string name, bool exported, IrType result, IReadOnlyList<IrLocal> parameters, params IrStmt[] body)
    => new(name, exported, result, parameters, [], new IrBlock(body));

}
