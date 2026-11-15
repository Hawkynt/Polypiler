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
  public Task IntegerOperations_AgreeWithTheReference(string profile) => IntegerOperations(profile, IntegerCases.Seed, 200);

  /// <summary>
  /// The differential of #447 at its own size: every integer operation on every width on the
  /// boundaries of its arguments and on 10 000 random tuples (seed 1850), against the bit-by-bit
  /// reference, in the oldest and in the newest profile.
  /// </summary>
  [TestCase("py3.8"), TestCase("py3.14")]
  [RequiresToolchain("python"), Category("Slow"), Category("Exhaustive")]
  public Task IntegerOperations_AgreeWithTheReference_OnTheRandomTuplesOfTheIssue(string profile) => IntegerOperations(profile, 1850, 10_000);

  private static async Task IntegerOperations(string profile, int seed, int random) {
    var module = IntegerCases.Module(IntegerCases.All);
    var generator = new Random(seed);
    var cases = new Dictionary<PrototypeCall, IntegerCase>(ReferenceEqualityComparer.Instance);
    foreach (var @case in IntegerCases.All)
      foreach (var call in IntegerCases.Calls(@case, generator, random))
        cases[call] = @case;

    // Every integer operation has a Python form: a refusal here would pass the comparison by running nothing.
    var translated = PythonFixtures.Translate(module, PythonProfile.Parse(profile));
    Assert.That(translated.Problems, Is.Empty);
    await Compare("integer operations", translated, [.. cases.Keys],
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

  /// <summary>
  /// The async fixture (#1330): every async function is an <c>async def</c> the driver runs with
  /// <c>asyncio.run</c>, and every outcome - the staged sum, the completed tasks, the gathered squares,
  /// the first fault in argument order, the faults before and after the first await - is .NET's.
  /// </summary>
  [Test]
  [RequiresToolchain("python")]
  public async Task AsyncFixture_AgreesWithDotNet() {
    var module = AsyncFixtures.Imported();
    var translated = PythonFixtures.Translate(module);
    Assert.That(translated.Problems, Is.Empty);
    await Compare("async/Pipeline", translated, [.. AsyncFixtures.Calls()], AsyncFixtures.DotNetOutcome, call => AsyncFixtures.InterpretedOutcome(module, call));
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

  /// <summary>The most calls one run of the interpreter takes.</summary>
  private const int Part = 200_000;

  /// <summary>The fewest calls a run takes when the calls are shared among the processors: below, starting CPython costs more than it saves.</summary>
  private const int MinimumPart = 2_000;

  /// <summary>Runs the calls through the translation and compares every outcome with the oracle and the interpreter.</summary>
  private static async Task Compare(string fixture, PythonTranslation translated, IReadOnlyList<PrototypeCall> calls, Func<PrototypeCall, string> expected, Func<PrototypeCall, string> interpreted) {
    if (Refused(fixture, translated))
      return;

    var names = translated.Names!;
    // The calls go to CPython in parts, each within the memory the sandbox grants one run, one part per
    // processor at a time; meanwhile the oracle and the interpreter work through the calls in parallel.
    var size = Math.Clamp((calls.Count + Environment.ProcessorCount - 1) / Environment.ProcessorCount, MinimumPart, Part);
    var running = Task.WhenAll(calls.Chunk(size).Select(part => PythonDriver.OutcomesAsync(translated.Source!, [.. part.Select(call => call with { Method = names[call.Method] })])));
    var references = calls.AsParallel().AsOrdered().Select(call => (Oracle: expected(call), Interpreter: interpreted(call))).ToList();
    var outcomes = (await running).SelectMany(part => part).ToList();

    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var (oracle, reference) = references[index];
      if (outcomes[index] != oracle || reference != oracle)
        mismatches.Add($"{calls[index]}: oracle {oracle}, python {outcomes[index]}, interpreter {reference}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{translated.Source}");
  }

}
