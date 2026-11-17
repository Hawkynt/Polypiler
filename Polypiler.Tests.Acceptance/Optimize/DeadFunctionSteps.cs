using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>The scenarios of #1153 on the structured tree IR, including effects and indirect calls.</summary>
[Binding, Scope(Feature = "O0381 dead function elimination")]
public sealed class DeadFunctionSteps {

  private IrModule _before = null!;
  private OptimizationResult _after = null!;
  private readonly HashSet<string> _roots = [];

  private static IrConst I(int value) => new(IrType.I32, value);
  private static IrFunction F(string name, bool exported, IrType result, params IrStmt[] statements)
    => new(name, exported, result, [], [], new IrBlock(statements));

  [Given("^the fixture \"([^\"]+)\".*$")]
  public void Fixture(string path) {
    var name = path[(path.LastIndexOf('/') + 1)..];
    if (name == "cycle.pir") {
      var trace = new IrGlobal("trace", IrType.I32, I(0));
      this._before = new IrModule("cycle", [
        F("main", true, IrType.I32, new IrExprStmt(new IrCall("Used", IrType.Void, [])), new IrReturn(new IrGlobalRef(trace))),
        F("Used", false, IrType.Void, new IrGlobalAssign(trace, I(1)), new IrReturn(null)),
        F("Unused", false, IrType.Void, new IrExprStmt(new IrCall("AlsoUnused", IrType.Void, [])), new IrReturn(null)),
        F("AlsoUnused", false, IrType.Void, new IrExprStmt(new IrCall("Unused", IrType.Void, [])), new IrReturn(null))
      ]) { Globals = [trace] };
    } else if (name == "twice.pir") {
      var v = new IrLocal("v", IrType.I32);
      this._before = new IrModule("twice", [
        F("main", true, IrType.I32, new IrReturn(new IrBinary(IrBinaryOp.Add,
          new IrCall("Twice", IrType.I32, [I(3)]), new IrCall("Twice", IrType.I32, [I(4)])))),
        new IrFunction("Twice", false, IrType.I32, [v], [], new IrBlock([new IrReturn(new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(v), I(2)))]))
      ]) { Implementations = [("Twice", "Fixture.Twice")] };
    } else if (name == "table.pir") {
      // Global initializers admit pure calls; the factory returns the function reference.
      var signature = IrType.Function(IrType.I32);
      var table = new IrGlobal("table", signature, new IrCall("factory", signature, []));
      this._before = new IrModule("table", [
        F("main", true, IrType.I32, new IrReturn(new IrInvoke(IrType.I32, new IrGlobalRef(table), []))),
        F("factory", false, signature, new IrReturn(new IrFunctionRef(signature, "handler"))),
        F("handler", false, IrType.I32, new IrReturn(I(7)))
      ]) { Globals = [table] };
    } else if (name == "roots.pir") {
      this._before = new IrModule("roots", [
        F("api", true, IrType.I32, new IrReturn(I(1))),
        F("roundtrip", false, IrType.I32, new IrReturn(new IrCall("helper", IrType.I32, []))),
        F("helper", false, IrType.I32, new IrReturn(I(7))),
        F("orphan", false, IrType.I32, new IrReturn(I(8)))
      ]);
      this._roots.Add("roundtrip");
    } else {
      throw new ArgumentException("Unknown O0381 fixture: " + name);
    }
    Assert.That(IrVerifier.Verify(this._before), Is.Empty);
  }

  [When("the optimizer runs with only O0381 enabled")]
  public void Run() => this.Optimize(["O0381"]);

  [When("the pipeline runs at --opt balanced with only O0006 and O0381 enabled")]
  public void Inline() => this.Optimize(["O0006", "O0381"]);

  [When("the pipeline runs at --opt balanced with --disable-opt O0381")]
  public void Disabled() => this.Optimize([]);

  private void Optimize(string[] enabled) {
    this._after = IrPassPipeline.Default.Run(this._before, new OptimizerOptions {
      Verify = true, Roots = this._roots, Disabled = IrPassPipeline.Default.Ids.Except(enabled).ToHashSet()
    });
    Assert.That(IrVerifier.Verify(this._after.Module), Is.Empty);
  }

  [Then("the module contains exactly the functions @main and @Used")]
  public void CycleSurvivors()
    => Assert.That(this._after.Module.Functions.Select(function => function.Name),
      Is.EqualTo(new[] { "main", "Used" }));

  [Then("the opt-report contains \"O0381 applied: @Unused removed\" and \"O0381 applied: @AlsoUnused removed\"")]
  public void Report() {
    foreach (var name in new[] { "Unused", "AlsoUnused" })
      Assert.That(this._after.Notes.Any(note => note.StartsWith($"O0381 applied: @{name} removed", StringComparison.Ordinal)), Is.True);
  }

  [Then("^interpreting @main in both versions records \"@print\\(1\\)\"$")]
  public void Trace() {
    // The tree IR has no print instruction: a trace cell records the observable write.
    foreach (var module in new[] { this._before, this._after.Module }) {
      var instance = IrInterpreter.Instantiate(module);
      Assert.That(instance.Call("main"), Is.EqualTo(1));
    }
  }

  [Then("the module contains no function @Twice")]
  public void NoHelper()
    => Assert.That(this._after.Module.Functions.Any(function => function.Name == "Twice"), Is.False);

  [Then("interpreting @main in both versions returns the same value")]
  public void SameValue()
    => Assert.That(IrInterpreter.Call(this._after.Module, "main", []), Is.EqualTo(IrInterpreter.Call(this._before, "main", [])));

  [Then("the IR is unchanged")]
  public void Unchanged() {
    Assert.That(this._after.Module, Is.SameAs(this._before));
    if (this._before.Name == "table")
      Assert.That(IrInterpreter.Instantiate(this._after.Module).Call("main"), Is.EqualTo(7));
  }

  [Then("the module contains @api, the test \"roundtrip\" and @helper, and no @orphan")]
  public void Roots()
    => Assert.That(this._after.Module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "api", "roundtrip", "helper" }));

  [Then("the opt-report does not mention O0381")]
  public void NoReport() {
    Assert.That(this._after.Applications.Any(application => application.Id == "O0381"), Is.False);
    Assert.That(this._after.Notes.Any(note => note.Contains("O0381", StringComparison.Ordinal)), Is.False);
  }

}
