using System;
using System.Collections.Generic;
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

/// <summary>De Bruijn bit scans on the actual tree IR the C# frontend writes, with their zero case and constant tables.</summary>
[TestFixture]
public sealed class ZeroCountDeBruijnRecognitionTests {

  private sealed class InconclusiveProver : IRecognitionProver {
    public int Calls { get; private set; }
    public RecognitionProof Prove(string id, IrExpr before, IrExpr after, RuleContext context) {
      ++this.Calls;
      return new(false, "proof unknown");
    }
  }

  private static OptimizerOptions Options(IRecognitionProver? prover) => RecognitionFixtures.Options with {
    Prover = prover,
    Disabled = RuleTable.Default.Ids.Where(id => id != "O0104").ToHashSet()
  };

  private static IrModule Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0104", "debruijn.cs");
    var module = PrototypeFixture.Import("debruijn.cs", File.ReadAllText(path));
    return module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
  }

  private static IrModule Source(string method, params string[] tables)
    => PrototypeFixture.Import("Scan.cs", PrototypeFixture.Wrap(string.Join("\n", tables) + "\n" + method));

  /// <summary>The expected count from the BCL, with the table's first entry (0) for zero.</summary>
  private static int Expected(string form, ulong value, int width) => value == 0 ? 0
    : form.StartsWith("Trailing", StringComparison.Ordinal) ? BitOperations.TrailingZeroCount(value)
    : width - 1 - (BitOperations.LeadingZeroCount(value) - (64 - width));

  private static object Argument(int width, ulong value) => width == 32 ? (object)unchecked((uint)value) : value;

  private static void Agrees(IrModule before, IrModule after, string form, int width, ulong value) {
    var name = before.Functions[0].Name;
    var argument = Argument(width, value);
    Assert.That(IrInterpreter.Call(before, name, [argument]), Is.EqualTo(Expected(form, value, width)), $"original for {value:X}");
    Assert.That(IrInterpreter.Call(after, name, [argument]), Is.EqualTo(Expected(form, value, width)), $"optimized for {value:X}");
  }

  /// <summary>Every value the isolated bit or the smear can take, their neighbours, zero and the extremes.</summary>
  private static IEnumerable<ulong> Values(int width) {
    var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    yield return 0;
    yield return mask;
    for (var bit = 0; bit < width; ++bit) {
      var single = 1UL << bit;
      yield return single;
      yield return (single - 1) & mask;
      yield return (single | 1) & mask;
      yield return (mask << bit) & mask;
      yield return (0x9E3779B97F4A7C15UL & mask) | single;
    }
  }

  [TestCase("TrailingZeros", 32, "TrailingZeroCount.u32")]
  [TestCase("FloorLog2", 32, "LeadingZeroCount.u32")]
  [TestCase("TrailingZeros64", 64, "TrailingZeroCount.u64")]
  [TestCase("FloorLog64", 64, "LeadingZeroCount.u64")]
  public void EveryForm_IsProvedAndAgreesWithTheBcl(string form, int width, string report) {
    var before = Fixture(form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    Assert.That(after.Notes, Does.Contain($"O0104 applied: {report} from de Bruijn lookup [proof: smt-bitvector]"));
    Assert.That(IrTree.Nodes(after.Module.Functions[0].Body).OfType<IrArrayLoad>(), Is.Empty, "the lookup is gone");
    Assert.That(IrVerifier.Verify(after.Module), Is.Empty);
    foreach (var value in Values(width))
      Agrees(before, after.Module, form, width, value);
  }

  [TestCase("TrailingZeros", 32)]
  [TestCase("FloorLog2", 32)]
  [TestCase("TrailingZeros64", 64)]
  [TestCase("FloorLog64", 64)]
  [Category("Slow")]
  public void TenThousandInputs_AgreeWithTheBcl(string form, int width) {
    var before = Fixture(form);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1));
    var random = new Random(104);
    var bytes = new byte[8];
    for (var sample = 0; sample < 10_000; ++sample) {
      random.NextBytes(bytes);
      // Shift the sample right by a random amount as well, so that every bit position is the lowest or highest set bit often.
      var value = BitConverter.ToUInt64(bytes) >> random.Next(width) << random.Next(width) & (width == 64 ? ulong.MaxValue : uint.MaxValue);
      Agrees(before, after.Module, form, width, value);
    }

    TestContext.Out.WriteLine($"O0104 {form}: seed 104, 10,000 inputs, zero mismatches.");
  }

  [Test]
  public void AnotherConstantWithItsOwnTable_IsAlsoRecognized() {
    // 0x04653ADF is another de Bruijn sequence B(2, 5); its table follows from the definition, which the rule checks independently.
    const uint constant = 0x04653ADF;
    var table = new int[32];
    for (var bit = 0; bit < 32; ++bit)
      table[unchecked((1u << bit) * constant) >> 27] = bit;

    var module = Source("public static int F(uint x) => T[((x & (uint)-x) * 0x04653ADFu) >> 27];",
      $"private static readonly byte[] T = {{ {string.Join(", ", table)} }};");
    var after = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Has.Count.EqualTo(1), string.Join("; ", after.Notes));
    foreach (var value in Values(32))
      Agrees(module, after.Module, "Trailing", 32, value);
  }

  [TestCase("WrongEntry", "table check failed at index 2")]
  [TestCase("WrittenTable", "table not constant")]
  [TestCase("ClearsLowest", "operand is not an isolated bit")]
  [TestCase("WrongShift", "index is not multiply-shift")]
  public void TheExecutableCounterexamples_AreRefused(string function, string reason) {
    var before = Fixture(function);
    var after = Optimizer.Run(before, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(IrText.Of(after.Module), Is.EqualTo(IrText.Of(before)));
    Assert.That(after.Notes, Does.Contain("O0104 not applied: " + reason));
  }

  [Test]
  public void TheWrongEntry_GivesTheWrongCount() {
    var before = Fixture("WrongEntry");
    Assert.That(IrInterpreter.Call(before, "WrongEntry", [0x10000000u]), Is.EqualTo(27), "the table, not the rule, is wrong");
  }

  [TestCase(31)]
  [TestCase(33)]
  public void ATableOfAnotherLength_IsRefused(int length) {
    var entries = string.Join(", ", Enumerable.Range(0, length).Select(entry => entry % 32));
    var module = Source("public static int F(uint x) => T[((x & (uint)-x) * 0x077CB531u) >> 27];", $"private static readonly byte[] T = {{ {entries} }};");
    var after = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain($"O0104 not applied: table check failed: {length} entries for 32 positions"));
  }

  [Test]
  public void ANarrowScan_HasAnUnsupportedWidth() {
    // The IR has no 16-bit arithmetic: a 16-bit scan multiplies at 32 bits and cuts the product to 16 before the shift.
    var module = Source("public static int F(ushort x) => T[(ushort)((x & -x) * 0x0F2D) >> 12];",
      "private static readonly byte[] T = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };");
    var after = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0104 not applied: unsupported width"));
  }

  [Test]
  public void AnOperandThatMayTrap_IsRefused() {
    var module = Source("public static int F(uint[] a) => T[((a[0] & (uint)-a[0]) * 0x077CB531u) >> 27];",
      "private static readonly byte[] T = { 0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9 };");
    var after = Optimizer.Run(module, Options(new SmtRecognitionProver()));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0104 not applied: operand may trap or have effects"));
  }

  [TestCase("public static int F(uint x) => T[x & 31];")]
  [TestCase("public static int F(uint x) => T[(x * 0x9E3779B1u) >> 27];")]
  [TestCase("public static int F(uint x) => T[x >> 27];")]
  [TestCase("public static int F(uint x, uint y) => T[(x * y) >> 27];")]
  [TestCase("public static int F(uint x) => T[((x | 1u) * 0x077CB531u) >> 27];")]
  public void OtherTableLookups_AreSilent(string method) {
    var module = Source(method, "private static readonly byte[] T = { 0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9 };");
    var prover = new InconclusiveProver();
    var after = Optimizer.Run(module, Options(prover));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Is.Empty);
    Assert.That(prover.Calls, Is.Zero);
  }

  [TestCase("missing", "proof unavailable")]
  [TestCase("timeout", "proof timeout")]
  public void AnUnprovedCandidate_IsUntouched(string mode, string reason) {
    var before = Fixture("TrailingZeros");
    var prover = mode == "missing" ? null : new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.Zero) };
    var after = Optimizer.Run(before, Options(prover));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(IrText.Of(after.Module), Is.EqualTo(IrText.Of(before)));
    Assert.That(after.Notes, Does.Contain("O0104 not applied: " + reason));
  }

  [Test]
  public void AnUnknownProof_IsARefusal() {
    var prover = new InconclusiveProver();
    var after = Optimizer.Run(Fixture("FloorLog2"), Options(prover));
    Assert.That(prover.Calls, Is.GreaterThan(0));
    Assert.That(after.Applications, Is.Empty);
    Assert.That(after.Notes, Does.Contain("O0104 not applied: proof unknown"));
  }

  [Test]
  public void DisablingTheRule_AlsoDisablesItsReports() {
    var before = Fixture("TrailingZeros");
    var prover = new InconclusiveProver();
    foreach (var options in new[] { Options(prover) with { Disabled = RuleTable.Default.Ids.ToHashSet() }, Options(prover) with { Level = OptimizationLevel.None } }) {
      var after = Optimizer.Run(before, options);
      Assert.That(after.Applications, Is.Empty);
      Assert.That(after.Notes, Is.Empty);
    }

    Assert.That(prover.Calls, Is.Zero);
  }
}
