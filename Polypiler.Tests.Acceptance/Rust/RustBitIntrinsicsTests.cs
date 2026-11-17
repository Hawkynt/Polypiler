using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The bit intrinsics of #95, #96, #97 and #104 built by <c>rustc</c>: the one counts, the Boolean
/// functions, the signed field and the reversal of the bits in each byte, every width, on every
/// combination of the boundaries of their arguments and 200 random tuples (seed 2026), have the
/// outcome of the bit-by-bit reference - written with the helpers of the library at Rust 1.31, where
/// <c>leading_ones</c> and <c>reverse_bits</c> do not exist, and with the methods at Rust 1.85.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
public sealed class RustBitIntrinsicsTests {

  private static readonly IrIntrinsicOp[] _operations = [
    IrIntrinsicOp.LeadingOneCount, IrIntrinsicOp.TrailingOneCount,
    IrIntrinsicOp.Nand, IrIntrinsicOp.Nor, IrIntrinsicOp.Xnor, IrIntrinsicOp.AndNot, IrIntrinsicOp.OrNot, IrIntrinsicOp.Implies,
    IrIntrinsicOp.BitSelect, IrIntrinsicOp.Majority, IrIntrinsicOp.TernaryLogic,
    IrIntrinsicOp.ExtractBitsSigned, IrIntrinsicOp.ReverseBitsInBytes
  ];

  [TestCase("rust2018"), TestCase("rust2024")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task BitIntrinsics_ComputeTheReference(string profile) {
    var cases = IntegerCases.All.Where(@case => _operations.Contains(@case.Op)).ToList();
    var random = new Random(IntegerCases.Seed);
    var calls = cases.SelectMany(@case => IntegerCases.Calls(@case, random, 200).Select(call => (Case: @case, Call: call))).ToList();
    var module = IntegerCases.Module(cases);
    var rust = RustProfile.All.Single(candidate => candidate.Name == profile);
    var text = RustCrates.ModuleText(RustCrates.Emit(module, rust));
    Assert.That(text.Contains(".leading_ones()", StringComparison.Ordinal), Is.EqualTo(profile == "rust2024"), "leading_ones from Rust 1.46 on");
    Assert.That(text.Contains(".swap_bytes().reverse_bits()", StringComparison.Ordinal), Is.EqualTo(profile == "rust2024"), "reverse_bits from Rust 1.37 on");

    var outcomes = await RustRun.OutcomesAsync(module, [.. calls.Select(entry => entry.Call)], OverflowChecks.Panic, rust);
    var mismatches = calls.Select((entry, index) => (entry.Call, Expected: IntegerCases.Expected(entry.Case, entry.Call), Actual: outcomes[index]))
      .Where(outcome => outcome.Expected != outcome.Actual)
      .Select(outcome => $"{outcome.Call}: expected {outcome.Expected}, rustc {outcome.Actual}")
      .ToList();
    TestContext.Out.WriteLine($"{profile}: {cases.Count} signatures, {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

}
