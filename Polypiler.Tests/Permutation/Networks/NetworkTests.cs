using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Networks;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Permutation.Networks;

/// <summary>Typed candidate networks (#532), one case per row of the issue's test design.</summary>
[TestFixture]
public sealed class NetworkTests {

  private const string NibbleSwap = "v1 = And(v0, 0x0F); v2 = ShiftLeft(v1, 4); v3 = And(v0, 0xF0); v4 = ShiftRightLogical(v3, 4); v5 = Or(v2, v4)";

  private static PrimitiveCostTable Costs(string name) => PrimitiveCostTable.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "permutations", "costs", name + ".yaml"));

  /// <summary>The network's lowered function, run in the reference interpreter: the second evaluation.</summary>
  private static BigInteger Interpret(BitOperationNetwork network, BigInteger input) {
    var function = NetworkLowering.ToFunction(network, "F");
    var module = new IrModule("Network", [function], []);
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the lowered IR must be legal");
    var argument = network.InputWidth switch {
      8 => (object)(byte)input, 16 => (ushort)input, 32 => (uint)input, _ => (ulong)input
    };
    return IrInterpreter.Call(module, "F", [argument]) switch {
      byte value => value, ushort value => value, uint value => value, ulong value => value,
      var other => throw new InvalidOperationException($"unexpected {other}")
    };
  }

  private static string Diagnostics(BitOperationNetwork network) => string.Join(" | ", NetworkVerifier.Check(network).Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message));

  // 1 - happy path

  [Test]
  public void TheNibbleSwap_EvaluatesLowersAndCostsFive() {
    var network = NetworkText.Parse(NibbleSwap, 8, 8);
    Assert.That(NetworkVerifier.Check(network), Is.Empty);
    Assert.That(NetworkEvaluator.Evaluate(network, 0xB4), Is.EqualTo(new BigInteger(0x4B)));
    Assert.That(Interpret(network, 0xB4), Is.EqualTo(new BigInteger(0x4B)));
    var statistics = new NetworkStatistics(network);
    Assert.That(statistics.Cost(Costs("bitperm-modern")), Is.EqualTo(5));
    Assert.That(statistics.Depth, Is.EqualTo(3));
    Assert.That(statistics.StepsPerOperation[NetworkOp.And], Is.EqualTo(2));
    Assert.That(statistics.Constants, Is.EqualTo(2));
    Assert.That(statistics.ConstantBits, Is.EqualTo(4 + 8));
    // v0 lives until its second mask and v2 until the or; an operand dies at the step that last reads it.
    Assert.That(statistics.PeakLive, Is.EqualTo(2));
    Assert.That(NetworkText.Print(network), Is.EqualTo(NibbleSwap));
    Assert.That(network.ToString(), Is.EqualTo(NibbleSwap));
  }

  [Test]
  public void AssemblingATerm_AddsTheInversionAndConstantFixUps() {
    var mapping = BitMapping.Parse("8:[~0,1,2,3,=1,?,?,?]");
    var term = new PrimitiveCandidate("and", 0x0F, 1, "low nibble", builder => NetworkText.Emit(builder, "v1 = And(v0, 0x0F)"));
    var network = CompositeNetwork.Assemble(mapping, [term]);
    Assert.That(NetworkText.Print(network), Is.EqualTo("v1 = And(v0, 0x0F); v2 = Xor(v1, 0x01); v3 = Or(v2, 0x10)"));
    Assert.That(NetworkEvaluator.Evaluate(network, 0x05), Is.EqualTo(new BigInteger(0x14)));
    Assert.That(NetworkVerifier.Check(network), Is.Empty);
  }

  // 3 - every core operation agrees with the interpreter of its lowering

  private static IEnumerable<TestCaseData> CoreOperations() {
    yield return new TestCaseData("v1 = Const(0xA5)", 8, 8);
    yield return new TestCaseData("v1 = And(v0, 0x3C)", 8, 8);
    yield return new TestCaseData("v1 = Not(v0); v2 = And(v0, v1)", 8, 8);
    yield return new TestCaseData("v1 = Or(v0, 0x81)", 8, 8);
    yield return new TestCaseData("v1 = ShiftLeft(v0, 3); v2 = Or(v0, v1)", 8, 8);
    yield return new TestCaseData("v1 = Xor(v0, 0x5A)", 8, 8);
    yield return new TestCaseData("v1 = ShiftRightLogical(v0, 5); v2 = Xor(v0, v1)", 8, 8);
    yield return new TestCaseData("v1 = Not(v0)", 8, 8);
    yield return new TestCaseData("v1 = ZeroExtend(v0, 16); v2 = ShiftLeft(v1, 9)", 8, 16);
    yield return new TestCaseData("v1 = ShiftLeft(v0, 3); v2 = Truncate(v1, 8)", 16, 8);
    yield return new TestCaseData("v1 = ZeroExtend(v0, 64); v2 = ShiftLeft(v1, 60); v3 = Not(v2); v4 = Truncate(v3, 32)", 8, 32);
    yield return new TestCaseData("v1 = Truncate(v0, 8)", 16, 8);
    yield return new TestCaseData("v1 = Not(v0); v2 = ShiftRightLogical(v1, 63)", 64, 64);
    yield return new TestCaseData("v1 = ShiftLeft(v0, 7); v2 = Truncate(v1, 16)", 32, 16);
  }

  [TestCaseSource(nameof(CoreOperations))]
  public void EveryCoreOperation_AgreesWithTheInterpreterOfItsLowering(string text, int input, int output) {
    var network = NetworkText.Parse(text, input, output);
    Assert.That(NetworkVerifier.Check(network), Is.Empty, Diagnostics(network));
    var random = new Random(532);
    var inputs = input == 8 ? Enumerable.Range(0, 256).Select(value => (BigInteger)value)
      : Enumerable.Range(0, 256).Select(_ => (BigInteger)(ulong)random.NextInt64() & ((BigInteger.One << input) - 1));
    foreach (var value in inputs)
      Assert.That(Interpret(network, value), Is.EqualTo(NetworkEvaluator.Evaluate(network, value)), $"{text} on {value}");
  }

  // 4 - builder sharing and elision

  [Test]
  public void TheBuilder_SharesIdenticalStepsAndOmitsThoseThatChangeNothing() {
    var builder = new NetworkBuilder(8);
    var first = builder.AndConstant(NetworkBuilder.Input, 0x0F);
    Assert.That(builder.AndConstant(NetworkBuilder.Input, 0x0F), Is.EqualTo(first));
    Assert.That(builder.AndConstant(first, 0xFF), Is.EqualTo(first));
    Assert.That(builder.OrConstant(first, 0), Is.EqualTo(first));
    Assert.That(builder.XorConstant(first, 0), Is.EqualTo(first));
    Assert.That(builder.ShiftLeft(first, 0), Is.EqualTo(first));
    Assert.That(builder.ShiftRightLogical(first, 0), Is.EqualTo(first));
    Assert.That(builder.ZeroExtend(first, 8), Is.EqualTo(first));
    Assert.That(builder.Truncate(first, 8), Is.EqualTo(first));
    Assert.That(builder.Steps, Has.Count.EqualTo(1));
    var both = builder.Xor(builder.Or(first, builder.Not(first)), builder.And(first, builder.Const(8, 3)));
    Assert.That(builder.Steps, Has.Count.EqualTo(6));
    var network = builder.Build(both);
    Assert.That(NetworkText.Print(network), Is.EqualTo("v1 = And(v0, 0x0F); v2 = Not(v1); v3 = Or(v1, v2); v4 = Const(0x03); v5 = And(v1, v4); v6 = Xor(v3, v5)"));
    Assert.That(NetworkEvaluator.Evaluate(network, 0x06), Is.EqualTo(new BigInteger(0xFF ^ 0x02)));
    Assert.Throws<ArgumentNullException>(() => builder.Emit(null!));
    Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkBuilder(0));
  }

  // 5 - each verifier rule

  [TestCase("v1 = And(v0, v2); v2 = Not(v0)", 8, 8, "PLY0810: network step 1 (And): operand v2 is not defined before use")]
  [TestCase("v1 = ShiftLeft(v0, 8)", 8, 8, "PLY0810: network step 1 (ShiftLeft): amount 8 is outside 1..7")]
  [TestCase("v1 = Not(v0); v2 = And(v0, 0x0F)", 8, 8, "PLY0810: network step 1 (Not): is unused")]
  [TestCase("v1 = Const(0x1FF)", 8, 8, "PLY0810: network step 1 (Const): constant 0x1FF does not fit 8 bits")]
  [TestCase("v1 = ZeroExtend(v0, 16); v2 = And(v0, v1)", 8, 8, "PLY0810: network step 2 (And): operand v1 has 16 bits, not 8")]
  [TestCase("v1 = ZeroExtend(v0, 8)", 8, 8, "PLY0810: network step 1 (ZeroExtend): widens nothing: 8 to 8 bits")]
  [TestCase("v1 = Truncate(v0, 16)", 8, 16, "PLY0810: network step 1 (Truncate): narrows nothing: 8 to 16 bits")]
  [TestCase("v1 = And(v0, [1,2])", 8, 8, "PLY0810: network step 1 (And): needs a second operand or a bit pattern, not bytes")]
  [TestCase("v1 = And(v0)", 8, 8, "PLY0810: network step 1 (And): needs a second operand or a bit pattern, not none")]
  [TestCase("v1 = And(v0, v0, v0)", 8, 8, "PLY0810: network step 1 (And): takes two operands, or one and a bit pattern, not 3")]
  [TestCase("v1 = And(v0, 0x100)", 8, 8, "PLY0810: network step 1 (And): constant 0x100 does not fit 8 bits")]
  [TestCase("v1 = Not(v0, v0)", 8, 8, "PLY0810: network step 1 (Not): takes 1 operand, not 2")]
  [TestCase("v1 = Not(v0, 3)", 8, 8, "PLY0810: network step 1 (Not): needs no immediate, not amount")]
  [TestCase("v1 = Const(v0, 0x01)", 8, 8, "PLY0810: network step 1 (Const): takes 0 operands, not 1")]
  [TestCase("v1 = Const()", 8, 8, "PLY0810: network step 1 (Const): needs a bit pattern, not none")]
  [TestCase("v1 = ShiftLeft(v0, 0x01)", 8, 8, "PLY0810: network step 1 (ShiftLeft): needs an amount, not bits")]
  [TestCase("v1 = ShiftLeft(v0, 0)", 8, 8, "PLY0810: network step 1 (ShiftLeft): amount 0 is outside 1..7")]
  [TestCase("v1 = ShiftRightLogical(v0, v0)", 8, 8, "PLY0810: network step 1 (ShiftRightLogical): takes 1 operand, not 2")]
  [TestCase("v1 = ZeroExtend(v0, v0, 16)", 8, 16, "PLY0810: network step 1 (ZeroExtend): takes 1 operand, not 2")]
  [TestCase("v1 = Truncate(v0, 0x01, 4)", 8, 4, "PLY0810: network step 1 (Truncate): needs no immediate, not bits")]
  [TestCase("v1 = Not(v0)", 8, 16, "PLY0810: network step 0 (result): result v1 has 8 bits, not 16")]
  public void AnIllFormedNetwork_IsReportedByItsRule(string text, int input, int output, string first)
    => Assert.That(NetworkVerifier.Check(NetworkText.Parse(text, input, output)).Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message).First(), Is.EqualTo(first));

  [Test]
  public void TheRulesOfTheWholeNetwork_AreStepZero() {
    var undefined = new BitOperationNetwork(8, 8, [new NetworkStep(NetworkOp.Not, 8, [0], null)], 5);
    Assert.That(Diagnostics(undefined), Does.Contain("network step 0 (result): result v5 is not defined"));
    var zeroWidth = new BitOperationNetwork(0, 0, [], 0);
    Assert.That(Diagnostics(zeroWidth), Does.Contain("network step 0 (input): width 0 is not positive"));
    var stepWidth = new BitOperationNetwork(8, 0, [new NetworkStep(NetworkOp.Not, 0, [0], null)], 1);
    Assert.That(Diagnostics(stepWidth), Does.Contain("network step 1 (Not): width 0 is not positive"));
    var unregistered = new BitOperationNetwork(8, 8, [new NetworkStep(NetworkOp.Not, 8, [0], null)], 1);
    Assert.That(NetworkVerifier.Check(unregistered, new NetworkOpRegistry()).Single().Message, Is.EqualTo("network step 1 (Not): is not registered"));
    Assert.That(NetworkVerifier.Check(NetworkText.Parse("v1 = Not(v0); v2 = Not(v1)", 8, 8)), Is.Empty);
  }

  // 6 - the empty network

  [Test]
  public void TheEmptyNetwork_IsItsInput() {
    var network = NetworkText.Parse("", 8, 8);
    Assert.That(NetworkVerifier.Check(network), Is.Empty);
    Assert.That(NetworkEvaluator.Evaluate(network, 0x1B4), Is.EqualTo(new BigInteger(0xB4)), "the input is reduced to its width");
    Assert.That(Interpret(network, 0xB4), Is.EqualTo(new BigInteger(0xB4)));
    var statistics = new NetworkStatistics(network);
    Assert.That((statistics.Cost(Costs("bitperm-modern")), statistics.Depth, statistics.PeakLive, statistics.Constants), Is.EqualTo((0L, 0, 1, 0)));
    Assert.That(NetworkText.Print(network), Is.Empty);
  }

  // 7 - widths

  [TestCase(1, false)]
  [TestCase(64, true)]
  [TestCase(128, false)]
  [TestCase(4096, false)]
  public void ANotAtEveryWidth_EvaluatesAndLowersWhereTheIrHasTheType(int width, bool lowered) {
    var network = NetworkText.Parse("v1 = Not(v0)", width, width);
    Assert.That(NetworkVerifier.Check(network), Is.Empty);
    var mask = (BigInteger.One << width) - 1;
    Assert.That(NetworkEvaluator.Evaluate(network, 0), Is.EqualTo(mask));
    if (lowered) {
      Assert.That(Interpret(network, 0), Is.EqualTo(mask));
      return;
    }

    var refused = Assert.Throws<PermutationModelException>(() => NetworkLowering.ToFunction(network, "F"));
    Assert.That(refused!.Message, Is.EqualTo($"PLY0812: primitive 'input' does not support width {width}"));
  }

  [Test]
  public void AStepWidthTheIrHasNoTypeOf_IsRefusedByLowering() {
    var network = NetworkText.Parse("v1 = ZeroExtend(v0, 12); v2 = Truncate(v1, 8)", 8, 8);
    var refused = Assert.Throws<PermutationModelException>(() => NetworkLowering.ToFunction(network, "F"));
    Assert.That(refused!.Diagnostic.Code, Is.EqualTo("PLY0812"));
    Assert.That(refused.Message, Does.Contain("'ZeroExtend'").And.Contain("width 12"));
  }

  [Test]
  public void LoweringOverAnInputOfTheCarrier_NeedsNoConversion() {
    var network = NetworkText.Parse("v1 = Not(v0)", 32, 32);
    var input = new IrLocalRef(new IrLocal("x", IrType.U32));
    var lowered = NetworkLowering.Lower(network, input);
    Assert.That(lowered.Locals.Single().Type, Is.EqualTo(IrType.U32));
    Assert.That(lowered.Result, Is.InstanceOf<IrLocalRef>());
    Assert.That(NetworkLowering.Masked(input, 32), Is.SameAs(input));
  }

  // 9 - the term contract

  [Test]
  public void ATermThatLeaksBits_IsRefused() {
    var mapping = BitMapping.Parse("8:[?,?,?,?,0,1,2,3]");
    var term = new PrimitiveCandidate("shift", 0x30, 1, "shift", builder => NetworkText.Emit(builder, "v1 = ShiftLeft(v0, 4)"));
    var refused = Assert.Throws<PermutationModelException>(() => CompositeNetwork.Assemble(mapping, [term]));
    Assert.That(refused!.Message, Does.Contain("PLY0810").And.Contain("term 1 sets bits 0xC0 outside its routed mask 0x30"));
  }

  [Test]
  public void OverlappingAndMissingTerms_AreRefused() {
    var mapping = BitMapping.Parse("8:[0,1,2,3,4,5,6,7]");
    PrimitiveCandidate Mask(string mask, int routed) => new("and", routed, 1, mask, builder => NetworkText.Emit(builder, $"v1 = And(v0, {mask})"));
    var overlap = Assert.Throws<PermutationModelException>(() => CompositeNetwork.Assemble(mapping, [Mask("0x0F", 0x0F), Mask("0x18", 0x18)]));
    Assert.That(overlap!.Message, Does.Contain("term 2 routes bits 0x08 another term routes"));
    var missing = Assert.Throws<PermutationModelException>(() => CompositeNetwork.Assemble(mapping, [Mask("0x0F", 0x0F)]));
    Assert.That(missing!.Message, Does.Contain("source bits 0xF0 are routed by no term"));
    var wrong = Assert.Throws<PermutationModelException>(() => CompositeNetwork.Assemble(mapping,
      [new PrimitiveCandidate("shift", 0x02, 1, "wrong", builder => NetworkText.Emit(builder, "v1 = And(v0, 0x01); v2 = ShiftLeft(v1, 1)")), Mask("0xFD", 0xFD)]));
    Assert.That(wrong!.Message, Does.Contain("term 1 gives 0x02 for input 0x01, its routed bits want 0x00"));
    var swap = CompositeNetwork.Assemble(mapping, [Mask("0x0F", 0x0F), Mask("0xF0", 0xF0)]);
    Assert.That(NetworkText.Print(swap), Is.EqualTo("v1 = And(v0, 0x0F); v2 = And(v0, 0xF0); v3 = Or(v1, v2)"));
  }

  [Test]
  public void ANonSquareMapping_IsAssembledAtItsLargerWidth() {
    var widening = BitMapping.Parse("4:[0,1,2,3,=1,=1,=1,=1]");
    var copy = new PrimitiveCandidate("and", 0x0F, 1, "copy", builder => NetworkText.Emit(builder, "v1 = And(v0, 0x0F)", 1));
    var wide = CompositeNetwork.Assemble(widening, [copy]);
    Assert.That((wide.InputWidth, wide.OutputWidth), Is.EqualTo((4, 8)));
    Assert.That(NetworkText.Print(wide), Is.EqualTo("v1 = ZeroExtend(v0, 8); v2 = And(v1, 0x0F); v3 = Or(v2, 0xF0)"));
    Assert.That(NetworkEvaluator.Evaluate(wide, 0x9), Is.EqualTo(new BigInteger(0xF9)));

    var narrowing = BitMapping.Parse("8:[4,5,6,7]");
    var high = new PrimitiveCandidate("shift", 0x0F, 1, "high", builder => NetworkText.Emit(builder, "v1 = ShiftRightLogical(v0, 4)"));
    var narrow = CompositeNetwork.Assemble(narrowing, [high]);
    Assert.That(NetworkText.Print(narrow), Is.EqualTo("v1 = ShiftRightLogical(v0, 4); v2 = Truncate(v1, 4)"));
    Assert.That(NetworkEvaluator.Evaluate(narrow, 0xB4), Is.EqualTo(new BigInteger(0xB)));

    var constant = CompositeNetwork.Assemble(BitMapping.Parse("4:[=1,=0,=1,=0]"), []);
    Assert.That(NetworkText.Print(constant), Is.EqualTo("v1 = Const(0x00); v2 = Or(v1, 0x05)"));
  }

  // 10, 11 - cost tables

  [Test]
  public void APrimitiveMissingFromTheCostTable_IsUnavailable() {
    var costs = Costs("bitperm-6502");
    Assert.That(costs.Name, Is.EqualTo("bitperm-6502"));
    Assert.That(costs.Primitives, Does.Contain("shift"));
    var refused = Assert.Throws<PermutationModelException>(() => costs.Cycles("multiply"));
    Assert.That(refused!.Message, Is.EqualTo("PLY0811: primitive 'multiply' is not available in cost table 'bitperm-6502'"));
    Assert.That(costs.Has("multiply"), Is.False);
    Assert.That(costs.Cycles("shift"), Is.EqualTo(2));
  }

  [Test]
  public void AMissingOrInvalidCostDocument_IsRefused() {
    Assert.Throws<FileNotFoundException>(() => PrimitiveCostTable.Load(Path.Combine(Path.GetTempPath(), "no-such-cost-table-polypiler.yaml")));
    var directory = Directory.CreateTempSubdirectory("polypiler-costs-");
    try {
      foreach (var (name, text) in new[] { ("empty", ""), ("no-spec", "id: x\n"), ("no-costs", "spec:\n  kind: cost\n"), ("bad-entry", "spec:\n  costs:\n    - {primitive: and}\n"), ("scalar", "spec:\n  costs:\n    - and\n") }) {
        var path = Path.Combine(directory.FullName, name + ".yaml");
        File.WriteAllText(path, text);
        Assert.Throws<InvalidDataException>(() => PrimitiveCostTable.Load(path), name);
      }
    } finally {
      directory.Delete(true);
    }
  }

  // the text form

  [TestCase("v2 = Not(v0)", "'v2 = Not(v0)' does not define v1")]
  [TestCase("v1 = Spin(v0)", "network step 1 (Spin): is no operation")]
  [TestCase("v1 = Not v0", "'Not v0' is no operation")]
  [TestCase("v1 = And(v0, q)", "'q' is no operand or immediate")]
  [TestCase("v1 = And(v0, [1,300])", "'300' is no byte")]
  [TestCase("v1 = 7(v0)", "network step 1 (7): is no operation")]
  public void AnUnreadableText_IsRefused(string text, string problem) {
    var refused = Assert.Throws<PermutationModelException>(() => NetworkText.Parse(text, 8, 8));
    Assert.That(refused!.Diagnostic.Code, Is.EqualTo("PLY0810"));
    Assert.That(refused.Message, Does.Contain(problem));
  }

  [Test]
  public void TheTextForm_RoundTripsEveryImmediateAndWidth() {
    const string text = "v1 = Const(0x0F, 16); v2 = ZeroExtend(v0, 16); v3 = Or(v1, v2); v4 = Truncate(v3, 8); v5 = And(v4, [1,2]); v6 = And(v4, v4)";
    var network = NetworkText.Parse(text.Replace("; ", "\n", StringComparison.Ordinal), 8, 8);
    Assert.That(NetworkText.Print(network), Is.EqualTo(text));
    Assert.That(network.Steps[4].Immediate, Is.EqualTo(new NetworkImmediate.Bytes([1, 2])));
    Assert.That(new NetworkImmediate.Bytes([1, 2]).GetHashCode(), Is.EqualTo(new NetworkImmediate.Bytes([1, 2]).GetHashCode()));
    var table = new NetworkImmediate.Table([1, 0x1F]);
    Assert.That((table.ToString(), table.Equals(new NetworkImmediate.Table([1, 0x1F])), table.GetHashCode() == new NetworkImmediate.Table([1, 0x1F]).GetHashCode()), Is.EqualTo(("{0x01,0x1F}", true, true)));
    Assert.That(NetworkText.Step(new NetworkStep(NetworkOp.Const, 8, [], new NetworkImmediate.Bits(0))), Is.EqualTo("Const(0x00, 8)"));
    Assert.That(NetworkText.Hex(0x1FF), Is.EqualTo("0x1FF"));
    Assert.That(network.WidthOf(1), Is.EqualTo(16));
    Assert.Throws<ArgumentOutOfRangeException>(() => network.WidthOf(7));
    Assert.That(new NetworkStep(NetworkOp.Not, 8, [0], null), Is.EqualTo(new NetworkStep(NetworkOp.Not, 8, [0], null)));
    Assert.That(new NetworkStep(NetworkOp.Not, 8, [0], null).Equals(null), Is.False);
  }

  [Test]
  public void EvaluatingAnIllFormedNetwork_IsRefused() {
    var forward = NetworkText.Parse("v1 = And(v0, v2); v2 = Not(v0)", 8, 8);
    Assert.That(Assert.Throws<PermutationModelException>(() => NetworkEvaluator.Evaluate(forward, 1))!.Message, Does.Contain("operand v2 is not defined before use"));
    var network = NetworkText.Parse("v1 = Not(v0)", 8, 8);
    Assert.That(Assert.Throws<PermutationModelException>(() => NetworkEvaluator.Evaluate(network, 1, new NetworkOpRegistry()))!.Message, Does.Contain("is not registered"));
    Assert.That(Assert.Throws<PermutationModelException>(() => NetworkLowering.ToFunction(network, "F", new NetworkOpRegistry()))!.Message, Does.Contain("is not registered"));
    Assert.Throws<InvalidOperationException>(() => NetworkOpRegistry.Core().Register(new ExtraNot()));
  }

  // the primitive extension point

  private sealed class FakePrimitive(string id, int widest) : IPermutationPrimitive {
    public string Id => id;
    public IReadOnlyList<NetworkOp> Ops => [NetworkOp.ShiftLeft];
    public bool Supports(int width) => width <= widest;
    public IEnumerable<PrimitiveCandidate> FindRoutings(RoutingProblem problem) => [];
  }

  private sealed class ExtraNot : INetworkOpSemantics {
    public NetworkOp Op => NetworkOp.Not;
    public string? Primitive => null;
    public BigInteger Evaluate(NetworkStep step, IReadOnlyList<BigInteger> operands) => 0;
    public IEnumerable<string> Check(NetworkStep step, BitOperationNetwork network) => [];
    public IrExpr Lower(NetworkStep step, IReadOnlyList<IrExpr> operands) => operands[0];
  }

  [Test]
  public void ThePrimitiveRegistry_GivesOnlyPricedPrimitivesOfTheWidth() {
    var costs = Costs("bitperm-6502");
    var registry = new PrimitiveRegistry().Register(new FakePrimitive("shift", 16)).Register(new FakePrimitive("multiply", 64));
    Assert.That(registry.Primitives.Select(primitive => primitive.Id), Is.EqualTo(new[] { "shift", "multiply" }));
    Assert.That(registry.Available(costs, 8).Select(primitive => primitive.Id), Is.EqualTo(new[] { "shift" }));
    Assert.That(registry.Available(costs, 32), Is.Empty);
    Assert.That(registry.Require("shift", 16, costs).Id, Is.EqualTo("shift"));
    Assert.That(Assert.Throws<PermutationModelException>(() => registry.Require("multiply", 8, costs))!.Message, Is.EqualTo("PLY0811: primitive 'multiply' is not available in cost table 'bitperm-6502'"));
    Assert.That(Assert.Throws<PermutationModelException>(() => registry.Require("rotate", 8, costs))!.Diagnostic.Code, Is.EqualTo("PLY0811"));
    Assert.That(Assert.Throws<PermutationModelException>(() => registry.Require("shift", 32, costs))!.Message, Is.EqualTo("PLY0812: primitive 'shift' does not support width 32"));
    Assert.Throws<InvalidOperationException>(() => registry.Register(new FakePrimitive("shift", 8)));
    Assert.That(new FakePrimitive("shift", 8).FindRoutings(new RoutingProblem(BitMapping.Identity(8), 0xFF, costs)), Is.Empty);
  }

  [Test]
  public void EveryStep_CostsItsPrimitiveAndWidthChangesAndConstantsCostNothing() {
    var network = NetworkText.Parse("v1 = Const(0x0F); v2 = Not(v0); v3 = And(v2, v1); v4 = ZeroExtend(v3, 16); v5 = Truncate(v4, 8); v6 = And(v5, v5)", 8, 8);
    Assert.That(NetworkVerifier.Check(network), Is.Empty, "v5 is read twice by v6, which the use check visits once");
    Assert.That(new NetworkStatistics(network).Cost(Costs("bitperm-6502")), Is.EqualTo(2 + 2 + 2), "not, and, and at 2 cycles each");
  }

  [Test]
  public void AnOperandOfAnotherWidth_IsReportedForEveryOperation() {
    var wide = new NetworkStep(NetworkOp.ZeroExtend, 16, [0], null);
    foreach (var step in new[] { new NetworkStep(NetworkOp.Not, 8, [1], null), new NetworkStep(NetworkOp.ShiftLeft, 8, [1], new NetworkImmediate.Amount(1)) }) {
      var network = new BitOperationNetwork(8, 8, [wide, step], 2);
      Assert.That(Diagnostics(network), Does.Contain($"network step 2 ({step.Op}): operand v1 has 16 bits, not 8"));
    }
  }
}
