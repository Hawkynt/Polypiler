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

/// <summary>Wrapping magnitude on the actual promoted tree IR, including its smallest signed values.</summary>
[TestFixture]
public sealed class AbsRecognitionTests {

  private static readonly string[] _forms = ["xor-sub", "xor-sub-swapped", "add-xor", "add-xor-swapped", "add-xor-right", "add-xor-right-swapped",
    "lt-neg", "le-neg", "gt-neg", "ge-neg", "lt-sub", "le-sub", "gt-sub", "ge-sub",
    "lt-neg-mirror", "le-neg-mirror", "gt-neg-mirror", "ge-neg-mirror", "lt-sub-mirror", "le-sub-mirror", "gt-sub-mirror", "ge-sub-mirror"];

  private static IEnumerable<TestCaseData> Forms() {
    foreach (var width in new[] { 8, 16, 32, 64 })
      foreach (var form in _forms)
        yield return new TestCaseData(width, form);
  }

  private static IEnumerable<TestCaseData> WideForms => Forms().Where(data => data.Arguments[0] is int width && width != 8);

  private static IrType Signed(int width) => width switch { 8 => IrType.I8, 16 => IrType.I16, 32 => IrType.I32, _ => IrType.I64 };

  private sealed class InconclusiveProver : IRecognitionProver {
    public int Calls { get; private set; }
    public RecognitionProof Prove(string id, IrExpr before, IrExpr after, RuleContext context) {
      ++this.Calls;
      return new(false, "proof unknown");
    }
  }

  internal static OptimizerOptions Options(IRecognitionProver? prover) => RecognitionFixtures.Options with {
    Prover = prover,
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0121").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0121", "absolute.cs");
    var module = PrototypeFixture.Import("absolute.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  internal static IrModule Module(int width, string form, string? nearMiss = null) {
    var type = Signed(width);
    var carrier = width == 64 ? IrType.I64 : IrType.I32;
    var x = new IrLocal("x", type);
    var y = new IrLocal("y", type);
    IrExpr Extend(IrExpr value) => value.Type == carrier ? value : new IrConvert(carrier, value);
    IrExpr Cut(IrExpr value) => value.Type == type ? value : new IrConvert(type, value);
    IrExpr value = Extend(new IrLocalRef(x));
    var statements = new List<IrStmt>();
    var locals = new List<IrLocal>();
    IrExpr result;
    if (form.Contains("xor", StringComparison.Ordinal)) {
      var shifted = nearMiss == "other" ? Extend(new IrLocalRef(y)) : value;
      var mask = new IrLocal("m", type);
      var shift = new IrBinary(nearMiss == "logical" ? IrBinaryOp.ShiftRightLogical : IrBinaryOp.ShiftRight,
        shifted, new IrConst(IrType.I32, width - (nearMiss == "shift" ? 2 : 1)));
      statements.Add(new IrDeclare(mask, Cut(shift)));
      locals.Add(mask);
      var m = Extend(new IrLocalRef(mask));
      result = form switch {
        "xor-sub" => new IrBinary(nearMiss == "checked" ? IrBinaryOp.SubtractChecked : IrBinaryOp.Subtract, new IrBinary(IrBinaryOp.Xor, value, m), m),
        "xor-sub-swapped" => new IrBinary(IrBinaryOp.Subtract, new IrBinary(IrBinaryOp.Xor, m, value), m),
        "add-xor" => new IrBinary(IrBinaryOp.Xor, new IrBinary(IrBinaryOp.Add, value, m), m),
        "add-xor-swapped" => new IrBinary(IrBinaryOp.Xor, new IrBinary(IrBinaryOp.Add, m, value), m),
        "add-xor-right" => new IrBinary(IrBinaryOp.Xor, m, new IrBinary(IrBinaryOp.Add, value, m)),
        _ => new IrBinary(IrBinaryOp.Xor, m, new IrBinary(IrBinaryOp.Add, m, value))
      };
    } else {
      var op = form[..2] switch { "lt" => IrBinaryOp.Less, "le" => IrBinaryOp.LessOrEqual, "gt" => IrBinaryOp.Greater, _ => IrBinaryOp.GreaterOrEqual };
      var negative = op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual;
      IrExpr compared = nearMiss == "unsigned" ? new IrConvert(width == 64 ? IrType.U64 : IrType.U32, value) : value;
      var zero = new IrConst(compared.Type, 0);
      var condition = form.EndsWith("-mirror", StringComparison.Ordinal)
        ? new IrBinary(op, zero, compared) : new IrBinary(op, compared, zero);
      if (form.EndsWith("-mirror", StringComparison.Ordinal))
        negative = !negative;

      IrExpr negated = form.Contains("-sub", StringComparison.Ordinal)
        ? new IrBinary(nearMiss == "checked" ? IrBinaryOp.SubtractChecked : IrBinaryOp.Subtract, new IrConst(carrier, 0), value)
        : new IrUnary(nearMiss == "checked" ? IrUnaryOp.NegateChecked : IrUnaryOp.Negate, value);
      if (nearMiss == "swapped")
        negative = !negative;

      result = negative ? new IrConditional(condition, negated, value) : new IrConditional(condition, value, negated);
    }

    statements.Add(new IrReturn(Cut(result)));
    return new("Magnitude", [new IrFunction("F", true, type, nearMiss == "other" ? [x, y] : [x], locals, new(statements))], []);
  }

  [TestCaseSource(nameof(Forms))]
  public void EveryForm_IsProvedAtItsActualWidth(int width, string form) {
    var module = Module(width, form);
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the input must be legal promoted IR");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    var returned = ((IrReturn)result.Module.Functions[0].Body.Statements.Last()).Value;
    Assert.That(returned, Is.TypeOf<IrIntrinsic>());
    Assert.That(((IrIntrinsic)returned!).Op, Is.EqualTo(IrIntrinsicOp.AbsWrap));
    Assert.That(returned.Type, Is.EqualTo(Signed(width)));
    Assert.That(result.Notes.Any(note => note.Contains($"Abs.i{width}[wrap]", StringComparison.Ordinal) && note.EndsWith("[proof: smt-bitvector]", StringComparison.Ordinal)), Is.True);
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var input in new[] { 0UL, 1UL, ulong.MaxValue, (1UL << (width - 1)) - 1, 1UL << (width - 1) })
      Agrees(module, result.Module, width, input);
  }

  [TestCaseSource(nameof(_forms))]
  public void EightBits_AlsoDischargeAnIndependentExhaustiveObligation(string form) {
    var module = Module(8, form);
    var function = module.Functions[0];
    var before = ((IrReturn)function.Body.Statements.Last()).Value!;
    var after = new IrIntrinsic(IrIntrinsicOp.AbsWrap, IrType.I8, [new IrLocalRef(function.Parameters[0])]);
    var obligation = SmtRecognitionProver.CreateObligation("O0121", before, after, new(function, RuleTarget.Neutral));
    Assert.That(obligation, Is.Not.Null);
    Assert.That(new ExhaustiveProver().Prove(obligation!, new(MaxInputBits: 8)), Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 256)));
  }

  [TestCase("xor-sub", "logical", "logical right shift")]
  [TestCase("xor-sub", "shift", "shift amount is not width-1")]
  [TestCase("xor-sub", "other", "mask operand differs")]
  [TestCase("xor-sub", "checked", "checked arithmetic")]
  [TestCase("lt-neg", "checked", "checked arithmetic")]
  [TestCase("lt-sub", "checked", "checked arithmetic")]
  [TestCase("lt-neg", "unsigned", "unsigned comparison")]
  public void RegisteredCounterexamples_AreRefused(string form, string nearMiss, string reason) {
    var module = Module(32, form, nearMiss);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0121 not applied: " + reason));
  }

  [TestCase(8)]
  [TestCase(16)]
  [TestCase(32)]
  [TestCase(64)]
  public void AReversedSelection_IsNotMagnitude(int width) {
    var module = Module(width, "lt-neg", "swapped");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [TestCase(8, "missing", "proof unavailable")]
  [TestCase(16, "missing", "proof unavailable")]
  [TestCase(32, "missing", "proof unavailable")]
  [TestCase(64, "missing", "proof unavailable")]
  [TestCase(8, "timeout", "proof timeout")]
  [TestCase(16, "timeout", "proof timeout")]
  [TestCase(32, "timeout", "proof timeout")]
  [TestCase(64, "timeout", "proof timeout")]
  public void AnUnprovedCandidate_IsUntouched(int width, string mode, string reason) {
    var module = Module(width, "xor-sub");
    var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0121 not applied: " + reason));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Module(32, "xor-sub");
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
  public void AnUnknownProof_IsARefusal() {
    var module = Module(32, "xor-sub");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0121 not applied: proof unknown"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void AValueThatMayTrap_IsNotEvaluatedFewerTimes() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var load = new IrArrayLoad(new IrLocalRef(array), new IrConst(IrType.I32, 0));
    var condition = new IrBinary(IrBinaryOp.Less, load, new IrConst(IrType.I32, 0));
    var choice = new IrConditional(condition, new IrUnary(IrUnaryOp.Negate, load), load);
    var module = new IrModule("Trapping", [new IrFunction("F", true, IrType.I32, [array], [], new([new IrReturn(choice)]))], []);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0121 not applied: operand may trap or have effects"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.Catch<IrException>(() => IrInterpreter.Call(result.Module, "F", [Array.Empty<int>()]));
  }

  [Test]
  public void ARepeatedEffect_IsNotDropped() {
    var module = PrototypeFixture.Import("Effects.cs", PrototypeFixture.Wrap("public static int Read(int[] a) { a[0]++; return a[0]; } public static int F(int[] a) => Read(a) < 0 ? -Read(a) : Read(a);"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    var values = new[] { -3 };
    Assert.That(IrInterpreter.Call(result.Module, "F", [values]), Is.EqualTo(1));
    Assert.That(values, Is.EqualTo(new[] { -1 }));
  }

  [TestCase("public static int F(int x) { int m = x >> 31; x += 2; return (x ^ m) - m; }")]
  [TestCase("public static int F(int x) { byte m = (byte)(x >> 31); return (x ^ m) - m; }")]
  public void AnUnstableOrZeroExtendedMask_IsNotTheCurrentSign(string method) {
    var module = PrototypeFixture.Import("Mask.cs", PrototypeFixture.Wrap(method));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0121 not applied: mask operand differs"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void BothMaskUses_MustHaveTheSameDefinition() {
    var module = PrototypeFixture.Import("Masks.cs", PrototypeFixture.Wrap("public static int F(int x, int y) { int m = x >> 31; int n = y >> 31; return (x ^ m) - n; }"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0121 not applied: mask operand differs"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void TruncatingAWideMagnitude_IsNotMagnitudeOfTheTruncatedInput() {
    var module = PrototypeFixture.Import("Truncate.cs", PrototypeFixture.Wrap("public static sbyte F(int x) => (sbyte)(x < 0 ? -x : x);"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    var absolutes = IrTree.Nodes(result.Module.Functions[0].Body).OfType<IrIntrinsic>().Where(node => node.Op == IrIntrinsicOp.AbsWrap).ToArray();
    Assert.That(absolutes, Has.Length.EqualTo(1));
    Assert.That(absolutes[0].Type, Is.EqualTo(IrType.I32));
    Assert.That(IrInterpreter.Call(result.Module, "F", [129]), Is.EqualTo((sbyte)-127));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  [Test]
  public void TheExecutableNarrowFixture_UsesANarrowIntrinsic() {
    var before = Fixture("Narrow");
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    var intrinsic = IrTree.Nodes(after.Module.Functions[0].Body).OfType<IrIntrinsic>().Single(node => node.Op == IrIntrinsicOp.AbsWrap);
    Assert.That(intrinsic.Type, Is.EqualTo(IrType.I8));
    for (var bits = 0UL; bits < 256; ++bits)
      Agrees(before, after.Module, 8, bits);

    TestContext.Out.WriteLine(string.Join("\n", after.Notes));
    TestContext.Out.WriteLine("Solver: " + new Z3Solver().Version);
  }

  [Test, Category("Slow")]
  public void ThePinnedPixelDifferenceContract_AgreesForEveryBytePair() {
    var before = Fixture("MediaDroydDelta");
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(IrVerifier.Verify(after.Module), Is.Empty);
    for (var previous = 0; previous < 256; ++previous)
      for (var current = 0; current < 256; ++current) {
        var expected = Math.Abs(previous - current);
        Assert.That(IrInterpreter.Call(before, "MediaDroydDelta", [previous, current]), Is.EqualTo(expected));
        Assert.That(IrInterpreter.Call(after.Module, "MediaDroydDelta", [previous, current]), Is.EqualTo(expected));
      }

    TestContext.Out.WriteLine("MediaDroyd 7f8b6b995cbe54a6afb290a900748393bf01e272 scalar pixel contract: 65,536 pairs, zero mismatches.");
    TestContext.Out.WriteLine("Before:\n" + IrText.Of(before) + "After:\n" + IrText.Of(after.Module) + string.Join("\n", after.Notes));
  }

  private static object Argument(int width, ulong bits) => width switch {
    8 => (object)unchecked((sbyte)bits),
    16 => (object)unchecked((short)bits),
    32 => (object)unchecked((int)bits),
    _ => unchecked((long)bits)
  };

  [TestCase(15)]
  [TestCase(16)]
  [TestCase(17)]
  [TestCase(32)]
  public void SignedCastChains_PreserveTheSignMask(int length) {
    var x = new IrLocal("x", IrType.I32);
    var value = new IrLocalRef(x);
    IrExpr mask = new IrBinary(IrBinaryOp.ShiftRight, value, new IrConst(IrType.I32, 31));
    for (var index = 0; index < length; ++index)
      mask = new IrConvert(IrType.I32, mask);

    var expression = new IrBinary(IrBinaryOp.Subtract, new IrBinary(IrBinaryOp.Xor, value, mask), mask);
    var module = new IrModule("CastChain", [new IrFunction("F", true, IrType.I32, [x], [], new([new IrReturn(expression)]))], []);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(Optimizer.Run(module, Options(new SmtRecognitionProver())).Applications, Has.Count.EqualTo(1));
  }

  [Test]
  public void ADefectiveCyclicMaskDefinition_IsRefusedWithoutFollowingItForever() {
    var x = new IrLocal("x", IrType.I32);
    var m = new IrLocal("m", IrType.I32);
    var value = new IrLocalRef(x);
    var mask = new IrLocalRef(m);
    var expression = new IrBinary(IrBinaryOp.Subtract, new IrBinary(IrBinaryOp.Xor, value, mask), mask);
    var function = new IrFunction("F", true, IrType.I32, [x], [m], new([new IrDeclare(m, mask), new IrReturn(expression)]));
    Assert.That(RuleTable.Default.Find("O0121")!.TryApply(expression, new(function, RuleTarget.Neutral), out _), Is.False);
  }

  private static void Agrees(IrModule before, IrModule after, int width, ulong bits) {
    var argument = Argument(width, bits);
    var modulus = BigInteger.One << width;
    var magnitude = BigInteger.Abs(new BigInteger(Convert.ToInt64(argument, CultureInfo.InvariantCulture))) % modulus;
    var expected = magnitude >= modulus / 2 ? magnitude - modulus : magnitude;
    var function = before.Functions[0].Name;
    Assert.That(Convert.ToInt64(IrInterpreter.Call(before, function, [argument]), CultureInfo.InvariantCulture), Is.EqualTo((long)expected), "original vs independent BCL magnitude");
    Assert.That(Convert.ToInt64(IrInterpreter.Call(after, function, [argument]), CultureInfo.InvariantCulture), Is.EqualTo((long)expected), "optimized vs independent BCL magnitude");
  }

  [TestCaseSource(nameof(_forms))]
  public void EveryEightBitInput_AgreesWithTheIndependentMagnitude(string form) {
    var before = Module(8, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    for (var input = 0UL; input < 256; ++input)
      Agrees(before, after.Module, 8, input);
  }

  [TestCaseSource(nameof(WideForms))]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithAnIndependentMagnitude(int width, string form) {
    var before = Module(width, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(121);
    var bytes = new byte[8];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      Agrees(before, after.Module, width, BitConverter.ToUInt64(bytes));
    }

    TestContext.Out.WriteLine($"O0121 {form}, {width} bits: seed 121, 10,000 inputs, zero mismatches.");
  }
}
