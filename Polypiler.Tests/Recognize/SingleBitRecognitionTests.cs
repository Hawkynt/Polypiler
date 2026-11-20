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

/// <summary>Single-bit test, set, clear and flip on the actual promoted tree IR, with every position a shift can take.</summary>
[TestFixture]
public sealed class SingleBitRecognitionTests {

  /// <summary>Each form and the operation it becomes.</summary>
  private static readonly (string Form, IrIntrinsicOp Op)[] _forms = [
    ("test-shift", IrIntrinsicOp.TestBit), ("test-logical", IrIntrinsicOp.TestBit), ("test-shift-commuted", IrIntrinsicOp.TestBit),
    ("test-eq0", IrIntrinsicOp.TestBit), ("test-eq1", IrIntrinsicOp.TestBit), ("test-mask", IrIntrinsicOp.TestBit), ("test-mask-commuted", IrIntrinsicOp.TestBit),
    ("test-masked-position", IrIntrinsicOp.TestBit), ("test-constant-position", IrIntrinsicOp.TestBit),
    ("set", IrIntrinsicOp.SetBit), ("set-commuted", IrIntrinsicOp.SetBit), ("clear", IrIntrinsicOp.ClearBit), ("clear-commuted", IrIntrinsicOp.ClearBit),
    ("flip", IrIntrinsicOp.ToggleBit), ("flip-commuted", IrIntrinsicOp.ToggleBit), ("set-constant-position", IrIntrinsicOp.SetBit)];

  private static IEnumerable<TestCaseData> Forms() {
    foreach (var width in new[] { 8, 16, 32, 64 })
      foreach (var signed in new[] { true, false })
        foreach (var (form, op) in _forms)
          yield return new TestCaseData(width, signed, form, op);
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
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0108").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0108", "bits.cs");
    var module = PrototypeFixture.Import("bits.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  /// <summary>One spelling at a width and signedness, promoted as C# promotes it, or one of its near misses.</summary>
  internal static IrModule Module(int width, bool signed, string form, string? nearMiss = null) {
    var type = Type(width, signed);
    var carrier = width < 32 ? IrType.I32 : type;
    var x = new IrLocal("x", type);
    var n = new IrLocal("n", IrType.I32);
    IrExpr value = width < 32 ? new IrConvert(carrier, new IrLocalRef(x)) : new IrLocalRef(x);
    IrExpr position = form.EndsWith("constant-position", StringComparison.Ordinal) ? new IrConst(IrType.I32, 5)
      : form.EndsWith("masked-position", StringComparison.Ordinal) ? new IrBinary(IrBinaryOp.And, new IrLocalRef(n), new IrConst(IrType.I32, carrier.Bits - 1))
      : new IrLocalRef(n);
    IrExpr one = nearMiss == "narrow-one" ? new IrConvert(carrier, new IrBinary(IrBinaryOp.ShiftLeft, new IrConst(IrType.I32, 1), position))
      : new IrBinary(IrBinaryOp.ShiftLeft, new IrConst(carrier, nearMiss == "two" ? 2 : 1), position);
    IrExpr Constant(long number) => new IrConst(carrier, number);
    IrExpr Shifted(IrBinaryOp op) => new IrBinary(op, value, position);

    IrExpr result;
    if (form.StartsWith("test", StringComparison.Ordinal)) {
      var field = Constant(nearMiss == "field" ? 3 : 1);
      IrExpr masked = form switch {
        "test-logical" => new IrBinary(IrBinaryOp.And, Shifted(IrBinaryOp.ShiftRightLogical), field),
        "test-shift-commuted" => new IrBinary(IrBinaryOp.And, field, Shifted(IrBinaryOp.ShiftRight)),
        "test-mask" => new IrBinary(IrBinaryOp.And, value, one),
        "test-mask-commuted" => new IrBinary(IrBinaryOp.And, one, value),
        _ => new IrBinary(IrBinaryOp.And, Shifted(IrBinaryOp.ShiftRight), field)
      };
      result = form switch {
        "test-eq0" => new IrBinary(IrBinaryOp.Equal, masked, Constant(0)),
        "test-eq1" => new IrBinary(IrBinaryOp.Equal, masked, Constant(1)),
        _ when nearMiss == "mask-one" => new IrBinary(IrBinaryOp.Equal, masked, Constant(1)),
        _ => new IrBinary(IrBinaryOp.NotEqual, masked, Constant(0))
      };
    } else {
      var complement = new IrUnary(IrUnaryOp.BitNot, one);
      IrExpr combined = form switch {
        "set" or "set-constant-position" => new IrBinary(IrBinaryOp.Or, value, nearMiss == "constant" ? Constant(0x10) : one),
        "set-commuted" => new IrBinary(IrBinaryOp.Or, one, value),
        "clear" => new IrBinary(IrBinaryOp.And, value, complement),
        "clear-commuted" => new IrBinary(IrBinaryOp.And, complement, value),
        "flip" => new IrBinary(IrBinaryOp.Xor, value, one),
        _ => new IrBinary(IrBinaryOp.Xor, one, value)
      };
      result = combined.Type == type ? combined : new IrConvert(type, combined);
    }

    return new("Bits", [new IrFunction("F", true, result.Type, [x, n], [], new([new IrReturn(result)]))], []);
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

  /// <summary>The bits of a result, a truth value as 0 or 1.</summary>
  private static ulong Bits(object? result) => result switch {
    bool truth => truth ? 1UL : 0UL,
    ulong unsigned => unsigned,
    _ => unchecked((ulong)Convert.ToInt64(result, CultureInfo.InvariantCulture))
  };

  /// <summary>The expected result, computed from the definition: the value widened as C# widens it, the position modulo the shift width.</summary>
  private static ulong Expected(int width, bool signed, string form, IrIntrinsicOp op, ulong bits, int n) {
    var carrier = width < 32 ? 32 : width;
    var widened = signed && (bits & (1UL << (width - 1))) != 0 ? bits | ~Mask(width) : bits & Mask(width);
    var pattern = widened & Mask(carrier);
    var position = form.EndsWith("constant-position", StringComparison.Ordinal) ? 5 : n & (carrier - 1);
    var single = 1UL << position;
    return op switch {
      IrIntrinsicOp.TestBit => ((pattern & single) != 0) == (form != "test-eq0") ? 1UL : 0UL,
      IrIntrinsicOp.SetBit => (pattern | single) & Mask(width),
      IrIntrinsicOp.ClearBit => pattern & ~single & Mask(width),
      _ => (pattern ^ single) & Mask(width)
    };
  }

  private static void Agrees(IrModule before, IrModule after, int width, bool signed, string form, IrIntrinsicOp op, ulong bits, int n) {
    var function = before.Functions[0];
    object[] arguments = [Argument(function.Parameters[0].Type, bits), n];
    var expected = Expected(width, signed, form, op, bits, n);
    var mask = function.ReturnType == IrType.Bool ? 1UL : Mask(function.ReturnType.Bits);
    Assert.That(Bits(IrInterpreter.Call(before, function.Name, arguments)) & mask, Is.EqualTo(expected), $"original vs definition for {bits:X}, {n}");
    Assert.That(Bits(IrInterpreter.Call(after, function.Name, arguments)) & mask, Is.EqualTo(expected), $"optimized vs definition for {bits:X}, {n}");
  }

  private static ulong[] Values(int width) => [0, 1, Mask(width), 1UL << (width - 1), (1UL << (width - 1)) - 1, 0x5A5A5A5A5A5A5A5AUL & Mask(width)];

  private static readonly int[] _positions = [0, 1, 7, 8, 15, 16, 31, 32, 33, 63, 64, 65, -1, int.MinValue, int.MaxValue];

  [TestCaseSource(nameof(Forms))]
  public void EveryForm_IsProvedAtItsActualWidthAndSignedness(int width, bool signed, string form, IrIntrinsicOp op) {
    var module = Module(width, signed, form);
    Assert.That(IrVerifier.Verify(module), Is.Empty, "the input must be legal promoted IR");
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), string.Join("; ", result.Notes));
    var intrinsic = IrTree.Nodes(result.Module.Functions[0].Body).OfType<IrIntrinsic>().Single();
    Assert.That(intrinsic.Op, Is.EqualTo(op));
    Assert.That(IrShape.IsTrapFree(intrinsic), Is.True, "the position is always within the width");
    Assert.That(result.Notes.Single(note => note.StartsWith("O0108 applied: ", StringComparison.Ordinal)),
      Does.StartWith($"O0108 applied: {op}.{intrinsic.Arguments[0].Type} from ").And.EndWith("[proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var bits in Values(width))
      foreach (var n in _positions)
        Agrees(module, result.Module, width, signed, form, op, bits, n);
  }

  [TestCaseSource(nameof(EightBitForms))]
  public void EveryEightBitValueAtEveryPosition_AgreesWithTheDefinition(int width, bool signed, string form, IrIntrinsicOp op) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    for (var bits = 0UL; bits < 256; ++bits)
      for (var n = -2; n <= 70; ++n)
        Agrees(before, after.Module, width, signed, form, op, bits, n);
  }

  [TestCase(64, false, "set", "narrow-one", "mask computed at another width")]
  [TestCase(64, true, "clear", "narrow-one", "mask computed at another width")]
  [TestCase(64, false, "test-mask", "narrow-one", "mask computed at another width")]
  [TestCase(32, false, "set", "two", "shifted constant is not 1")]
  [TestCase(32, false, "flip-commuted", "two", "shifted constant is not 1")]
  [TestCase(32, true, "clear", "two", "shifted constant is not 1")]
  [TestCase(32, false, "test-mask", "two", "shifted constant is not 1")]
  [TestCase(32, false, "test-shift", "field", "field wider than one bit")]
  [TestCase(32, true, "test-shift-commuted", "field", "field wider than one bit")]
  public void RegisteredCounterexamples_AreRefused(int width, bool signed, string form, string nearMiss, string reason) {
    var module = Module(width, signed, form, nearMiss);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0108 not applied: " + reason));
  }

  [Test]
  public void TheNarrowMask_SetsTheUpperHalfAndAnotherBit() {
    var module = Module(64, false, "set", "narrow-one");
    Assert.That(IrInterpreter.Call(module, "F", [0UL, 31]), Is.EqualTo(0xFFFFFFFF80000000UL));
    Assert.That(IrInterpreter.Call(module, "F", [0UL, 40]), Is.EqualTo(0x100UL));
  }

  [TestCase(32, false, "set", "constant")]
  [TestCase(32, false, "test-mask", "mask-one")]
  public void ACanonicalOrImpossibleForm_IsSilent(int width, bool signed, string form, string nearMiss) {
    var module = Module(width, signed, form, nearMiss);
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Is.Empty);
    Assert.That(prover.Calls, Is.Zero);
  }

  [TestCase("public static int F(int x, int n) => x | (n << 3);")]
  [TestCase("public static int F(int x, int n, int m) => x & (n << m);")]
  [TestCase("public static bool F(int x, int n) => ((x >> n) & 1) != 2;")]
  [TestCase("public static bool F(int x, int n) => ((x >> n) & 0) != 0;")]
  [TestCase("public static bool F(int x, int n) => (x & n) != 0;")]
  [TestCase("public static bool F(int x, int n) => x != n;")]
  [TestCase("public static int F(int x, int n) => x & ~n;")]
  [TestCase("public static int F(int x, int n) => x ^ (int)(byte)n;")]
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
    var module = Module(width, false, "test-shift");
    var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0108 not applied: " + reason));
  }

  [Test]
  public void AnUnknownProof_IsARefusal() {
    var module = Module(32, false, "flip");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0108 not applied: proof unknown"));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Module(32, false, "set");
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

  [TestCase("public static uint F(uint[] a, int i) => a[0] | (1u << i);")]
  [TestCase("public static bool F(uint x, int[] p) => ((x >> p[0]) & 1) != 0;")]
  public void AValueOrPositionThatMayTrap_IsRefused(string method) {
    var module = PrototypeFixture.Import("Trapping.cs", PrototypeFixture.Wrap(method));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0108 not applied: operand may trap or have effects"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [TestCase("TestShift", "TestBit.u32 from shift-and-mask")]
  [TestCase("TestMask", "TestBit.u32 from mask-and-compare")]
  [TestCase("TestArithmetic", "TestBit.i32 from shift-and-mask")]
  [TestCase("Set", "SetBit.u32 from or-mask")]
  [TestCase("Clear", "ClearBit.u32 from and-not-mask")]
  [TestCase("Flip", "ToggleBit.u32 from xor-mask")]
  [TestCase("SetByte", "SetBit.i32 from or-mask")]
  public void TheExecutableFixtures_AreProved(string function, string report) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(after.Notes, Does.Contain($"O0108 applied: {report} [proof: smt-bitvector]"));
    TestContext.Out.WriteLine("Before:\n" + IrText.Of(before) + "After:\n" + IrText.Of(after.Module) + string.Join("\n", after.Notes));
  }

  [TestCase("SetNarrowOne", "mask computed at another width")]
  [TestCase("SetTwo", "shifted constant is not 1")]
  [TestCase("Field", "field wider than one bit")]
  public void TheExecutableCounterexamples_AreRefused(string function, string reason) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0108 not applied: " + reason));
  }

  [TestCaseSource(nameof(WideForms))]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithTheDefinition(int width, bool signed, string form, IrIntrinsicOp op) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(108);
    var bytes = new byte[12];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      Agrees(before, after.Module, width, signed, form, op, BitConverter.ToUInt64(bytes, 0), BitConverter.ToInt32(bytes, 8));
    }

    TestContext.Out.WriteLine($"O0108 {form}, {(signed ? "signed" : "unsigned")} {width} bits: seed 108, 10,000 inputs, zero mismatches.");
  }
}
