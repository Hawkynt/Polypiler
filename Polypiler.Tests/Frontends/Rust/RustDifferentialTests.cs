using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// The interpreter running the imported Rust fixtures against what <c>rustc</c> did with their
/// original source, with overflow checks on and off. The outcomes of <c>rustc</c> are recorded
/// beside the fixtures; the acceptance tests run <c>rustc</c> again, hold the recordings to it and
/// run the same calls through the C# and the JavaScript emitted from the IR.
/// </summary>
[TestFixture]
public sealed class RustDifferentialTests {

  private static IEnumerable<TestCaseData> Cases()
    => RustFixture.Names.SelectMany(fixture => RustFixture.Modes.Select(mode => new TestCaseData(fixture, mode).SetArgDisplayNames(fixture, RustFixture.NameOf(mode))));

  /// <summary>
  /// Test design (#466) 2 to 14, 16a, 16b, 19, 20 and (#463) 1 to 13, 19: every operator, method and
  /// cast on every type at its boundaries and on random operands, every control-flow form and every
  /// range form over the bound pairs, in the interpreter as rustc ran them.
  /// </summary>
  [TestCaseSource(nameof(Cases)), Category("Slow")]
  public void Interpreter_AgreesWithRustc(string fixture, OverflowChecks mode) {
    var module = RustFixture.Import(fixture, mode);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var calls = RustCases.For(fixture, module);
    var recorded = RustFixture.RecordedLines(fixture, mode);
    Assert.That(recorded, Has.Length.EqualTo(calls.Count), "the recording does not belong to these calls");
    var mismatches = calls
      .Select((call, index) => (Interpreted: RustFixture.Line(call, RustFixture.Interpreted(module, call)), Recorded: recorded[index]))
      .Where(pair => pair.Interpreted != pair.Recorded)
      .Select(pair => $"interpreter {pair.Interpreted}, rustc {pair.Recorded}")
      .ToList();
    Assert.That(mismatches.Take(30), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>Every public function of a fixture is called at least once, so none is compared on nothing.</summary>
  [TestCaseSource(nameof(Cases))]
  public void EveryPublicFunction_IsCalled(string fixture, OverflowChecks mode) {
    var module = RustFixture.Import(fixture, mode);
    var called = RustCases.For(fixture, module).Select(call => call.Method).ToHashSet();
    Assert.That(module.Functions.Where(function => function.IsPublic).Select(function => function.Name), Is.SubsetOf(called));
  }

}
