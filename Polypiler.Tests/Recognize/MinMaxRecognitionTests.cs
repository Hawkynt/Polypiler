using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>Minimum and maximum on the actual promoted tree IR, signed and unsigned, including the extremes.</summary>
[TestFixture]
public sealed class MinMaxRecognitionTests {

  /// <summary>Each form and whether it is the minimum.</summary>
  private static readonly (string Form, bool Minimum)[] _forms = [
    ("select-lt", true), ("select-le", true), ("select-gt", false), ("select-ge", false), ("select-mirror", true), ("select-branches", false),
    ("mask", true), ("mask-max", false), ("mask-ones", true), ("mask-zero-sub", true), ("mask-commuted", true), ("mask-and-commuted", false)];

  private static readonly int[] _widths = [8, 16, 32, 64];

  private static IEnumerable<TestCaseData> Forms() {
    foreach (var width in _widths)
      foreach (var signed in new[] { true, false })
        foreach (var (form, minimum) in _forms)
          yield return new TestCaseData(width, signed, form, minimum);

    foreach (var width in new[] { 8, 16 })
      foreach (var signed in new[] { true, false }) {
        yield return new TestCaseData(width, signed, "subsign", true);
        yield return new TestCaseData(width, signed, "subsign-max", false);
        yield return new TestCaseData(width, signed, "subsign-commuted", true);
      }
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
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0120").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0120", "minmax.cs");
    var module = PrototypeFixture.Import("minmax.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  /// <summary>One spelling at a width and signedness, promoted as the C# frontend promotes it, or one of its near misses.</summary>
  internal static IrModule Module(int width, bool signed, string form, string? nearMiss = null) {
    var type = Type(width, signed);
    var carrier = width < 32 ? IrType.I32 : type;
    var x = new IrLocal("x", type);
    var y = new IrLocal("y", type);
    var k = new IrLocal("k", type);
    IrExpr Extend(IrExpr value) => value.Type == carrier ? value : new IrConvert(carrier, value);
    IrExpr a = Extend(new IrLocalRef(x));
    IrExpr b = Extend(new IrLocalRef(y));
    IrExpr Constant(long value) => new IrConst(carrier, IrOperators.Wrap(carrier, value));
    var statements = new List<IrStmt>();
    var locals = new List<IrLocal>();
    IrExpr result;
    if (form.StartsWith("select", StringComparison.Ordinal)) {
      IrExpr first = new IrLocalRef(x);
      IrExpr second = new IrLocalRef(y);
      result = form switch {
        "select-lt" => new IrConditional(new IrBinary(IrBinaryOp.Less, a, b), first, second),
        "select-le" => new IrConditional(new IrBinary(IrBinaryOp.LessOrEqual, a, b), first, second),
        "select-gt" => new IrConditional(new IrBinary(IrBinaryOp.Greater, a, b), first, second),
        "select-ge" => new IrConditional(new IrBinary(IrBinaryOp.GreaterOrEqual, a, b), first, second),
        "select-mirror" => new IrConditional(new IrBinary(IrBinaryOp.Greater, b, a), first, second),
        _ => new IrConditional(new IrBinary(IrBinaryOp.Less, a, b), second, first)
      };
    } else if (form.StartsWith("mask", StringComparison.Ordinal)) {
      IrExpr compared = nearMiss == "other" ? Extend(new IrLocalRef(k)) : b;
      var condition = new IrBinary(IrBinaryOp.Less, a, compared);
      var flag = new IrConditional(condition, Constant(1), Constant(0));
      IrExpr mask = form switch {
        "mask-ones" => new IrConditional(condition, Constant(-1), Constant(0)),
        // The IR negates only signed values; an unsigned mask is 0 - flag, as C# writes it.
        _ when form == "mask-zero-sub" || !carrier.IsSigned => new IrBinary(IrBinaryOp.Subtract, Constant(0), flag),
        _ => new IrUnary(IrUnaryOp.Negate, flag)
      };
      var kept = form == "mask-max" || form == "mask-and-commuted" ? a : b;
      IrExpr difference = form == "mask-commuted" ? new IrBinary(IrBinaryOp.Xor, b, a) : new IrBinary(IrBinaryOp.Xor, a, b);
      IrExpr masked = form == "mask-and-commuted" ? new IrBinary(IrBinaryOp.And, mask, difference) : new IrBinary(IrBinaryOp.And, difference, mask);
      result = form == "mask-commuted" ? new IrBinary(IrBinaryOp.Xor, masked, kept) : new IrBinary(IrBinaryOp.Xor, kept, masked);
    } else {
      // A checked difference may trap, so no local holds it as a definition: it is written where it is used.
      var d = new IrLocal("d", carrier);
      IrExpr Difference() => nearMiss == "checked" ? new IrBinary(IrBinaryOp.SubtractChecked, a, b) : new IrLocalRef(d);
      if (nearMiss != "checked") {
        statements.Add(new IrDeclare(d, new IrBinary(IrBinaryOp.Subtract, a, b)));
        locals.Add(d);
      }

      IrExpr shift = nearMiss == "logical"
        ? new IrConvert(carrier, new IrBinary(IrBinaryOp.ShiftRight, new IrConvert(carrier == IrType.I64 ? IrType.U64 : IrType.U32, Difference()), new IrConst(IrType.I32, carrier.Bits - 1)))
        : new IrBinary(IrBinaryOp.ShiftRight, Difference(), new IrConst(IrType.I32, carrier.Bits - (nearMiss == "short" ? 2 : 1)));
      var masked = new IrBinary(IrBinaryOp.And, Difference(), shift);
      result = form switch {
        "subsign-max" => new IrBinary(IrBinaryOp.Subtract, a, masked),
        "subsign-commuted" => new IrBinary(IrBinaryOp.Add, masked, b),
        _ => new IrBinary(IrBinaryOp.Add, b, masked)
      };
    }

    var parameters = nearMiss == "other" ? new[] { x, y, k } : new[] { x, y };
    statements.Add(new IrReturn(result));
    return new("MinMax", [new IrFunction("F", true, result.Type, parameters, locals, new(statements))], []);
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

  private static Int128 Number(object value) => value is ulong unsigned ? unsigned : Convert.ToInt64(value, CultureInfo.InvariantCulture);

  /// <summary>Both versions agree with the BCL's minimum or maximum of the two arguments' values.</summary>
  private static void Agrees(IrModule before, IrModule after, bool minimum, ulong first, ulong second) {
    var function = before.Functions[0];
    object[] arguments = [Argument(function.Parameters[0].Type, first), Argument(function.Parameters[1].Type, second)];
    var (a, b) = (Number(arguments[0]), Number(arguments[1]));
    var expected = minimum ? Int128.Min(a, b) : Int128.Max(a, b);
    Assert.That(Number(IrInterpreter.Call(before, function.Name, arguments)), Is.EqualTo(expected), $"original vs BCL for {a}, {b}");
    Assert.That(Number(IrInterpreter.Call(after, function.Name, arguments)), Is.EqualTo(expected), $"optimized vs BCL for {a}, {b}");
  }

  private static ulong[] Edges(int width, bool signed) {
    var top = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    var half = 1UL << (width - 1);
    return [0, 1, 2, 7, top, top - 2, half - 1, half, signed ? half + 1 : top - 1];
  }

  [TestCaseSource(nameof(Forms))]
  public void EveryForm_IsProvedAtItsActualWidthAndSignedness(int width, bool signed, string form, bool minimum) {
    var module = Module(width, signed, form);
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the input must be legal promoted IR");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    var intrinsic = IrTree.Nodes(result.Module.Functions[0].Body).OfType<IrIntrinsic>().Single();
    Assert.That(intrinsic.Op, Is.EqualTo(minimum ? IrIntrinsicOp.Min : IrIntrinsicOp.Max));
    var kind = form.StartsWith("select", StringComparison.Ordinal) ? "compare-select" : form.StartsWith("mask", StringComparison.Ordinal) ? "mask-select" : "subtract-sign";
    var name = (intrinsic.Arguments[0].Type.IsSigned ? "S" : "U") + intrinsic.Op;
    Assert.That(result.Notes, Does.Contain($"O0120 applied: {name}.{intrinsic.Type} from {kind} [proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var first in Edges(width, signed))
      foreach (var second in Edges(width, signed))
        Agrees(module, result.Module, minimum, first, second);
  }

  [TestCaseSource(nameof(EightBitForms))]
  public void EightBits_AlsoDischargeAnIndependentExhaustiveObligation(int width, bool signed, string form, bool minimum) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    var function = before.Functions[0];
    var candidate = ((IrReturn)function.Body.Statements.Last()).Value!;
    var replacement = ((IrReturn)after.Module.Functions[0].Body.Statements.Last()).Value!;
    var obligation = SmtRecognitionProver.CreateObligation("O0120", candidate, replacement, new(function, RuleTarget.Neutral));
    Assert.That(obligation, Is.Not.Null);
    Assert.That(obligation!.Inputs, Has.Count.EqualTo(2));
    Assert.That(new ExhaustiveProver().Prove(obligation, new(MaxInputBits: 16)), Is.InstanceOf<ProofResult.Proved>());
  }

  [TestCaseSource(nameof(EightBitForms))]
  public void EveryEightBitPair_AgreesWithTheIndependentExtreme(int width, bool signed, string form, bool minimum) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    for (var first = 0UL; first < 256; ++first)
      for (var second = 0UL; second < 256; ++second)
        Agrees(before, after.Module, minimum, first, second);
  }

  [TestCase(32, true, "subsign", null, "subtraction may overflow")]
  [TestCase(64, true, "subsign", null, "subtraction may overflow")]
  [TestCase(64, true, "subsign-max", null, "subtraction may overflow")]
  [TestCase(16, true, "subsign", "logical", "not equivalent to Min or Max")]
  [TestCase(16, true, "subsign", "short", "not equivalent to Min or Max")]
  [TestCase(16, true, "subsign", "checked", "checked arithmetic")]
  [TestCase(32, true, "mask", "other", "mask is not derived from the compared operands")]
  [TestCase(32, false, "mask-max", "other", "mask is not derived from the compared operands")]
  public void RegisteredCounterexamples_AreRefused(int width, bool signed, string form, string? nearMiss, string reason) {
    var module = Module(width, signed, form, nearMiss);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0120 not applied: " + reason));
  }

  [Test]
  public void TheOverflowingSubtraction_GivesTheWrongMinimum() {
    var module = Module(32, true, "subsign");
    Assert.That(IrInterpreter.Call(module, "F", [int.MaxValue, -1]), Is.EqualTo(int.MaxValue), "the idiom itself is not a minimum here");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(IrInterpreter.Call(result.Module, "F", [int.MaxValue, -1]), Is.EqualTo(int.MaxValue));
  }

  [Test]
  public void TheComparisonsSignedness_DecidesTheOperation() {
    var module = PrototypeFixture.Import("Signedness.cs", PrototypeFixture.Wrap("public static int F(int x, int y) => y ^ ((x ^ y) & -((uint)x < (uint)y ? 1 : 0));"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0120 not applied: mask is not derived from the compared operands"));
    Assert.That(IrInterpreter.Call(result.Module, "F", [5, -3]), Is.EqualTo(5));
  }

  [TestCase("public static int F(int x, int y, int z) => x ^ ((y ^ z) & -(y < z ? 1 : 0));")]
  [TestCase("public static int F(int x, int y, int z) => y + (x & (z >> 31));")]
  [TestCase("public static int F(int x, int y) { int d = x - y; return x + (d & (d >> 31)); }")]
  [TestCase("public static int F(int x, int y) { int d = x - y; return y - (d & (d >> 31)); }")]
  [TestCase("public static int F(int x, int y, int m) => y ^ ((x ^ y) & m);")]
  [TestCase("public static int F(int x, int y) => y ^ ((x ^ y) & (x < y ? 2 : 0));")]
  [TestCase("public static int F(int x, int y) => x == y ? x : y;")]
  [TestCase("public static int F(int x, int y, int z) => x < y ? x : z;")]
  [TestCase("public static double F(double x, double y) => x < y ? x : y;")]
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
    var module = Module(width, true, "mask");
    var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0120 not applied: " + reason));
  }

  [Test]
  public void AnUnknownProof_IsARefusalAndNotAnOverflow() {
    var module = Module(16, true, "subsign");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0120 not applied: proof unknown"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Module(32, true, "mask");
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
  public void AnOperandThatMayTrap_IsNotEvaluatedFewerTimes() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var y = new IrLocal("y", IrType.I32);
    var load = new IrArrayLoad(new IrLocalRef(array), new IrConst(IrType.I32, 0));
    var b = new IrLocalRef(y);
    var mask = new IrUnary(IrUnaryOp.Negate, new IrConditional(new IrBinary(IrBinaryOp.Less, load, b), new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0)));
    var expression = new IrBinary(IrBinaryOp.Xor, b, new IrBinary(IrBinaryOp.And, new IrBinary(IrBinaryOp.Xor, load, b), mask));
    var module = new IrModule("Trapping", [new IrFunction("F", true, IrType.I32, [array, y], [], new([new IrReturn(expression)]))], []);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0120 not applied: operand may trap or have effects"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void ACheckedOuterSubtraction_IsRefused() {
    var module = PrototypeFixture.Import("Checked.cs", PrototypeFixture.Wrap("public static int F(short x, short y) { int d = x - y; return checked(x - (d & (d >> 31))); }"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0120 not applied: checked arithmetic"));
  }

  [TestCase("MinSubtractSignBounded", "SMin.i32 from subtract-sign")]
  [TestCase("MaxSubtractSignBounded", "SMax.i32 from subtract-sign")]
  [TestCase("MaxMaskUnsigned", "UMax.u32 from mask-select")]
  public void TheExecutableFixtures_AreProved(string function, string report) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(after.Notes, Does.Contain($"O0120 applied: {report} [proof: smt-bitvector]"));
    TestContext.Out.WriteLine("Before:\n" + IrText.Of(before) + "After:\n" + IrText.Of(after.Module) + string.Join("\n", after.Notes));
    TestContext.Out.WriteLine("Solver: " + new Z3Solver().Version);
  }

  [TestCase("MinSubtractSign", "subtraction may overflow")]
  [TestCase("OtherMask", "mask is not derived from the compared operands")]
  [TestCase("LogicalShift", "not equivalent to Min or Max")]
  [TestCase("Checked", "checked arithmetic")]
  public void TheExecutableCounterexamples_AreRefused(string function, string reason) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0120 not applied: " + reason));
  }

  [TestCaseSource(nameof(WideForms))]
  [Category("Slow")]
  public void TenThousandPairs_AgreeWithAnIndependentExtreme(int width, bool signed, string form, bool minimum) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(120);
    var bytes = new byte[16];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      Agrees(before, after.Module, minimum, BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
    }

    TestContext.Out.WriteLine($"O0120 {form}, {(signed ? "signed" : "unsigned")} {width} bits: seed 120, 10,000 pairs, zero mismatches.");
  }
}
