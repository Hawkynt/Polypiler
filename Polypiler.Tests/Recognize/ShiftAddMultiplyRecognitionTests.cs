using System;
using System.Collections.Generic;
using System.Diagnostics;
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

/// <summary>Multiplication by a constant from shift-add chains on the actual promoted tree IR, modulo the width.</summary>
[TestFixture]
public sealed class ShiftAddMultiplyRecognitionTests {

  private static readonly string[] _forms = ["add", "sub", "nested", "triple", "repeat", "negative", "minus-one", "mul-term", "negate", "top", "chain", "local"];

  private static IEnumerable<TestCaseData> Forms() {
    foreach (var width in new[] { 8, 16, 32, 64 })
      foreach (var signed in new[] { true, false })
        foreach (var form in _forms)
          yield return new TestCaseData(width, signed, form);
  }

  private static IEnumerable<TestCaseData> WideForms => Forms().Where(data => data.Arguments[0] is int width && width != 8);

  private static IEnumerable<TestCaseData> EightBitForms => Forms().Where(data => data.Arguments[0] is 8);

  private static IrType Type(int width, bool signed) => (width, signed) switch {
    (8, true) => IrType.I8, (8, false) => IrType.U8, (16, true) => IrType.I16, (16, false) => IrType.U16,
    (32, true) => IrType.I32, (32, false) => IrType.U32, (64, true) => IrType.I64, _ => IrType.U64
  };

  private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

  /// <summary>The coefficient each form spells, modulo the width of the arithmetic.</summary>
  private static ulong Coefficient(string form, int carrier) => form switch {
    "add" or "nested" or "local" => 10,
    "sub" => 15,
    "triple" or "repeat" => 3,
    "negative" or "negate" => unchecked(0UL - 3) & Mask(carrier),
    "minus-one" => Mask(carrier),
    "mul-term" => 7,
    "top" => (1UL << (carrier - 1)) + 1,
    _ => 39
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
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0125").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0125", "mulconst.cs");
    var module = PrototypeFixture.Import("mulconst.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  /// <summary>One spelling at a width and signedness, promoted as C# promotes it, or one of its near misses.</summary>
  internal static IrModule Module(int width, bool signed, string form, string? nearMiss = null) {
    var type = Type(width, signed);
    var carrier = width < 32 ? IrType.I32 : type;
    var x = new IrLocal("x", type);
    var y = new IrLocal("y", type);
    var n = new IrLocal("n", IrType.I32);
    IrExpr Extend(IrExpr value) => value.Type == carrier ? value : new IrConvert(carrier, value);
    var value = Extend(new IrLocalRef(x));
    IrExpr Shl(IrExpr operand, int count) => new IrBinary(IrBinaryOp.ShiftLeft, operand, new IrConst(IrType.I32, count));
    IrExpr Add(IrExpr left, IrExpr right) => new IrBinary(nearMiss switch {
      "or" => IrBinaryOp.Or, "xor" => IrBinaryOp.Xor, "checked" => IrBinaryOp.AddChecked, _ => IrBinaryOp.Add
    }, left, right);
    IrExpr Sub(IrExpr left, IrExpr right) => new IrBinary(nearMiss == "checked" ? IrBinaryOp.SubtractChecked : IrBinaryOp.Subtract, left, right);
    var statements = new List<IrStmt>();
    var locals = new List<IrLocal>();
    var parameters = new List<IrLocal> { x };

    IrExpr result = nearMiss switch {
      "two" => Add(Shl(value, 3), Shl(Extend(new IrLocalRef(y)), 1)),
      "variable" => Add(new IrBinary(IrBinaryOp.ShiftLeft, value, new IrLocalRef(n)), value),
      "trivial-one" => Sub(Shl(value, 1), value),
      "trivial-power" => Add(Shl(value, 1), Shl(value, 1)),
      "trivial-zero" => Sub(value, value),
      "right-shift" => Add(Shl(value, 3), new IrBinary(IrBinaryOp.ShiftRightLogical, value, new IrConst(IrType.I32, 1))),
      _ => form switch {
        "add" => Add(Shl(value, 3), Shl(value, 1)),
        "sub" => Sub(Shl(value, 4), value),
        "nested" => Shl(Add(Shl(value, 2), value), 1),
        "triple" => Add(Shl(value, 1), value),
        "repeat" => Add(Add(value, value), value),
        "negative" => Sub(value, Shl(value, 2)),
        "minus-one" => Sub(value, Shl(value, 1)),
        "mul-term" => Add(new IrBinary(IrBinaryOp.Multiply, value, new IrConst(carrier, 5)), Shl(value, 1)),
        "negate" => Sub(new IrUnary(IrUnaryOp.Negate, Shl(value, 1)), value),
        "top" => Add(Shl(value, carrier.Bits - 1), value),
        "chain" => Add(Sub(Add(Shl(value, 5), Shl(value, 3)), Shl(value, 1)), value),
        _ => Local()
      }
    };

    IrExpr Local() {
      var a = new IrLocal("a", carrier);
      locals.Add(a);
      statements.Add(new IrDeclare(a, Shl(value, 3)));
      return Add(new IrLocalRef(a), Shl(value, 1));
    }

    if (nearMiss == "two")
      parameters.Add(y);
    if (nearMiss == "variable")
      parameters.Add(n);
    if (!signed && !carrier.IsSigned && form == "negate" && nearMiss is null)
      result = Sub(Sub(new IrConst(carrier, 0), Shl(value, 1)), value);

    var returned = result.Type == type ? result : new IrConvert(type, result);
    statements.Add(new IrReturn(returned));
    return new("Multiply", [new IrFunction("F", true, type, [.. parameters], locals, new(statements))], []);
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

  private static ulong Bits(object result) => result is ulong unsigned ? unsigned : unchecked((ulong)Convert.ToInt64(result, CultureInfo.InvariantCulture));

  /// <summary>Both versions agree with the wrapping product computed by the BCL.</summary>
  private static void Agrees(IrModule before, IrModule after, int width, bool signed, string form, ulong bits) {
    var carrier = width < 32 ? 32 : width;
    var widened = signed && (bits & (1UL << (width - 1))) != 0 ? bits | ~Mask(width) : bits & Mask(width);
    var expected = unchecked(widened * Coefficient(form, carrier)) & Mask(width);
    var argument = Argument(before.Functions[0].Parameters[0].Type, bits);
    Assert.That(Bits(IrInterpreter.Call(before, "F", [argument])) & Mask(width), Is.EqualTo(expected), $"original vs BCL product for {bits:X}");
    Assert.That(Bits(IrInterpreter.Call(after, "F", [argument])) & Mask(width), Is.EqualTo(expected), $"optimized vs BCL product for {bits:X}");
  }

  private static IrBinary Product(IrModule module)
    => IrTree.Nodes(module.Functions[0].Body).OfType<IrBinary>().Single(node => node.Op == IrBinaryOp.Multiply);

  [TestCaseSource(nameof(Forms))]
  public void EveryForm_IsProvedAtItsActualWidthAndSignedness(int width, bool signed, string form) {
    var module = Module(width, signed, form);
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the input must be legal promoted IR");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    var product = Product(result.Module);
    var carrier = product.Type.Bits;
    var factor = (IrConst)product.Right;
    Assert.That((ulong)factor.Value & Mask(carrier), Is.EqualTo(Coefficient(form, carrier)));
    Assert.That(result.Notes.Single(note => note.StartsWith("O0125 applied: ", StringComparison.Ordinal)),
      Does.StartWith($"O0125 applied: Mul.{product.Type} by {IrOperators.Number(factor.Type, factor.Value)} from ").And.EndWith(" shift/add terms [proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var bits in new[] { 0UL, 1UL, 7UL, 200UL, Mask(width), 1UL << (width - 1), (1UL << (width - 1)) + 1, 0x8000000180000001UL & Mask(width) })
      Agrees(module, result.Module, width, signed, form, bits);
  }

  [TestCaseSource(nameof(EightBitForms))]
  public void EightBits_AlsoDischargeAnIndependentExhaustiveObligation(int width, bool signed, string form) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    var function = before.Functions[0];
    var candidate = ((IrReturn)function.Body.Statements.Last()).Value!;
    var replacement = ((IrReturn)after.Module.Functions[0].Body.Statements.Last()).Value!;
    var obligation = SmtRecognitionProver.CreateObligation("O0125", candidate, replacement, new(function, RuleTarget.Neutral));
    Assert.That(obligation, Is.Not.Null);
    Assert.That(new ExhaustiveProver().Prove(obligation!, new(MaxInputBits: 8)), Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 256)));
    for (var bits = 0UL; bits < 256; ++bits)
      Agrees(before, after.Module, width, signed, form, bits);
  }

  [TestCase(32, false, "add", "or", "combiner is not Add or Sub")]
  [TestCase(32, true, "add", "xor", "combiner is not Add or Sub")]
  [TestCase(32, true, "add", "checked", "non-wrapping arithmetic")]
  [TestCase(64, true, "sub", "checked", "non-wrapping arithmetic")]
  [TestCase(32, false, "add", "two", "more than one variable")]
  [TestCase(32, false, "add", "variable", "non-constant shift amount")]
  [TestCase(32, false, "add", "trivial-one", "coefficient is trivial")]
  [TestCase(32, false, "add", "trivial-power", "coefficient is trivial")]
  [TestCase(32, false, "add", "trivial-zero", "coefficient is trivial")]
  public void RegisteredCounterexamples_AreRefused(int width, bool signed, string form, string nearMiss, string reason) {
    var module = Module(width, signed, form, nearMiss);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0125 not applied: " + reason));
  }

  [Test]
  public void OrAsCombiner_LosesTheCarry() {
    var module = Module(32, false, "add", "or");
    Assert.That(IrInterpreter.Call(module, "F", [5u]), Is.EqualTo(42u));
  }

  [Test]
  public void ARightShiftInTheChain_IsNotLinearAndIsSilent() {
    var module = Module(32, false, "add", "right-shift");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
    Assert.That(prover.Calls, Is.Zero);
  }

  [TestCase("public static uint F(uint x) => x << 3;")]
  [TestCase("public static uint F(uint x) => x * 10;")]
  [TestCase("public static uint F(uint x, uint y) => x + y;")]
  [TestCase("public static uint F(uint x, uint y) => x + (y << 1);")]
  [TestCase("public static uint F(uint x) => (x << 3) + 1 - 1 + 5;")]
  [TestCase("public static uint F(uint x) => (x << 3) | (x >> 29);")]
  [TestCase("public static uint F(uint hi, uint lo) => (hi << 16) | lo;")]
  [TestCase("public static uint F(uint a, uint b) => ((a ^ b) << 3) + 1;")]
  [TestCase("public static uint F(uint x) => (x & 0xFF) + (x >> 8);")]
  [TestCase("public static uint F(uint x, uint y) => x * y + 3;")]
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
    var module = Module(width, false, "add");
    var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0125 not applied: " + reason));
  }

  [Test]
  public void AnUnknownProof_IsARefusal() {
    var module = Module(32, false, "sub");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0125 not applied: proof unknown"));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Module(32, false, "add");
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
  public void AValueThatMayTrap_IsNotEvaluatedFewerTimes() {
    var module = PrototypeFixture.Import("Trapping.cs", PrototypeFixture.Wrap("public static uint F(uint[] a) => (a[0] << 3) + (a[0] << 1);"));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0125 not applied: operand may trap or have effects"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void ADefectiveCyclicDefinition_IsRefusedWithoutFollowingItForever() {
    var x = new IrLocal("x", IrType.I32);
    var a = new IrLocal("a", IrType.I32);
    var expression = new IrBinary(IrBinaryOp.Add, new IrLocalRef(a), new IrBinary(IrBinaryOp.ShiftLeft, new IrLocalRef(x), new IrConst(IrType.I32, 1)));
    var function = new IrFunction("F", true, IrType.I32, [x], [a], new([new IrDeclare(a, new IrLocalRef(a)), new IrReturn(expression)]));
    Assert.That(RuleTable.Default.Find("O0125")!.TryApply(expression, new(function, RuleTarget.Neutral), out _), Is.False);
  }

  [TestCase("Ten", "Mul.u32 by 10 from 2 shift/add terms")]
  [TestCase("TenNested", "Mul.u32 by 10 from 2 shift/add terms")]
  [TestCase("Fifteen", "Mul.u32 by 15 from 2 shift/add terms")]
  [TestCase("MinusThree", "Mul.u32 by 4294967293 from 2 shift/add terms")]
  [TestCase("NarrowTen", "Mul.i32 by 10 from 2 shift/add terms")]
  public void TheExecutableFixtures_AreProved(string function, string report) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(after.Notes, Does.Contain($"O0125 applied: {report} [proof: smt-bitvector]"));
    TestContext.Out.WriteLine("Before:\n" + IrText.Of(before) + "After:\n" + IrText.Of(after.Module) + string.Join("\n", after.Notes));
    TestContext.Out.WriteLine("Solver: " + new Z3Solver().Version);
  }

  [TestCase("OrTen", "combiner is not Add or Sub")]
  [TestCase("CheckedTen", "non-wrapping arithmetic")]
  [TestCase("TwoValues", "more than one variable")]
  [TestCase("VariableShift", "non-constant shift amount")]
  [TestCase("One", "coefficient is trivial")]
  public void TheExecutableCounterexamples_AreRefused(string function, string reason) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0125 not applied: " + reason));
  }

  [Test]
  public void TheWideChainProof_FitsItsBudget() {
    var module = Module(64, true, "chain");
    var function = module.Functions[0];
    var candidate = ((IrReturn)function.Body.Statements.Last()).Value!;
    var product = new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(function.Parameters[0]), new IrConst(IrType.I64, 39));
    var clock = Stopwatch.StartNew();
    var proof = new SmtRecognitionProver().Prove("O0125", candidate, product, new(function, RuleTarget.Neutral));
    TestContext.Out.WriteLine($"64-bit four-term chain: {proof} in {clock.Elapsed.TotalMilliseconds:F0} ms");
    Assert.That(proof.Proved, Is.True);
  }

  [TestCaseSource(nameof(WideForms))]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithTheBclProduct(int width, bool signed, string form) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(125);
    var bytes = new byte[8];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      Agrees(before, after.Module, width, signed, form, BitConverter.ToUInt64(bytes));
    }

    TestContext.Out.WriteLine($"O0125 {form}, {(signed ? "signed" : "unsigned")} {width} bits: seed 125, 10,000 inputs, zero mismatches.");
  }
}
