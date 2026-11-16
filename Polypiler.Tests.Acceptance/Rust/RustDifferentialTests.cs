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
/// overflow checks on and off - the outcome of every call, the final contents of every array it was
/// given included, must be what the C# compiler's build of the fixture and the interpreter give. A
/// fixture with constructs the backend does not write is refused with a code of the backend that names them.
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
      Assert.That(problems.Select(problem => problem.Code), Has.All.AnyOf(RustEmitter.Inexpressible, RustEmitter.ExceptionRegion, RustEmitter.MayAlias));
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

  /// <summary>Every fixture of the prototype is written: the scalar ones, and since #460 those with arrays and tables. The list never shrinks unnoticed.</summary>
  [Test]
  public void EveryFixture_IsWritten()
    => Assert.That(PrototypeFixture.Names.Where(name => RustCrates.Check(PrototypeFixture.Imported(name)).Count > 0), Is.Empty);

}
