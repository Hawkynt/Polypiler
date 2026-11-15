using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Python;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.BigInt;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// Every operation and conversion of the arbitrary-precision integer, written as Python's own
/// <c>int</c> and run by the pinned CPython in the oldest and the newest profile: every call has the
/// interpreter's outcome.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PythonBigIntTests {

  [TestCase("py3.8"), TestCase("py3.14")]
  [RequiresToolchain("python")]
  public async Task CPythonOnTheEmittedModule_AgreesWithTheInterpreter(string profile) {
    var module = BigIntCases.Module();
    var translated = PythonFixtures.Translate(module, PythonProfile.Parse(profile));
    Assert.That(translated.Problems, Is.Empty);
    var calls = BigIntCases.Calls().Select(call => new PrototypeCall(call.Function, call.Arguments)).ToList();
    var outcomes = await PythonDriver.OutcomesAsync(translated.Source!, [.. calls.Select(call => call with { Method = translated.Names![call.Method] })]);
    var mismatches = calls.Select((call, index) => (call, Python: outcomes[index], Expected: PrototypeFixture.InterpretedOutcome(module, call)))
      .Where(row => row.Python != row.Expected)
      .Select(row => $"{row.call}: python {row.Python}, interpreter {row.Expected}")
      .ToList();
    TestContext.Out.WriteLine($"{profile}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{translated.Source}");
  }

}
