using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The measurement "differential against rust" of #464, #465 and #467: every public function of the
/// fixtures of functions and closures (seed 1530), memory (seed 1520) and types (seed 1540) whose
/// parameters are scalars or slices of them, called with <see cref="RustRandomCalls.PerFunction"/>
/// seeded random argument tuples (<see cref="RustRandomCalls"/>) in both overflow modes. One
/// <c>rustc</c> driver per fixture and mode runs all of them in one process; the interpreter's
/// outcome of every call - its result, the slices after it, or the class of its panic - is
/// <c>rustc</c>'s, with no mismatch.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
[RequiresToolchain("rust")]
public sealed class RustRandomDifferentialTests {

  private static readonly string[] _fixtures = ["functions", "closures", "memory", "types"];

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() => this._directory = System.IO.Directory.CreateTempSubdirectory("polypiler-rust-random-").FullName;

  [TearDown]
  public void DeleteDirectory() => System.IO.Directory.Delete(this._directory, true);

  private static IEnumerable<TestCaseData> Cases()
    => _fixtures.SelectMany(fixture => RustFixture.Modes.Select(mode => new TestCaseData(fixture, mode).SetArgDisplayNames(fixture, RustFixture.NameOf(mode))));

  [TestCaseSource(nameof(Cases))]
  public void RandomCalls_AreWhatRustcDoes(string fixture, OverflowChecks mode) {
    var module = RustFixture.Import(fixture, mode);
    var calls = RustRandomCalls.For(module, RustRandomCalls.SeedOf(fixture));
    var functions = calls.Select(call => call.Method).Distinct().Count();
    Assert.That(calls, Has.Count.EqualTo(functions * RustRandomCalls.PerFunction));
    Assert.That(functions, Is.GreaterThan(0));

    var expected = RustOracle.OutcomesOfInputFiles(this._directory, RustFixture.PathOf(fixture), module, calls, mode);
    var mismatches = calls.Select((call, index) => (call, rustc: expected[index], interpreted: RustFixture.Interpreted(module, call)))
      .Where(outcome => outcome.rustc != outcome.interpreted)
      .Select(outcome => $"{outcome.call}: rustc {outcome.rustc}, interpreter {outcome.interpreted}")
      .ToList();
    TestContext.Out.WriteLine($"{fixture} ({RustFixture.NameOf(mode)}), seed {RustRandomCalls.SeedOf(fixture)}: {functions} functions, {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

}
