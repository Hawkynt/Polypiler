using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Core.Recognize.Rules;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>The SWAR population count on the actual tree IR: a structural gate, then the proof against PopCount decides.</summary>
[TestFixture]
public sealed class PopCountSwarRecognitionTests {

  private static readonly string[] _forms = ["sub-mul", "add-mul", "sub-fold", "add-fold"];

  private static IEnumerable<TestCaseData> Forms() {
    foreach (var width in new[] { 16, 32, 64 })
      foreach (var signed in new[] { false, true })
        foreach (var form in _forms)
          yield return new TestCaseData(width, signed, form);
  }

  private static IEnumerable<TestCaseData> WideForms => Forms().Where(data => data.Arguments[0] is not 16);

  private static IEnumerable<TestCaseData> NarrowForms => Forms().Where(data => data.Arguments[0] is 16);

  private sealed class InconclusiveProver : IRecognitionProver {
    public int Calls { get; private set; }
    public RecognitionProof Prove(string id, IrExpr before, IrExpr after, RuleContext context) {
      ++this.Calls;
      return new(false, "proof unknown");
    }
  }

  private static OptimizerOptions Options(IRecognitionProver? prover) => RecognitionFixtures.Options with {
    Prover = prover,
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0102").ToHashSet()
  };

  private static ulong Pattern(byte value, int width) {
    var repeated = 0UL;
    for (var shift = 0; shift < width; shift += 8)
      repeated |= (ulong)value << shift;
    return repeated;
  }

  /// <summary>
  /// One spelling of the SWAR count, one statement per step through stable locals, or one of its near misses.
  /// A 16-bit value is counted in 32-bit arithmetic, as C# widens it; masks are of the counted width.
  /// </summary>
  internal static IrModule Module(int width, bool signed, string form, string? nearMiss = null, int padding = 0, bool inline = false) {
    var type = width switch { 16 => signed ? IrType.I16 : IrType.U16, 32 => signed ? IrType.I32 : IrType.U32, _ => signed ? IrType.I64 : IrType.U64 };
    var carrier = width == 16 ? (signed ? IrType.I32 : IrType.U32) : type;
    var maskWidth = nearMiss == "narrow-masks" ? width / 2 : nearMiss == "byte" ? 8 : width;
    var x = new IrLocal("x", type);
    var y = new IrLocal("y", type);
    var locals = new List<IrLocal>();
    var statements = new List<IrStmt>();
    IrExpr Mask(byte value) => new IrConst(carrier, unchecked((long)Pattern(value, maskWidth)));
    IrExpr Shift(IrExpr value, int count) => new IrBinary(IrBinaryOp.ShiftRight, value, new IrConst(IrType.I32, count));
    IrExpr And(IrExpr value, IrExpr mask) => new IrBinary(IrBinaryOp.And, value, mask);
    IrExpr Add(IrExpr left, IrExpr right) => new IrBinary(IrBinaryOp.Add, left, right);
    IrExpr Let(string name, IrExpr value) {
      if (inline)
        return value;

      var local = new IrLocal(name, carrier);
      locals.Add(local);
      statements.Add(new IrDeclare(local, value));
      return new IrLocalRef(local);
    }

    var array = new IrLocal("values", IrType.Array(type));
    IrExpr read = nearMiss == "array" ? new IrArrayLoad(new IrLocalRef(array), new IrConst(IrType.I32, 0)) : new IrLocalRef(x);
    IrExpr value = width == 16 ? new IrConvert(carrier, read) : read;
    IrExpr shiftedValue = nearMiss == "two" ? (width == 16 ? new IrConvert(carrier, new IrLocalRef(y)) : new IrLocalRef(y)) : value;
    var a = form.StartsWith("sub", StringComparison.Ordinal)
      ? Let("a", new IrBinary(IrBinaryOp.Subtract, value, And(Shift(shiftedValue, 1), Mask(0x55))))
      : Let("a", Add(And(value, Mask(0x55)), And(Shift(shiftedValue, 1), Mask(0x55))));
    var second = nearMiss == "bad-mask" ? new IrConst(carrier, unchecked((long)(Pattern(0x33, maskWidth) - 1))) : Mask(0x33);
    var b = Let("b", Add(And(a, Mask(0x33)), And(Shift(a, 2), second)));
    var nibbles = And(Add(b, Shift(b, 4)), Mask(0x0F));
    var c = nearMiss == "byte" ? nibbles : Let("c", nibbles);
    for (var pad = 0; pad < padding; ++pad)
      c = new IrBinary(IrBinaryOp.Or, c, new IrConst(carrier, 0));

    IrExpr result;
    if (nearMiss == "byte")
      result = c;
    else if (form.EndsWith("mul", StringComparison.Ordinal)) {
      var top = nearMiss == "shift" ? width - 16 : width - 8;
      result = Shift(new IrBinary(IrBinaryOp.Multiply, c, new IrConst(carrier, unchecked((long)Pattern(0x01, width)))), top);
      // In wider arithmetic the product keeps the high byte's own count above the sum: a 16-bit count is masked.
      if (carrier.Bits > width)
        result = And(result, new IrConst(carrier, width * 2 - 1));
    } else {
      var folded = c;
      for (var step = 8; step < width; step *= 2)
        folded = Let("f" + step, Add(folded, Shift(folded, step)));
      result = And(folded, new IrConst(carrier, width * 2 - 1));
    }

    var parameters = nearMiss == "two" ? new[] { x, y } : nearMiss == "array" ? new[] { array } : new[] { x };
    statements.Add(new IrReturn(result));
    return new("PopCount", [new IrFunction("F", true, carrier, parameters, locals, new(statements))], []);
  }

  private static object Argument(IrType type, ulong bits) => type.Kind switch {
    IrTypeKind.I16 => unchecked((short)bits),
    IrTypeKind.U16 => unchecked((ushort)bits),
    IrTypeKind.I32 => unchecked((int)bits),
    IrTypeKind.U32 => unchecked((uint)bits),
    IrTypeKind.I64 => unchecked((long)bits),
    _ => bits
  };

  private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

  /// <summary>Both versions count the bits of the argument as the BCL does.</summary>
  private static void Agrees(IrModule before, IrModule after, int width, ulong bits) {
    var function = before.Functions[0];
    var argument = Argument(function.Parameters[0].Type, bits);
    var expected = (ulong)BitOperations.PopCount(bits & Mask(width));
    ulong Counted(IrModule module) => IrInterpreter.Call(module, function.Name, [argument]) switch {
      ulong unsigned => unsigned,
      var other => unchecked((ulong)Convert.ToInt64(other, System.Globalization.CultureInfo.InvariantCulture))
    };
    Assert.That(Counted(before), Is.EqualTo(expected), $"original vs BCL for {bits:X}");
    Assert.That(Counted(after), Is.EqualTo(expected), $"optimized vs BCL for {bits:X}");
  }

  [TestCaseSource(nameof(Forms))]
  public void EveryForm_IsProvedAtItsActualWidth(int width, bool signed, string form) {
    var module = Module(width, signed, form);
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the input must be legal IR");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    var count = IrTree.Nodes(result.Module.Functions[0].Body).OfType<IrIntrinsic>().Single(node => node.Op == IrIntrinsicOp.PopCount);
    Assert.That(count.Arguments[0].Type.Bits, Is.EqualTo(width));
    Assert.That(result.Notes, Does.Contain($"O0102 applied: PopCount.{count.Arguments[0].Type} from SWAR sequence [proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var bits in new[] { 0UL, 1UL, Mask(width), 1UL << (width - 1), (1UL << (width - 1)) + 1, 0x123456789ABCDEF0UL & Mask(width), 0x5555555555555555UL & Mask(width) })
      Agrees(module, result.Module, width, bits);
  }

  [TestCaseSource(nameof(NarrowForms))]
  public void SixteenBits_DischargeAnIndependentExhaustiveObligationAndAgreeEverywhere(int width, bool signed, string form) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var function = before.Functions[0];
    var candidate = ((IrReturn)function.Body.Statements.Last()).Value!;
    var replacement = ((IrReturn)after.Module.Functions[0].Body.Statements.Last()).Value!;
    var obligation = SmtRecognitionProver.CreateObligation("O0102", candidate, replacement, new(function, RuleTarget.Neutral));
    Assert.That(obligation, Is.Not.Null);
    Assert.That(new ExhaustiveProver().Prove(obligation!, new(MaxInputBits: 16)), Is.InstanceOf<ProofResult.Proved>());
    for (var bits = 0UL; bits < 65536; ++bits)
      Agrees(before, after.Module, width, bits);
  }

  [TestCase(32, "bad-mask", "not equivalent to PopCount")]
  [TestCase(64, "bad-mask", "not equivalent to PopCount")]
  [TestCase(64, "narrow-masks", "not equivalent to PopCount")]
  [TestCase(32, "shift", "not equivalent to PopCount")]
  [TestCase(32, "two", "more than one variable")]
  [TestCase(16, "two", "more than one variable")]
  [TestCase(32, "byte", "unsupported width")]
  public void RegisteredCounterexamples_AreRefused(int width, string nearMiss, string reason) {
    var module = Module(width, false, "sub-mul", nearMiss);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0102 not applied: " + reason));
  }

  [Test]
  public void TheWrongMask_CountsFourAsZero() {
    var module = Module(32, false, "sub-mul", "bad-mask");
    Assert.That(IrInterpreter.Call(module, "F", [4u]), Is.EqualTo(0u));
    Assert.That(IrInterpreter.Call(Module(64, false, "sub-mul", "narrow-masks"), "F", [0x0000000100000000UL]), Is.EqualTo(0UL));
  }

  /// <summary>The size limit counts the operations of the candidate, through its stable locals.</summary>
  [TestCase(12, true)]
  [TestCase(13, false)]
  public void TheSizeLimit_IsTwentyFourOperations(int padding, bool applies) {
    var module = Module(32, false, "sub-mul", padding: padding);
    var function = module.Functions[0];
    var root = ((IrReturn)function.Body.Statements.Last()).Value!;
    Assert.That(root, Is.Not.InstanceOf<IrLocalRef>());
    var notes = new List<string>();
    var context = new RuleContext(function, RuleTarget.Neutral) { Prover = new SmtRecognitionProver(), Note = notes.Add };
    Assert.That(RuleTable.Default.Find("O0102")!.TryApply(root, context, out _), Is.EqualTo(applies), string.Join("; ", notes));
    if (!applies)
      Assert.That(notes, Is.EqualTo(new[] { "O0102 not applied: expression too large" }));
  }

  [TestCase("public static uint F(uint x) => x >> 8;")]
  [TestCase("public static uint F(uint x) => x & 0x3Fu;")]
  [TestCase("public static uint F(uint x) => ((x & 0x55555555u) | ((x >> 1) & 0x33333333u)) >> 8;")]
  [TestCase("public static uint F(uint x) => ((x & 0x55555555u) + (x & 0x33333333u) + (x & 0x0F0F0F0Fu)) & 0x1FFu;")]
  [TestCase("public static uint F(uint x, int n) => ((x & 0x55555555u) + (x & 0x33333333u) + (x & 0x0F0F0F0Fu)) >> n;")]
  [TestCase("public static uint F(uint x) => ((x & 0x55555555u) + (x & 0x33333333u) + (x & 0x0F0F0F0Fu)) >> 4;")]
  public void UnrelatedExpressions_AreSilent(string method) {
    var module = PrototypeFixture.Import("Unrelated.cs", PrototypeFixture.Wrap(method));
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
    Assert.That(prover.Calls, Is.Zero);
  }

  [Test]
  public void AValueThatMayTrap_IsRefused() {
    var module = Module(32, false, "sub-mul", "array", inline: true);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0102 not applied: operand may trap or have effects"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [TestCase(16, "missing", "proof unavailable")]
  [TestCase(64, "missing", "proof unavailable")]
  [TestCase(16, "timeout", "proof timeout")]
  [TestCase(32, "timeout", "proof timeout")]
  [TestCase(64, "timeout", "proof timeout")]
  public void AnUnprovedCandidate_IsUntouched(int width, string mode, string reason) {
    var module = Module(width, false, "sub-mul");
    var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0102 not applied: " + reason));
  }

  [Test]
  public void AnUnknownProof_IsARefusalAndNotARefutation() {
    var module = Module(32, false, "add-fold");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0102 not applied: proof unknown"));
    Assert.That(result.Notes, Has.None.Contains("not equivalent"));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Module(32, false, "sub-mul");
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

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0102", "popcount.cs");
    var module = PrototypeFixture.Import("popcount.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  [TestCase("Multiply32", "u32")]
  [TestCase("Fold32", "u32")]
  [TestCase("Multiply64", "u64")]
  [TestCase("Narrow16", "u16")]
  public void TheExecutableFixtures_AreProved(string function, string type) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(after.Notes, Does.Contain($"O0102 applied: PopCount.{type} from SWAR sequence [proof: smt-bitvector]"));
    TestContext.Out.WriteLine("Before:\n" + IrText.Of(before) + "After:\n" + IrText.Of(after.Module) + string.Join("\n", after.Notes));
    TestContext.Out.WriteLine("Solver: " + new Z3Solver().Version);
  }

  [TestCase("BadMask32", "not equivalent to PopCount")]
  [TestCase("U64WithU32Masks", "not equivalent to PopCount")]
  [TestCase("Shift16", "not equivalent to PopCount")]
  [TestCase("TwoVariables", "more than one variable")]
  public void TheExecutableCounterexamples_AreRefused(string function, string reason) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0102 not applied: " + reason));
  }

  [TestCaseSource(nameof(WideForms))]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithTheBcl(int width, bool signed, string form) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(102);
    var bytes = new byte[8];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      Agrees(before, after.Module, width, BitConverter.ToUInt64(bytes));
    }

    TestContext.Out.WriteLine($"O0102 {form}, {(signed ? "signed" : "unsigned")} {width} bits: seed 102, 10,000 inputs, zero mismatches.");
  }
}
