using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>Parity on the actual promoted tree IR, from folds, the nibble table and the population count.</summary>
[TestFixture]
public sealed class ParityRecognitionTests {

  private static readonly string[] _forms = ["fold", "fold-logical", "fold-mask-left", "nibble", "nibble-logical", "popcount"];

  private static IEnumerable<TestCaseData> Forms() {
    foreach (var width in new[] { 8, 16, 32, 64 })
      foreach (var signed in new[] { true, false })
        foreach (var form in _forms)
          // The 64-bit population count's carries are not proved against the parity within budget: see PopCount64_IsNotProvedWithinBudget.
          if (width != 64 || form != "popcount")
            yield return new TestCaseData(width, signed, form);
  }

  private static IEnumerable<TestCaseData> WideForms => Forms().Where(data => data.Arguments[0] is int width && width != 8);

  private static IEnumerable<TestCaseData> EightBitForms => Forms().Where(data => data.Arguments[0] is 8);

  private static IrType Type(int width, bool signed) => (width, signed) switch {
    (8, true) => IrType.I8,
    (8, false) => IrType.U8,
    (16, true) => IrType.I16,
    (16, false) => IrType.U16,
    (32, true) => IrType.I32,
    (32, false) => IrType.U32,
    (64, true) => IrType.I64,
    _ => IrType.U64
  };

  private sealed class InconclusiveProver : IRecognitionProver {
    public int Calls { get; private set; }
    public RecognitionProof Prove(string id, IrExpr before, IrExpr after, RuleContext context) {
      ++this.Calls;
      return new(false, "proof unknown");
    }
  }

  private static OptimizerOptions Options(IRecognitionProver? prover) => RecognitionFixtures.Options with {
    Prover = prover,
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0110").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0110", "parity.cs");
    var module = PrototypeFixture.Import("parity.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  /// <summary>
  /// One spelling at a width and signedness, promoted as C# promotes it, its steps sharing their
  /// operands as a run of assignments composes them; or one of its near misses.
  /// </summary>
  internal static IrModule Module(int width, bool signed, string form, string? nearMiss = null, int padding = 0) {
    var type = Type(width, signed);
    var carrier = width < 32 ? IrType.I32 : type;
    var x = new IrLocal("x", type);
    IrExpr value = width < 32 ? new IrConvert(carrier, new IrLocalRef(x)) : new IrLocalRef(x);
    IrExpr Constant(long number) => new IrConst(carrier, IrOperators.Wrap(carrier, number));
    var shift = form.EndsWith("logical", StringComparison.Ordinal) ? IrBinaryOp.ShiftRightLogical : IrBinaryOp.ShiftRight;

    IrExpr result;
    if (form == "popcount") {
      var count = new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [new IrLocalRef(x)]);
      result = new IrBinary(IrBinaryOp.And, count, new IrConst(IrType.I32, 1));
    } else {
      var nibble = form.StartsWith("nibble", StringComparison.Ordinal);
      var folded = value;
      for (var step = width / 2; step >= (nibble ? 4 : 1); step /= 2) {
        // A skipped middle step leaves out bits of every low part; a skipped first step would be the exact parity of the low half.
        if (nearMiss == "skip" && step == (nibble ? 8 : 4))
          continue;

        folded = new IrBinary(IrBinaryOp.Xor, folded, new IrBinary(shift, folded, new IrConst(IrType.I32, step)));
      }

      for (var pad = 0; pad < padding; ++pad)
        folded = new IrBinary(IrBinaryOp.And, folded, Constant(-1));

      if (nibble) {
        var table = new IrConst(IrType.I32, nearMiss == "complement-table" ? 0x9669 : 0x6996);
        IrExpr index = new IrBinary(IrBinaryOp.And, folded, Constant(15));
        if (index.Type != IrType.I32)
          index = new IrConvert(IrType.I32, index);

        result = new IrBinary(IrBinaryOp.And, new IrBinary(IrBinaryOp.ShiftRight, table, index), new IrConst(IrType.I32, 1));
      } else {
        var mask = Constant(nearMiss == "mask-three" ? 3 : 1);
        result = form == "fold-mask-left" ? new IrBinary(IrBinaryOp.And, mask, folded) : new IrBinary(IrBinaryOp.And, folded, mask);
      }
    }

    return new("Parity", [new IrFunction("F", true, result.Type, [x], [], new([new IrReturn(result)]))], []);
  }

  private static object Argument(IrType type, ulong bits) => type.Kind switch {
    IrTypeKind.I8 => unchecked((sbyte)bits),
    IrTypeKind.U8 => unchecked((byte)bits),
    IrTypeKind.I16 => unchecked((short)bits),
    IrTypeKind.U16 => unchecked((ushort)bits),
    IrTypeKind.I32 => unchecked((int)bits),
    IrTypeKind.U32 => unchecked((uint)bits),
    IrTypeKind.I64 => unchecked((long)bits),
    _ => bits
  };

  private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

  private static long Number(object value) => value is ulong unsigned ? unchecked((long)unsigned) : Convert.ToInt64(value, CultureInfo.InvariantCulture);

  /// <summary>Both versions agree with the BCL's population count of the value's bits, masked to its lowest bit.</summary>
  private static void Agrees(IrModule before, IrModule after, int width, ulong bits) {
    var function = before.Functions[0];
    var argument = Argument(function.Parameters[0].Type, bits);
    long expected = BitOperations.PopCount(bits & Mask(width)) & 1;
    Assert.That(Number(IrInterpreter.Call(before, function.Name, [argument])), Is.EqualTo(expected), $"original vs BCL for {bits:X}");
    Assert.That(Number(IrInterpreter.Call(after, function.Name, [argument])), Is.EqualTo(expected), $"optimized vs BCL for {bits:X}");
  }

  private static ulong[] Edges(int width) {
    var all = Mask(width);
    var top = 1UL << (width - 1);
    return [0, 1, top, all, all - 1, top | 1, top - 1, 0x12345678UL & all, 0x9669_6996_1234_5678UL & all];
  }

  [TestCaseSource(nameof(Forms))]
  public void EveryForm_IsProvedAtItsActualWidthAndSignedness(int width, bool signed, string form) {
    var module = Module(width, signed, form);
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the input must be legal promoted IR");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    var parity = IrTree.Nodes(result.Module.Functions[0].Body).OfType<IrIntrinsic>().Single(node => node.Op == IrIntrinsicOp.Parity);
    var kind = form.StartsWith("nibble", StringComparison.Ordinal) ? "nibble-lookup" : form == "popcount" ? "popcount-mask" : "xor-fold";
    Assert.That(result.Notes, Does.Contain($"O0110 applied: Parity.{parity.Arguments[0].Type} from {kind} [proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var bits in Edges(width))
      Agrees(module, result.Module, width, bits);
  }

  [TestCaseSource(nameof(EightBitForms))]
  public void EightBits_AreExhaustiveByTheObligationAndByTheBcl(int width, bool signed, string form) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var function = before.Functions[0];
    var candidate = ((IrReturn)function.Body.Statements.Last()).Value!;
    var replacement = ((IrReturn)after.Module.Functions[0].Body.Statements.Last()).Value!;
    var obligation = SmtRecognitionProver.CreateObligation("O0110", candidate, replacement, new(function, RuleTarget.Neutral));
    Assert.That(obligation, Is.Not.Null);
    Assert.That(new ExhaustiveProver().Prove(obligation!, new(MaxInputBits: 8)), Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 256)));
    for (var bits = 0UL; bits < 256; ++bits)
      Agrees(before, after.Module, width, bits);
  }

  [TestCase(32, false, "fold", "skip")]
  [TestCase(64, true, "fold", "skip")]
  [TestCase(16, false, "fold-logical", "skip")]
  [TestCase(32, false, "fold", "mask-three")]
  [TestCase(8, false, "nibble", "complement-table")]
  [TestCase(32, true, "nibble", "complement-table")]
  [TestCase(64, false, "nibble", "skip")]
  public void RegisteredCounterexamples_AreNotParity(int width, bool signed, string form, string nearMiss) {
    var module = Module(width, signed, form, nearMiss);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0110 not applied: not equivalent to parity"));
  }

  [Test]
  public void AFoldWithoutTheFourStep_MissesBitFour() {
    var module = Module(32, false, "fold", "skip");
    Assert.That(IrInterpreter.Call(module, "F", [0x00000010u]), Is.EqualTo(0u));
    Assert.That(BitOperations.PopCount(0x00000010u) & 1, Is.EqualTo(1));
  }

  [TestCase(32, 16)]
  [TestCase(64, 32)]
  public void AFoldWithoutItsFirstStep_IsTheExactParityOfTheLowHalf(int width, int half) {
    // The work order lists this as a counterexample for the word; it is the parity of the low half, which the rule proves.
    var x = new IrLocal("x", width == 64 ? IrType.U64 : IrType.U32);
    IrExpr folded = new IrLocalRef(x);
    for (var step = half / 2; step >= 1; step /= 2)
      folded = new IrBinary(IrBinaryOp.Xor, folded, new IrBinary(IrBinaryOp.ShiftRight, folded, new IrConst(IrType.I32, step)));

    var result = new IrBinary(IrBinaryOp.And, folded, new IrConst(x.Type, 1));
    var module = new IrModule("Half", [new IrFunction("F", true, x.Type, [x], [], new([new IrReturn(result)]))], []);
    var after = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(after.Notes, Does.Contain($"O0110 applied: Parity.u{half} from xor-fold [proof: smt-bitvector]"));
    foreach (var bits in Edges(width))
      Assert.That(Number(IrInterpreter.Call(after.Module, "F", [Argument(x.Type, bits)])), Is.EqualTo((long)(BitOperations.PopCount(bits & Mask(half)) & 1)));
  }

  [TestCase(true)]
  [TestCase(false)]
  [Category("Slow")]
  public void PopCount64_IsNotProvedWithinBudget(bool signed) {
    var module = Module(64, signed, "popcount");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0110 not applied: proof timeout"));
  }

  [TestCase(13, true)]
  [TestCase(14, false)]
  public void TheFoldSize_IsBoundedAtFourLog2WidthPlusFour(int padding, bool rewritten) {
    // Eleven operations fold a word; the padding brings them to 24, the bound at 32 bits, and to 25.
    var module = Module(32, false, "fold", padding: padding);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(rewritten ? 1 : 0), string.Join("; ", result.Notes));
    if (!rewritten)
      Assert.That(result.Notes, Does.Contain("O0110 not applied: expression too large"));
  }

  [TestCase("public static uint F(uint x) => x & 1;")]
  [TestCase("public static uint F(uint x, uint y) => (x ^ y) & 1;")]
  [TestCase("public static uint F(uint x) => (x ^ (x >> 1)) & 1;")]
  [TestCase("public static uint F(uint x, uint y) => (x ^ (x >> 16) ^ y) & 1;")]
  [TestCase("public static uint F(uint x) { x ^= x >> 16; x ^= x >> 8; return x; }")]
  [TestCase("public static int F(int x, int y) => (y >> (x & 15)) & 1;")]
  [TestCase("public static int F(int x) => (0x6996 >> (x & 7)) & 1;")]
  public void UnrelatedExpressions_AreSilent(string method) {
    var module = PrototypeFixture.Import("Unrelated.cs", PrototypeFixture.Wrap(method));
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
    Assert.That(prover.Calls, Is.Zero);
  }

  [TestCase(8, "missing", "proof unavailable")]
  [TestCase(64, "missing", "proof unavailable")]
  [TestCase(8, "timeout", "proof timeout")]
  [TestCase(16, "timeout", "proof timeout")]
  [TestCase(32, "timeout", "proof timeout")]
  [TestCase(64, "timeout", "proof timeout")]
  public void AnUnprovedCandidate_IsUntouched(int width, string mode, string reason) {
    var module = Module(width, false, "fold");
    var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0110 not applied: " + reason));
  }

  [Test]
  public void AnUnknownProof_IsARefusal() {
    var module = Module(32, false, "nibble");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0110 not applied: proof unknown"));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Module(32, false, "fold");
    var prover = new InconclusiveProver();
    foreach (var options in new[] { Options(prover) with { Disabled = RuleTable.Default.Ids.ToHashSet() },
      Options(prover) with { Level = OptimizationLevel.None } }) {
      var result = Optimizer.Run(module, options);
      Assert.That(result.Applications, Is.Empty);
      Assert.That(result.Notes, Is.Empty);
      Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    }

    Assert.That(prover.Calls, Is.Zero);
  }

  [Test]
  public void AValueThatMayTrap_IsRefused() {
    var module = PrototypeFixture.Import("Trapping.cs", PrototypeFixture.Wrap("public static uint F(uint[] a) => (a[0] ^ (a[0] >> 4) ^ (a[0] >> 2) ^ (a[0] >> 6) ^ (a[0] >> 1) ^ (a[0] >> 5) ^ (a[0] >> 3) ^ (a[0] >> 7)) & 1;"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [TestCase("Fold32", "Parity.u32 from xor-fold")]
  [TestCase("Nibble8", "Parity.u8 from nibble-lookup")]
  [TestCase("Nibble32", "Parity.u32 from nibble-lookup")]
  [TestCase("Signed", "Parity.i32 from xor-fold")]
  public void TheExecutableFixtures_AreProved(string function, string report) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(after.Notes, Does.Contain($"O0110 applied: {report} [proof: smt-bitvector]"));
    TestContext.Out.WriteLine("Before:\n" + IrText.Of(before) + "After:\n" + IrText.Of(after.Module) + string.Join("\n", after.Notes));
    TestContext.Out.WriteLine("Solver: " + new Z3Solver().Version);
  }

  [TestCase("SkipsAStep")]
  [TestCase("ComplementTable")]
  [TestCase("MaskThree")]
  public void TheExecutableCounterexamples_AreRefused(string function) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0110 not applied: not equivalent to parity"));
  }

  [TestCaseSource(nameof(WideForms))]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithTheBcl(int width, bool signed, string form) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(110);
    var bytes = new byte[8];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      Agrees(before, after.Module, width, BitConverter.ToUInt64(bytes));
    }

    TestContext.Out.WriteLine($"O0110 {form}, {(signed ? "signed" : "unsigned")} {width} bits: seed 110, 10,000 inputs, zero mismatches.");
  }
}
