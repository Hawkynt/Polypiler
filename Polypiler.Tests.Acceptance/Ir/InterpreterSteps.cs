using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Ir;

/// <summary>
/// The steps of <c>tests/features/ir/ir-interp.feature</c> (#119): C# methods imported by the C# frontend and run in
/// process by <see cref="IrInterpreter.Run"/>; an argument is read by the type of its parameter.
/// </summary>
[Binding, Scope(Feature = "Reference interpreter")]
public sealed class InterpreterSteps {

  private IrModule? _module;
  private InterpreterOptions _options = InterpreterOptions.Default;
  private ExecutionResult? _result;

  private ExecutionResult Result => this._result ?? throw new AssertionException("nothing was run");

  [Given("^the C# methods " + StepText.Quoted + "$")]
  public void Methods(string methods)
    => this._module = PrototypeFixture.Import("Snippet.cs", "using System.Runtime.InteropServices; " + PrototypeFixture.Wrap(StepText.Unescape(methods)));

  [Given(@"^(\w+) modelled as a ([\w.]+):(\w+) event$")]
  public void Modelled(string function, string kind, string channel)
    => this._options = this._options with { Externals = new ExternalModel().AddEvent(function, channel, kind) };

  [Given("^no external model$")]
  public void NoModel() => this._options = this._options with { Externals = null };

  [Given(@"^MaxSteps (\d+)$")]
  public void MaxSteps(long steps) => this._options = this._options with { MaxSteps = steps };

  [When(@"^(\w+) is run$")]
  public void Run(string function) => this.RunWith(function, "");

  [When(@"^(\w+) is run with (.+)$")]
  public void RunWith(string function, string arguments) {
    var module = this._module ?? throw new AssertionException("no module");
    var parameters = module.Functions.Single(candidate => candidate.Name == function).Parameters;
    var values = arguments.Length == 0 ? [] : arguments.Split(", ")
      .Select((text, index) => Value(parameters[index].Type, text)).ToImmutableArray();
    this._result = IrInterpreter.Run(module, function, values, this._options);
  }

  private static IrValue Value(IrType type, string text)
    => type == IrType.Bool ? IrValue.Bool(text == "true") : IrValue.Integer(type, long.Parse(text, CultureInfo.InvariantCulture));

  [Then(@"^the outcome is Returned with the value (\d+)$")]
  public void Returned(long value) {
    Assert.That(this.Result.Outcome, Is.EqualTo(ExecutionOutcome.Returned), this.Result.Message);
    Assert.That(this.Result.Value!.Number, Is.EqualTo(new System.Numerics.BigInteger(value)));
  }

  [Then("^the trace is " + StepText.Quoted + " followed by " + StepText.Quoted + "$")]
  public void Trace(string first, string second)
    => Assert.That(this.Result.Trace.Select(effect => effect.ToString()), Is.EqualTo(new[] { first, second }), this.Result.Message);

  [Then(@"^the outcome is Aborted with (PLY\d{4})$")]
  public void Aborted(string code) {
    Assert.That(this.Result.Outcome, Is.EqualTo(ExecutionOutcome.Aborted));
    Assert.That(this.Result.Diagnostic!.Code, Is.EqualTo(code));
  }

  [Then(@"^the outcome is Aborted with (PLY\d{4}) after exactly (\d+) steps$")]
  public void AbortedAfter(string code, long steps) {
    this.Aborted(code);
    Assert.That(this.Result.Steps, Is.EqualTo(steps));
  }

  [Then("^the outcome is Aborted with (PLY\\d{4}) naming " + StepText.Quoted + "$")]
  public void AbortedNaming(string code, string name) {
    this.Aborted(code);
    Assert.That(this.Result.Message, Does.Contain(name));
  }

  [Then("^the trace contains no event$")]
  public void NoEvent() => Assert.That(this.Result.Trace, Is.Empty);

  [Then(@"^MaxFrameDepth is (\d+) and Allocations is (\d+)$")]
  public void Probes(int depth, long allocations)
    => Assert.That((this.Result.MaxFrameDepth, this.Result.Allocations), Is.EqualTo((depth, allocations)));

}
