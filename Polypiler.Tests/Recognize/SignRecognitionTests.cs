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

/// <summary>The three-way sign on the actual promoted tree IR, including the smallest signed values.</summary>
[TestFixture]
public sealed class SignRecognitionTests {

  private static readonly string[] _forms = ["difference", "difference-mirror", "difference-select", "shift-or", "shift-or-swapped", "shift-or-subtract", "difference-sum",
    "select-gt-lt", "select-lt-gt", "select-eq-gt", "select-ne-ge", "select-ge-nested", "select-mirror"];

  private static IEnumerable<TestCaseData> Forms() {
    foreach (var width in new[] { 8, 16, 32, 64 })
      foreach (var form in _forms)
        yield return new TestCaseData(width, form);

    // A negation written as ~x + 1 is decided on weighted bits, which see it only at the carrier's own width.
    yield return new TestCaseData(32, "shift-or-complement");
    yield return new TestCaseData(64, "shift-or-complement");
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

  private static OptimizerOptions Options(IRecognitionProver? prover) => RecognitionFixtures.Options with {
    Prover = prover,
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0122").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0122", "sign.cs");
    var module = PrototypeFixture.Import("sign.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  /// <summary>One spelling of the sign at a width, as the C# frontend promotes it, or one of its near misses.</summary>
  internal static IrModule Module(int width, string form, string? nearMiss = null) {
    var type = Signed(width);
    var carrier = width == 64 ? IrType.I64 : IrType.I32;
    var unsigned = width == 64 ? IrType.U64 : IrType.U32;
    var x = new IrLocal("x", type);
    var y = new IrLocal("y", type);
    IrExpr Extend(IrExpr value) => value.Type == carrier ? value : new IrConvert(carrier, value);
    IrExpr value = Extend(new IrLocalRef(x));
    IrExpr other = Extend(new IrLocalRef(y));
    IrExpr compared = nearMiss == "unsigned" ? new IrConvert(unsigned, value) : value;
    IrExpr Zero() => new IrConst(compared.Type, 0);
    IrExpr Int(long number) => new IrConst(IrType.I32, number);
    IrExpr Compare(IrBinaryOp op, IrExpr operand) => new IrBinary(op, operand, new IrConst(operand.Type, 0));
    IrExpr Flag(IrBinaryOp op, IrExpr operand) => new IrConditional(Compare(op, operand), Int(1), Int(0));

    IrExpr result;
    if (form.StartsWith("shift-or", StringComparison.Ordinal)) {
      var top = new IrConst(IrType.I32, carrier.Bits - (nearMiss == "shift" ? 2 : 1));
      IrExpr sign = new IrBinary(IrBinaryOp.ShiftRight, value, top);
      IrExpr negated = form switch {
        "shift-or-subtract" => new IrBinary(IrBinaryOp.Subtract, new IrConst(carrier, 0), value),
        "shift-or-complement" => new IrBinary(IrBinaryOp.Add, new IrUnary(IrUnaryOp.BitNot, value), new IrConst(carrier, 1)),
        _ => new IrUnary(IrUnaryOp.Negate, value)
      };
      IrExpr logical = nearMiss switch {
        "one" => new IrConst(carrier, 1),
        "arithmetic" => new IrBinary(IrBinaryOp.ShiftRight, negated, new IrConst(IrType.I32, carrier.Bits - 1)),
        "not-negated" => new IrConvert(carrier, new IrBinary(IrBinaryOp.ShiftRight, new IrConvert(unsigned, value), new IrConst(IrType.I32, carrier.Bits - 1))),
        _ => new IrConvert(carrier, new IrBinary(IrBinaryOp.ShiftRight, new IrConvert(unsigned, negated), new IrConst(IrType.I32, carrier.Bits - (nearMiss == "logical-count" ? 2 : 1))))
      };
      result = form == "shift-or-swapped" ? new IrBinary(IrBinaryOp.Or, logical, sign) : new IrBinary(IrBinaryOp.Or, sign, logical);
    } else if (form.StartsWith("difference", StringComparison.Ordinal)) {
      var (positive, negative) = nearMiss == "swapped" ? (IrBinaryOp.Less, IrBinaryOp.Greater) : (IrBinaryOp.Greater, IrBinaryOp.Less);
      var second = nearMiss == "other" ? other : compared;
      result = form switch {
        "difference" => new IrBinary(IrBinaryOp.Subtract, Flag(positive, compared), Flag(negative, second)),
        "difference-sum" => new IrBinary(IrBinaryOp.Add, Flag(positive, compared), new IrConditional(Compare(negative, second), Int(-1), Int(0))),
        "difference-mirror" => new IrBinary(IrBinaryOp.Subtract,
          new IrConditional(new IrBinary(Mirrored(positive), Zero(), compared), Int(1), Int(0)),
          new IrConditional(new IrBinary(Mirrored(negative), new IrConst(second.Type, 0), second), Int(1), Int(0))),
        _ => new IrBinary(IrBinaryOp.Subtract, Flag(positive, compared), new IrConditional(Compare(negative, second), Int(1), Int(0)))
      };
    } else {
      result = form switch {
        "select-gt-lt" => new IrConditional(Compare(IrBinaryOp.Greater, compared), Int(1), new IrConditional(Compare(IrBinaryOp.Less, compared), Int(-1), Int(0))),
        "select-lt-gt" => new IrConditional(Compare(IrBinaryOp.Less, compared), Int(-1), new IrConditional(Compare(IrBinaryOp.Greater, compared), Int(1), Int(0))),
        "select-eq-gt" => new IrConditional(Compare(IrBinaryOp.Equal, compared), Int(0), new IrConditional(Compare(IrBinaryOp.Greater, compared), Int(1), Int(-1))),
        "select-ne-ge" => new IrConditional(Compare(IrBinaryOp.NotEqual, compared), new IrConditional(Compare(IrBinaryOp.GreaterOrEqual, compared), Int(1), Int(-1)), Int(0)),
        "select-ge-nested" => new IrConditional(Compare(IrBinaryOp.GreaterOrEqual, compared), new IrConditional(Compare(IrBinaryOp.LessOrEqual, compared), Int(0), Int(1)), Int(-1)),
        _ => new IrConditional(new IrBinary(IrBinaryOp.Less, Zero(), compared), Int(1), new IrConditional(new IrBinary(IrBinaryOp.Greater, Zero(), compared), Int(-1), Int(0)))
      };
    }

    return new("Signum", [new IrFunction("F", true, result.Type, nearMiss == "other" ? [x, y] : [x], [], new([new IrReturn(result)]))], []);
  }

  /// <summary>The comparison that reads the same with zero on the left.</summary>
  private static IrBinaryOp Mirrored(IrBinaryOp op) => op == IrBinaryOp.Greater ? IrBinaryOp.Less : IrBinaryOp.Greater;

  private static object Argument(int width, ulong bits) => width switch {
    8 => (object)unchecked((sbyte)bits),
    16 => (object)unchecked((short)bits),
    32 => (object)unchecked((int)bits),
    _ => unchecked((long)bits)
  };

  /// <summary>Both versions agree with the BCL's sign of the argument.</summary>
  private static void Agrees(IrModule before, IrModule after, int width, ulong bits) {
    var argument = Argument(width, bits);
    var expected = Math.Sign(Convert.ToInt64(argument, CultureInfo.InvariantCulture));
    var function = before.Functions[0].Name;
    Assert.That(Convert.ToInt64(IrInterpreter.Call(before, function, [argument]), CultureInfo.InvariantCulture), Is.EqualTo(expected), "original vs independent BCL sign");
    Assert.That(Convert.ToInt64(IrInterpreter.Call(after, function, [argument]), CultureInfo.InvariantCulture), Is.EqualTo(expected), "optimized vs independent BCL sign");
  }

  private static IrIntrinsic SignOf(IrModule module)
    => IrTree.Nodes(module.Functions[0].Body).OfType<IrIntrinsic>().Single(node => node.Op == IrIntrinsicOp.Sign);

  [TestCaseSource(nameof(Forms))]
  public void EveryForm_IsProvedAtItsActualWidth(int width, string form) {
    var module = Module(width, form);
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the input must be legal promoted IR");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    var returned = ((IrReturn)result.Module.Functions[0].Body.Statements.Last()).Value!;
    Assert.That(returned is IrConvert { Operand: var inner } ? inner : returned, Is.SameAs(SignOf(result.Module)));
    Assert.That(returned.Type, Is.EqualTo(module.Functions[0].ReturnType));
    var kind = form.StartsWith("shift-or", StringComparison.Ordinal) ? "shift-or" : form.StartsWith("difference", StringComparison.Ordinal) ? "compare-difference" : "select";
    Assert.That(result.Notes, Does.Contain($"O0122 applied: Sign.i{width} from {kind} [proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var input in new[] { 0UL, 1UL, ulong.MaxValue, 5UL, unchecked((ulong)-5L), (1UL << (width - 1)) - 1, 1UL << (width - 1) })
      Agrees(module, result.Module, width, input);
  }

  [TestCaseSource(nameof(_forms))]
  public void EightBits_AlsoDischargeAnIndependentExhaustiveObligation(string form) {
    var module = Module(8, form);
    var function = module.Functions[0];
    var before = ((IrReturn)function.Body.Statements.Last()).Value!;
    IrExpr after = new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [new IrConvert(IrType.I32, new IrLocalRef(function.Parameters[0]))]);
    if (after.Type != before.Type)
      after = new IrConvert(before.Type, after);
    var obligation = SmtRecognitionProver.CreateObligation("O0122", before, after, new(function, RuleTarget.Neutral));
    Assert.That(obligation, Is.Not.Null);
    Assert.That(new ExhaustiveProver().Prove(obligation!, new(MaxInputBits: 8)), Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 256)));
  }

  [TestCase("shift-or", "one", "not equivalent to Sign")]
  [TestCase("shift-or", "arithmetic", "not equivalent to Sign")]
  [TestCase("shift-or", "not-negated", "not equivalent to Sign")]
  [TestCase("shift-or", "shift", "not equivalent to Sign")]
  [TestCase("shift-or-swapped", "one", "not equivalent to Sign")]
  [TestCase("shift-or", "logical-count", "not equivalent to Sign")]
  [TestCase("difference", "swapped", "not equivalent to Sign")]
  [TestCase("difference", "unsigned", "unsigned comparison")]
  [TestCase("select-gt-lt", "unsigned", "unsigned comparison")]
  [TestCase("difference", "other", "operands differ")]
  public void RegisteredCounterexamples_AreRefused(string form, string nearMiss, string reason) {
    var module = Module(32, form, nearMiss);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0122 not applied: " + reason));
  }

  [TestCase(8)]
  [TestCase(16)]
  [TestCase(32)]
  [TestCase(64)]
  public void TheTwoValuedSign_IsNotASignumAndKeepsItsValueForZero(int width) {
    var module = Module(width, "shift-or", "one");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(Convert.ToInt64(IrInterpreter.Call(result.Module, "F", [Argument(width, 0)]), CultureInfo.InvariantCulture), Is.EqualTo(1));
  }

  [Test]
  public void APartialSelection_IsSilent() {
    var x = new IrLocal("x", IrType.I32);
    var condition = new IrBinary(IrBinaryOp.Less, new IrLocalRef(x), new IrConst(IrType.I32, 0));
    var flag = new IrConditional(condition, new IrConst(IrType.I32, -1), new IrConst(IrType.I32, 0));
    var module = new IrModule("Partial", [new IrFunction("F", true, IrType.I32, [x], [], new([new IrReturn(flag)]))], []);
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
    Assert.That(prover.Calls, Is.Zero);
  }

  [TestCase("public static int F(int x, int y) => (x >> 3) | y;")]
  [TestCase("public static int F(int x, int y) => (x > y ? 1 : 0) - (x < y ? 1 : 0);")]
  [TestCase("public static int F(int x, bool b) => b ? (x > 0 ? 1 : 0) : 2;")]
  [TestCase("public static int F(int x, int y) => (x > 0 ? y : 0) - (x < 0 ? 1 : 0);")]
  [TestCase("public static int F(int x) => (x > 0 ? 1 : 0) * (x < 0 ? 1 : 0);")]
  [TestCase("public static int F(int x, int y) => (x >> y) | 1;")]
  [TestCase("public static int F(uint x) => x != 0 ? 1 : 0;")]
  [TestCase("public static int F(bool a, bool b) => (a == b ? 1 : 0) - (a != b ? 1 : 0);")]
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
    var module = Module(width, "difference");
    var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0122 not applied: " + reason));
  }

  [Test]
  public void AnUnknownProof_IsARefusal() {
    var module = Module(32, "shift-or");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0122 not applied: proof unknown"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Module(32, "difference");
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
  public void ASelectionOfEnumMembers_IsNotASign() {
    var sign = new IrEnumType("Sign", IrType.I8, [new IrEnumMember("Minus", -1), new IrEnumMember("Zero", 0), new IrEnumMember("Plus", 1)]);
    var x = new IrLocal("x", IrType.I64);
    var value = new IrLocalRef(x);
    IrExpr Member(long member) => new IrConst(sign.Type, member);
    var choice = new IrConditional(new IrBinary(IrBinaryOp.Less, value, new IrConst(IrType.I64, 0)), Member(-1),
      new IrConditional(new IrBinary(IrBinaryOp.Equal, value, new IrConst(IrType.I64, 0)), Member(0), Member(1)));
    var module = new IrModule("Enum", [new IrFunction("F", true, sign.Type, [x], [], new([new IrReturn(choice)]))], []) { Types = [sign] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  [Test]
  public void AValueThatMayTrap_IsNotEvaluatedFewerTimes() {
    var array = new IrLocal("a", IrType.Array(IrType.I32));
    var load = new IrArrayLoad(new IrLocalRef(array), new IrConst(IrType.I32, 0));
    IrExpr Flag(IrBinaryOp op) => new IrConditional(new IrBinary(op, load, new IrConst(IrType.I32, 0)), new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0));
    var difference = new IrBinary(IrBinaryOp.Subtract, Flag(IrBinaryOp.Greater), Flag(IrBinaryOp.Less));
    var module = new IrModule("Trapping", [new IrFunction("F", true, IrType.I32, [array], [], new([new IrReturn(difference)]))], []);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0122 not applied: operand may trap or have effects"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [TestCase("public static long F(int x) => (x > 0 ? 1 : 0) - (x < 0 ? 1 : 0);", 1L)]
  [TestCase("public static long F(int x) => (x > 0 ? 1L : 0L) - (x < 0 ? 1 : 0);", 1L)]
  [TestCase("public static long F(long x) => x < 0 ? -1L : x == 0 ? 0L : 1L;", 1L)]
  public void ConversionsAroundAndInsideTheIdiom_AreProved(string method, long positive) {
    var module = PrototypeFixture.Import("Converted.cs", PrototypeFixture.Wrap(method));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    var type = module.Functions[0].Parameters[0].Type;
    foreach (var input in new long[] { -9, 0, 9 }) {
      var argument = type == IrType.I32 ? (object)(int)input : input;
      Assert.That(IrInterpreter.Call(result.Module, "F", [argument]), Is.EqualTo(IrInterpreter.Call(module, "F", [argument])));
      Assert.That(Convert.ToInt64(IrInterpreter.Call(result.Module, "F", [argument]), CultureInfo.InvariantCulture), Is.EqualTo(positive * Math.Sign(input)));
    }
  }

  [Test]
  public void UnsignedComparisonsOfTwoValues_ReportTheFirstRefusal() {
    var module = PrototypeFixture.Import("Both.cs", PrototypeFixture.Wrap("public static int F(uint x, uint y) => (x > 0 ? 1 : 0) - (y < 0 ? 1 : 0);"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.EqualTo(new[] { "O0122 not applied: unsigned comparison" }));
  }

  [Test]
  public void ANarrowedResult_IsTheSignInTheNarrowType() {
    var module = PrototypeFixture.Import("Narrowed.cs", PrototypeFixture.Wrap("public static sbyte F(short x) => (sbyte)((x > 0 ? 1 : 0) - (x < 0 ? 1 : 0));"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    Assert.That(result.Notes, Does.Contain("O0122 applied: Sign.i16 from compare-difference [proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var input in new short[] { short.MinValue, -1, 0, 1, short.MaxValue })
      Assert.That(IrInterpreter.Call(result.Module, "F", [input]), Is.EqualTo((sbyte)Math.Sign(input)));
  }

  [Test]
  public void AnUnsignedResultType_ComparesTheWrappedSign() {
    var x = new IrLocal("x", IrType.I32);
    IrExpr Flag(IrBinaryOp op) => new IrConditional(new IrBinary(op, new IrLocalRef(x), new IrConst(IrType.I32, 0)), new IrConst(IrType.U32, 1), new IrConst(IrType.U32, 0));
    var difference = new IrBinary(IrBinaryOp.Subtract, Flag(IrBinaryOp.Greater), Flag(IrBinaryOp.Less));
    var module = new IrModule("Unsigned", [new IrFunction("F", true, IrType.U32, [x], [], new([new IrReturn(difference)]))], []);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    Assert.That(IrInterpreter.Call(result.Module, "F", [-7]), Is.EqualTo(uint.MaxValue));
    Assert.That(IrInterpreter.Call(result.Module, "F", [7]), Is.EqualTo(1u));
  }

  [Test]
  public void TheExecutableNarrowFixture_IsProved() {
    var before = Fixture("Narrow");
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(after.Notes, Does.Contain("O0122 applied: Sign.i8 from compare-difference [proof: smt-bitvector]"));
    for (var bits = 0UL; bits < 256; ++bits)
      Agrees(before, after.Module, 8, bits);

    TestContext.Out.WriteLine(string.Join("\n", after.Notes));
    TestContext.Out.WriteLine("Solver: " + new Z3Solver().Version);
  }

  [TestCase("TwoValues", "operands differ")]
  [TestCase("Swapped", "not equivalent to Sign")]
  [TestCase("WrongShift", "not equivalent to Sign")]
  public void TheExecutableCounterexamples_AreRefused(string function, string reason) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0122 not applied: " + reason));
  }

  [TestCaseSource(nameof(_forms))]
  public void EveryEightBitInput_AgreesWithTheIndependentSign(string form) {
    var before = Module(8, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    for (var input = 0UL; input < 256; ++input)
      Agrees(before, after.Module, 8, input);
  }

  [TestCaseSource(nameof(WideForms))]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithAnIndependentSign(int width, string form) {
    var before = Module(width, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(122);
    var bytes = new byte[8];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      Agrees(before, after.Module, width, BitConverter.ToUInt64(bytes));
    }

    TestContext.Out.WriteLine($"O0122 {form}, {width} bits: seed 122, 10,000 inputs, zero mismatches.");
  }
}
