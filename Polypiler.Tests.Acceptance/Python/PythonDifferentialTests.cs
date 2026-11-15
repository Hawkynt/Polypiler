using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// Every prototype fixture the Python backend writes runs under the pinned CPython and has, call for
/// call, the outcome the C# compiler's build of it and the IR interpreter have; a fixture with a
/// construct outside what the backend writes is refused with a diagnostic of its own that names the
/// construct. The expected outcome of a call is never computed here: it is the C# compiler's build,
/// the bit-by-bit reference of the integer operations, or, for floating point, the interpreter, which
/// runs the same portable implementations the Python module carries.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PythonDifferentialTests {

  [TestCaseSource(typeof(PythonFixtures), nameof(PythonFixtures.Prototype))]
  [RequiresToolchain("python")]
  public async Task Python_Interpreter_AndCompiledFixture_Agree(string fixture) {
    var calls = PrototypeCases.For(fixture);
    var module = PrototypeFixture.Imported(fixture);
    await Compare(fixture, PythonFixtures.Translate(module), calls, call => PrototypeFixture.CompiledOutcome(fixture, call), call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  [TestCaseSource(typeof(BitsFixtures), nameof(BitsFixtures.CSharp))]
  [RequiresToolchain("python")]
  public async Task BitsFixture_AgreesWithTheCompiledFixture(string fixture) {
    var module = BitsFixtures.Imported(fixture);
    await Compare(fixture, PythonFixtures.Translate(module), BitsFixtures.Calls(fixture), call => BitsFixtures.CompiledOutcome(fixture, call), call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  /// <summary>
  /// Every integer operation on every width it has, on the boundaries of its arguments and on random
  /// tuples (seed 2026), against the bit-by-bit reference; in the oldest profile, which counts bits
  /// with the library's helper, and in the newest, which has <c>int.bit_count</c>.
  /// </summary>
  [TestCase("py3.8"), TestCase("py3.14")]
  [RequiresToolchain("python")]
  public async Task IntegerOperations_AgreeWithTheReference(string profile) {
    var module = IntegerCases.Module(IntegerCases.All);
    var generator = new Random(IntegerCases.Seed);
    var cases = new Dictionary<PrototypeCall, IntegerCase>(ReferenceEqualityComparer.Instance);
    foreach (var @case in IntegerCases.All)
      foreach (var call in IntegerCases.Calls(@case, generator, 200))
        cases[call] = @case;

    await Compare("integer operations", PythonFixtures.Translate(module, PythonProfile.Parse(profile)), [.. cases.Keys],
      call => IntegerCases.Expected(cases[call], call), call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  /// <summary>The floating-point algorithms: every operation the Python module carries is the portable implementation, so its bits are the interpreter's.</summary>
  [TestCase("float/Dct8"), TestCase("float/BoxMuller"), TestCase("float/Haversine")]
  [RequiresToolchain("python")]
  public async Task FloatAlgorithm_HasTheBitsOfTheInterpreter(string fixture) {
    var module = PrototypeFixture.Imported(fixture);
    List<PrototypeCall> calls = fixture switch {
      "float/Dct8" => [
        new("Transform", [new double[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new double[8]]),
        new("Transform", [new double[] { 0.5, -1.25, 3.75, 0, -0.0, 1e-3, 255, -128 }, new double[8]]),
        new("TransformSingle", [new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new float[8]])
      ],
      "float/BoxMuller" => [
        new("Normals", [new double[] { 0.5, 0.25, 0.9, 0.125, 1e-6, 0.999, 1.0, 0.0 }, new double[8]]),
        new("NormalsSingle", [new float[] { 0.5f, 0.25f, 0.9f, 0.125f, 1e-6f, 0.999f, 1.0f, 0.0f }, new float[8]])
      ],
      _ => [
        new("Distance", [52.5200, 13.4050, 48.8566, 2.3522, 6371.0]),
        new("Distance", [-33.8688, 151.2093, 40.7128, -74.0060, 6371.0]),
        new("Distance", [0.0, 0.0, 0.0, 180.0, 6371.0]),
        new("DistanceSingle", [52.52f, 13.405f, 48.8566f, 2.3522f, 6371.0f])
      ]
    };
    await Compare(fixture, PythonFixtures.Translate(module), calls, call => PrototypeFixture.InterpretedOutcome(module, call), call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  /// <summary>The state of a module lives from one call to the next: a sequence of calls on one import has the outcomes the fixture's issue states.</summary>
  [TestCaseSource(typeof(StateFixtures), nameof(StateFixtures.All))]
  [RequiresToolchain("python")]
  public async Task ModuleState_KeepsItsValueBetweenCalls((string File, string Class, string Script) fixture) {
    var calls = StateFixtures.Sequence(fixture.File);
    var translated = PythonFixtures.Translate(StateFixtures.SourceOf(fixture.File + ".cs"), fixture.File + ".cs");
    if (Refused(fixture.File, translated))
      return;

    var outcomes = await PythonDriver.OutcomesAsync(translated.Source!, [.. calls.Select(call => call with { Method = translated.Names![call.Method] })]);
    Assert.That(outcomes, Is.EqualTo(StateFixtures.Expected(fixture.File)), translated.Source);
  }

  /// <summary>Whether a translation was refused, which then names its constructs with a code of the Python backend.</summary>
  private static bool Refused(string fixture, PythonTranslation translated) {
    if (translated.Source is not null)
      return false;

    Assert.That(translated.Problems, Is.Not.Empty.And.All.Match(@"^error PLY18(5[01]|58|74): "), "a refusal names its construct with a code of the Python backend");
    TestContext.Out.WriteLine($"{fixture} is refused: {string.Join("; ", translated.Problems)}");
    return true;
  }

  /// <summary>Runs the calls through the translation and compares every outcome with the oracle and the interpreter.</summary>
  private static async Task Compare(string fixture, PythonTranslation translated, IReadOnlyList<PrototypeCall> calls, Func<PrototypeCall, string> expected, Func<PrototypeCall, string> interpreted) {
    if (Refused(fixture, translated))
      return;

    var names = translated.Names!;
    var outcomes = await PythonDriver.OutcomesAsync(translated.Source!, [.. calls.Select(call => call with { Method = names[call.Method] })]);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var oracle = expected(calls[index]);
      var reference = interpreted(calls[index]);
      if (outcomes[index] != oracle || reference != oracle)
        mismatches.Add($"{calls[index]}: oracle {oracle}, python {outcomes[index]}, interpreter {reference}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{translated.Source}");
  }

}
