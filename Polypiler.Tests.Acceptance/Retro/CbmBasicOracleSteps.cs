using System;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Emulators;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Retro;

/// <summary>
/// The steps of <c>tests/features/retro/fmt-cbmbasic-oracle.feature</c>. Every run is a real one:
/// VICE and petcat of <c>tooling/toolchains.lock</c> in the sandbox of this host, so a scenario
/// without them is inconclusive outside CI.
/// </summary>
[Binding, Scope(Feature = "VICE and petcat oracles for Commodore BASIC")]
public sealed class CbmBasicOracleSteps {

  private string _name = "PROG.PRG";
  private byte[] _prg = [];
  private AddressRange[] _ranges = [];
  private long _cycleLimit = 20_000_000;
  private ViceResult? _result;
  private string _listing = "";

  private ViceResult Result => this._result ?? throw new InvalidOperationException("no program has run");

  private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

  private static int Number(string text) => text.StartsWith("0x", StringComparison.Ordinal) ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(text, CultureInfo.InvariantCulture);

  /// <summary>Checks that a fixture holds the listing the scenario says it does, with petcat.</summary>
  private void Contains(string listing) {
    var listed = CbmTools.Petcat().DetokenizeAsync(this._prg, "c64-basic-v2").GetAwaiter().GetResult();
    Assert.That(listed.Listing, Is.EqualTo(listing), string.Join("; ", listed.Diagnostics));
  }

  private void Fixture(string name) {
    this._name = name;
    this._prg = CbmTools.Fixture(name);
  }

  [Given("^the PRG " + StepText.Quoted + " with the bytes " + StepText.Quoted + "$")]
  public void PrgWithBytes(string name, string bytes) {
    this.Fixture(name);
    Assert.That(Convert.ToHexString(this._prg), Is.EqualTo(Convert.ToHexString(Bytes(bytes))));
  }

  [Given("^the PRG " + StepText.Quoted + "$")]
  public void Prg(string name) => this.Fixture(name);

  [Given("^the PRG " + StepText.Quoted + " containing the line " + StepText.Quoted + " and the range (0x[0-9A-F]+) of length (\\d+) to read$")]
  public void PrgAndRange(string name, string line, string start, int length) {
    this.Fixture(name);
    this.Contains(line);
    this._ranges = [new((ushort)Number(start), length)];
  }

  [Given("^the PRG " + StepText.Quoted + " containing " + StepText.Quoted + " and a cycle limit of (\\d+)$")]
  public void PrgAndLimit(string name, string line, long limit) {
    this.Fixture(name);
    this.Contains(line);
    this._cycleLimit = limit;
  }

  [Given("^a PRG consisting of the bytes " + StepText.Quoted + "$")]
  public void PrgOfBytes(string bytes) => this._prg = Bytes(bytes);

  private void RunOn(CbmMachine machine, bool requireReturn) {
    var request = CbmTools.Request(machine, this._prg, this._cycleLimit, null, this._ranges) with { Name = this._name, RequireReturn = requireReturn };
    this._result = CbmTools.Run(CbmTools.Vice(machine), request);
  }

  [When("^it is run on the machine (\\w+)$")]
  public void RunOnMachine(CbmMachine machine) => this.RunOn(machine, false);

  [When("^it is run on the machine (\\w+) with ReturnedToBasic required$")]
  public void RunRequired(CbmMachine machine) => this.RunOn(machine, true);

  [When("^an empty program is run on the machine (\\w+) with the start-up banner captured$")]
  public void RunEmpty(CbmMachine machine) {
    this._prg = [0x01, 0x08, 0x00, 0x00];
    this.RunOn(machine, true);
  }

  [When("^petcat detokenizes it for dialect " + StepText.Quoted + "$")]
  public void Detokenize(string dialect) {
    var result = CbmTools.Petcat().DetokenizeAsync(this._prg, dialect).GetAwaiter().GetResult();
    Assert.That(result.Diagnostics, Is.Empty);
    this._listing = result.Listing;
  }

  [Then("^the status is (\\w+)$")]
  public void Status(ViceStatus status) => Assert.That(this.Result.Status, Is.EqualTo(status), string.Join("; ", this.Result.Diagnostics));

  [Then("^the screen text is " + StepText.Quoted + "$")]
  public void ScreenText(string text) => Assert.That(this.Result.ScreenText, Is.EqualTo(StepText.Unescape(text)));

  [Then("^the low nibble of the byte read at (0x[0-9A-F]+) is (0x[0-9A-F])$")]
  public void LowNibble(string address, string nibble) {
    Assert.That(this.Result.Status, Is.EqualTo(ViceStatus.ReturnedToBasic), string.Join("; ", this.Result.Diagnostics));
    var block = this.Result.Memory.Single(block => block.Start == Number(address));
    Assert.That(block.Bytes[0] & 0x0F, Is.EqualTo(Number(nibble)));
  }

  [Then("^the banner contains " + StepText.Quoted + "$")]
  public void Banner(string text) => Assert.That(this.Result.Banner, Does.Contain(text));

  [Then("^the normalised listing is '([^']*)'$")]
  public void Listing(string listing) => Assert.That(this._listing, Is.EqualTo(listing));

  [Then("^petcat tokenizing that listing with load address (0x[0-9A-F]+) yields the bytes of " + StepText.Quoted + "$")]
  public void Tokenize(string address, string name) {
    var result = CbmTools.Petcat().TokenizeAsync(this._listing, "c64-basic-v2", (ushort)Number(address)).GetAwaiter().GetResult();
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(Convert.ToHexString(result.Prg.Span), Is.EqualTo(Convert.ToHexString(CbmTools.Fixture(name))));
  }

  [Then("^no ReturnedToBasic result is reported$")]
  public void NotReturned() => Assert.That((this.Result.Status, this.Result.Memory.Length, this.Result.Cycles), Is.EqualTo((ViceStatus.CycleLimit, 0, this._cycleLimit)));

  [Then("^the diagnostic (PLY\\d{4}) is reported$")]
  public void Reported(string code) => Assert.That(this.Result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(code));

}
