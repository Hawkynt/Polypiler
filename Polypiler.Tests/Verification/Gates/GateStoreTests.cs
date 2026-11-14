using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Gates;

namespace Polypiler.Tests.Verification.Gates;

/// <summary>Loading gate records: every record checked against its evidence, rejected whole when one piece fails.</summary>
[TestFixture]
public sealed class GateStoreTests {

  private TemporaryArtifacts _artifacts = null!;

  [SetUp]
  public void CreateArtifacts() => this._artifacts = new();

  [TearDown]
  public void DeleteArtifacts() => this._artifacts.Dispose();

  private static string Rejection(string fixture, string state, string reason) => $"fixture '{fixture}' claims '{state}': {reason}";

  /// <summary>The scenario "A pair is advertised at the lowest state among its fixtures", and the second valid pair with all ten states.</summary>
  [Test]
  public void TheValidRecords_GiveTheirPairsTheLowestFixtureState() {
    var (store, diagnostics) = GateFixtures.Load(GateFixtures.PathOf("valid"));
    Assert.That(diagnostics, Is.Empty);
    Assert.That(store.Pairs, Is.EqualTo(new[] { GateFixtures.CSharpToJavaScript, GateFixtures.JavaScriptToCSharp }));
    Assert.That(store.StateOf(GateFixtures.CSharpToJavaScript), Is.EqualTo(GateState.Compiled));
    Assert.That(store.FixturesOf(GateFixtures.CSharpToJavaScript).Select(fixture => (fixture.FixtureId, fixture.State)),
      Is.EqualTo(new[] { ("add32", GateState.Verified), ("mix32", GateState.Compiled) }));
    Assert.That(store.StateOf(GateFixtures.JavaScriptToCSharp), Is.EqualTo(GateState.PerformanceValidated));
    Assert.That(store.StateOf(new PairKey("kotlin", "perl", "jvm", "perl5")), Is.EqualTo(GateState.Planned));
    Assert.That(store.FixturesOf(new PairKey("kotlin", "perl", "jvm", "perl5")), Is.Empty);
  }

  [Test]
  public void Loading_ChangesNoFile() {
    var path = this._artifacts.Place("valid/" + GateFixtures.CSharpToJavaScript.FileName, GateFixtures.CSharpToJavaScript);
    var bytes = File.ReadAllBytes(path);
    var written = File.GetLastWriteTimeUtc(path);
    GateFixtures.Load(this._artifacts.Gates);
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
    Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(written));
  }

  /// <summary>Test design 11 to 14 and the measurement "invalid fixture records rejected, 5 of 5".</summary>
  [TestCase("missing-evidence.json", "add32", "parsed", "evidence file missing")]
  [TestCase("hash-mismatch.json", "add32", "parsed", "evidence hash differs")]
  [TestCase("manifest-below-state.json", "add32", "verified", "manifest state 'built' is below 'verified'")]
  [TestCase("wrong-evidence-kind.json", "add32", "proved", "evidence kind 'ir-dump' not accepted for this state")]
  [TestCase("unknown-state.json", "add32", "certified", "unknown state 'certified'")]
  public void AnInvalidRecord_IsRejectedWhole(string record, string fixture, string state, string reason) {
    var path = this._artifacts.Place("invalid/" + record, GateFixtures.CSharpToJavaScript);
    var (store, diagnostics) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(diagnostics, Is.EqualTo(new[] { $"PLY0058 gate record '{path}': {Rejection(fixture, state, reason)}" }));
    Assert.That(store.StateOf(GateFixtures.CSharpToJavaScript), Is.EqualTo(GateState.Planned));
    Assert.That(store.Pairs, Is.Empty);
  }

  [Test]
  public void TheDiagnostic_NamesTheFile_AndExitsWithAnEnvironmentError() {
    var path = this._artifacts.Place("invalid/hash-mismatch.json", GateFixtures.CSharpToJavaScript);
    var bag = new Polypiler.Core.DiagnosticBag();
    GateStore.Load(this._artifacts.Gates, bag);
    var diagnostic = bag.Items.Single();
    Assert.That((diagnostic.Code, diagnostic.File, diagnostic.ExitCode, diagnostic.Rule), Is.EqualTo(("PLY0058", path, (Polypiler.Core.ExitCode?)Polypiler.Core.ExitCode.EnvironmentError, "gates.record")));
    Assert.That((int)bag.ExitCode, Is.EqualTo(1));
  }

  /// <summary>Test design 15: a record that is no JSON or fails the schema is rejected, and the other pairs still load.</summary>
  [TestCase("{ \"schemaVersion\": 1, \"pair\": {", "not JSON")]
  [TestCase("{ \"schemaVersion\": 2, \"pair\": { \"from\": \"csharp\", \"to\": \"javascript\", \"sourceProfile\": \"net10\", \"targetProfile\": \"node-es2022\" }, \"commit\": \"c\", \"fixtures\": [] }", "/schemaVersion: ")]
  [TestCase("{ \"schemaVersion\": 1, \"pair\": { \"from\": \"csharp\", \"to\": \"javascript\", \"sourceProfile\": \"net10\", \"targetProfile\": \"node-es2022\" }, \"commit\": \"c\", \"fixtures\": [], \"extra\": 1 }", "/extra: property 'extra' is not allowed")]
  [TestCase("{ \"schemaVersion\": 1, \"pair\": { \"from\": \"csharp\", \"to\": \"javascript\", \"sourceProfile\": \"net10\", \"targetProfile\": \"node-es2022\" }, \"commit\": \"c\", \"fixtures\": [ { \"fixture\": \"a\", \"evidence\": [] } ] }", "/fixtures/0/evidence: ")]
  public void AMalformedRecord_IsRejected_AndTheOthersLoad(string text, string reason) {
    var path = this._artifacts.Write(GateFixtures.CSharpToJavaScript.FileName, text);
    this._artifacts.Place("valid/" + GateFixtures.JavaScriptToCSharp.FileName, GateFixtures.JavaScriptToCSharp);
    var (store, diagnostics) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(diagnostics, Is.Not.Empty.And.All.StartsWith($"PLY0058 gate record '{path}': {reason}"));
    Assert.That(store.Pairs, Is.EqualTo(new[] { GateFixtures.JavaScriptToCSharp }));
    Assert.That(store.StateOf(GateFixtures.JavaScriptToCSharp), Is.EqualTo(GateState.PerformanceValidated));
  }

  [Test]
  public void ARecordNamedAfterAnotherPair_IsRejected() {
    var path = this._artifacts.Place("valid/" + GateFixtures.JavaScriptToCSharp.FileName, GateFixtures.CSharpToJavaScript);
    var (store, diagnostics) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(diagnostics, Is.EqualTo(new[] { $"PLY0058 gate record '{path}': the record is of {GateFixtures.JavaScriptToCSharp}, the file name of {GateFixtures.CSharpToJavaScript}" }));
    Assert.That(store.Pairs, Is.Empty);
  }

  [Test]
  public void AFileNotNamedAfterAPair_IsRejected() {
    var path = this._artifacts.Write("missing-evidence.json", File.ReadAllText(GateFixtures.PathOf("invalid/missing-evidence.json")));
    this._artifacts.Write("notes.txt", "not a record");
    var (store, diagnostics) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(diagnostics, Is.EqualTo(new[] { $"PLY0058 gate record '{path}': the file name is not <from>--<to>--<source-profile>--<target-profile>.json" }));
    Assert.That(store.Pairs, Is.Empty);
  }

  [Test]
  public void AFixtureListedTwice_AndAStateClaimedTwice_AreRejected() {
    var recorder = this._artifacts.Recorder();
    recorder.Record(GateFixtures.CSharpToJavaScript, "add32", recorder.Evidence(GateState.Parsed, this._artifacts.PathOf("evidence/frontend.log")));
    var path = recorder.PathOf(GateFixtures.CSharpToJavaScript);
    var text = File.ReadAllText(path);
    var fixture = text[(text.IndexOf("    {", StringComparison.Ordinal))..text.LastIndexOf("  ]", StringComparison.Ordinal)].TrimEnd();
    var entry = fixture[fixture.IndexOf("        {", StringComparison.Ordinal)..(fixture.IndexOf("        }", StringComparison.Ordinal) + 9)];
    File.WriteAllText(path, text.Replace(fixture, fixture + ",\n" + fixture.Replace(entry, entry + ",\n" + entry, StringComparison.Ordinal), StringComparison.Ordinal));

    var (store, diagnostics) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(diagnostics, Is.EqualTo(new[] {
      $"PLY0058 gate record '{path}': fixture 'add32' is listed 2 times",
      $"PLY0058 gate record '{path}': {Rejection("add32", "parsed", "the state is claimed twice")}",
      $"PLY0058 gate record '{path}': {Rejection("add32", "parsed", "the state is claimed twice")}"
    }));
    Assert.That(store.Pairs, Is.Empty);
  }

  [TestCase("planned", "frontend-log", "evidence/frontend.log", "unknown state 'planned'")]
  [TestCase("parsed", "frontend-log", "../gates/x.log", "evidence path '../gates/x.log' leaves the artifacts directory")]
  [TestCase("emitted", "manifest", "evidence/frontend.log", "the manifest is invalid: manifest '")]
  public void OtherBadEvidence_IsRejected(string state, string kind, string relative, string reason) {
    var hash = File.Exists(this._artifacts.PathOf(relative)) ? Polypiler.Core.Verification.Manifests.ManifestWriter.Sha256(File.ReadAllBytes(this._artifacts.PathOf(relative))) : GateFixturesHashes.Abc;
    var path = this._artifacts.Write(GateFixtures.CSharpToJavaScript.FileName, $$"""
      { "schemaVersion": 1, "pair": { "from": "csharp", "to": "javascript", "sourceProfile": "net10", "targetProfile": "node-es2022" }, "commit": "c",
        "fixtures": [ { "fixture": "f", "evidence": [ { "state": "{{state}}", "kind": "{{kind}}", "path": "{{relative}}", "sha256": "{{hash}}" } ] } ] }
      """);
    var (store, diagnostics) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(diagnostics, Has.Length.EqualTo(1));
    Assert.That(diagnostics[0], Does.StartWith($"PLY0058 gate record '{path}': {Rejection("f", state, reason)}"));
    Assert.That(store.Pairs, Is.Empty);
  }

  /// <summary>Test design 4: no file, a file without fixtures and a directory that does not exist are all <c>planned</c>.</summary>
  [Test]
  public void NoEvidence_IsPlanned() {
    this._artifacts.Write(GateFixtures.CSharpToJavaScript.FileName, """
      { "schemaVersion": 1, "pair": { "from": "csharp", "to": "javascript", "sourceProfile": "net10", "targetProfile": "node-es2022" }, "commit": "c", "fixtures": [] }
      """);
    var (store, diagnostics) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(diagnostics, Is.Empty);
    Assert.That(store.Pairs, Is.EqualTo(new[] { GateFixtures.CSharpToJavaScript }));
    Assert.That(store.StateOf(GateFixtures.CSharpToJavaScript), Is.EqualTo(GateState.Planned));
    Assert.That(store.StateOf(GateFixtures.JavaScriptToCSharp), Is.EqualTo(GateState.Planned));

    var (none, nothing) = GateFixtures.Load(Path.Combine(this._artifacts.Root, "absent"));
    Assert.That(none.Pairs, Is.Empty);
    Assert.That(nothing, Is.Empty);
  }

  /// <summary>The scenario "A lower state than the baseline is reported as a regression".</summary>
  [Test]
  public void ALowerState_IsOneRegression() {
    var current = GateFixtures.Load(GateFixtures.PathOf("valid")).Store;
    var baseline = GateFixtures.Load(GateFixtures.PathOf("baseline")).Store;
    Assert.That(baseline.StateOf(GateFixtures.CSharpToJavaScript), Is.EqualTo(GateState.Verified));
    Assert.That(current.CompareWith(baseline), Is.EqualTo(new[] { new GateRegression(GateFixtures.CSharpToJavaScript, GateState.Verified, GateState.Compiled) }));
    Assert.That(baseline.CompareWith(current), Is.Empty, "a higher state is no regression");
    Assert.That(current.CompareWith(current), Is.Empty);
  }

  /// <summary>Test design 16: a pair that disappeared, also one that was only planned, and one whose record became invalid.</summary>
  [Test]
  public void ADisappearedPair_IsARegression() {
    var baseline = GateFixtures.Load(GateFixtures.PathOf("baseline")).Store;
    this._artifacts.Place("invalid/hash-mismatch.json", GateFixtures.CSharpToJavaScript);
    var (current, _) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(current.CompareWith(baseline), Is.EqualTo(new[] {
      new GateRegression(GateFixtures.CSharpToJavaScript, GateState.Verified, GateState.Planned),
      new GateRegression(GateFixtures.JavaScriptToCSharp, GateState.PerformanceValidated, GateState.Planned)
    }));

    File.WriteAllText(Path.Combine(this._artifacts.Gates, GateFixtures.CSharpToJavaScript.FileName), """
      { "schemaVersion": 1, "pair": { "from": "csharp", "to": "javascript", "sourceProfile": "net10", "targetProfile": "node-es2022" }, "commit": "c", "fixtures": [] }
      """);
    var planned = GateFixtures.Load(this._artifacts.Gates).Store;
    var empty = GateFixtures.Load(Path.Combine(this._artifacts.Root, "absent")).Store;
    Assert.That(empty.CompareWith(planned), Is.EqualTo(new[] { new GateRegression(GateFixtures.CSharpToJavaScript, GateState.Planned, GateState.Planned) }));
  }

  [Test]
  public void MissingArguments_AreRefused() {
    var store = GateFixtures.Load(GateFixtures.PathOf("valid")).Store;
    Assert.That(() => GateStore.Load("", new Polypiler.Core.DiagnosticBag()), Throws.ArgumentException);
    Assert.That(() => GateStore.Load(this._artifacts.Gates, null!), Throws.ArgumentNullException);
    Assert.That(() => store.StateOf(null!), Throws.ArgumentNullException);
    Assert.That(() => store.FixturesOf(null!), Throws.ArgumentNullException);
    Assert.That(() => store.CompareWith(null!), Throws.ArgumentNullException);
  }

}
