using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Gates;

namespace Polypiler.Tests.Verification.Gates;

/// <summary>Recording evidence: one call per piece, canonical files, idempotent, and nothing invalid written.</summary>
[TestFixture]
public sealed class GateRecorderTests {

  private TemporaryArtifacts _artifacts = null!;

  [SetUp]
  public void CreateArtifacts() => this._artifacts = new();

  [TearDown]
  public void DeleteArtifacts() => this._artifacts.Dispose();

  private void RecordChain(GateRecorder recorder, PairKey pair, string fixture, params GateState[] states) {
    foreach (var state in states)
      recorder.Record(pair, fixture, recorder.Evidence(state, this._artifacts.PathOf(GateFixtures.EvidenceOf(state))));
  }

  private static readonly GateState[] _toVerified = [GateState.Parsed, GateState.Lifted, GateState.Legalized, GateState.Emitted, GateState.Compiled, GateState.Executed, GateState.Verified];

  /// <summary>
  /// The valid fixture records are what the recorder writes for the same evidence, in any order of
  /// recording: canonical, fixtures by ID, evidence by state.
  /// </summary>
  [Test]
  public void TheValidRecords_AreWhatTheRecorderWrites() {
    var recorder = this._artifacts.Recorder();
    this.RecordChain(recorder, GateFixtures.CSharpToJavaScript, "mix32", GateState.Compiled, GateState.Emitted, GateState.Legalized, GateState.Lifted, GateState.Parsed);
    this.RecordChain(recorder, GateFixtures.CSharpToJavaScript, "add32", _toVerified);
    this.RecordChain(recorder, GateFixtures.JavaScriptToCSharp, "crc32", [.. GateStates.Evidenced]);
    foreach (var pair in new[] { GateFixtures.CSharpToJavaScript, GateFixtures.JavaScriptToCSharp })
      Assert.That(File.ReadAllBytes(recorder.PathOf(pair)), Is.EqualTo(File.ReadAllBytes(GateFixtures.PathOf("valid/" + pair.FileName))), pair.FileName);
  }

  /// <summary>Test design 10: the same evidence recorded twice is one entry, and the file is not even rewritten.</summary>
  [Test]
  public void RecordingTwice_LeavesTheBytesAndTheFileAsTheyAre() {
    var recorder = this._artifacts.Recorder();
    var evidence = recorder.Evidence(GateState.Parsed, this._artifacts.PathOf("evidence/frontend.log"));
    recorder.Record(GateFixtures.CSharpToJavaScript, "add32", evidence);
    var path = recorder.PathOf(GateFixtures.CSharpToJavaScript);
    var bytes = File.ReadAllBytes(path);
    var written = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(path, written);

    recorder.Record(GateFixtures.CSharpToJavaScript, "add32", evidence);

    Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
    Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(written));
    Assert.That(GateFixtures.Load(this._artifacts.Gates).Store.FixturesOf(GateFixtures.CSharpToJavaScript).Single().Evidence, Has.Count.EqualTo(1));
  }

  [Test]
  public void ANewPieceForTheSameState_ReplacesTheOld() {
    var recorder = this._artifacts.Recorder();
    recorder.Record(GateFixtures.CSharpToJavaScript, "add32", recorder.Evidence(GateState.Lifted, this._artifacts.PathOf("evidence/lifted.ir")));
    recorder.Record(GateFixtures.CSharpToJavaScript, "add32", recorder.Evidence(GateState.Lifted, this._artifacts.PathOf("evidence/legal.ir")));
    var evidence = GateFixtures.Load(this._artifacts.Gates).Store.FixturesOf(GateFixtures.CSharpToJavaScript).Single().Evidence;
    Assert.That(evidence, Is.EqualTo(new[] { new GateEvidence(GateState.Lifted, "ir-dump", "evidence/legal.ir", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855") }));
  }

  [Test]
  public void Forget_RemovesTheFixture_AndKeepsTheOthers() {
    var recorder = this._artifacts.Recorder();
    this.RecordChain(recorder, GateFixtures.CSharpToJavaScript, "add32", _toVerified);
    this.RecordChain(recorder, GateFixtures.CSharpToJavaScript, "mix32", GateState.Parsed);
    recorder.Forget(GateFixtures.CSharpToJavaScript, "mix32");
    recorder.Forget(GateFixtures.CSharpToJavaScript, "absent");
    recorder.Forget(GateFixtures.JavaScriptToCSharp, "crc32");

    var (store, diagnostics) = GateFixtures.Load(this._artifacts.Gates);
    Assert.That(diagnostics, Is.Empty);
    Assert.That(store.StateOf(GateFixtures.CSharpToJavaScript), Is.EqualTo(GateState.Verified));
    Assert.That(File.Exists(recorder.PathOf(GateFixtures.JavaScriptToCSharp)), Is.False, "forgetting creates no file");
  }

  [Test]
  public void TheEvidence_OfAFile_IsItsKindHashAndPathBelowTheRoot() {
    var recorder = this._artifacts.Recorder();
    Assert.That(recorder.Root, Is.EqualTo(Path.GetFullPath(this._artifacts.Root)));
    Assert.That(recorder.Evidence(GateState.Parsed, this._artifacts.PathOf(Path.Combine("evidence", "frontend.log"))),
      Is.EqualTo(new GateEvidence(GateState.Parsed, "frontend-log", "evidence/frontend.log", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")));
  }

  [Test]
  public void AFileOutsideTheRoot_IsNoEvidence() {
    var recorder = this._artifacts.Recorder();
    Assert.That(() => recorder.Evidence(GateState.Parsed, GateFixtures.PathOf("evidence/frontend.log")), Throws.ArgumentException);
    Assert.That(() => recorder.Evidence(GateState.Planned, this._artifacts.PathOf("evidence/frontend.log")), Throws.ArgumentException);
  }

  [TestCase(GateState.Parsed, "ir-dump", "evidence/frontend.log", GateFixturesHashes.Abc, "kind")]
  [TestCase(GateState.Planned, "frontend-log", "evidence/frontend.log", GateFixturesHashes.Abc, "takes no evidence")]
  [TestCase(GateState.Parsed, "frontend-log", "evidence/frontend.log", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "SHA-256")]
  [TestCase(GateState.Parsed, "frontend-log", "evidence/frontend.log", "abc", "SHA-256")]
  [TestCase(GateState.Parsed, "frontend-log", "../frontend.log", GateFixturesHashes.Abc, "relative path")]
  [TestCase(GateState.Parsed, "frontend-log", "/evidence/frontend.log", GateFixturesHashes.Abc, "relative path")]
  [TestCase(GateState.Parsed, "frontend-log", "evidence\\frontend.log", GateFixturesHashes.Abc, "relative path")]
  [TestCase(GateState.Parsed, "frontend-log", "c:evidence/frontend.log", GateFixturesHashes.Abc, "relative path")]
  [TestCase(GateState.Parsed, "frontend-log", "evidence/./frontend.log", GateFixturesHashes.Abc, "relative path")]
  [TestCase(GateState.Parsed, "frontend-log", "evidence//frontend.log", GateFixturesHashes.Abc, "relative path")]
  [TestCase(GateState.Parsed, "frontend-log", "", GateFixturesHashes.Abc, "relative path")]
  public void InvalidEvidence_IsRefused_AndNothingIsWritten(GateState state, string kind, string path, string sha256, string message) {
    var recorder = this._artifacts.Recorder();
    Assert.That(() => recorder.Record(GateFixtures.CSharpToJavaScript, "add32", new(state, kind, path, sha256)), Throws.ArgumentException.With.Message.Contains(message));
    Assert.That(Directory.GetFiles(this._artifacts.Gates), Is.Empty);
  }

  /// <summary>Test design 18: a profile with <c>--</c> fails before anything is recorded.</summary>
  [Test]
  public void AProfileWithTwoHyphens_FailsBeforeAnythingIsWritten() {
    var recorder = this._artifacts.Recorder();
    Assert.That(() => recorder.Record(new PairKey("csharp", "javascript", "a--b", "node"), "add32", recorder.Evidence(GateState.Parsed, this._artifacts.PathOf("evidence/frontend.log"))),
      Throws.ArgumentException);
    Assert.That(Directory.GetFiles(this._artifacts.Gates), Is.Empty);
  }

  [TestCase("{", "no gate record")]
  [TestCase("{ \"schemaVersion\": 2 }", "no gate record")]
  public void AnExistingFileThatIsNoRecord_IsNotOverwritten(string text, string message) {
    var path = this._artifacts.Write(GateFixtures.CSharpToJavaScript.FileName, text);
    var recorder = this._artifacts.Recorder();
    Assert.That(() => this.RecordChain(recorder, GateFixtures.CSharpToJavaScript, "add32", GateState.Parsed), Throws.TypeOf<InvalidDataException>().With.Message.Contains(message));
    Assert.That(File.ReadAllText(path), Is.EqualTo(text));
  }

  [Test]
  public void AnExistingRecordOfAnotherPair_IsNotOverwritten() {
    this._artifacts.Place("valid/" + GateFixtures.JavaScriptToCSharp.FileName, GateFixtures.CSharpToJavaScript);
    var recorder = this._artifacts.Recorder();
    Assert.That(() => recorder.Forget(GateFixtures.CSharpToJavaScript, "crc32"), Throws.TypeOf<InvalidDataException>().With.Message.Contains("records javascript"));
  }

  [Test]
  public void AnExistingRecordWithAnUnknownState_IsNotOverwritten() {
    this._artifacts.Place("invalid/unknown-state.json", GateFixtures.CSharpToJavaScript);
    var recorder = this._artifacts.Recorder();
    Assert.That(() => recorder.Forget(GateFixtures.CSharpToJavaScript, "add32"), Throws.TypeOf<InvalidDataException>().With.Message.Contains("'certified'"));
  }

  [Test]
  public void TheDirectory_IsCreatedOnTheFirstRecord() {
    var recorder = new GateRecorder(Path.Combine(this._artifacts.Root, "more", "gates"), "c0ffee");
    var file = Path.Combine(this._artifacts.Root, "more", "a.log");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, "abc");
    recorder.Record(GateFixtures.CSharpToJavaScript, "add32", recorder.Evidence(GateState.Parsed, file));
    Assert.That(File.ReadAllText(recorder.PathOf(GateFixtures.CSharpToJavaScript)), Does.Contain("\"commit\": \"c0ffee\"").And.Contain("\"path\": \"a.log\""));
  }

  [Test]
  public void TheDefaultCommit_IsThatOfTheEnvironment_OrUnknown() {
    var expected = Environment.GetEnvironmentVariable(GateRecorder.CommitVariable) is { Length: > 0 } commit ? commit : GateRecorder.UnknownCommit;
    Assert.That(new GateRecorder(this._artifacts.Gates).Commit, Is.EqualTo(expected));
  }

  [Test]
  public void MissingArguments_AreRefused() {
    var recorder = this._artifacts.Recorder();
    var evidence = recorder.Evidence(GateState.Parsed, this._artifacts.PathOf("evidence/frontend.log"));
    Assert.That(() => new GateRecorder(""), Throws.ArgumentException);
    Assert.That(() => new GateRecorder(this._artifacts.Gates, ""), Throws.ArgumentException);
    Assert.That(() => recorder.PathOf(null!), Throws.ArgumentNullException);
    Assert.That(() => recorder.Evidence(GateState.Parsed, null!), Throws.ArgumentNullException);
    Assert.That(() => recorder.Record(null!, "a", evidence), Throws.ArgumentNullException);
    Assert.That(() => recorder.Record(GateFixtures.CSharpToJavaScript, "", evidence), Throws.ArgumentException);
    Assert.That(() => recorder.Record(GateFixtures.CSharpToJavaScript, "a", null!), Throws.ArgumentNullException);
    Assert.That(() => recorder.Record(GateFixtures.CSharpToJavaScript, "a", evidence with { Sha256 = null! }), Throws.ArgumentException);
    Assert.That(() => recorder.Forget(null!, "a"), Throws.ArgumentNullException);
    Assert.That(() => recorder.Forget(GateFixtures.CSharpToJavaScript, ""), Throws.ArgumentException);
  }

}

/// <summary>FIPS 180-4's published SHA-256 examples.</summary>
internal static class GateFixturesHashes {

  /// <summary>SHA-256 of the three bytes <c>abc</c>.</summary>
  public const string Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

  /// <summary>SHA-256 of the empty message.</summary>
  public const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

  /// <summary>SHA-256 of the 448-bit message <c>abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq</c>.</summary>
  public const string TwoBlock = "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1";

}
