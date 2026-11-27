using System.Globalization;
using NUnit.Framework;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Interpreter;
using Polypiler.Tests.MachineSemantics.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>Checks examples of the architectural x64 register profile through MachineIR.</summary>
[Binding, Scope(Feature = "x86-64 integer register profile")]
public sealed class X64MachineProfileSteps {

  private string _root = "";
  private ulong _initial;
  private MachineModule? _module;
  private string[] _diagnostics = [];

  [Given("^the x64 register \"([^\"]*)\" holds \"0x([0-9A-F]+)\"$")]
  public void InitialRegister(string root, string hex) {
    this._root = root;
    this._initial = ParseHex(hex);
  }

  [When("^MachineIR writes \"0x([0-9A-F]+)\" to the x64 register \"([^\"]*)\"$")]
  public void WriteRegister(string hex, string alias) {
    var width = MachineTesting.Profile("x64").Register(alias)!.Bits;
    this._module = MachineTesting.Verified($"machine x64\n@f {{ entry: RegWrite.u{width} @{alias}, 0x{hex}; Ret }}");
  }

  [When("^MachineIR verifies a write to the x64 register \"([^\"]*)\"$")]
  public void VerifyUnknownRegister(string register)
    => this._diagnostics = MachineTesting.Codes($"machine x64\n@f {{ entry: RegWrite.u64 @{register}, 1; Ret }}");

  [Then("^the x64 register \"([^\"]*)\" holds \"0x([0-9A-F]+)\" before and after normalization$")]
  public void FinalRegister(string root, string hex) {
    var module = this._module!;
    foreach (var form in new[] { module, MachineNormalizer.ToRootRegisters(module) }) {
      var result = MachineInterpreter.Run(form, "f", new MachineState(form.Profile!).SetRegister(this._root, this._initial));
      Assert.That(result.Diagnostics, Is.Empty);
      Assert.That(result.Trace.Final.Registers[root], Is.EqualTo(ParseHex(hex)));
    }
  }

  [Then("^the register diagnostic is \"([^\"]*)\"$")]
  public void RegisterDiagnostic(string code) => Assert.That(this._diagnostics, Is.EqualTo(new[] { code }));

  private static ulong ParseHex(string value) => ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

}
