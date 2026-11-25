using System;
using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

[Binding, Scope(Feature = "Nullable standard output text")]
public sealed class StandardOutputSteps {

  private readonly List<string> _lines = [];
  private IrModule _module = null!;
  private Exception? _error;

  [Given("^a verified IR function that writes " + StepText.Quoted + " and then a null string$")]
  public void GivenAWriteWithPrefix(string prefix) => this.Define(new IrWriteLine(new IrStrConst(prefix)));

  [Given("a verified IR function that writes a null string")]
  public void GivenANullWrite() => this.Define();

  [When("the function runs with an output sink")]
  public void RunWithSink() => this._error = Catch(() => IrInterpreter.Instantiate(this._module, this._lines.Add).Call("Run"));

  [When("the function runs without an output sink")]
  public void RunWithoutSink() => this._error = Catch(() => IrInterpreter.Call(this._module, "Run", []));

  [Then("^the sink receives " + StepText.Quoted + " followed by the empty string$")]
  public void CheckLines(string first) => Assert.That(this._lines, Is.EqualTo(new[] { first, "" }));

  [Then("the function completes normally")]
  public void CheckSuccess() => Assert.That(this._error, Is.Null);

  [Then("^it raises InvalidOperationException mentioning " + StepText.Quoted + "$")]
  public void CheckError(string message) {
    Assert.That(this._error, Is.TypeOf<InvalidOperationException>());
    Assert.That(this._error?.Message, Does.Contain(message));
  }

  private void Define(params IrStmt[] before) {
    this._module = new("NullableOutput", [new IrFunction("Run", true, IrType.Void, [], [],
      new IrBlock([.. before, new IrWriteLine(new IrNull(IrType.String)), new IrReturn(null)]))]);
    Assert.That(IrVerifier.Verify(this._module), Is.Empty);
  }

  private static Exception? Catch(Action execute) {
    try {
      execute();
      return null;
    } catch (Exception error) {
      return error;
    }
  }

}
