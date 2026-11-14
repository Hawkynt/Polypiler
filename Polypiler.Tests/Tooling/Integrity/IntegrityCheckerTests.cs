using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Integrity;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Integrity;

/// <summary>
/// <see cref="IntegrityChecker"/> on synthetic trees: the lock comparison, the provenance rules and
/// sealing (test design 1 to 3, 12 to 18), and the repository's own golden files against its lock.
/// </summary>
[TestFixture]
public sealed class IntegrityCheckerTests {

  private const string Upstream = "Hawkynt/Hawkynt.github.io@b3a8a8e92cbca83475986391c3cac56f0b185d29:Cipher/algorithms/checksum/";

  private GoldenTree _tree = null!;

  [SetUp]
  public void CreateTree() => this._tree = new(Scratch.Create("polypiler-integrity-"));

  [TearDown]
  public void DeleteTree() => Scratch.Delete(this._tree.Root);

  private IntegrityChecker Checker => new(this._tree.Root);

  private string[] Check() => [.. this.Checker.Check().Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")];

  private string LockText => this._tree.Read(GoldenLock.RepositoryPath);

  [Test]
  public void RepositoryGoldenFiles_MatchTheLock_AndHaveIndependentProvenance() {
    var checker = new IntegrityChecker(PrototypeFixture.RepositoryRoot);
    Assert.That(checker.Check().Select(diagnostic => diagnostic.ToString()), Is.Empty);
    var coverage = checker.Coverage();
    Assert.That(coverage.Files, Does.Contain(GoldenTree.Adler32));
    Assert.That(coverage.Files, Does.Contain("tests/vectors/float/sin.f64.txt").And.Contain("tests/vectors/float/generate/versions.txt"));
    Assert.That(coverage.Files, Does.Contain("tests/fixtures/prototype/js/recorded/adler32.txt"));
    Assert.That(coverage.Files.Where(file => file.EndsWith(".types.txt", StringComparison.Ordinal)), Is.Empty);
    Assert.That(coverage.Files.Where(file => file.StartsWith("tests/fixtures/infra/integrity/", StringComparison.Ordinal)), Is.Empty);
  }

  [Test]
  public void SealedReferenceRun_Passes() {
    this._tree.Write(GoldenTree.Adler32, GoldenTree.RepositoryAdler32).Seal(GoldenTree.Adler32);
    Assert.That(this.Check(), Is.Empty);
    Assert.That(GoldenLock.Parse(this.LockText).Entries.Single(), Is.EqualTo(GoldenLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, GoldenLock.RepositoryPath)).Find(GoldenTree.Adler32)));
  }

  [Test]
  public void DeliberateChange_SealedAgain_Passes_AndOnlyItsEntryChanges() {
    this._tree.Write(GoldenTree.Adler32, GoldenTree.RepositoryAdler32).Write("tests/fixtures/x/golden/out.bin", "o").Seal(GoldenTree.Adler32, "tests/fixtures/x/golden/out.bin");
    var before = GoldenLock.Parse(this.LockText);
    this._tree.Write(GoldenTree.Adler32, GoldenTree.WithoutVectorA(GoldenTree.RepositoryAdler32));
    Assert.That(this.Check(), Is.EqualTo(new[] { $"PLY0027: golden file '{GoldenTree.Adler32}' was modified: hash differs from the golden lock" }));
    this._tree.Seal(GoldenTree.Adler32);
    var after = GoldenLock.Parse(this.LockText);
    Assert.That(this.Check(), Is.Empty);
    Assert.That(after.Find("tests/fixtures/x/golden/out.bin"), Is.EqualTo(before.Find("tests/fixtures/x/golden/out.bin")));
    Assert.That(after.Find(GoldenTree.Adler32), Is.Not.EqualTo(before.Find(GoldenTree.Adler32)));
  }

  [TestCase("published.json")]
  [TestCase("upstream-embedded.json")]
  [TestCase("kinds.json")]
  public void EveryKindOfProvenance_IsAccepted(string fixture) {
    this._tree.Write("tests/fixtures/k/" + fixture.Replace(".json", VectorFile.Extension), GoldenTree.Fixture(fixture)).Seal("tests/fixtures/k/" + fixture.Replace(".json", VectorFile.Extension));
    Assert.That(this.Check(), Is.Empty);
  }

  [Test]
  public void ByNameAndByFolder_AreLocked_OthersAreNot() {
    this._tree.Write("tests/fixtures/x/a.vectors.json", GoldenTree.Fixture("published.json")).Write("tests/fixtures/x/golden/out.bin", "o").Write("tests/fixtures/x/input.txt", "i");
    Assert.That(this.Check(), Is.EqualTo(new[] {
      "PLY0027: golden file 'tests/fixtures/x/a.vectors.json' was modified: not in the golden lock",
      "PLY0027: golden file 'tests/fixtures/x/golden/out.bin' was modified: not in the golden lock"
    }));
    this._tree.Seal("tests/fixtures/x/a.vectors.json", "tests/fixtures/x/golden/out.bin");
    Assert.That(GoldenLock.Parse(this.LockText).Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "tests/fixtures/x/a.vectors.json", "tests/fixtures/x/golden/out.bin" }));
    Assert.That(this.Check(), Is.Empty);
    Assert.That(() => this.Checker.Seal(["tests/fixtures/x/input.txt"]), Throws.ArgumentException.With.Message.EqualTo("'tests/fixtures/x/input.txt' is not a golden file"));
  }

  [Test]
  public void DeletedFile_IsMissingOnDisk_UntilItsEntryIsSealedAway() {
    this._tree.Write("tests/fixtures/x/golden/out.bin", "o").Seal("tests/fixtures/x/golden/out.bin");
    File.Delete(this._tree.Resolve("tests/fixtures/x/golden/out.bin"));
    Assert.That(this.Check(), Is.EqualTo(new[] { "PLY0027: golden file 'tests/fixtures/x/golden/out.bin' was modified: missing on disk" }));
    this._tree.Seal("tests/fixtures/x/golden/out.bin");
    Assert.That((this.Check(), GoldenLock.Parse(this.LockText).Entries.Count), Is.EqualTo((Array.Empty<string>(), 0)));
    Assert.That(() => this.Checker.Seal(["tests/fixtures/x/golden/out.bin"]), Throws.ArgumentException.With.Message.EqualTo("'tests/fixtures/x/golden/out.bin' does not exist and is not in the golden lock"));
  }

  [TestCase("\"provenance\"", "\"origin\"", "no provenance")]
  [TestCase("\"kind\": \"reference-run\"", "\"kind\": \"guess\"", "provenance kind 'guess' is unknown; it is 'published', 'reference-run' or 'upstream-embedded'")]
  [TestCase("\"generatedBy\": \"python\"", "\"generatedBy\": \"polypiler\"", "generatedBy 'polypiler' names Polypiler itself")]
  [TestCase("\"generatedBy\": \"python\"", "\"generatedBy\": \"polypiler-tooling\"", "generatedBy 'polypiler-tooling' names Polypiler itself")]
  [TestCase("\"source\": \"python\"", "\"source\": \"nodejs\"", "source is not a toolchain key of tooling/toolchains.lock: 'nodejs'")]
  [TestCase("\"kind\": \"reference-run\", \"source\": \"python\"", "\"kind\": \"upstream-embedded\", \"source\": \"" + Upstream + "nothing.js\"", "source is not in the corpus lock: '" + Upstream + "nothing.js'")]
  [TestCase("\"kind\": \"reference-run\", \"source\": \"python\"", "\"kind\": \"upstream-embedded\", \"source\": \"adler.js\"", "source is not 'owner/repo@<commit>:<path>': 'adler.js'")]
  public void NotIndependent_IsRefused_ByCheckAndBySeal(string original, string replacement, string reason) {
    var text = GoldenTree.RepositoryAdler32.Replace("\n    \"source\"", " \"source\"", StringComparison.Ordinal);
    Assert.That(text, Does.Contain(original));
    this._tree.Write(GoldenTree.Adler32, GoldenTree.RepositoryAdler32).Seal(GoldenTree.Adler32);
    var sealedLock = this.LockText;
    this._tree.Write(GoldenTree.Adler32, text.Replace(original, replacement, StringComparison.Ordinal));
    Assert.That(this.Check(), Does.Contain($"PLY0028: vectors '{GoldenTree.Adler32}' lack independent provenance: {reason}"));
    Assert.That(this.Checker.Seal([GoldenTree.Adler32]).Select(diagnostic => diagnostic.Message), Does.Contain($"vectors '{GoldenTree.Adler32}' lack independent provenance: {reason}"));
    Assert.That(this.LockText, Is.EqualTo(sealedLock));
  }

  [Test]
  public void WithoutToolchainOrCorpusLock_NoSourceResolves() {
    File.WriteAllText(this._tree.Resolve(ToolchainLock.RepositoryPath), "schema: [");
    File.WriteAllText(this._tree.Resolve(CorpusLock.RepositoryPath), "{");
    this._tree.Write("tests/fixtures/a.vectors.json", GoldenTree.RepositoryAdler32).Write("tests/fixtures/b.vectors.json", GoldenTree.Fixture("upstream-embedded.json"));
    Assert.That(this.Checker.Seal(["tests/fixtures/a.vectors.json", "tests/fixtures/b.vectors.json"]).Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      "vectors 'tests/fixtures/a.vectors.json' lack independent provenance: source is not a toolchain key of tooling/toolchains.lock: 'python'",
      $"vectors 'tests/fixtures/b.vectors.json' lack independent provenance: source is not in the corpus lock: '{Upstream}adler.js'"
    }));
    Assert.That(File.Exists(this._tree.Resolve(GoldenLock.RepositoryPath)), Is.False);
  }

  [Test]
  public void Markers_AreHeldToTheSameRules() {
    this._tree.Write("tests/vectors/f/golden.provenance.json", "{\"schema\":1,\"provenance\":{\"kind\":\"reference-run\",\"source\":\"mpfr\",\"detail\":\"d\",\"generatedBy\":\"mpfr\"}}")
      .Write("tests/vectors/f/sin.txt", "s")
      .Seal("tests/vectors/f/golden.provenance.json", "tests/vectors/f/sin.txt");
    Assert.That(this.Check(), Is.Empty);
    var checker = this.Checker;
    Assert.That(checker.ProvenanceOf("tests/vectors/f/sin.txt", checker.Coverage())!.Source, Is.EqualTo("mpfr"));

    this._tree.Write("tests/vectors/f/golden.provenance.json", "{\"schema\":1,\"provenance\":{\"kind\":\"reference-run\",\"source\":\"mpfr\",\"detail\":\"d\",\"generatedBy\":\"Polypiler\"}}");
    Assert.That(checker.Seal(["tests/vectors/f/sin.txt"]).Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      "vectors 'tests/vectors/f/golden.provenance.json' lack independent provenance: generatedBy 'Polypiler' names Polypiler itself"
    }));
    this._tree.Write("tests/vectors/f/golden.provenance.json", "{\"schema\":1}");
    Assert.That(this.Check(), Is.EqualTo(new[] {
      "PLY0027: golden file 'tests/vectors/f/golden.provenance.json' was modified: hash differs from the golden lock",
      "PLY0028: vectors 'tests/vectors/f/golden.provenance.json' lack independent provenance: no provenance"
    }));
    Assert.That(checker.ProvenanceOf("tests/vectors/f/sin.txt", checker.Coverage()), Is.Null);
  }

  [Test]
  public void ProvenanceOf_AnInvalidVectorFile_IsNone() {
    this._tree.Write("tests/fixtures/a.vectors.json", "{}");
    var checker = this.Checker;
    Assert.That(checker.ProvenanceOf("tests/fixtures/a.vectors.json", checker.Coverage()), Is.Null);
    Assert.That(checker.ProvenanceOf("tests/fixtures/x/golden/out.bin", checker.Coverage()), Is.Null);
  }

  [Test]
  public void InvalidLock_IsReported_ByCheckAndBySeal() {
    this._tree.Write(GoldenLock.RepositoryPath, "{");
    Assert.That(this.Check().Single(), Does.StartWith($"PLY0027: golden file '{GoldenLock.RepositoryPath}' was modified: not a valid golden lock: not JSON"));
    Assert.That(this.Checker.Seal(["tests/fixtures/a.vectors.json"]).Single().Code, Is.EqualTo(IntegrityDiagnostics.GoldenModified));
  }

}
