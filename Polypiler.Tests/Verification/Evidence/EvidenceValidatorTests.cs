using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Evidence;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Evidence;

/// <summary>
/// <see cref="EvidenceValidator"/>: the schema, the items each kind of scenario must attach and the
/// contradiction of a passed scenario with a failed vector, each a <c>PLY0060</c>.
/// </summary>
[TestFixture]
public sealed class EvidenceValidatorTests {

  private EvidenceFixtures _fixtures = null!;

  [SetUp]
  public void CreateRoot() => this._fixtures = new EvidenceFixtures();

  [TearDown]
  public void DeleteRoot() => this._fixtures.Dispose();

  private static string Only(string bundle) {
    var (valid, bag) = EvidenceFixtures.Validate(bundle);
    Assert.That((valid, bag.Items.Count), Is.EqualTo((false, 1)), string.Join(Environment.NewLine, bag.Items));
    var diagnostic = bag.Items[0];
    Assert.That((diagnostic.Code, diagnostic.ExitCode, (int)bag.ExitCode, diagnostic.File), Is.EqualTo(("PLY0060", ExitCode.EnvironmentError, 1, Path.Combine(bundle, "evidence.json"))));
    Assert.That(diagnostic.Message, Does.StartWith($"evidence bundle '{bundle}' "));
    return diagnostic.Message[$"evidence bundle '{bundle}' ".Length..];
  }

  private static void AssertValid(string bundle) {
    var (valid, bag) = EvidenceFixtures.Validate(bundle);
    Assert.That((valid, bag.Items.Count), Is.EqualTo((true, 0)), string.Join(Environment.NewLine, bag.Items));
  }

  /// <summary>Test design 1 and 3: a complete bundle of each kind of scenario validates.</summary>
  [TestCase("translation")]
  [TestCase("optimization")]
  [TestCase("")]
  public void CompleteBundle_Validates(string tag) {
    var collector = tag == "translation" ? EvidenceFixtures.Complete() : new EvidenceCollector();
    collector.AddToolVersion("dotnet-sdk", "10.0.404");
    if (tag == "optimization")
      collector.AddOptDiagnostics("O0100 rotate");
    AssertValid(this._fixtures.Write(collector, EvidenceFixtures.Identity("Complete " + tag, 0, tag.Length > 0 ? new[] { tag } : Array.Empty<string>())));
  }

  /// <summary>Test design 3: an untagged scenario needs only its identity, its outcome, tool versions and the CI link.</summary>
  [Test]
  public void UntaggedScenario_NeedsToolVersions()
    => Assert.That(Only(this._fixtures.Write(new EvidenceCollector(), EvidenceFixtures.Identity("Untagged"))), Is.EqualTo("is incomplete: missing tool versions"));

  /// <summary>Test design 12: each required item of a translation scenario dropped in turn is named, and only it.</summary>
  [TestCase("source", "source hash")]
  [TestCase("target", "target code hash")]
  [TestCase("manifest", "generated manifest")]
  [TestCase("tool", "tool versions")]
  [TestCase("process", "stdout/stderr/exit code")]
  [TestCase("comparison", "reference result, target result, diff, exactness policy")]
  public void TranslationWithoutAnItem_NamesIt(string without, string items)
    => Assert.That(Only(this._fixtures.Write(EvidenceFixtures.Complete(without), EvidenceFixtures.Identity("Without " + without, 0, "translation"))),
      Is.EqualTo($"is incomplete: missing {items}"));

  /// <summary>Test design 12, the seventh item: a bundle without its CI link.</summary>
  [TestCase(null)]
  [TestCase("")]
  public void WithoutCiLink_NamesIt(string? link) {
    var bundle = this._fixtures.Write(EvidenceFixtures.Complete(), EvidenceFixtures.Identity("No link", 0, "translation"));
    var json = EvidenceFixtures.Json(bundle);
    json["ciLink"] = link;
    EvidenceFixtures.Save(bundle, json);
    Assert.That(Only(bundle), Is.EqualTo("is incomplete: missing CI link"));
  }

  /// <summary>Test design 13: a passed translation scenario with a failed vector contradicts itself; a failed one does not.</summary>
  [Test]
  public void PassedWithFailedVector_Contradicts() {
    EvidenceCollector Failing() {
      var collector = EvidenceFixtures.Complete("comparison");
      collector.AddComparison(EvidenceFixtures.Compare("b90f2ae4", "aece2ad5"), new ExactnessPolicy.Exact());
      return collector;
    }

    var passed = this._fixtures.Write(Failing(), EvidenceFixtures.Identity("Passed", 0, "translation"));
    Assert.That(Only(passed), Is.EqualTo("contradicts its outcome: 1 vector(s) failed"));
    Assert.That(File.ReadAllText(Path.Combine(passed, "diff.txt")), Does.Contain("expected b90f2ae4 actual aece2ad5 @0"));
    AssertValid(this._fixtures.Write(Failing(), EvidenceFixtures.Identity("Failed", 0, "translation"), ScenarioOutcome.Failed));
    AssertValid(this._fixtures.Write(Failing(), EvidenceFixtures.Identity("Untagged")));
  }

  /// <summary>An incomplete bundle that also contradicts itself gets both diagnostics.</summary>
  [Test]
  public void IncompleteAndContradicting_GetsBoth() {
    var collector = new EvidenceCollector();
    collector.AddComparison(EvidenceFixtures.Compare("b90f2ae4", "aece2ad5"), new ExactnessPolicy.Exact());
    var (valid, bag) = EvidenceFixtures.Validate(this._fixtures.Write(collector, EvidenceFixtures.Identity("Both", 0, "translation")));
    Assert.That(valid, Is.False);
    Assert.That(bag.Items.Select(diagnostic => diagnostic.Message.Split("' ")[1]), Is.EqualTo(new[] {
      "is incomplete: missing source hash, target code hash, generated manifest, tool versions, stdout/stderr/exit code",
      "contradicts its outcome: 1 vector(s) failed"
    }));
  }

  /// <summary>Test design 14: an optimization scenario without opt diagnostics.</summary>
  [Test]
  public void OptimizationWithoutOptDiagnostics_NamesThem() {
    var collector = new EvidenceCollector();
    collector.AddToolVersion("dotnet-sdk", "10.0.404");
    Assert.That(Only(this._fixtures.Write(collector, EvidenceFixtures.Identity("Opt", 0, "optimization"))), Is.EqualTo("is incomplete: missing opt diagnostics"));
  }

  /// <summary>Test design 15: a source deleted before the bundle is written leaves no hash, whatever the tags.</summary>
  [TestCase("translation")]
  [TestCase("infra")]
  public void SourceGone_IsAMissingSourceHash(string tag) {
    var collector = tag == "translation" ? EvidenceFixtures.Complete("source") : new EvidenceCollector();
    collector.AddToolVersion("dotnet-sdk", "10.0.404");
    var source = Path.Combine(this._fixtures.Root, "work", "source.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
    File.WriteAllText(source, "abc");
    collector.AddSource(source);
    File.Delete(source);
    Assert.That(Only(this._fixtures.Write(collector, EvidenceFixtures.Identity("Gone " + tag, 0, tag))), Is.EqualTo("is incomplete: missing source hash"));
  }

  /// <summary>A copy that disappeared from the bundle after it was written is missing too.</summary>
  [TestCase("target/target.txt", "target code hash")]
  [TestCase("polypiler.manifest.json", "generated manifest")]
  [TestCase("process/run.stdout.txt", "stdout/stderr/exit code")]
  [TestCase("process/run.stderr.txt", "stdout/stderr/exit code")]
  [TestCase("diff.txt", "diff")]
  public void CopyGoneFromTheBundle_IsMissing(string file, string item) {
    var bundle = this._fixtures.Write(EvidenceFixtures.Complete(), EvidenceFixtures.Identity("Copy gone", 0, "translation"));
    File.Delete(Path.Combine(bundle, file));
    Assert.That(Only(bundle), Is.EqualTo($"is incomplete: missing {item}"));
  }

  /// <summary>Opt diagnostics and other files that are recorded must be in the bundle, whatever the tags.</summary>
  [Test]
  public void RecordedFilesGone_AreMissing() {
    var collector = new EvidenceCollector();
    collector.AddToolVersion("dotnet-sdk", "10.0.404");
    collector.AddOptDiagnostics("O0100");
    collector.AddFile("trace", EvidenceFixtures.PathOf("source.txt"));
    collector.AddFile("log", Path.Combine(this._fixtures.Root, "never.txt"));
    var bundle = this._fixtures.Write(collector, EvidenceFixtures.Identity("Files gone"));
    File.Delete(Path.Combine(bundle, "opt-report.txt"));
    File.Delete(Path.Combine(bundle, "files", "trace", "source.txt"));
    Assert.That(Only(bundle), Is.EqualTo("is incomplete: missing opt diagnostics, file 'files/trace/source.txt', file 'files/log/never.txt'"));
  }

  /// <summary>A manifest that is not valid has no state, so the manifest is missing.</summary>
  [Test]
  public void InvalidManifest_IsMissing() {
    var collector = EvidenceFixtures.Complete("manifest");
    collector.AddManifest(EvidenceFixtures.PathOf("source.txt"));
    Assert.That(Only(this._fixtures.Write(collector, EvidenceFixtures.Identity("Bad manifest", 0, "translation"))), Is.EqualTo("is incomplete: missing generated manifest"));
  }

  /// <summary>Test design 16: an <c>evidence.json</c> that breaks the schema is reported at its JSON pointer.</summary>
  [Test]
  public void SchemaError_NamesTheJsonPointer() {
    var bundle = this._fixtures.Write(EvidenceFixtures.Complete(), EvidenceFixtures.Identity("Edited", 0, "translation"));
    var json = EvidenceFixtures.Json(bundle);
    json["outcome"] = "ok";
    EvidenceFixtures.Save(bundle, json);
    Assert.That(Only(bundle), Does.StartWith("is invalid: /outcome: "));
  }

  /// <summary>An <c>evidence.json</c> that is not an object is reported at the root.</summary>
  [Test]
  public void NotAnObject_IsReportedAtTheRoot() {
    var bundle = this._fixtures.Write(new EvidenceCollector(), EvidenceFixtures.Identity("Array"));
    EvidenceFixtures.Save(bundle, new JsonArray());
    Assert.That(Only(bundle), Does.StartWith("is invalid: /: "));
  }

  /// <summary>Text that is not JSON, and a missing <c>evidence.json</c>.</summary>
  [Test]
  public void UnreadableBundle_IsReported() {
    var bundle = this._fixtures.Write(new EvidenceCollector(), EvidenceFixtures.Identity("Unreadable"));
    File.WriteAllText(Path.Combine(bundle, "evidence.json"), "{ \"schemaVersion\": 1,");
    Assert.That(Only(bundle), Does.StartWith("is invalid: /: not JSON: "));
    File.Delete(Path.Combine(bundle, "evidence.json"));
    Assert.That(Only(bundle), Is.EqualTo("is incomplete: missing evidence.json"));
  }

  /// <summary>The arguments are required.</summary>
  [Test]
  public void Validate_RequiresItsArguments() {
    Assert.That(() => EvidenceValidator.Validate(null!, new DiagnosticBag()), Throws.ArgumentNullException);
    Assert.That(() => EvidenceValidator.Validate(this._fixtures.Root, null!), Throws.ArgumentNullException);
  }

  /// <summary>Measurement: every item of PRD §6.6 is a property of the schema, 12 of 12.</summary>
  [Test]
  public void EveryPrdItem_IsRepresentableInTheSchema() {
    var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "evidence.schema.json")))!;
    var properties = schema["properties"]!.AsObject();
    var required = schema["required"]!.AsArray().Select(name => name!.GetValue<string>()).ToHashSet();
    Assert.That(EvidenceValidator.PrdItems, Has.Count.EqualTo(12));
    Assert.That(EvidenceValidator.PrdItems.Where(item => properties.ContainsKey(item.Property) && required.Contains(item.Property)).Select(item => item.Item),
      Is.EqualTo(EvidenceValidator.PrdItems.Select(item => item.Item)));
    var prd = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "prd", "03-behavior-and-testing.md"));
    Assert.That(prd, Does.Contain("Each scenario attaches: source hash, target code hash, generated manifest, tool versions, fixture values, stdout/stderr/exit code, reference result, target result, diff, exactness/tolerance policy, opt diagnostics, and CI link."));
  }

}
