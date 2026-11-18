using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>Whole-value reversal, including every swap order and counted byte loops.</summary>
[TestFixture]
public sealed class PermutationRecognitionTests {

  internal static OptimizationResult Optimize(IrModule module, string id) => Optimizer.Run(module,
    RecognitionFixtures.Options with { Disabled = RuleTable.Default.Ids.Where(other => other != id).ToHashSet() });

  internal static IrModule Import(string method) => PrototypeFixture.Import("Permutation.cs", PrototypeFixture.Wrap(method));

  internal static string ByteLoop(int width, int trips, bool effect = false) {
    var type = width == 32 ? "uint" : "ulong";
    return $"public static {type} F({type} x) {{ {type} r = 0; for (int i = 0; i < {trips}; i++) {{ r = (r << 8) | (x & 255); x >>= 8; {(effect ? "System.Console.WriteLine(i);" : "")} }} return r; }}";
  }

  [TestCase(32)]
  [TestCase(64)]
  public void ByteLoop_BecomesByteSwap(int width) {
    var module = Import(ByteLoop(width, width / 8));
    var result = Optimize(module, "O0106");
    Assert.That(result.Applications.Select(application => application.Id), Is.EqualTo(new[] { "O0106" }));
    Assert.That(IrText.Of(result.Module), Does.Contain("ByteSwap<"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  [TestCase(32, 3)]
  [TestCase(32, 5)]
  [TestCase(64, 7)]
  [TestCase(64, 9)]
  public void ByteLoop_WrongTripCountIsRefused(int width, int trips) {
    var module = Import(ByteLoop(width, trips));
    var result = Optimize(module, "O0106");
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0106 not applied: trip count differs from byte count"));
  }

  [TestCase("|")]
  [TestCase("^")]
  [TestCase("+")]
  public void ByteSwap_DisjointCombiners(string combiner) {
    var module = Import($"public static uint F(uint x) => (x >> 24) {combiner} ((x >> 8) & 0xFF00u) {combiner} ((x << 8) & 0xFF0000u) {combiner} (x << 24);");
    Assert.That(Optimize(module, "O0106").Applications, Has.Count.EqualTo(1));
  }

  [TestCase(8)]
  [TestCase(16)]
  [TestCase(32)]
  [TestCase(64)]
  public void BitCascade_AscendingAndDescendingSteps(int width) {
    var steps = Enumerable.Range(0, System.Numerics.BitOperations.Log2((uint)width)).Select(bit => 1 << bit).ToArray();
    foreach (var order in new[] { steps, steps.Reverse().ToArray() }) {
      var result = Optimize(Import(Cascade(width, order)), "O0105");
      Assert.That(result.Applications, Has.Count.EqualTo(1), $"width {width}, steps {string.Join(",", order)}; {string.Join("; ", result.Notes)}");
      Assert.That(IrText.Of(result.Module), Does.Contain("BitReverse<"));
    }
  }

  private static IEnumerable<TestCaseData> Orders() {
    foreach (var width in new[] { 8, 16, 32, 64 }) {
      var steps = Enumerable.Range(0, System.Numerics.BitOperations.Log2((uint)width)).Select(bit => 1 << bit).ToArray();
      foreach (var order in Permutations(steps))
        yield return new TestCaseData(width, order).SetArgDisplayNames(width.ToString(), string.Join(",", order));
    }
  }

  private static IEnumerable<int[]> Permutations(int[] steps) {
    if (steps.Length == 0) {
      yield return [];
      yield break;
    }

    foreach (var first in steps)
      foreach (var tail in Permutations(steps.Where(step => step != first).ToArray()))
        yield return [first, .. tail];
  }

  [TestCaseSource(nameof(Orders))]
  public void EveryCascadeOrder_IsProved(int width, int[] steps) {
    var module = CascadeModule(width, steps);
    var result = Optimize(module, "O0105");
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    Assert.That(result.Notes, Does.Contain($"O0105 applied: ReverseBits.u{width} from cascade [proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  internal static IrModule CascadeModule(int width, IEnumerable<int> steps) {
    var type = width switch { 8 => IrType.U8, 16 => IrType.U16, 32 => IrType.U32, _ => IrType.U64 };
    var carrier = width <= 32 ? IrType.U32 : IrType.U64;
    var x = new IrLocal("x", type);
    IrExpr value = type == carrier ? new IrLocalRef(x) : new IrConvert(carrier, new IrLocalRef(x));
    foreach (var shift in steps) {
      var mask = 0UL;
      for (var bit = 0; bit < width; ++bit)
        if ((bit & shift) == 0)
          mask |= 1UL << bit;

      var constant = new IrConst(carrier, unchecked((long)mask));
      var count = new IrConst(IrType.I32, shift);
      value = new IrBinary(IrBinaryOp.Or,
        new IrBinary(IrBinaryOp.And, new IrBinary(IrBinaryOp.ShiftRightLogical, value, count), constant),
        new IrBinary(IrBinaryOp.ShiftLeft, new IrBinary(IrBinaryOp.And, value, constant), count));
    }

    if (value.Type != type)
      value = new IrConvert(type, value);

    return new("Cascade", [new IrFunction("F", true, type, [x], [], new IrBlock([new IrReturn(value)]))], []);
  }

  [TestCase("O0105")]
  [TestCase("O0106")]
  public void ProofTimeout_LeavesCandidateUnchanged(string id) {
    var module = id == "O0105" ? CascadeModule(32, [1, 2, 4, 8, 16]) : Import(ByteLoop(32, 4));
    var result = Optimizer.Run(module, RecognitionFixtures.Options with {
      Prover = new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) },
      Disabled = RuleTable.Default.Ids.Where(other => other != id).ToHashSet()
    });
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain($"{id} not applied: proof timeout"));
  }

  [Test]
  public void MissingProver_IsARefusal() {
    var module = CascadeModule(32, [1, 2, 4, 8, 16]);
    var result = Optimizer.Run(module, RecognitionFixtures.Options with { Prover = null });
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0105 not applied: proof unavailable"));
  }

  [Test]
  public void AlreadyCanonicalPermutation_DoesNotReportAnApplication() {
    var x = new IrLocal("x", IrType.U32);
    IrExpr value = new IrConvert(IrType.U32, new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U8, [new IrConvert(IrType.U8, new IrLocalRef(x))]));
    var module = new IrModule("Canonical", [new IrFunction("F", true, IrType.U32, [x], [], new IrBlock([new IrReturn(value)]))], []);
    var result = Optimize(module, "O0105");
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes.Any(note => note.StartsWith("O0105 applied:", StringComparison.Ordinal)), Is.False);
  }

  [Test]
  public void RejectedLoop_DoesNotReportItsProvisionalProofAsApplied() {
    var module = Import("public static ulong F(ulong x, ulong y) { ulong r = 0; for (int i = 0; i < 8; ++i) { r = (r << 8) | (x & 255); x >>= 8; y = y * r; } return r ^ y; }");
    var result = Optimize(module, "O0106");
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes.Any(note => note.StartsWith("O0106 applied:", StringComparison.Ordinal)), Is.False);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [TestCase(40, true)]
  [TestCase(41, false)]
  public void CascadeGate_RespectsInstructionBudget(int instructions, bool applies) {
    var module = CascadeModule(32, [1, 2, 4, 8, 16]);
    var function = module.Functions[0];
    var expression = ((IrReturn)function.Body.Statements[0]).Value!;
    for (var count = 25; count < instructions; ++count)
      expression = new IrBinary(IrBinaryOp.Or, expression, new IrConst(IrType.U32, 0));

    var notes = new List<string>();
    var context = new RuleContext(function, RuleTarget.Neutral) { Prover = new SmtRecognitionProver(), Note = notes.Add };
    Assert.That(RuleTable.Default.Find("O0105")!.TryApply(expression, context, out _), Is.EqualTo(applies));
    if (!applies)
      Assert.That(notes, Does.Contain("O0105 not applied: expression too large"));
  }

  [TestCase("O0105")]
  [TestCase("O0106")]
  public void DisabledRule_ProducesNeitherApplicationNorProofNote(string id) {
    var module = id == "O0105" ? CascadeModule(32, [1, 2, 4, 8, 16]) : Import(ByteLoop(32, 4));
    var result = Optimizer.Run(module, RecognitionFixtures.Options with { Disabled = RuleTable.Default.Ids.ToHashSet() });
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void EightBitCascade_IsAlsoExhaustivelyProved() {
    var module = CascadeModule(8, [1, 2, 4]);
    var function = module.Functions[0];
    var before = ((IrReturn)function.Body.Statements[0]).Value!;
    var after = new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U8, [new IrLocalRef(function.Parameters[0])]);
    var obligation = SmtRecognitionProver.CreateObligation("O0105", before, after, new RuleContext(function, RuleTarget.Neutral));
    Assert.That(obligation, Is.Not.Null);
    Assert.That(new ExhaustiveProver().Prove(obligation!, new(MaxInputBits: 8)), Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 256)));
  }

  [Test]
  public void ArithmeticShiftLoop_KeepsTheSignFilledFinalValue() {
    var module = Import("public static int F(int x) { int r = 0; for (int i = 0; i < 32; ++i) { r = (r << 1) | (x & 1); x >>= 1; } return r ^ x; }");
    var result = Optimize(module, "O0105");
    Assert.That(result.Applications, Has.Count.EqualTo(1));
    foreach (var input in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
      Assert.That(IrInterpreter.Call(result.Module, "F", [input]), Is.EqualTo(IrInterpreter.Call(module, "F", [input])));
  }

  [TestCase("O0105", 1, 1, 32)]
  [TestCase("O0106", 8, 255, 4)]
  public void ObservableLoopBody_IsRefusedAndExplained(string id, int shift, int mask, int trips) {
    var module = Import($"public static uint F(uint x, uint[] output) {{ uint r = 0; for (int i = 0; i < {trips}; ++i) {{ r = (r << {shift}) | (x & {mask}); x >>= {shift}; output[i] = r; }} return r; }}");
    var result = Optimize(module, id);
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain($"{id} not applied: loop body has effects"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [TestCase(8)]
  [TestCase(16)]
  [TestCase(32)]
  [TestCase(64)]
  public void BitLoop_EveryWidthIsProved(int width) {
    var type = width switch { 8 => "byte", 16 => "ushort", 32 => "uint", _ => "ulong" };
    var carrier = width <= 32 ? "uint" : "ulong";
    var module = Import($"public static {type} F({type} x) {{ {carrier} r = 0, v = x; for (int i = 0; i < {width}; ++i) {{ r = (r << 1) | (v & 1); v >>= 1; }} return ({type})r; }}");
    var result = Optimize(module, "O0105");
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  [Test]
  public void TrailingCodeLengthShift_RemainsAfterRecognition() {
    var source = Cascade(32, [1, 2, 4, 8, 16]).Replace("F(uint x)", "F(uint x, int length)", StringComparison.Ordinal)
      .Replace("return (uint)v;", "return v >> (32 - length);", StringComparison.Ordinal);
    var result = Optimize(Import(source), "O0105");
    Assert.That(result.Applications, Has.Count.EqualTo(1));
    Assert.That(IrText.Of(result.Module), Does.Contain("BitReverse<").And.Contain("ShiftRight"));
    Assert.That(IrInterpreter.Call(result.Module, "F", [3u, 3]), Is.EqualTo(6u));
  }

  [TestCase("bitReversal")]
  [TestCase("byteSwap")]
  public void CapturedPinnedUpstreamOutputs_Agree(string operation) {
    using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "oracle-vectors.json")));
    var bit = operation == "bitReversal";
    var source = bit ? Cascade(32, [1, 2, 4, 8, 16]).Replace("F(uint x)", "F(uint x, int length)", StringComparison.Ordinal)
      .Replace("return (uint)v;", "return v >> (32 - length);", StringComparison.Ordinal) : ByteLoop(64, 8);
    var module = Import(source);
    var result = Optimize(module, bit ? "O0105" : "O0106");
    Assert.That(result.Applications, Has.Count.EqualTo(1));
    foreach (var vector in vectors.RootElement.GetProperty(operation).GetProperty("cases").EnumerateArray()) {
      var input = ulong.Parse(vector.GetProperty("input").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      var expected = ulong.Parse(vector.GetProperty("expected").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      object[] arguments = bit ? [(uint)input, vector.GetProperty("length").GetInt32()] : [input];
      Assert.That(Convert.ToUInt64(IrInterpreter.Call(module, "F", arguments)), Is.EqualTo(expected), "original vs pinned upstream");
      Assert.That(Convert.ToUInt64(IrInterpreter.Call(result.Module, "F", arguments)), Is.EqualTo(expected), "optimized vs pinned upstream");
    }
  }

  [TestCase("o0105/reversal.cs", "Cascade", "O0105", 23)]
  [TestCase("o0106/byteswap.cs", "Terms", "O0106", 9)]
  public void CanonicalExample_ReducesToOneOperation(string file, string function, string id, int instructions) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", file);
    var imported = PrototypeFixture.Import(file, File.ReadAllText(path));
    var module = imported with { Functions = [imported.Functions.Single(candidate => candidate.Name == function)] };
    int Count(IrModule value) => IrTree.Nodes(value.Functions[0].Body).Count(node => node is IrBinary or IrUnary or IrConvert or IrIntrinsic);
    var result = Optimize(module, id);
    Assert.That(Count(module), Is.EqualTo(instructions));
    Assert.That(Count(result.Module), Is.EqualTo(1));
    TestContext.Out.WriteLine($"{id}: {instructions} -> 1 IR operations; {string.Join("; ", result.Notes)}");
  }

  [TestCase(32, "O0105", 105)]
  [TestCase(64, "O0105", 105)]
  [TestCase(32, "O0106", 106)]
  [TestCase(64, "O0106", 106)]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithTheInterpreterAndAnIndependentBitOracle(int width, string id, int seed) {
    var module = id == "O0105" ? Import(Cascade(width, Enumerable.Range(0, System.Numerics.BitOperations.Log2((uint)width)).Select(bit => 1 << bit)))
      : Import(ByteLoop(width, width / 8));
    var result = Optimize(module, id);
    Assert.That(result.Applications, Has.Count.EqualTo(1));
    var random = new Random(seed);
    var bytes = new byte[8];
    for (var sample = 0; sample < 10_004; ++sample) {
      random.NextBytes(bytes);
      var input = sample switch { 0 => 0UL, 1 => ulong.MaxValue, 2 => 1UL, 3 => 1UL << (width - 1), _ => BitConverter.ToUInt64(bytes) };
      if (width == 32)
        input &= uint.MaxValue;

      object argument = width == 32 ? (object)(uint)input : input;
      var expected = 0UL;
      var unit = id == "O0105" ? 1 : 8;
      var mask = (1UL << unit) - 1;
      for (var position = 0; position < width; position += unit)
        expected = (expected << unit) | ((input >> position) & mask);

      Assert.That(Convert.ToUInt64(IrInterpreter.Call(module, "F", [argument])), Is.EqualTo(expected), $"source, seed {seed}, sample {sample}");
      Assert.That(Convert.ToUInt64(IrInterpreter.Call(result.Module, "F", [argument])), Is.EqualTo(expected), $"optimized, seed {seed}, sample {sample}");
    }
    TestContext.Out.WriteLine($"{id}, width {width}, seed {seed}: 10,000 random inputs and four boundary inputs; zero mismatches.");
  }

  internal static string Cascade(int width, IEnumerable<int> steps) {
    var type = width switch { 8 => "byte", 16 => "ushort", 32 => "uint", _ => "ulong" };
    var carrier = width <= 32 ? "uint" : "ulong";
    var suffix = width <= 32 ? "u" : "UL";
    var statements = steps.Select(shift => {
      var mask = 0UL;
      for (var bit = 0; bit < width; ++bit)
        if ((bit & shift) == 0)
          mask |= 1UL << bit;

      return $"v = ((v >> {shift}) & {mask}{suffix}) | ((v & {mask}{suffix}) << {shift});";
    });
    return $"public static {type} F({type} x) {{ {carrier} v = x; {string.Join(" ", statements)} return ({type})v; }}";
  }
}
