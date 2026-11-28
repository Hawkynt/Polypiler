using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.VisualBasic;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// <c>Variants.bas</c> translated to Python, whose dynamic values are tuples of a tag and a payload,
/// computes in CPython what the IR interpreter computes - VBA's promotion, <c>Empty</c> and <c>Null</c>.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class VisualBasicVariantPythonTests {

  [Test, RequiresToolchain("python")]
  public async Task Python_ComputesWhatTheInterpreterComputes() {
    var module = VisualBasicVariantCalls.Module();
    var translated = PythonFixtures.Translate(module);
    Assert.That(translated.Problems, Is.Empty);
    var calls = VisualBasicVariantCalls.Calls();
    var outcomes = await PythonDriver.OutcomesAsync(translated.Source!, [.. calls.Select(call => call with { Method = translated.Names![call.Method] })]);
    Assert.That(outcomes, Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))), translated.Source);
  }

}
