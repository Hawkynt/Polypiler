using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Kotlin;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Kotlin;

/// <summary>
/// Every prototype fixture the Kotlin backend writes compiles with the pinned <c>kotlinc</c>, warnings
/// as errors, runs on the pinned JDK and has, call for call, the outcome the C# compiler's build of it
/// and the IR interpreter have; a fixture with a construct outside what the backend writes is refused
/// with a diagnostic of its own that names the construct. The expected outcome of a call is never
/// computed here: it is the C# compiler's build, the bit-by-bit reference of the integer operations,
/// or, for floating point, the interpreter, which runs the same portable implementations the Kotlin
/// file carries. All fixtures compile in one run of the compiler; each then runs in a JVM of its own.
/// </summary>
[TestFixture, Layer("jvm")]
[Category("EndToEnd")]
public sealed class KotlinDifferentialTests {

  /// <summary>The fixtures of the prototype and of the bit operations, translated, each with the module it was emitted from.</summary>
  private static readonly Lazy<IReadOnlyDictionary<string, KotlinTranslation>> _translations = new(() => {
    var translations = new Dictionary<string, KotlinTranslation>(StringComparer.Ordinal);
    foreach (var fixture in PrototypeFixture.Names)
      translations[fixture] = KotlinFixtures.Translate(PrototypeFixture.Imported(fixture));
    foreach (var fixture in BitsFixtures.CSharp)
      translations["bits/" + fixture] = KotlinFixtures.Translate(BitsFixtures.Imported(fixture));
    return translations;
  });

  private static readonly Lazy<Task<ReadOnlyMemory<byte>>> _jar = new(() => KotlinDriver.BuildAsync([.. _translations.Value.Values.Where(translation => translation.Source is not null).Select(Unit)]));

  private static KotlinUnit Unit(KotlinTranslation translation) => new(translation.Source!, translation.Package!, translation.Module!, translation.Names!);

  [TestCaseSource(typeof(KotlinFixtures), nameof(KotlinFixtures.Prototype))]
  [RequiresToolchain("kotlinc"), RequiresToolchain("jdk")]
  public async Task Kotlin_Interpreter_AndCompiledFixture_Agree(string fixture) {
    var module = PrototypeFixture.Imported(fixture);
    await Compare(fixture, _translations.Value[fixture], PrototypeCases.For(fixture),
      call => PrototypeFixture.CompiledOutcome(fixture, call), call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  [TestCaseSource(typeof(BitsFixtures), nameof(BitsFixtures.CSharp))]
  [RequiresToolchain("kotlinc"), RequiresToolchain("jdk")]
  public async Task BitsFixture_AgreesWithTheCompiledFixture(string fixture) {
    var module = BitsFixtures.Imported(fixture);
    await Compare(fixture, _translations.Value["bits/" + fixture], BitsFixtures.Calls(fixture),
      call => BitsFixtures.CompiledOutcome(fixture, call), call => PrototypeFixture.InterpretedOutcome(module, call));
  }

  /// <summary>
  /// Every integer operation on every width it has, on the boundaries of its arguments and on random
  /// tuples (seed 2026), against the bit-by-bit reference, on the oldest and the newest profile and on
  /// the newest JVM target, whose class library has <c>Math.unsignedMultiplyHigh</c> and <c>Integer.compress</c>.
  /// </summary>
  [TestCase("kotlin2.0"), TestCase("kotlin2.4"), TestCase("kotlin2.4-jvm21")]
  [RequiresToolchain("kotlinc"), RequiresToolchain("jdk")]
  public async Task IntegerOperations_AgreeWithTheReference(string name) {
    var profile = KotlinProfile.Parse(name);
    var module = IntegerCases.Module(IntegerCases.All);
    var generator = new Random(IntegerCases.Seed);
    var cases = new Dictionary<PrototypeCall, IntegerCase>(ReferenceEqualityComparer.Instance);
    foreach (var @case in IntegerCases.All)
      foreach (var call in IntegerCases.Calls(@case, generator, 200))
        cases[call] = @case;

    // Every integer operation has a Kotlin form: a refusal here would pass the comparison by running nothing.
    var translated = KotlinFixtures.Translate(module, profile);
    Assert.That(translated.Problems, Is.Empty);
    var jar = await KotlinDriver.BuildAsync([Unit(translated)], profile.CompilerOptions);
    await Compare("integer operations", translated, [.. cases.Keys], call => IntegerCases.Expected(cases[call], call), call => PrototypeFixture.InterpretedOutcome(module, call), jar);
  }

  /// <summary>The floating-point algorithms: every operation the Kotlin file carries is the portable implementation or correctly rounded, so its bits are the interpreter's.</summary>
  [TestCase("float/Dct8"), TestCase("float/BoxMuller"), TestCase("float/Haversine")]
  [RequiresToolchain("kotlinc"), RequiresToolchain("jdk")]
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
    var translated = KotlinFixtures.Translate(module);
    if (Refused(fixture, translated))
      return;

    var jar = await KotlinDriver.BuildAsync([Unit(translated)]);
    await Compare(fixture, translated, calls, call => PrototypeFixture.InterpretedOutcome(module, call), call => PrototypeFixture.InterpretedOutcome(module, call), jar);
  }

  /// <summary>Whether a translation was refused, which then names its constructs with a code of the Kotlin backend.</summary>
  private static bool Refused(string fixture, KotlinTranslation translated) {
    if (translated.Source is not null)
      return false;

    Assert.That(translated.Problems, Is.Not.Empty.And.All.Match(@"^error PLY16(5[0-9]|6[0-9]|7[0-9]|8[0-9]): "), "a refusal names its construct with a code of the Kotlin backend");
    TestContext.Out.WriteLine($"{fixture} is refused: {string.Join("; ", translated.Problems)}");
    return true;
  }

  /// <summary>Runs the calls through the translation and compares every outcome with the oracle and the interpreter.</summary>
  private static async Task Compare(string fixture, KotlinTranslation translated, IReadOnlyList<PrototypeCall> calls, Func<PrototypeCall, string> expected, Func<PrototypeCall, string> interpreted, ReadOnlyMemory<byte>? jar = null) {
    if (Refused(fixture, translated))
      return;

    var running = KotlinDriver.OutcomesAsync(jar ?? await _jar.Value, translated.Package!, calls);
    var references = calls.AsParallel().AsOrdered().Select(call => (Oracle: expected(call), Interpreter: interpreted(call))).ToList();
    var outcomes = await running;
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var (oracle, reference) = references[index];
      if (outcomes[index] != oracle || reference != oracle)
        mismatches.Add($"{calls[index]}: oracle {oracle}, kotlin {outcomes[index]}, interpreter {reference}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{translated.Source}");
  }

}
