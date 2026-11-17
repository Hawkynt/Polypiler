using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Optimize;

/// <summary>
/// Every pass of <see cref="IrPassPipeline.Default"/> over every fixture of the prototype and of the
/// recognition rules: the optimized module computes what the imported one computes - result, trap and
/// the arrays a call leaves behind - for every call the fixture lists (for a recognition fixture the
/// first <see cref="CallsPerFunction"/> of its calls); switched off with <c>--disable-opt</c> a pass
/// changes nothing while the others still run; and every pass of the pipeline is a ticked box of the
/// optimization checklist with a page of its own.
/// </summary>
[TestFixture]
public sealed partial class PassPipelineFixtureTests {

  /// <summary>The calls tried per public function of a recognition fixture.</summary>
  private const int CallsPerFunction = 64;

  private static readonly OptimizerOptions _options = new() { Verify = true };

  private static readonly ConcurrentDictionary<string, Lazy<(IrModule Original, OptimizationResult Optimized)>> _optimized = new(StringComparer.Ordinal);

  private static IEnumerable<TestCaseData> Fixtures()
    => PrototypeFixture.Names.Concat(RecognitionFixtures.Files).Select(fixture => new TestCaseData(fixture).SetArgDisplayNames(fixture));

  private static IEnumerable<TestCaseData> Passes()
    => IrPassPipeline.Default.Ids.Select(id => new TestCaseData(id).SetArgDisplayNames(id));

  private static bool IsRecognition(string fixture) => RecognitionFixtures.Files.Contains(fixture);

  private static IrModule Imported(string fixture)
    => IsRecognition(fixture) ? RecognitionFixtures.Imported(fixture) : PrototypeFixture.Imported(fixture);

  private static (IrModule Original, OptimizationResult Optimized) Optimized(string fixture)
    => _optimized.GetOrAdd(fixture, key => new(() => {
      var original = Imported(key);
      return (original, IrPassPipeline.Default.Run(original, _options));
    })).Value;

  private static IEnumerable<PrototypeCall> Calls(string fixture, IrModule module)
    => IsRecognition(fixture)
      ? module.Functions.Where(function => function.IsPublic).SelectMany(function => RecognitionFixtures.Calls(function).Take(CallsPerFunction))
      : PrototypeCases.For(fixture);

  /// <summary>The interpreter equivalence of the whole pipeline on a fixture; seconds on the fixtures with many calls, so <c>Slow</c>.</summary>
  [TestCaseSource(nameof(Fixtures)), Category("Slow")]
  public void Pipeline_ComputesWhatTheImportedModuleComputes(string fixture) {
    var (original, optimized) = Optimized(fixture);
    Assert.That(IrVerifier.Verify(optimized.Module), Is.Empty);
    var mismatches = new List<string>();
    foreach (var call in Calls(fixture, original)) {
      var expected = RecognitionFixtures.Outcome(original, call);
      var actual = RecognitionFixtures.Outcome(optimized.Module, call);
      if (expected != actual && mismatches.Count < 10)
        mismatches.Add($"{call}: {actual}, not {expected}");
    }

    Assert.That(mismatches, Is.Empty);
  }

  /// <summary>A pass <c>--disable-opt</c> names makes no change; the pipeline without it still computes the same.</summary>
  [TestCaseSource(nameof(Passes)), Category("Slow")]
  public void DisabledPass_ChangesNothing(string id) {
    foreach (var fixture in PrototypeFixture.First) {
      var original = PrototypeFixture.Imported(fixture);
      var result = IrPassPipeline.Default.Run(original, _options with { Disabled = new HashSet<string>(StringComparer.Ordinal) { id } });
      Assert.That(result.Applications.Select(application => application.Id), Has.None.EqualTo(id), fixture);
      foreach (var call in PrototypeCases.For(fixture))
        Assert.That(RecognitionFixtures.Outcome(result.Module, call), Is.EqualTo(RecognitionFixtures.Outcome(original, call)), $"{fixture}: {call}");
    }
  }

  /// <summary>Every pass of the pipeline is implemented in the checklist and has its page.</summary>
  [TestCaseSource(nameof(Passes))]
  public void EveryPass_IsATickedBoxWithAPage(string id) {
    var directory = Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "optimizations");
    var checklist = File.ReadAllText(Path.Combine(directory, "README.md"));
    var box = Box().Matches(checklist).FirstOrDefault(match => match.Groups[2].Value == id);
    Assert.That(box, Is.Not.Null, $"{id} is in the checklist");
    Assert.That(box!.Groups[1].Value, Is.EqualTo("x"), $"{id} is ticked");
    Assert.That(File.Exists(Path.Combine(directory, box.Groups[3].Value)), Is.True, $"{id} has its page");
  }

  [GeneratedRegex(@"^- \[( |x)\] \[(O\d{4})\]\(([^)]+)\)", RegexOptions.Multiline)]
  private static partial Regex Box();

}
