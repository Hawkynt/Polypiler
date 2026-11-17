using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.MachineSemantics.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Native;

/// <summary>
/// The steps of <c>tests/features/native/mach-regions.feature</c>: <see cref="RegionClassifier"/> on one
/// access of the <c>test-m32</c> machine against the IBM PC map of <c>pc-vga.map.json</c>, and the
/// overlap rule of <see cref="AddressMap"/>, run in process.
/// </summary>
[Binding, Scope(Feature = "Memory-region classes for machine accesses")]
public sealed class MachRegionsSteps {

  private IAddressMap? _map;
  private string? _instruction;
  private AccessClass? _class;
  private AddressRange[] _ranges = [];
  private MachineException? _refusal;

  private AccessClass Class => this._class ?? throw new AssertionException("nothing was classified");

  /// <summary>A memory-map fixture.</summary>
  [Given("^the address map " + StepText.Quoted + "$")]
  public void TheMap(string file) => this._map = RegionFixtures.Map(file);

  /// <summary>No memory map at all.</summary>
  [Given("^no address map$")]
  public void NoMap() => this._map = null;

  /// <summary>The access to classify; every value it uses is read from a register, so it has no provenance.</summary>
  [Given("^the instruction " + StepText.Quoted + "$")]
  public void TheInstruction(string instruction) => this._instruction = instruction;

  /// <summary>An access through a pointer of which nothing is known.</summary>
  [Given("^" + StepText.Quoted + " where %p has no provenance fact$")]
  public void WithoutProvenance(string instruction) => this._instruction = instruction;

  /// <summary>Classifies the access without provenance facts.</summary>
  [When("^RegionClassifier.Classify runs$")]
  public void Classify() {
    var instruction = this._instruction ?? throw new AssertionException("no instruction was given");
    var values = Regex.Matches(instruction, @"%(\w+)").Select(match => match.Groups[1].Value).Distinct().Select((name, index) => $"  %{name} = RegRead.u32 @r{index}\n");
    var function = RegionFixtures.Parse($"machine test-m32\n\n@f {{\nentry:\n{string.Concat(values)}  {instruction}\n  Ret\n}}\n").Functions.Single();
    var block = function.Blocks[0];
    this._class = RegionClassifier.Classify(block.Instructions[^2], block, this._map, NoProvenanceFacts.Instance, new DiagnosticBag(), function.Name);
  }

  /// <summary>The class and the one channel.</summary>
  [Then("^the class is (\\w+) and the channel is " + StepText.Quoted + "$")]
  public void ClassAndChannel(MemoryClass expected, string channel) => Assert.Multiple(() => {
    Assert.That(this.Class.Class, Is.EqualTo(expected));
    Assert.That(this.Class.Channels, Is.EqualTo(new[] { channel }));
  });

  /// <summary>The class and the reason.</summary>
  [Then("^the class is (\\w+) with reason " + StepText.Quoted + "$")]
  public void ClassAndReason(MemoryClass expected, string reason) => Assert.Multiple(() => {
    Assert.That(this.Class.Class, Is.EqualTo(expected));
    Assert.That(this.Class.Reason, Is.EqualTo(reason));
  });

  /// <summary>The class alone.</summary>
  [Then("^the class is (\\w+)$")]
  public void ClassOnly(MemoryClass expected) => Assert.That(this.Class.Class, Is.EqualTo(expected));

  /// <summary>Exactly two channels, in order.</summary>
  [Then("^the channels are " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void TwoChannels(string first, string second) => Assert.That(this.Class.Channels, Is.EqualTo(new[] { first, second }));

  /// <summary>Two RAM ranges.</summary>
  [Given("^ranges " + StepText.Quoted + " (0x[0-9A-F]+)-(0x[0-9A-F]+) and " + StepText.Quoted + " (0x[0-9A-F]+)-(0x[0-9A-F]+)$")]
  public void TwoRanges(string a, string aStart, string aEnd, string b, string bStart, string bEnd) => this._ranges = [
    new(a, RegionFixtures.Number(aStart), RegionFixtures.Number(aEnd), AddressKind.Ram, false),
    new(b, RegionFixtures.Number(bStart), RegionFixtures.Number(bEnd), AddressKind.Ram, false)];

  /// <summary>Builds the map.</summary>
  [When("^the AddressMap is built$")]
  public void Build() {
    try {
      this._map = new AddressMap(this._ranges, 32, "test-m32");
    } catch (MachineException refusal) {
      this._refusal = refusal;
    }
  }

  /// <summary>The refusal names both ranges and the first shared address.</summary>
  [Then("^it fails with (PLY\\d{4}) naming " + StepText.Quoted + ", " + StepText.Quoted + " and (0x[0-9A-F]+)$")]
  public void Refused(string code, string a, string b, string address) {
    var refusal = this._refusal ?? throw new AssertionException("the map was built");
    Assert.That(refusal.Diagnostic.Code, Is.EqualTo(code));
    Assert.That(refusal.Diagnostic.Message, Is.EqualTo($"address ranges '{a}' and '{b}' overlap at {address}"));
  }

}
