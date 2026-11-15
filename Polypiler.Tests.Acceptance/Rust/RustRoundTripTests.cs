using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;
using BuildChecks = Polypiler.Core.Verification.Runners.OverflowChecks;
using SourceChecks = Polypiler.Core.Frontends.Rust.OverflowChecks;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// Rust to the IR and back to Rust: every Rust fixture, imported in each overflow mode, goes through
/// the Rust backend into a crate, which the pinned toolchain builds with overflow checks on and off
/// and calls on every case of <see cref="RustCases"/>. Every outcome must be what <c>rustc</c> did
/// with the original in the mode it was imported in - the recording the frontend's differential
/// tests hold to <c>rustc</c>. The build mode of the generated crate must not matter: the mode is
/// part of the IR the frontend wrote, and the backend spells every operation with its contract.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
public sealed class RustRoundTripTests {

  private static IEnumerable<TestCaseData> Cases()
    => RustFixture.Names.SelectMany(fixture => RustFixture.Modes.Select(mode => new TestCaseData(fixture, mode).SetArgDisplayNames(fixture, RustFixture.NameOf(mode))));

  [TestCaseSource(nameof(Cases))]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task Fixture_ThroughTheIrAndBack_DoesWhatRustcDid(string fixture, SourceChecks mode) {
    var module = RustFixture.Import(fixture, mode);
    Assert.That(RustCrates.Check(module), Is.Empty, "the Rust backend writes every construct the Rust frontend imports");

    var calls = RustCases.For(fixture, module);
    var recorded = RustFixture.RecordedLines(fixture, mode);
    Assert.That(recorded, Has.Length.EqualTo(calls.Count));
    foreach (var build in new[] { BuildChecks.Panic, BuildChecks.Wrap }) {
      var actual = await RustRun.OutcomesAsync(module, calls, build);
      var mismatches = calls.Select((call, index) => (call, index))
        .Select(pair => (pair.call, Expected: recorded[pair.index][(recorded[pair.index].IndexOf(" => ", System.StringComparison.Ordinal) + 4)..], Actual: RustFixture.Normalized(actual[pair.index])))
        .Where(triple => triple.Expected != triple.Actual)
        .Select(triple => $"{triple.call}: rustc {triple.Expected}, round trip {triple.Actual}").ToList();
      TestContext.Out.WriteLine($"{fixture} ({RustFixture.NameOf(mode)}, crate built with overflow checks {build}): {calls.Count} calls, {mismatches.Count} mismatches");
      Assert.That(mismatches.Take(30), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ with the crate built with overflow checks {build}");
    }
  }

}
