using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Runners;

[Binding, Scope(Feature = "Language runner contract")]
public sealed class LanguageRunnerSteps {

  private byte[] _stdout = [];
  private IReadOnlyList<CallOutcome> _outcomes = [];
  private ImageException? _error;

  [Given("^the function driver outputs " + StepText.Quoted + "$")]
  public void DriverOutput(string lines) => this._stdout = Encoding.UTF8.GetBytes(lines.Replace(';', '\n') + "\n");

  [When(@"^the runner reads the output for (\d+) signed 32-bit results$")]
  public void ReadOutput(int calls) {
    try {
      this._outcomes = LanguageRunnerProtocol.Read(this._stdout, new CallSignature([], ImageType.Parse("i32")), calls);
    } catch (ImageException error) {
      this._error = error;
    }
  }

  [Then("^the first result is the image " + StepText.Quoted + "$")]
  public void FirstResult(string image) => Assert.That(this._outcomes[0].Image, Is.EqualTo(System.Convert.FromHexString(image)));

  [Then("^the second result is the trap " + StepText.Quoted + "$")]
  public void SecondResult(string trap) => Assert.That(this._outcomes[1].Trap, Is.EqualTo(TrapClass.Parse(trap)));

  [Then("^reading the driver output fails with " + StepText.Quoted + "$")]
  public void ProtocolError(string reason) {
    Assert.That(this._error?.Message, Does.Contain(reason));
    Assert.That(this._outcomes, Is.Empty);
  }

}
