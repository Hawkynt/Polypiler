using System;
using System.IO;
using System.Text.Json.Nodes;
using Polypiler.Core;
using Polypiler.Core.Verification.Evidence;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Evidence;

/// <summary>The fixtures of <c>tests/fixtures/verify/evidence/</c>, collectors made from them and a root to write bundles to.</summary>
internal sealed class EvidenceFixtures : IDisposable {

  /// <summary>The fixture directory, relative to the repository.</summary>
  public const string Folder = "tests/fixtures/verify/evidence";

  /// <summary>SHA-256 of the three bytes <c>abc</c>, FIPS 180-4's published example.</summary>
  public const string Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

  /// <summary>SHA-256 of the empty message, FIPS 180-4's published example.</summary>
  public const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

  /// <summary>A fresh, empty root for bundles.</summary>
  public string Root { get; } = Path.Combine(Path.GetTempPath(), "polypiler-evidence-" + Guid.NewGuid().ToString("N"));

  /// <summary>The full path of a fixture.</summary>
  public static string PathOf(string name) => Path.Combine(PrototypeFixture.RepositoryRoot, Folder, name);

  /// <summary>The comparison of one vector <c>vector-1</c>.</summary>
  public static ComparisonReport Compare(string reference, string actual, ExactnessPolicy? policy = null)
    => DifferentialComparer.Compare([new TestVector("vector-1", [], null)], [Convert.FromHexString(reference)], [Convert.FromHexString(actual)], policy ?? new ExactnessPolicy.Exact());

  /// <summary>A collector with every item a <c>@translation</c> scenario needs, except the one named.</summary>
  /// <param name="without">One of <c>source</c>, <c>target</c>, <c>manifest</c>, <c>tool</c>, <c>process</c>, <c>comparison</c>; empty for none.</param>
  public static EvidenceCollector Complete(string without = "") {
    var collector = new EvidenceCollector();
    if (without != "source")
      collector.AddSource(PathOf("source.txt"));
    if (without != "target")
      collector.AddTargetCode(PathOf("target.txt"));
    if (without != "manifest")
      collector.AddManifest(PathOf("sample.manifest.json"));
    if (without != "tool")
      collector.AddToolVersion("node", "24.22.2");
    if (without != "process")
      collector.AddProcess("run", "024d0127", "", 0);
    if (without != "comparison")
      collector.AddComparison(Compare("024d0127", "024d0127"), new ExactnessPolicy.Exact());
    return collector;
  }

  /// <summary>The identity of a scenario of the feature <c>Evidence</c>.</summary>
  public static ScenarioIdentity Identity(string scenario, int row = 0, params string[] tags) => new("Evidence", scenario, tags, row);

  /// <summary>Writes a bundle under <see cref="Root"/>.</summary>
  public string Write(EvidenceCollector collector, ScenarioIdentity identity, ScenarioOutcome outcome = ScenarioOutcome.Passed)
    => EvidenceWriter.Write(collector, identity, outcome, this.Root);

  /// <summary>The <c>evidence.json</c> of a bundle.</summary>
  public static JsonObject Json(string bundle) => JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, EvidenceWriter.FileName)))!.AsObject();

  /// <summary>Replaces the <c>evidence.json</c> of a bundle.</summary>
  public static void Save(string bundle, JsonNode tree) => File.WriteAllText(Path.Combine(bundle, EvidenceWriter.FileName), tree.ToJsonString());

  /// <summary>Validates a bundle and returns the messages of its diagnostics.</summary>
  public static (bool Valid, DiagnosticBag Bag) Validate(string bundle) {
    var bag = new DiagnosticBag();
    return (EvidenceValidator.Validate(bundle, bag), bag);
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (Directory.Exists(this.Root))
      Directory.Delete(this.Root, true);
  }

}
