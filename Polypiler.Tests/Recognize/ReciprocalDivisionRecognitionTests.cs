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

/// <summary>Division and remainder by a constant recovered from reciprocal multiplications, with the certificate as the proof.</summary>
[TestFixture]
public sealed class ReciprocalDivisionRecognitionTests {

  private static OptimizerOptions Options(RuleTarget? target = null) => RecognitionFixtures.Options with {
    Target = target ?? RuleTarget.Neutral,
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0126").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0126", "divide.cs");
    var module = PrototypeFixture.Import("divide.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  private static IrModule Source(string method) => PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method));

  private static BigInteger Number(object? value) => value switch {
    ulong unsigned => unsigned,
    _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
  };

  private static object Argument(IrType type, BigInteger value) => type.Kind switch {
    IrTypeKind.U16 => (ushort)value,
    IrTypeKind.I16 => (short)value,
    IrTypeKind.U32 => (uint)value,
    IrTypeKind.I32 => (int)value,
    IrTypeKind.U64 => (ulong)value,
    _ => (long)value
  };

  /// <summary>Both versions give the BCL's quotient or remainder: <see cref="BigInteger.Divide"/> truncates as the IR does.</summary>
  private static void Agrees(IrModule before, IrModule after, BigInteger divisor, bool remainder, IEnumerable<BigInteger> inputs) {
    var function = before.Functions[0];
    var width = function.ReturnType.Bits;
    var modulus = BigInteger.One << width;
    foreach (var input in inputs) {
      var expected = remainder ? BigInteger.Remainder(input, divisor) : BigInteger.Divide(input, divisor);
      expected = function.ReturnType.IsSigned ? expected : (expected % modulus + modulus) % modulus;
      object[] arguments = [Argument(function.Parameters[0].Type, input)];
      Assert.That(Number(IrInterpreter.Call(before, function.Name, arguments)), Is.EqualTo(expected), $"original for {input}");
      Assert.That(Number(IrInterpreter.Call(after, function.Name, arguments)), Is.EqualTo(expected), $"recovered for {input}");
    }
  }

  private static IEnumerable<BigInteger> Edges(IrType type, BigInteger divisor) {
    var (low, high) = type.IsSigned ? (-(BigInteger.One << (type.Bits - 1)), (BigInteger.One << (type.Bits - 1)) - 1) : (BigInteger.Zero, (BigInteger.One << type.Bits) - 1);
    BigInteger[] candidates = [low, low + 1, -divisor - 1, -divisor, -divisor + 1, -17, -1, 0, 1, divisor - 1, divisor, divisor + 1,
      (high + 1) / divisor * divisor - 1, high - 1, high];
    return candidates.Where(value => value >= low && value <= high).Distinct();
  }

  private static IEnumerable<BigInteger> Random(IrType type, int seed, int count) {
    var random = new Random(seed);
    var bytes = new byte[8];
    for (var index = 0; index < count; ++index) {
      random.NextBytes(bytes);
      var bits = new BigInteger(bytes, isUnsigned: true) & ((BigInteger.One << type.Bits) - 1);
      yield return type.IsSigned && bits >= BigInteger.One << (type.Bits - 1) ? bits - (BigInteger.One << type.Bits) : bits;
    }
  }

  [TestCase("Udiv10", 10, false, "UDiv.u32 by 10 (M=0xCCCCCCCD, shift=35, e=2) from widened-multiply")]
  [TestCase("Udiv7AddBack", 7, false, "UDiv.u32 by 7 (M=0x124924925, shift=35, e=3) from add-back")]
  [TestCase("Udiv10Narrow", 10, false, "UDiv.u32 by 10 (M=0xCCCD, shift=19, e=2) from widened-multiply")]
  [TestCase("Sdiv10", 10, false, "SDiv.i32 by 10 (M=0x66666667, shift=34, e=6) from widened-multiply")]
  [TestCase("Urem10", 10, true, "URem.u32 by 10 from the quotient")]
  [TestCase("Udiv3Wide", 3, false, "UDiv.u64 by 3 (M=0xAAAAAAAAAAAAAAAB, shift=65, e=1) from mul-high")]
  public void TheExecutableFixtures_AreRecovered(string name, int divisor, bool remainder, string report) {
    var before = Fixture(name);
    var after = Optimizer.Run(before, Options());
    Assert.That(after.Notes, Does.Contain($"O0126 applied: {report} [proof: validated-rule]"));
    Assert.That(IrVerifier.Verify(after.Module), Is.Empty);
    var operation = remainder ? IrBinaryOp.Remainder : IrBinaryOp.Divide;
    Assert.That(IrTree.Nodes(after.Module.Functions[0].Body).OfType<IrBinary>().Any(node => node.Op == operation && node.Right is IrConst { Value: var d } && d == divisor), Is.True);
    var type = before.Functions[0].Parameters[0].Type;
    Agrees(before, after.Module, divisor, remainder, Edges(type, divisor).Concat(Random(type, 126, 10_000)));
    TestContext.Out.WriteLine("Before:\n" + IrText.Of(before) + "After:\n" + IrText.Of(after.Module) + string.Join("\n", after.Notes));
  }

  [TestCase("Udiv10BadMagic", "reciprocal is not exact for the operand range", 10UL, 0UL)]
  [TestCase("Udiv10NarrowMul", "multiplication may wrap", 81920UL, 0UL)]
  [TestCase("Sdiv10Uncorrected", "missing sign correction", null, null)]
  [TestCase("Urem10By9", "remainder uses another divisor", null, null)]
  public void TheExecutableCounterexamples_AreRefused(string name, string reason, ulong? input, ulong? output) {
    var before = Fixture(name);
    var after = Optimizer.Run(before, Options());
    Assert.That(after.Notes, Does.Contain("O0126 not applied: " + reason));
    Assert.That(IrTree.Nodes(after.Module.Functions[0].Body).OfType<IrBinary>().Where(node => node.Op == IrBinaryOp.Remainder), Is.Empty);
    if (input is { } x)
      Assert.That(IrInterpreter.Call(after.Module, name, [(uint)x]), Is.EqualTo((uint)output!.Value));
  }

  [Test]
  public void TheSignCorrectionMayBeAddedAsTheLogicalSignBit() {
    var module = Source("public static int F(int x) { int q = (int)(((long)x * 0x66666667L) >> 34); return q + (int)((uint)x >> 31); }");
    var after = Optimizer.Run(module, Options());
    Assert.That(after.Notes, Does.Contain("O0126 applied: SDiv.i32 by 10 (M=0x66666667, shift=34, e=6) from widened-multiply [proof: validated-rule]"));
    Agrees(module, after.Module, 10, false, Edges(IrType.I32, 10).Concat(Random(IrType.I32, 126, 10_000)));
  }

  [TestCase("public static int F(int x) { int q = (int)(((long)x * 0x66666667L) >> 34); return q - (int)((uint)x >> 31); }")]
  [TestCase("public static int F(int x) { int q = (int)(((long)x * 0x66666667L) >> 34); return q + (x >> 31); }")]
  [TestCase("public static int F(int x, int y) { int q = (int)(((long)x * 0x66666667L) >> 34); return q - (y >> 31); }")]
  [TestCase("public static int F(int x) { int q = (int)(((long)x * 0x66666667L) >> 34); return q - (x >> 30); }")]
  public void AWrongSignCorrection_IsNotATruncatingDivision(string method) {
    var after = Optimizer.Run(Source(method), Options());
    Assert.That(after.Applications, Is.Empty);
  }

  [Test]
  public void TheRemainder_IsRecoveredFromAnInlineQuotient() {
    var module = Source("public static uint F(uint x) => x - (uint)(((ulong)x * 0xCCCCCCCDUL) >> 35) * 10u;");
    var after = Optimizer.Run(module, Options());
    // Whether the quotient inside is recovered first or the remainder is read from the reciprocal directly, the remainder is proved.
    Assert.That(after.Notes, Has.Some.StartsWith("O0126 applied: URem.u32 by 10 ").And.Some.EndsWith("[proof: validated-rule]"));
    Agrees(module, after.Module, 10, true, Edges(IrType.U32, 10).Concat(Random(IrType.U32, 126, 10_000)));
  }

  [Test]
  public void TheSignedRemainder_IsRecovered() {
    var module = Source("public static int F(int x) { int q = (int)(((long)x * 0x66666667L) >> 34) - (x >> 31); return x - q * 10; }");
    var after = Optimizer.Run(module, Options());
    Assert.That(after.Notes, Has.Some.StartsWith("O0126 applied: SRem.i32 by 10 "));
    Agrees(module, after.Module, 10, true, Edges(IrType.I32, 10).Concat(Random(IrType.I32, 126, 10_000)));
  }

  [Test]
  public void ATargetThatPrefersTheRemainderFromTheQuotient_KeepsIt() {
    var after = Optimizer.Run(Fixture("Urem10"), Options(new RuleTarget(true)));
    Assert.That(after.Notes.Count(note => note.StartsWith("O0126 applied", StringComparison.Ordinal)), Is.EqualTo(1));
    Assert.That(IrTree.Nodes(after.Module.Functions[0].Body).OfType<IrBinary>().Where(node => node.Op == IrBinaryOp.Remainder), Is.Empty);
  }

  [TestCase("public static uint F(uint x, uint d) => x - (x / d) * d;")]
  [TestCase("public static uint F(uint x, uint y) => x - (y / 10u) * 10u;")]
  [TestCase("public static ulong F(uint x) => x - (ulong)(x / 10u) * 10ul;")]
  [TestCase("public static uint F(uint x) => x - x * 10u;")]
  [TestCase("public static uint F(uint x) => (uint)(((ulong)x * 1234567UL) >> 35);")]
  [TestCase("public static uint F(uint x) => (uint)(((ulong)x * 0xCCCCCCCDUL) >> 32);")]
  [TestCase("public static uint F(uint x) => x * 3u >> 1;")]
  [TestCase("public static uint F(uint x, ulong m) => (uint)(((ulong)x * m) >> 35);")]
  [TestCase("public static int F(int x) => (x * 0x6667) >> 18;")]
  public void UnrelatedExpressions_AreSilent(string method) {
    var after = Optimizer.Run(Source(method), Options());
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Is.Empty);
  }

  [Test]
  public void AnOperandThatMayTrap_IsRefused() {
    var after = Optimizer.Run(Source("public static uint F(uint[] a) => (uint)(((ulong)a[0] * 0xCCCCCCCDUL) >> 35);"), Options());
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0126 not applied: operand may trap or have effects"));
  }

  [Test]
  public void ARemainderOperandThatMayTrap_IsRefused() {
    var after = Optimizer.Run(Source("public static uint F(uint[] a) { uint q = a[0] / 10u; return a[0] - q * 10u; }"), Options());
    Assert.That(after.Notes, Is.Empty, "a quotient of a load that may trap is not a stable definition");
    var trapping = new IrLocal("a", IrType.Array(IrType.U32));
    var load = new IrArrayLoad(new IrLocalRef(trapping), new IrConst(IrType.I32, 0));
    var q = new IrLocal("q", IrType.U32);
    var x = new IrLocal("x", IrType.U32);
    var difference = new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(x), new IrBinary(IrBinaryOp.Multiply, new IrBinary(IrBinaryOp.Divide, new IrLocalRef(x), new IrConst(IrType.U32, 10)), new IrConst(IrType.U32, 10)));
    var withLoad = new IrBinary(IrBinaryOp.Add, difference, load);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.U32, [x, trapping], [q], new([new IrReturn(withLoad)]))], []);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options());
    Assert.That(result.Notes, Does.Contain("O0126 applied: URem.u32 by 10 from the quotient [proof: validated-rule]"));
    var effect = new IrBinary(IrBinaryOp.Subtract, load, new IrBinary(IrBinaryOp.Multiply, new IrBinary(IrBinaryOp.Divide, load, new IrConst(IrType.U32, 10)), new IrConst(IrType.U32, 10)));
    var trappingModule = new IrModule("M", [new IrFunction("F", true, IrType.U32, [x, trapping], [q], new([new IrReturn(effect)]))], []);
    var refused = Optimizer.Run(trappingModule, Options());
    Assert.That(refused.Notes, Does.Contain("O0126 not applied: operand may trap or have effects"));
  }

  [TestCase(0x80000000L, 62, 0x80000000UL)]
  [TestCase(1L, 31, 0x80000000UL)]
  [TestCase(1L, 32, null)]
  public void TheDivisorRange_EndsAtHalfTheWidth(long multiplier, int shift, ulong? divisor) {
    var module = Source($"public static uint F(uint x) => (uint)(((ulong)x * {multiplier}UL) >> {shift});");
    var after = Optimizer.Run(module, Options());
    if (divisor is { } d) {
      Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
      Agrees(module, after.Module, d, false, Edges(IrType.U32, d).Concat(Random(IrType.U32, 126, 1000)));
    } else {
      Assert.That(after.Applications, Is.Empty);
    }
  }

  [Test]
  public void ADivisorAboveHalfTheRange_IsOutOfRange() {
    // M = 2^32 / (2^31 + 1) rounded: the derived divisor 2^31 + 1 leaves only quotients 0 and 1.
    var after = Optimizer.Run(Source("public static uint F(uint x) => (uint)(((ulong)x * 0xFFFFFFFEUL) >> 63);"), Options());
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0126 not applied: divisor out of range"));
  }

  [Test]
  public void ANarrowTrickInASignedCarrier_MayWrap() {
    var module = Source("public static int F(ushort x) => (x * 0xCCCD) >> 19;");
    var after = Optimizer.Run(module, Options());
    Assert.That(after.Notes, Does.Contain("O0126 not applied: multiplication may wrap"));
    Assert.That(IrInterpreter.Call(module, "F", [(ushort)65535]), Is.Not.EqualTo(6553), "the int product wraps");
  }

  [Test]
  public void ASignedNarrowOperand_IsRecoveredWithItsPromotedCorrection() {
    var module = Source("public static int F(short x) => ((x * 0x6667) >> 18) - (x >> 15);");
    var after = Optimizer.Run(module, Options());
    Assert.That(after.Notes, Does.Contain("O0126 applied: SDiv.i32 by 10 (M=0x6667, shift=18, e=6) from widened-multiply [proof: validated-rule]"));
    for (var x = short.MinValue; x < short.MaxValue; ++x)
      Assert.That(IrInterpreter.Call(after.Module, "F", [x]), Is.EqualTo(x / 10));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Fixture("Udiv10");
    foreach (var options in new[] { Options() with { Disabled = RuleTable.Default.Ids.ToHashSet() }, Options() with { Level = OptimizationLevel.None } }) {
      var result = Optimizer.Run(module, options);
      Assert.That(result.Applications, Is.Empty);
      Assert.That(result.Notes, Is.Empty);
      Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    }
  }

  [Test]
  public void TheCertificate_NeedsNoSolver() {
    var after = Optimizer.Run(Fixture("Udiv10"), Options() with { Prover = null });
    Assert.That(after.Applications, Has.Count.EqualTo(1));
  }

  /// <summary>
  /// The validation of the rule at 16 bits: for every divisor up to 300, then a geometric selection up to 32768, every
  /// shift and the rounded-down and rounded-up multiplier of <c>(x16 * M) &gt;&gt; k</c>. The rule must rewrite exactly
  /// those that divide every one of the 65,536 inputs by the divisor nearest to <c>2^k / M</c> - the certificate is
  /// necessary and sufficient.
  /// </summary>
  [Test, Category("Slow")]
  public void EveryUnsigned16BitReciprocal_IsAcceptedExactlyWhenItDivides() {
    var x = new IrLocal("x", IrType.U16);
    var (accepted, checkedCount) = (0, 0);
    foreach (var d in Divisors(32768))
      for (var k = 16; k <= 33; ++k) {
        var exact = (1L << k) / d;
        foreach (var multiplier in new[] { exact, exact + 1 }) {
          var derived = ((2L << k) + multiplier) / (2 * multiplier);
          var divides = derived is >= 2 and <= 32768;
          for (var value = 0L; value < 65536 && divides; ++value)
            divides = (multiplier * value) >> k == value / derived;

          var product = new IrBinary(IrBinaryOp.Multiply, new IrConvert(IrType.U64, new IrLocalRef(x)), new IrConst(IrType.U64, multiplier));
          var module = new IrModule("V", [new IrFunction("F", true, IrType.U64, [x], [], new([new IrReturn(new IrBinary(IrBinaryOp.ShiftRight, product, new IrConst(IrType.I32, k)))]))], []);
          var applied = Optimizer.Run(module, Options()).Applications.Count == 1;
          Assert.That(applied, Is.EqualTo(divides), $"d={d}, k={k}, M={multiplier}");
          ++checkedCount;
          accepted += applied ? 1 : 0;
        }
      }

    TestContext.Out.WriteLine($"{checkedCount} unsigned (d, k, M) cases at 16 bits, {accepted} accepted, each against all 65,536 inputs.");
  }

  private static IEnumerable<long> Divisors(long last) {
    for (var d = 2L; d <= 300; ++d)
      yield return d;
    for (var d = 301L; d <= last; d = d * 3 / 2)
      yield return d;
    yield return last - 1;
    yield return last;
  }

  /// <summary>
  /// The signed validation at 16 bits: <c>((x16 * M) &gt;&gt; k) - (x16 &gt;&gt; 15)</c> with the rounded-up multiplier. Every
  /// accepted case truncates on all 65,536 inputs, and every case that truncates with the signed certificate's bound holds is accepted.
  /// </summary>
  [Test, Category("Slow")]
  public void EverySigned16BitReciprocal_IsAcceptedOnlyWhenItTruncates() {
    var x = new IrLocal("x", IrType.I16);
    var (accepted, checkedCount) = (0, 0);
    foreach (var d in Divisors(16384))
      for (var k = 15; k <= 31; ++k) {
        var multiplier = ((1L << k) + d - 1) / d;
        if (multiplier >= 1 << 15)
          continue;

        var value = new IrConvert(IrType.I32, new IrLocalRef(x));
        var shifted = new IrBinary(IrBinaryOp.ShiftRight, new IrBinary(IrBinaryOp.Multiply, value, new IrConst(IrType.I32, multiplier)), new IrConst(IrType.I32, k));
        var corrected = new IrBinary(IrBinaryOp.Subtract, shifted, new IrBinary(IrBinaryOp.ShiftRight, value, new IrConst(IrType.I32, 15)));
        var module = new IrModule("V", [new IrFunction("F", true, IrType.I32, [x], [], new([new IrReturn(corrected)]))], []);
        var applied = Optimizer.Run(module, Options()).Applications.Count == 1;
        // The divisor the multiplier is nearest to, which the rule derives as well.
        var derived = ((2L << k) + multiplier) / (2 * multiplier);
        var truncates = derived is >= 2 and <= 1 << 15;
        for (long input = short.MinValue; input <= short.MaxValue && truncates; ++input)
          truncates = ((multiplier * input) >> k) - (input >> 15) == input / derived;
        if (applied)
          Assert.That(truncates, Is.True, $"accepted but wrong: d={derived}, k={k}, M={multiplier}");

        var excess = multiplier * derived - (1L << k);
        if (truncates && excess > 0 && excess << 15 <= 1L << k)
          Assert.That(applied, Is.True, $"refused although certified: d={derived}, k={k}, M={multiplier}");

        ++checkedCount;
        accepted += applied ? 1 : 0;
      }

    Assert.That(accepted, Is.GreaterThan(100));
    TestContext.Out.WriteLine($"{checkedCount} signed (d, k, M) cases at 16 bits, {accepted} accepted, each truncating on all 65,536 inputs.");
  }

  [TestCase("Udiv10Narrow")]
  public void The16BitFixture_IsProvedBySmtAndExhaustively(string name) {
    var before = Fixture(name);
    var after = Optimizer.Run(before, Options());
    var function = before.Functions[0];
    var candidate = ((IrReturn)function.Body.Statements.Last()).Value!;
    var replacement = ((IrReturn)after.Module.Functions[0].Body.Statements.Last()).Value!;
    var obligation = SmtRecognitionProver.CreateObligation("O0126", candidate, replacement, new(function, RuleTarget.Neutral));
    Assert.That(obligation, Is.Not.Null);
    Assert.That(new ExhaustiveProver().Prove(obligation!, new(MaxInputBits: 16)), Is.InstanceOf<ProofResult.Proved>());
  }

  [Test, Category("Slow")]
  public void The16BitFixture_IsProvedByTheSolverGivenTime() {
    var before = Fixture("Udiv10Narrow");
    var after = Optimizer.Run(before, Options());
    var function = before.Functions[0];
    var candidate = ((IrReturn)function.Body.Statements.Last()).Value!;
    var replacement = ((IrReturn)after.Module.Functions[0].Body.Statements.Last()).Value!;
    var prover = new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.FromMinutes(5)) };
    var proof = prover.Prove("O0126", candidate, replacement, new(function, RuleTarget.Neutral));
    TestContext.Out.WriteLine($"Z3 {new Z3Solver().Version}: {proof}");
    Assert.That(proof, Is.EqualTo(new RecognitionProof(true, "smt-bitvector")));
  }
}
