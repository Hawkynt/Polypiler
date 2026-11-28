using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.BinaryFormats.Discovery;
using Polypiler.Core.MachineSemantics.Ports;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Fuzz;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Steps;

[Binding, Scope(Feature = "Function and instruction discovery")]
public sealed class DiscoverySteps {

  private const string Address = "(0x[0-9A-Fa-f]+)";
  private LoadedBinary? _binary;
  private DiscoveryResult? _result;

  private static ulong Number(string text) => ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

  private DiscoveredFunction Function(string entry) => this._result!.Functions.Single(function => function.Entry == Number(entry));

  [Given("^the toy image " + StepText.Quoted + " (?:loaded )?at " + Address + " with the entr(?:y|ies) (.+)$")]
  public void Image(string hex, string at, string entries)
    => this._binary = ToyIsa.Load(ToyIsa.Bytes(hex), Number(at), [.. Regex.Matches(entries, "0x[0-9A-Fa-f]+").Select(match => Number(match.Value))]);

  [When(@"^Discovery.Run executes$")]
  public void Run() => this._result = Core.BinaryFormats.Discovery.Discovery.Run(this._binary!, new ToyIsa(), null);

  [When(@"^Discovery.Run executes with the toy jump-table resolver$")]
  public void RunWithResolver() => this._result = Core.BinaryFormats.Discovery.Discovery.Run(this._binary!, new ToyIsa(), new ToyIsa());

  [Then("^function " + Address + " is Proven with one block " + Address + @"\.\." + Address + "$")]
  public void Proven(string entry, string start, string end) {
    var function = this.Function(entry);
    Assert.That(function.Confidence, Is.EqualTo(Confidence.Proven));
    Assert.That(function.Blocks.Select(block => (block.Start, block.End)), Is.EqualTo(new[] { (Number(start), Number(end)) }));
  }

  [Then("^function " + Address + " is Derived with evidence " + StepText.Quoted + " and one block " + Address + @"\.\." + Address + "$")]
  public void Derived(string entry, string evidence, string start, string end) {
    var function = this.Function(entry);
    Assert.That((function.Confidence, function.Evidence), Is.EqualTo((Confidence.Derived, evidence)));
    Assert.That(function.Blocks.Select(block => (block.Start, block.End)), Is.EqualTo(new[] { (Number(start), Number(end)) }));
  }

  [Then("^function " + Address + " has the blocks (.+)$")]
  public void Blocks(string entry, string blocks)
    => Assert.That(this.Function(entry).Blocks.Select(block => (block.Start, block.End)),
      Is.EqualTo(Regex.Matches(blocks, @"(0x[0-9A-Fa-f]+)\.\.(0x[0-9A-Fa-f]+)").Select(match => (Number(match.Groups[1].Value), Number(match.Groups[2].Value)))));

  [Then("^byte " + Address + " is (Undecoded|Code|Data|Conflict)$")]
  public void Byte(string address, string kind) => Assert.That(this._result!.Bytes[Number(address)], Is.EqualTo(Enum.Parse<ByteClass>(kind)));

  [Then("^bytes " + Address + @"\.\." + Address + " are (Undecoded|Code|Data|Conflict)$")]
  public void Bytes(string first, string last, string kind) {
    for (var address = Number(first); address <= Number(last); ++address)
      Assert.That(this._result!.Bytes[address], Is.EqualTo(Enum.Parse<ByteClass>(kind)), $"byte 0x{address:X}");
  }

  [Then("^every byte is Code$")]
  public void EveryByteIsCode() {
    var section = this._binary!.Image.Sections.Single();
    for (var address = section.Address; address < section.Address + section.Size; ++address)
      Assert.That(this._result!.Bytes[address], Is.EqualTo(ByteClass.Code));
  }

  [Then("^the only block ends in an indirect jump marked unresolved$")]
  public void Unresolved() {
    var block = this._result!.Functions.Single().Blocks.Single();
    Assert.That((block.Instructions[^1].Flow, block.Unresolved), Is.EqualTo((FlowKind.IndirectJump, true)));
  }

  [Then("^the diagnostics contain (PLY\\d{4}) naming " + Address + "$")]
  public void Naming(string code, string address)
    => Assert.That(this._result!.Diagnostics.Where(diagnostic => diagnostic.Code == code).Select(diagnostic => diagnostic.Message), Has.Some.Contains(address));

  [Then("^the indirect jump has the targets " + Address + " and " + Address + "$")]
  public void Targets(string first, string second)
    => Assert.That(this._result!.Functions.Single().Blocks[0].Successors, Is.EqualTo(new[] { Number(first), Number(second) }));

  [Then("^byte " + Address + " is Conflict and the diagnostics contain (PLY\\d{4}) naming " + Address + " and " + Address + "$")]
  public void Conflict(string address, string code, string first, string second) {
    this.Byte(address, "Conflict");
    Assert.That(this._result!.Diagnostics.Where(diagnostic => diagnostic.Code == code).Select(diagnostic => diagnostic.Message),
      Has.Some.Contains($"at {first} and at {second}"));
  }

  [Then("^function " + Address + " is not Complete and the diagnostics contain (PLY\\d{4}) naming " + Address + "$")]
  public void Incomplete(string entry, string code, string address) {
    Assert.That(this.Function(entry).Complete, Is.False);
    this.Naming(code, address);
  }

}
