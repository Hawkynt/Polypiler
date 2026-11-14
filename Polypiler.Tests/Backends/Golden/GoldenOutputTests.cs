using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Polypiler.Tests.Backends.Golden;

/// <summary>
/// The backends write with <c>--opt none</c> exactly what they wrote before they built target syntax
/// trees (#1336): every source of the prototype fixtures, every variant of both targets, byte for
/// byte, refusals included. One case per source and target; a failure names every variant that
/// differs and shows the first one. A case translates some thirty variants, which takes longer than
/// the fast tier allows.
/// </summary>
[TestFixture]
[Category("Slow")]
public sealed class GoldenOutputTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-golden-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static IEnumerable<TestCaseData> Cases()
    => from source in GoldenOutputs.All
       from target in GoldenOutputs.Targets
       select new TestCaseData(source, target).SetName($"Golden({source}, {target})");

  [TestCaseSource(nameof(Cases))]
  public void EveryVariant_IsByteIdentical(string source, string target) {
    var recorded = GoldenOutputs.Recorded(source, target);
    var variants = GoldenOutputs.Variants(target);
    Assert.That(recorded.Keys, Is.EquivalentTo(variants.Select(variant => variant.Name)), "the golden file has another set of variants; record it again");
    var differing = variants
      .Select(variant => (variant.Name, Now: GoldenOutputs.Translate(source, variant, this._directory)))
      .Where(result => result.Now != recorded[result.Name])
      .ToList();
    Assert.That(differing.Select(result => result.Name), Is.Empty,
      differing.Count == 0 ? "" : $"first difference, {differing[0].Name}:\n--- recorded\n{recorded[differing[0].Name]}\n--- now\n{differing[0].Now}");
  }

}

/// <summary>
/// Writes the golden files of <see cref="GoldenOutputs"/>. Run only on purpose, on the code whose
/// output is to become the reference:
/// <c>dotnet test Polypiler.Tests -c Release --filter "FullyQualifiedName~GoldenOutputRecorder"</c>.
/// </summary>
[TestFixture]
[Explicit("writes the golden files of the backends")]
public sealed class GoldenOutputRecorder {

  [Test]
  public void Record() {
    var workspace = Path.Combine(Path.GetTempPath(), "polypiler-golden-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    try {
      Parallel.ForEach(GoldenOutputs.All.SelectMany(source => GoldenOutputs.Targets.Select(target => (source, target))), pair => {
        var text = new StringBuilder();
        foreach (var variant in GoldenOutputs.Variants(pair.target))
          text.Append(GoldenOutputs.Translate(pair.source, variant, workspace));

        var path = GoldenOutputs.PathOf(pair.source, pair.target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString());
      });
    } finally {
      Directory.Delete(workspace, true);
    }

    Assert.That(Directory.EnumerateFiles(GoldenOutputs.Root, "*.golden", SearchOption.AllDirectories).Count(), Is.EqualTo(GoldenOutputs.All.Count * GoldenOutputs.Targets.Count));
  }

}
