using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Emulators;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Retro;

/// <summary>The steps of <c>tests/features/retro/fmt-gwbasic-oracle.feature</c>: PC-BASIC itself, on the fixtures.</summary>
[Binding, Scope(Feature = "PC-BASIC oracle for GW-BASIC token files")]
public sealed class PcBasicSteps {

  private readonly PcBasic _pcBasic = PcBasicTesting.Adapter();
  private byte[] _tokenized = [];
  private string _listing = "";
  private TimeSpan _limit = TimeSpan.FromSeconds(30);
  private string? _converted;
  private byte[] _tokens = [];
  private PcBasicRun? _run;

  private PcBasicRun Run => this._run ?? throw new InvalidOperationException("nothing was run");

  /// <summary>Every scenario starts PC-BASIC: without it at its pin the scenario is inconclusive, in CI a failure.</summary>
  [BeforeScenario]
  public static void RequirePcBasic() => PcBasicTesting.RequirePcBasic();

  [Given("^the token file " + StepText.Quoted + " with the bytes " + StepText.Quoted + "$")]
  public void TokenFileWithBytes(string name, string hex) {
    this.TokenFile(name);
    Assert.That(Convert.ToHexString(this._tokenized), Is.EqualTo(hex.Replace(" ", "", StringComparison.Ordinal)));
  }

  [Given("^the token file " + StepText.Quoted + "$")]
  public void TokenFile(string name) => this._tokenized = File.ReadAllBytes(PcBasicTesting.Fixture(name));

  [Given("^the token file " + StepText.Quoted + " containing " + StepText.Quoted + " and a limit of (\\d+) seconds$")]
  public void TokenFileWithLimit(string name, string listing, int seconds) {
    this.TokenFile(name);
    Assert.That(this._pcBasic.ToAscii(this._tokenized), Is.EqualTo(listing));
    this._limit = TimeSpan.FromSeconds(seconds);
  }

  [Given("^the listing " + StepText.Quoted + "$")]
  public void Listing(string listing) => this._listing = StepText.Unescape(listing);

  [When("^PC-BASIC converts it to ASCII$")]
  public void ConvertToAscii() => this._converted = this._pcBasic.ToAscii(this._tokenized);

  [When("^PC-BASIC converts it to a token file$")]
  public void ConvertToTokens() => this._tokens = this._pcBasic.ToTokenized(this._listing);

  [When("^PC-BASIC runs it$")]
  public void RunIt() => this._run = this._pcBasic.Run(this._tokenized, null, this._limit);

  [Then("^the listing is " + StepText.Quoted + "$")]
  public void ListingIs(string listing) => Assert.That(this._converted, Is.EqualTo(StepText.Unescape(listing)));

  [Then("^the bytes from offset 3 are " + StepText.Quoted + " and the first byte is FF$")]
  public void BytesFromOffset3(string hex) {
    Assert.That(Convert.ToHexString(this._tokens.AsSpan(3)), Is.EqualTo(hex.Replace(" ", "", StringComparison.Ordinal)));
    Assert.That(this._tokens[0], Is.EqualTo(0xFF));
  }

  [Then("^the output is " + StepText.Quoted + "$")]
  public void OutputIs(string output) => Assert.That((this.Run.Outcome, this.Run.Output), Is.EqualTo((RunOutcome.Completed, (string?)StepText.Unescape(output))));

  [Then("^the outcome is TimedOut and the diagnostic PLY0031 is reported$")]
  public void TimedOut() {
    Assert.That(this.Run.Outcome, Is.EqualTo(RunOutcome.TimedOut));
    Assert.That(this.Run.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY3960", "PLY0031" }));
  }

  [Then("^no output is returned as a result$")]
  public void NoOutput() => Assert.That((this.Run.Succeeded, this.Run.Output), Is.EqualTo((false, (string?)null)));

}
