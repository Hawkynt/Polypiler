using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>Executable tree fixtures for the O0383 work order, reconciled with ADR 0009.</summary>
[Binding, Scope(Feature = "O0383 dead global elimination")]
public sealed class DeadGlobalSteps {

  private IrModule _before = null!;
  private OptimizationResult _after = null!;
  private readonly HashSet<string> _globalRoots = [];

  private static IrConst I(int value) => new(IrType.I32, value);
  private static IrFunction F(string name, bool exported, IrExpr value)
    => new(name, exported, value.Type, [], [], new IrBlock([new IrReturn(value)]));

  [Given("^the O0383 fixture \"([^\"]+)\"$")]
  public void Fixture(string name) {
    Assert.That(File.Exists(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "optimize", "o0383", name)), Is.True);
    var limit = new IrGlobal("limit", IrType.I32, I(320));
    var tableType = IrType.Array(IrType.U16);
    var table = new IrGlobal("table", tableType, new IrIntrinsic(IrIntrinsicOp.ArrayOf, tableType,
      Enumerable.Range(1, 4).Select(value => (IrExpr)new IrConst(IrType.U16, value)).ToArray()));
    var signature = IrType.Function(IrType.I32);
    var ops = new IrGlobal("ops", signature, new IrCall("factory", signature, []));
    var factory = F("factory", false, new IrFunctionRef(signature, "area"));
    var area = F("area", false, I(7));
    if (name == "tables.pir") {
      this._before = new("tables", [F("main", true, new IrGlobalRef(limit)), F("Unused", false, new IrArrayLoad(new IrGlobalRef(table), I(0))), factory, area]) { Globals = [limit, table, ops] };
    } else if (name == "nested.pir") {
      var inner = new IrGlobal("inner", IrType.I32, I(7));
      var outer = new IrGlobal("outer", IrType.I32, new IrGlobalRef(inner));
      this._before = new("nested", [F("main", true, new IrGlobalRef(outer))]) { Globals = [inner, outer] };
    } else if (name == "external.pir") {
      this._before = new("external", [F("main", true, I(0))]) { Globals = [new IrGlobal("external", IrType.I32, I(1)), new IrGlobal("device", IrType.I32, I(2))] };
    } else if (name == "disabled.pir") {
      this._before = new("disabled", [F("main", true, I(0)), factory, area]) { Globals = [ops] };
    } else if (name == "trap.pir") {
      this._before = new("trap", [F("main", true, I(0))]) { Globals = [new IrGlobal("bad", IrType.I32, new IrBinary(IrBinaryOp.Divide, I(1), I(0)))] };
    } else {
      throw new ArgumentException("Unknown O0383 fixture: " + name);
    }
    Assert.That(IrVerifier.Verify(this._before), Is.Empty);
  }

  [Given("^the host retains global \"([^\"]+)\"$")]
  public void HostRoot(string name) => this._globalRoots.Add(name);

  [When("^the global optimizer enables \"([^\"]+)\"$")]
  public void Optimize(string enabled) {
    this._after = IrPassPipeline.Default.Run(this._before, new OptimizerOptions {
      Verify = true, GlobalRoots = this._globalRoots, Disabled = IrPassPipeline.Default.Ids.Except(enabled.Split(',')).ToHashSet()
    });
    Assert.That(IrVerifier.Verify(this._after.Module), Is.Empty);
  }

  [Then("^exactly globals \"([^\"]*)\" and functions \"([^\"]*)\" remain$")]
  public void Survivors(string globals, string functions) {
    Assert.That(this._after.Module.Globals.Select(g => g.Name), Is.EqualTo(globals.Split(',', StringSplitOptions.RemoveEmptyEntries)));
    Assert.That(this._after.Module.Functions.Select(f => f.Name), Is.EqualTo(functions.Split(',', StringSplitOptions.RemoveEmptyEntries)));
  }

  [Then("^both loaded modules return (\\d+) from main$")]
  public void Result(int expected) {
    foreach (var module in new[] { this._before, this._after.Module })
      Assert.That(IrInterpreter.Instantiate(module).Call("main"), Is.EqualTo(expected));
  }

  [Then("^the global report lists \"([^\"]*)\"$")]
  public void Report(string names) {
    Assert.That(this._after.Applications.Where(a => a.Id == "O0383").Select(a => a.Function), Is.EqualTo(names.Split(',', StringSplitOptions.RemoveEmptyEntries)));
    foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries))
      Assert.That(this._after.Notes.Any(note => note.StartsWith("O0383 applied: global @" + name + " removed", StringComparison.Ordinal)), Is.True);
  }

  [Then("the global module is unchanged")]
  public void Unchanged() => Assert.That(this._after.Module, Is.SameAs(this._before));

  [Then("loading either module traps with division by zero")]
  public void Trap() {
    foreach (var module in new[] { this._before, this._after.Module })
      Assert.That(() => IrInterpreter.Instantiate(module), Throws.TypeOf<IrTrapException>().With.Message.Contains("divide by zero"));
  }

}
