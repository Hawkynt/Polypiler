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

/// <summary>Zero-count loops imported from C# at every width and signedness, against the BCL's bit counts.</summary>
[TestFixture]
public sealed class ZeroCountLoopRecognitionTests {

  /// <summary>Each form, whether it counts leading zeros, and the bit length variant.</summary>
  private static readonly string[] _forms = ["trailing-position", "trailing-shift", "leading-shift", "leading-moving", "unguarded-return", "unguarded-enclosing", "bit-length"];

  private static readonly (int Width, bool Signed, string Name)[] _types = [
    (8, false, "byte"), (8, true, "sbyte"), (16, false, "ushort"), (16, true, "short"), (32, false, "uint"), (32, true, "int"), (64, false, "ulong"), (64, true, "long")];

  private static IEnumerable<TestCaseData> Forms() {
    foreach (var (width, signed, _) in _types)
      foreach (var form in _forms)
        if (Supported(width, signed, form))
          yield return new TestCaseData(width, signed, form);
  }

  /// <summary>
  /// A narrow signed value is sign-extended before a logical shift, so its bit-length loop does not end for a
  /// negative value; the rule refuses it, and it is tested as a counterexample instead.
  /// </summary>
  private static bool Supported(int width, bool signed, string form) => !(form == "bit-length" && signed && width < 32);

  private static IEnumerable<TestCaseData> WideForms => Forms().Where(data => data.Arguments[0] is int width && width != 8);

  private static IEnumerable<TestCaseData> EightBitForms => Forms().Where(data => data.Arguments[0] is 8);

  private sealed class InconclusiveProver : IRecognitionProver {
    public int Calls { get; private set; }
    public RecognitionProof Prove(string id, IrExpr before, IrExpr after, RuleContext context) {
      ++this.Calls;
      return new(false, "proof unknown");
    }
  }

  private static OptimizerOptions Options(IRecognitionProver? prover) => RecognitionFixtures.Options with {
    Prover = prover,
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0103").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0103", "zerocount.cs");
    var module = PrototypeFixture.Import("zerocount.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  private static string TypeName(int width, bool signed) => _types.Single(type => type.Width == width && type.Signed == signed).Name;

  /// <summary>The C# source of one form, which returns the count and folds the shifted value in where it has one.</summary>
  internal static string Source(int width, bool signed, string form) {
    var type = TypeName(width, signed);
    var top = width switch { 8 => "0x80", 16 => "0x8000", 32 => signed ? "int.MinValue" : "0x80000000u", _ => signed ? "long.MinValue" : "0x8000000000000000UL" };
    var shiftedOne = width == 64 ? "1L" : "1";
    var body = form switch {
      "trailing-position" => $"int c = 0; while (c < {width} && ((x >> c) & 1) == 0) c++; return c;",
      "trailing-shift" => $"int c = 0; while (c < {width} && (x & 1) == 0) {{ x >>= 1; c++; }} return c + (x == 0 ? 100 : 0);",
      "leading-shift" => $"int c = 0; while (c < {width} && (x & {top}) == 0) {{ x <<= 1; c++; }} return c + (x == 0 ? 100 : 0);",
      "leading-moving" => $"int c = 0; while (c < {width} && ((x << c) & {top}) == 0) c++; return c;",
      "unguarded-return" => $"if (x == 0) return {width}; int c = 0; while ((x & {shiftedOne}) == 0) {{ x >>= 1; c++; }} return c;",
      "unguarded-enclosing" => $"int c = {width}; if (x != 0) {{ c = 0; while ((x & {shiftedOne}) == 0) {{ x >>= 1; c++; }} }} return c;",
      _ => $"int c = 0; while (x != 0) {{ x {(signed ? ">>>=" : ">>=")} 1; c++; }} return c;"
    };
    return $"public static int F({type} x) {{ {body} }}";
  }

  private static IrModule Module(int width, bool signed, string form) => PrototypeFixture.Import("ZeroCount.cs", PrototypeFixture.Wrap(Source(width, signed, form)));

  private static object Argument(int width, bool signed, ulong bits) => (width, signed) switch {
    (8, true) => unchecked((sbyte)bits), (8, false) => unchecked((byte)bits), (16, true) => unchecked((short)bits), (16, false) => unchecked((ushort)bits),
    (32, true) => unchecked((int)bits), (32, false) => unchecked((uint)bits), (64, true) => unchecked((long)bits), _ => bits
  };

  private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

  /// <summary>The count a form returns, from the BCL: the shifted value is zero after the loop exactly for a zero input.</summary>
  private static int Expected(int width, string form, ulong bits) {
    var pattern = bits & Mask(width);
    var trailing = pattern == 0 ? width : BitOperations.TrailingZeroCount(pattern);
    var leading = pattern == 0 ? width : BitOperations.LeadingZeroCount(pattern) - (64 - width);
    return form switch {
      "trailing-position" or "unguarded-return" or "unguarded-enclosing" => trailing,
      "trailing-shift" => trailing + (pattern == 0 ? 100 : 0),
      "leading-shift" => leading + (pattern == 0 ? 100 : 0),
      "leading-moving" => leading,
      _ => width - leading
    };
  }

  private static void Agrees(IrModule before, IrModule after, int width, bool signed, string form, ulong bits) {
    object[] arguments = [Argument(width, signed, bits)];
    var expected = Expected(width, form, bits);
    Assert.That(IrInterpreter.Call(before, "F", arguments), Is.EqualTo(expected), $"original vs BCL for {bits:X}");
    Assert.That(IrInterpreter.Call(after, "F", arguments), Is.EqualTo(expected), $"optimized vs BCL for {bits:X}");
  }

  private static ulong[] Edges(int width) => [0, 1, 2, 8, Mask(width), 1UL << (width - 1), (1UL << (width - 1)) - 1, 1UL << (width / 2), 0xA5A5A5A5A5A5A5A5UL & Mask(width)];

  [TestCaseSource(nameof(Forms))]
  public void EveryForm_IsProvedAtItsActualWidthAndSignedness(int width, bool signed, string form) {
    var module = Module(width, signed, form);
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Has.Count.EqualTo(1), Source(width, signed, form) + "\n" + string.Join("; ", result.Notes));
    var intrinsic = IrTree.Nodes(result.Module.Functions[0].Body).OfType<IrIntrinsic>().Single();
    var leading = form.StartsWith("leading", StringComparison.Ordinal) || form == "bit-length";
    Assert.That(intrinsic.Op, Is.EqualTo(leading ? IrIntrinsicOp.LeadingZeroCount : IrIntrinsicOp.TrailingZeroCount));
    Assert.That(IrStatements.All(result.Module.Functions[0].Body).OfType<IrWhile>(), Is.Empty, "the loop is gone");
    var kind = form == "bit-length" ? "bit-length" : form.StartsWith("unguarded", StringComparison.Ordinal) ? "unguarded" : "guarded";
    Assert.That(result.Notes.Single(note => note.StartsWith("O0103 applied: ", StringComparison.Ordinal)), Does.EndWith($"from {kind} loop [proof: smt-bitvector]"));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    foreach (var bits in Edges(width))
      Agrees(module, result.Module, width, signed, form, bits);
  }

  [TestCaseSource(nameof(EightBitForms))]
  public void EveryEightBitInput_AgreesWithTheBitCounts(int width, bool signed, string form) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    for (var bits = 0UL; bits < 256; ++bits)
      Agrees(before, after.Module, width, signed, form, bits);
  }

  [TestCase("public static int F(uint x) { int c = 0; while ((x & 1) == 0) { x >>= 1; c++; } return c; }", "input may be zero")]
  [TestCase("public static int F(uint x, uint y) { if (y == 0) return 32; int c = 0; while ((x & 1) == 0) { x >>= 1; c++; } return c; }", "input may be zero")]
  [TestCase("public static int F(int x) { if (x < 0) return 32; int c = 0; while ((x & 1) == 0) { x >>= 1; c++; } return c; }", "input may be zero")]
  [TestCase("public static int F(uint x) { if (x == 0) return 32; x = x - 1; int c = 0; while ((x & 1) == 0) { x >>= 1; c++; } return c; }", "input may be zero")]
  [TestCase("public static int F(uint x) { int c = 32; if (x == 0) { c = 0; while ((x & 1) == 0) { x >>= 1; c++; } } return c; }", "input may be zero")]
  [TestCase("public static int F(int v) { int n = 0; while (v != 0) { v >>= 1; n++; } return n; }", "arithmetic right shift")]
  [TestCase("public static int F(long v) { int n = 0; while (0 != v) { v >>= 1; n++; } return n; }", "arithmetic right shift")]
  [TestCase("public static int F(short v) { int n = 0; while (v != 0) { v >>= 1; n++; } return n; }", "arithmetic right shift")]
  [TestCase("public static int F(uint x) { int c = 0; while (c < 31 && ((x >> c) & 1) == 0) c++; return c; }", "bound differs from width")]
  [TestCase("public static int F(uint x) { int c = 0; while (c < 33 && ((x >> c) & 1) == 0) c++; return c; }", "bound differs from width")]
  [TestCase("public static int F(ulong x) { int c = 0; while (c < 32 && (x & 1) == 0) { x >>= 1; c++; } return c; }", "bound differs from width")]
  [TestCase("public static int F(uint x) { int c = 1; while (c < 32 && ((x >> c) & 1) == 0) c++; return c; }", "unrecognized loop form")]
  [TestCase("public static int F(uint x) { int c = 0; while (c < 32 && (x & 2) == 0) { x >>= 1; c++; } return c; }", "unrecognized loop form")]
  [TestCase("public static int F(uint x) { int c = 0; while (c < 32 && (x & 1) == 0) { x >>= 2; c++; } return c; }", "unrecognized loop form")]
  [TestCase("public static int F(uint x, uint y) { int c = 0; while (c < 32 && (x & 1) == 0) { x >>= 1; y >>= 1; c++; } return c; }", "unrecognized loop form")]
  [TestCase("public static int F(uint x) { int c = 0; while (c < 32 && (x & 1) == 0) { x >>= 1; c += 2; } return c; }", "unrecognized loop form")]
  [TestCase("public static int F(sbyte v) { int n = 0; while (v != 0) { v >>>= 1; n++; } return n; }", "unrecognized loop form")]
  [TestCase("public static int F(uint x) { int c = 0; while (x != 0) { x <<= 1; c++; } return c; }", "unrecognized loop form")]
  [TestCase("public static int F(uint x, uint y) { int c = 0; while (x != 0) { y >>= 1; c++; } return c; }", "unrecognized loop form")]
  [TestCase("public static int F(uint x) { int c = 0; while (x != 0) { c++; } return c; }", "unrecognized loop form")]
  [TestCase("public static int F(uint x, int[] log) { int c = 0; while (c < 32 && ((x >> c) & 1) == 0) { log[0] = c; c++; } return c; }", "loop body has effects")]
  public void RegisteredCounterexamples_AreRefused(string method, string reason) {
    var module = PrototypeFixture.Import("Refused.cs", PrototypeFixture.Wrap(method));
    var result = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
    Assert.That(result.Notes, Does.Contain("O0103 not applied: " + reason));
  }

  [TestCase("public static int F(int[] a, int n) { int i = 0; while (i < n && a[i] != 0) i++; return i; }")]
  [TestCase("public static int F(int n) { int i = 0; while (i < 10) i++; return i + n; }")]
  [TestCase("public static int F(uint x) { int c = 0; while (x > 5) { x >>= 1; c++; } return c; }")]
  [TestCase("public static int F(uint x, uint y) { int c = 0; while (c < 32 && x != y) { x >>= 1; c++; } return c; }")]
  [TestCase("public static int F(uint x) { int c = 0; return c + (int)x; }")]
  public void UnrelatedLoops_AreSilent(string method) {
    var module = PrototypeFixture.Import("Unrelated.cs", PrototypeFixture.Wrap(method));
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes.Where(note => note.StartsWith("O0103", StringComparison.Ordinal)), Is.Empty);
    Assert.That(prover.Calls, Is.Zero);
  }

  [TestCase("missing", "proof unavailable")]
  [TestCase("timeout", "proof timeout")]
  public void AnUnprovedCandidate_IsUntouched(string mode, string reason) {
    foreach (var form in new[] { "trailing-position", "unguarded-return", "bit-length" }) {
      var module = Module(32, false, form);
      var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
      var result = Optimizer.Run(module, Options(prover));
      Assert.That(result.Applications, Is.Empty, form);
      Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
      Assert.That(result.Notes, Does.Contain("O0103 not applied: " + reason));
    }
  }

  [Test]
  public void AnUnknownProof_IsARefusal() {
    var module = Module(32, false, "trailing-shift");
    var prover = new InconclusiveProver();
    var result = Optimizer.Run(module, Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(result.Applications, Is.Empty);
    Assert.That(result.Notes, Does.Contain("O0103 not applied: proof unknown"));
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var module = Module(32, false, "trailing-position");
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

  [TestCase("TrailingGuarded", "TrailingZeroCount.u32 from guarded loop")]
  [TestCase("TrailingUnguardedNonZero", "TrailingZeroCount.u32 from unguarded loop")]
  [TestCase("BitLength", "LeadingZeroCount.u32 from bit-length loop")]
  [TestCase("LeadingGuarded", "LeadingZeroCount.u32 from guarded loop")]
  public void TheExecutableFixtures_AreProved(string function, string report) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(after.Notes, Does.Contain($"O0103 applied: {report} [proof: smt-bitvector]"));
    TestContext.Out.WriteLine("Before:\n" + IrText.Of(before) + "After:\n" + IrText.Of(after.Module) + string.Join("\n", after.Notes));
    TestContext.Out.WriteLine("Solver: " + new Z3Solver().Version);
  }

  [TestCase("TrailingUnguarded", "input may be zero")]
  [TestCase("BitLengthArithmetic", "arithmetic right shift")]
  [TestCase("WrongBound", "bound differs from width")]
  [TestCase("StartsAtOne", "unrecognized loop form")]
  [TestCase("Logged", "loop body has effects")]
  public void TheExecutableCounterexamples_AreRefused(string function, string reason) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0103 not applied: " + reason));
  }

  [Test]
  public void TheStartingAtOneCounterexample_GivesTheWidthForOne() {
    var before = Fixture("StartsAtOne");
    Assert.That(IrInterpreter.Call(before, "StartsAtOne", [1u]), Is.EqualTo(32));
  }

  [TestCaseSource(nameof(WideForms))]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithTheBitCounts(int width, bool signed, string form) {
    var before = Module(width, signed, form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(103);
    var bytes = new byte[8];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      // A random shift of a random value reaches every count, not only the small ones of uniform inputs.
      var bits = BitConverter.ToUInt64(bytes) >> (bytes[0] % 64);
      Agrees(before, after.Module, width, signed, form, bits);
    }

    TestContext.Out.WriteLine($"O0103 {form}, {(signed ? "signed" : "unsigned")} {width} bits: seed 103, 10,000 inputs, zero mismatches.");
  }
}
