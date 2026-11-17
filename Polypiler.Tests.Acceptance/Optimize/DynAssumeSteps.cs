using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>
/// The steps of <c>tests/features/optimize/dyn-assume.feature</c>: C# methods imported in a class,
/// the caller contracts enforced on the module, and the IR interpreter running the result in one
/// instance per scenario.
/// </summary>
[Binding, Scope(Feature = "--assume contracts")]
public sealed class DynAssumeSteps {

  private readonly DiagnosticBag _diagnostics = new();
  private IrModule? _source;
  private ContractAssumption? _assumption;
  private ContractEnforcement? _enforcement;
  private IrModuleInstance? _instance;

  /// <summary>Imports C# methods, which stand in a class.</summary>
  [Given("^the C# methods " + StepText.Quoted + "$")]
  public void Methods(string methods) {
    var imported = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(StepText.Unescape(methods)));
    Assert.That(imported.Diagnostics, Is.Empty);
    this._source = imported.Module;
  }

  /// <summary>States a checked contract.</summary>
  [Given("^the assumption " + StepText.Quoted + " in checked mode$")]
  public void Checked(string text) => this._assumption = ContractParser.Parse(StepText.Unescape(text));

  /// <summary>States an unchecked contract.</summary>
  [Given("^the assumption " + StepText.Quoted + " given with --assume-unchecked$")]
  public void Unchecked(string text) {
    Assert.That(ContractParser.TryParse(StepText.Unescape(text), false, ContractParser.Unchecked, out this._assumption, out var problem), Is.True, problem);
  }

  /// <summary>Runs the enforcer on the module.</summary>
  [When("^the contracts are enforced$")]
  public void Enforce() {
    this._enforcement = ContractEnforcer.Enforce(this._source!, [this._assumption!], "csharp", this._diagnostics);
    if (this._enforcement.Module is { } module) {
      Assert.That(IrVerifier.Verify(module), Is.Empty);
      this._instance = IrInterpreter.Instantiate(module);
    }
  }

  /// <summary>Reads a contract the way the command line does.</summary>
  [When("^the assumption " + StepText.Quoted + " is read$")]
  public void Read(string text) {
    if (!ContractParser.TryParse(StepText.Unescape(text), true, ContractParser.Checked, out _, out var problem))
      this._diagnostics.Report(Diagnostic.Create(ContractDiagnostics.Invalid, null, StepText.Unescape(text), problem!));
  }

  /// <summary>Checks that the first statement of a function is the check of a contract.</summary>
  [Then("^the entry of " + StepText.Quoted + " starts with the check of " + StepText.Quoted + "$")]
  public void StartsWithTheCheck(string function, string contract) {
    var first = this.Function(function).Body.Statements[0];
    Assert.That(first, Is.InstanceOf<IrIf>());
    var thrown = (IrThrow)((IrIf)first).Then.Statements.Single();
    Assert.That(((IrStrConst)((IrNew)thrown.Exception).Arguments.Single()).Value, Is.EqualTo(ContractEnforcer.ViolationPrefix + StepText.Unescape(contract)));
  }

  /// <summary>Checks that a function is as the frontend imported it.</summary>
  [Then("^the entry of " + StepText.Quoted + " has no check$")]
  public void HasNoCheck(string function)
    => Assert.That(this.Function(function).Body, Is.EqualTo(this._source!.Functions.Single(candidate => candidate.Name == StepText.Unescape(function)).Body));

  /// <summary>Checks the manifest record of the contract.</summary>
  [Then("^the assumption is listed with checked = (true|false)$")]
  public void Listed(bool isChecked)
    => Assert.That(this._enforcement!.Assumptions.Single(), Is.EqualTo(new ContractRecord(this._assumption!.Target, this._assumption.Contract.Text, isChecked, this._assumption.Origin, 0)));

  /// <summary>Calls a function of the instance with one integer.</summary>
  [Then("^interpreting " + StepText.Quoted + " with (-?\\d+) returns (-?\\d+)$")]
  public void Returns(string function, int argument, int result) => Assert.That(this._instance!.Call(StepText.Unescape(function), argument), Is.EqualTo(result));

  /// <summary>Calls a function of the instance without arguments.</summary>
  [Then("^interpreting " + StepText.Quoted + " returns (-?\\d+)$")]
  public void ReturnsWithout(string function, int result) => Assert.That(this._instance!.Call(StepText.Unescape(function)), Is.EqualTo(result));

  /// <summary>Calls a function of the instance with a violating integer.</summary>
  [Then("^interpreting " + StepText.Quoted + " with (-?\\d+) raises ContractViolation naming " + StepText.Quoted + "$")]
  public void Raises(string function, int argument, string contract) {
    var thrown = Assert.Throws<IrException>(() => this._instance!.Call(StepText.Unescape(function), argument))!;
    Assert.That((thrown.Class, thrown.Message), Is.EqualTo((IrExceptionClass.ArgumentException, ContractEnforcer.ViolationPrefix + StepText.Unescape(contract))));
  }

  /// <summary>Checks the one warning of a contract.</summary>
  [Then("^warning (PLY\\d{4}) is reported for " + StepText.Quoted + "$")]
  public void Warning(string code, string contract) {
    var reported = this._diagnostics.Items.Single();
    Assert.That((reported.Code, reported.Severity), Is.EqualTo((code, DiagnosticSeverity.Warning)));
    Assert.That(reported.Message, Does.Contain($"'{StepText.Unescape(contract)}'"));
  }

  /// <summary>Checks the one error and a text in its message.</summary>
  [Then("^error (PLY\\d{4}) is reported naming " + StepText.Quoted + "$")]
  public void Error(string code, string text) {
    var reported = this._diagnostics.Items.Single();
    Assert.That((reported.Code, reported.Severity), Is.EqualTo((code, DiagnosticSeverity.Error)));
    Assert.That(reported.Message, Does.Contain(StepText.Unescape(text)));
  }

  /// <summary>Checks that the enforcer gave no module.</summary>
  [Then("^no module is produced$")]
  public void NoModule() => Assert.That(this._enforcement!.Module, Is.Null);

  private IrFunction Function(string name) => this._enforcement!.Module!.Functions.Single(function => function.Name == StepText.Unescape(name));

}
