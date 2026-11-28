using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Frontends.Kotlin;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Kotlin.Testing;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Kotlin;

/// <summary>
/// The Kotlin fixtures (<c>tests/fixtures/kotlin</c>) against <c>kotlinc</c>: every call of every
/// fixture has one outcome in the pinned compiler's build of the original, in the recording the unit
/// tests hold the interpreter against, in the Kotlin the backend writes from the imported IR, and in
/// the C# the C# backend writes from it, built by the C# compiler. The expected outcome is never
/// computed here: it is what <c>kotlinc</c> and the JDK do.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), Layer("jvm")]
[RequiresToolchain("kotlinc"), RequiresToolchain("jdk")]
public sealed class KotlinFrontendDifferentialTests {

  private static IEnumerable<string> Fixtures => KotlinFixture.Names;

  /// <summary>
  /// The recordings are what <c>kotlinc</c> does with the original. Run with <c>POLYPILER_RECORD=1</c>
  /// to write them after a fixture or its calls changed.
  /// </summary>
  [TestCaseSource(nameof(Fixtures))]
  public async Task Recording_IsWhatKotlincDoes(string fixture) {
    var module = KotlinFixture.Import(fixture);
    var calls = KotlinFixture.Cases(module);
    var unit = new KotlinUnit(KotlinFixture.SourceOf(fixture), fixture.ToLowerInvariant(), module, KotlinFixture.NamesOf(module));
    var jar = await KotlinDriver.BuildAsync([unit]);
    var outcomes = await KotlinDriver.OutcomesAsync(jar, unit.Package, calls);
    var lines = calls.Select((call, index) => KotlinFixture.Line(call, outcomes[index])).ToArray();
    if (Environment.GetEnvironmentVariable("POLYPILER_RECORD") == "1")
      KotlinFixture.Record(fixture, lines);

    Assert.That(KotlinFixture.RecordedLines(fixture), Is.EqualTo(lines));
  }

  /// <summary>The Kotlin and the C# emitted from the imported IR do what <c>kotlinc</c> recorded, call by call.</summary>
  [TestCaseSource(nameof(Fixtures))]
  public async Task EmittedKotlinAndCSharp_DoWhatKotlincDoes(string fixture) {
    var module = KotlinFixture.Import(fixture);
    var calls = KotlinFixture.Cases(module);
    var recorded = KotlinFixture.RecordedLines(fixture);

    var kotlin = KotlinFixtures.Translate(module);
    Assert.That(kotlin.Problems, Is.Empty);
    var jar = await KotlinDriver.BuildAsync([new KotlinUnit(kotlin.Source!, kotlin.Package!, kotlin.Module!, kotlin.Names!)]);
    var roundTrip = await KotlinDriver.OutcomesAsync(jar, kotlin.Package!, calls);

    var optimized = Optimizer.Run(module, new OptimizerOptions { Target = RuleTarget.Neutral }).Module;
    var build = GeneratedCSharp.Compile(LegalizedCSharp.Emit(optimized).Single().Content, false);
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var expected = recorded[index][(recorded[index].IndexOf(" => ", StringComparison.Ordinal) + 4)..];
      var csharp = build.Outcome(calls[index]);
      if (roundTrip[index] != expected || csharp != expected)
        mismatches.Add($"{calls[index]}: kotlinc {expected}, Kotlin {roundTrip[index]}, C# {csharp}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(30), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ\n{kotlin.Source}");
  }

}
