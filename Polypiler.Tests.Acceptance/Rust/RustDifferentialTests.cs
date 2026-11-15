using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// Every prototype fixture through the Rust backend: a fixture whose constructs the backend writes is
/// built by the pinned toolchain and called on every case of <see cref="PrototypeCases"/>, with
/// overflow checks on and off - the outcome of every call must be what the C# compiler's build of the
/// fixture and the interpreter give. A fixture with constructs the backend does not write yet (arrays,
/// strings, types, module state) is refused with <c>PLY1550</c>, which names them.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustDifferentialTests {

  private static IEnumerable<TestCaseData> Fixtures() => PrototypeFixture.Names.Select(name => new TestCaseData(name));

  [TestCaseSource(nameof(Fixtures))]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task Fixture_AgreesWithDotNetAndTheInterpreter_OrIsRefusedByName(string fixture) {
    var module = PrototypeFixture.Imported(fixture);
    var problems = RustCrates.Check(module);
    if (problems.Count > 0) {
      Assert.That(problems.Select(problem => problem.Code), Has.All.AnyOf(RustEmitter.Inexpressible, RustEmitter.ExceptionRegion));
      Assert.That(problems.Select(problem => problem.Message), Has.All.Match("'[^']+'"));
      return;
    }

    var calls = PrototypeCases.For(fixture);
    var expected = calls.Select(call => PrototypeFixture.CompiledOutcome(fixture, call)).ToList();
    var interpreted = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    Assert.That(interpreted, Is.EqualTo(expected));
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap }) {
      var actual = await RustRun.OutcomesAsync(module, calls, checks);
      var mismatches = calls.Select((call, index) => (call, index)).Where(pair => actual[pair.index] != expected[pair.index])
        .Select(pair => $"{pair.call}: C# {expected[pair.index]}, Rust {actual[pair.index]}").ToList();
      Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ with overflow checks {checks}");
    }
  }

  /// <summary>The fixtures the backend writes today: the scalar ones. The list grows with the families; it never shrinks unnoticed.</summary>
  [Test]
  public void ScalarFixtures_AreWritten()
    => Assert.That(PrototypeFixture.Names.Where(name => RustCrates.Check(PrototypeFixture.Imported(name)).Count == 0),
      Is.SupersetOf(new[] { "Numbers", "Operators", "wide/Conversions", "wide/Library", "wide/OperatorsWide", "wide/SplitMix64", "wide/Xorshift64", "wide/DigitSum" }));

}
