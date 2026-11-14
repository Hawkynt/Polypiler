using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Evidence;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Tests.Verification.Evidence;

/// <summary><see cref="EvidenceWriter"/>: the bundle layout, the hashes of the files on disk, the CI link and the folder names.</summary>
[TestFixture]
public sealed class EvidenceWriterTests {

  private EvidenceFixtures _fixtures = null!;

  [SetUp]
  public void CreateRoot() => this._fixtures = new EvidenceFixtures();

  [TearDown]
  public void DeleteRoot() => this._fixtures.Dispose();

  /// <summary>Test design 1 and 10: every item is written, the files copied byte for byte with the hashes of FIPS 180-4.</summary>
  [Test]
  public void CompleteTranslationBundle_IsWritten() {
    var bundle = this._fixtures.Write(EvidenceFixtures.Complete(), EvidenceFixtures.Identity("A translation scenario", 0, "@translation"));
    Assert.That(bundle, Is.EqualTo(Path.Combine(Path.GetFullPath(this._fixtures.Root), "evidence", "a-translation-scenario")));
    var json = EvidenceFixtures.Json(bundle);
    Assert.That(json.Select(pair => pair.Key), Is.EqualTo(new[] {
      "schemaVersion", "feature", "scenario", "tags", "outcome", "sources", "targetCode", "manifest", "toolVersions", "fixtureValues",
      "processes", "reference", "target", "diff", "exactness", "optDiagnostics", "files", "ciLink"
    }));
    Assert.That((json["feature"]!.GetValue<string>(), json["scenario"]!.GetValue<string>(), json["outcome"]!.GetValue<string>()), Is.EqualTo(("Evidence", "A translation scenario", "passed")));
    Assert.That(json["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()), Is.EqualTo(new[] { "translation" }));
    Assert.That((json["sources"]![0]!["path"]!.GetValue<string>(), json["sources"]![0]!["sha256"]!.GetValue<string>()), Is.EqualTo(("source/source.txt", EvidenceFixtures.Abc)));
    Assert.That((json["targetCode"]![0]!["path"]!.GetValue<string>(), json["targetCode"]![0]!["sha256"]!.GetValue<string>()), Is.EqualTo(("target/target.txt", EvidenceFixtures.Empty)));
    Assert.That(File.ReadAllBytes(Path.Combine(bundle, "source", "source.txt")), Is.EqualTo("abc"u8.ToArray()));
    Assert.That(new FileInfo(Path.Combine(bundle, "target", "target.txt")).Length, Is.Zero);
    Assert.That((json["manifest"]!["path"]!.GetValue<string>(), json["manifest"]!["state"]!.GetValue<string>()), Is.EqualTo(("polypiler.manifest.json", "generated")));
    Assert.That(File.ReadAllBytes(Path.Combine(bundle, "polypiler.manifest.json")), Is.EqualTo(File.ReadAllBytes(EvidenceFixtures.PathOf("sample.manifest.json"))));
    Assert.That(json["toolVersions"]!["node"]!.GetValue<string>(), Is.EqualTo("24.22.2"));
    Assert.That(json["processes"]!.ToJsonString(), Is.EqualTo("""[{"label":"run","exitCode":0,"stdout":"process/run.stdout.txt","stderr":"process/run.stderr.txt"}]"""));
    Assert.That(File.ReadAllText(Path.Combine(bundle, "process", "run.stdout.txt")), Is.EqualTo("024d0127"));
    Assert.That((json["reference"]!["vector-1"]!.GetValue<string>(), json["target"]!["vector-1"]!.GetValue<string>()), Is.EqualTo(("024d0127", "024d0127")));
    Assert.That(json["diff"]!.GetValue<string>(), Is.EqualTo("diff.txt"));
    Assert.That(new FileInfo(Path.Combine(bundle, "diff.txt")).Length, Is.Zero);
    Assert.That(json["exactness"]!.ToJsonString(), Is.EqualTo("""{"mode":"exact","specialValues":"signed-zero"}"""));
    Assert.That((json["optDiagnostics"], json["files"]!.AsArray().Count), Is.EqualTo(((object?)null, 0)));
    Assert.That(json["ciLink"]!.GetValue<string>(), Is.EqualTo(EvidenceWriter.CiLink(Environment.GetEnvironmentVariable)));
    Assert.That(File.ReadAllText(Path.Combine(bundle, EvidenceWriter.FileName)), Does.EndWith("}\n").And.Not.Contain("\r"));
  }

  /// <summary>Test design 2 and 11: the CI link needs all three variables.</summary>
  [TestCase("https://github.com", "Hawkynt/Polypiler", "42", "https://github.com/Hawkynt/Polypiler/actions/runs/42")]
  [TestCase("https://github.com/", "Hawkynt/Polypiler", "42", "https://github.com/Hawkynt/Polypiler/actions/runs/42")]
  [TestCase("https://github.com", "Hawkynt/Polypiler", null, "local")]
  [TestCase("https://github.com", null, "42", "local")]
  [TestCase(null, "Hawkynt/Polypiler", "42", "local")]
  [TestCase("", "Hawkynt/Polypiler", "42", "local")]
  [TestCase(null, null, null, "local")]
  public void CiLink_NeedsAllThreeVariables(string? server, string? repository, string? run, string link) {
    var variables = new Dictionary<string, string?> { ["GITHUB_SERVER_URL"] = server, ["GITHUB_REPOSITORY"] = repository, ["GITHUB_RUN_ID"] = run };
    Assert.That(EvidenceWriter.CiLink(name => variables[name]), Is.EqualTo(link));
  }

  /// <summary>Test design 4: a failed scenario still gets its bundle.</summary>
  [Test]
  public void FailedScenario_GetsItsBundle() {
    var bundle = this._fixtures.Write(EvidenceFixtures.Complete(), EvidenceFixtures.Identity("Failing"), ScenarioOutcome.Failed);
    Assert.That(EvidenceFixtures.Json(bundle)["outcome"]!.GetValue<string>(), Is.EqualTo("failed"));
  }

  /// <summary>Test design 5: the rows of an outline end in their number.</summary>
  [Test]
  public void OutlineRows_EndInTheirNumber() {
    var first = this._fixtures.Write(new EvidenceCollector(), EvidenceFixtures.Identity("Titles", 1));
    var second = this._fixtures.Write(new EvidenceCollector(), EvidenceFixtures.Identity("Titles", 2));
    Assert.That((Path.GetFileName(first), Path.GetFileName(second)), Is.EqualTo(("titles-1", "titles-2")));
  }

  /// <summary>Test design 9: a process without output leaves two zero-byte files.</summary>
  [Test]
  public void ProcessWithoutOutput_LeavesZeroByteFiles() {
    var collector = new EvidenceCollector();
    collector.AddProcess("quiet", "", "", 3);
    var bundle = this._fixtures.Write(collector, EvidenceFixtures.Identity("Quiet"));
    Assert.That(new FileInfo(Path.Combine(bundle, "process", "quiet.stdout.txt")).Length, Is.Zero);
    Assert.That(new FileInfo(Path.Combine(bundle, "process", "quiet.stderr.txt")).Length, Is.Zero);
    Assert.That(EvidenceFixtures.Json(bundle)["processes"]![0]!["exitCode"]!.GetValue<int>(), Is.EqualTo(3));
  }

  /// <summary>Test design 17: two scenarios with the same slug keep two folders; writing one again rewrites its own.</summary>
  [Test]
  public void SameSlug_GetsANumber_AndARewriteKeepsItsFolder() {
    var first = new EvidenceCollector();
    var second = new EvidenceCollector();
    var firstBundle = this._fixtures.Write(first, EvidenceFixtures.Identity("Same title"));
    var secondBundle = this._fixtures.Write(second, EvidenceFixtures.Identity("Same  title!"));
    Assert.That((Path.GetFileName(firstBundle), Path.GetFileName(secondBundle)), Is.EqualTo(("same-title", "same-title-2")));
    Assert.That(this._fixtures.Write(first, EvidenceFixtures.Identity("Same title")), Is.EqualTo(firstBundle));
    Assert.That(Directory.Exists(firstBundle) && Directory.Exists(secondBundle), Is.True);
  }

  /// <summary>A bundle is deleted and rewritten: a file of an earlier write is gone.</summary>
  [Test]
  public void Rewrite_DeletesWhatWasThere() {
    var collector = new EvidenceCollector();
    var bundle = this._fixtures.Write(collector, EvidenceFixtures.Identity("Rewritten"));
    var stray = Path.Combine(bundle, "stray", "old.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
    File.WriteAllText(stray, "old");
    File.SetAttributes(stray, FileAttributes.ReadOnly);
    this._fixtures.Write(collector, EvidenceFixtures.Identity("Rewritten"));
    Assert.That(File.Exists(stray), Is.False);
  }

  /// <summary>Test design 15: a file gone before the bundle is written is recorded without a hash and not copied.</summary>
  [Test]
  public void MissingFile_HasNoHash() {
    var collector = new EvidenceCollector();
    var gone = Path.Combine(this._fixtures.Root, "gone.txt");
    collector.AddSource(gone);
    collector.AddManifest(gone);
    var bundle = this._fixtures.Write(collector, EvidenceFixtures.Identity("Gone"));
    var json = EvidenceFixtures.Json(bundle);
    Assert.That((json["sources"]![0]!["path"]!.GetValue<string>(), json["sources"]![0]!["sha256"]), Is.EqualTo(("source/gone.txt", (object?)null)));
    Assert.That((json["manifest"]!["sha256"], json["manifest"]!["state"]), Is.EqualTo(((object?)null, (object?)null)));
    Assert.That(Directory.Exists(Path.Combine(bundle, "source")), Is.False);
  }

  /// <summary>A file that is not a valid manifest is copied, with no state.</summary>
  [Test]
  public void InvalidManifest_HasNoState() {
    var collector = new EvidenceCollector();
    collector.AddManifest(EvidenceFixtures.PathOf("source.txt"));
    var json = EvidenceFixtures.Json(this._fixtures.Write(collector, EvidenceFixtures.Identity("Invalid manifest")));
    Assert.That((json["manifest"]!["sha256"]!.GetValue<string>(), json["manifest"]!["state"]), Is.EqualTo((EvidenceFixtures.Abc, (object?)null)));
  }

  /// <summary>Files of the same name get a number; other files go to <c>files/&lt;role&gt;/</c>.</summary>
  [Test]
  public void SameFileNames_AreNumbered() {
    var collector = new EvidenceCollector();
    collector.AddSource(EvidenceFixtures.PathOf("source.txt"));
    collector.AddSource(EvidenceFixtures.PathOf("source.txt"));
    collector.AddFile("trace", EvidenceFixtures.PathOf("target.txt"));
    collector.AddFile("trace", EvidenceFixtures.PathOf("target.txt"));
    collector.AddFile("log", EvidenceFixtures.PathOf("source.txt"));
    var json = EvidenceFixtures.Json(this._fixtures.Write(collector, EvidenceFixtures.Identity("Names")));
    Assert.That(json["sources"]!.AsArray().Select(source => source!["path"]!.GetValue<string>()), Is.EqualTo(new[] { "source/source.txt", "source/source-2.txt" }));
    Assert.That(json["files"]!.ToJsonString(), Is.EqualTo(
      $$"""[{"role":"trace","path":"files/trace/target.txt","sha256":"{{EvidenceFixtures.Empty}}"},{"role":"trace","path":"files/trace/target-2.txt","sha256":"{{EvidenceFixtures.Empty}}"},{"role":"log","path":"files/log/source.txt","sha256":"{{EvidenceFixtures.Abc}}"}]"""));
  }

  /// <summary>A failed vector is one line of the diff; the opt diagnostics and fixture values are kept.</summary>
  [Test]
  public void FailedVector_IsALineOfTheDiff() {
    var collector = new EvidenceCollector();
    collector.AddComparison(EvidenceFixtures.Compare("b90f2ae4", "aece2ad5"), new ExactnessPolicy.Exact());
    collector.AddOptDiagnostics("O0100 rotate");
    collector.AddOptDiagnostics("O0101 popcount");
    collector.AddFixtureValue("input", "abc");
    var bundle = this._fixtures.Write(collector, EvidenceFixtures.Identity("Diff"));
    Assert.That(File.ReadAllText(Path.Combine(bundle, "diff.txt")), Is.EqualTo("vector-1: expected b90f2ae4 actual aece2ad5 @0\n"));
    Assert.That(File.ReadAllText(Path.Combine(bundle, "opt-report.txt")), Is.EqualTo("O0100 rotate\nO0101 popcount"));
    var json = EvidenceFixtures.Json(bundle);
    Assert.That((json["optDiagnostics"]!.GetValue<string>(), json["fixtureValues"]!["input"]!.GetValue<string>()), Is.EqualTo(("opt-report.txt", "abc")));
  }

  /// <summary>A vector the reference failed on before any comparison: no outcome, so empty results and an empty diff.</summary>
  [Test]
  public void NotCompared_HasEmptyResults() {
    var collector = new EvidenceCollector();
    collector.AddComparison(ComparisonReport.NotCompared(1), new ExactnessPolicy.Exact());
    var bundle = this._fixtures.Write(collector, EvidenceFixtures.Identity("Not compared"));
    var json = EvidenceFixtures.Json(bundle);
    Assert.That((json["reference"]!.ToJsonString(), json["target"]!.ToJsonString()), Is.EqualTo(("{}", "{}")));
    Assert.That(new FileInfo(Path.Combine(bundle, "diff.txt")).Length, Is.Zero);
  }

  /// <summary>The policy is written as the vector file declares it, for every mode.</summary>
  [Test]
  public void Exactness_IsWrittenAsTheVectorFileDeclaresIt() {
    string Written(ExactnessPolicy policy) {
      var collector = new EvidenceCollector();
      collector.AddComparison(EvidenceFixtures.Compare("00000000", "00000000", policy), policy);
      return EvidenceFixtures.Json(this._fixtures.Write(collector, EvidenceFixtures.Identity(Guid.NewGuid().ToString("N"))))["exactness"]!.ToJsonString();
    }

    Assert.That(Written(new ExactnessPolicy.Exact(FloatFormat.F64) { SpecialValues = SpecialValues.Bitwise }), Is.EqualTo("""{"mode":"exact","format":"f64","specialValues":"bitwise"}"""));
    Assert.That(Written(new ExactnessPolicy.Ulp(2, FloatFormat.F32, policy: "accurate")), Is.EqualTo("""{"mode":"ulp","bound":2,"policy":"accurate","format":"f32","specialValues":"signed-zero"}"""));
    Assert.That(Written(new ExactnessPolicy.Relative(0.5, FloatFormat.F32, reason: "series")), Is.EqualTo("""{"mode":"relative","bound":0.5,"reason":"series","format":"f32","specialValues":"signed-zero"}"""));
    Assert.That(Written(new ExactnessPolicy.Absolute(0.25, FloatFormat.F32, reason: "sum") { SpecialValues = SpecialValues.Class }), Is.EqualTo("""{"mode":"absolute","bound":0.25,"reason":"sum","format":"f32","specialValues":"class"}"""));
  }

  /// <summary>The arguments are required.</summary>
  [Test]
  public void Write_RequiresItsArguments() {
    var identity = EvidenceFixtures.Identity("x");
    Assert.That(() => EvidenceWriter.Write(null!, identity, ScenarioOutcome.Passed, this._fixtures.Root), Throws.ArgumentNullException);
    Assert.That(() => EvidenceWriter.Write(new EvidenceCollector(), null!, ScenarioOutcome.Passed, this._fixtures.Root), Throws.ArgumentNullException);
    Assert.That(() => EvidenceWriter.Write(new EvidenceCollector(), identity, ScenarioOutcome.Passed, null!), Throws.ArgumentNullException);
    Assert.That(() => EvidenceWriter.CiLink(null!), Throws.ArgumentNullException);
  }

}
