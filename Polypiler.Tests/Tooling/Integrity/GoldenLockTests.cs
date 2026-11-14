using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Integrity;

namespace Polypiler.Tests.Tooling.Integrity;

/// <summary>
/// <see cref="GoldenLock"/>, <see cref="ProvenanceMarker"/> and <see cref="GoldenCoverage"/>: the
/// lock's canonical form and every malformed lock, the markers, and which files are golden (test design
/// 4 and 5).
/// </summary>
[TestFixture]
public sealed class GoldenLockTests {

  private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-golden-lock-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  [Test]
  public void ToJson_IsSortedAndParsesBack() {
    var lockFile = new GoldenLock([new("b", Hash, 2), GoldenLockEntry.For("a", [1, 2, 3])]);
    var text = lockFile.ToJson();
    Assert.That(text, Is.EqualTo("""
      {
        "schema": 1,
        "files": [
          {
            "path": "a",
            "sha256": "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81",
            "bytes": 3
          },
          {
            "path": "b",
            "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
            "bytes": 2
          }
        ]
      }

      """.Replace("\r", "")));
    Assert.That(GoldenLock.Parse(text).Entries, Is.EqualTo(lockFile.Entries));
    Assert.That(lockFile.Find("b")!.Bytes, Is.EqualTo(2));
    Assert.That(lockFile.Find("c"), Is.Null);
  }

  [Test]
  public void With_ReplacesAddsAndRemovesOnlyTheNamedEntries() {
    var lockFile = new GoldenLock([new("a", Hash, 1), new("b", Hash, 2), new("c", Hash, 3)]);
    var changed = lockFile.With(new Dictionary<string, GoldenLockEntry?> { ["b"] = new("b", Hash, 20), ["c"] = null, ["d"] = new("d", Hash, 4) });
    Assert.That(changed.Entries.Select(entry => (entry.Path, entry.Bytes)), Is.EqualTo(new[] { ("a", 1L), ("b", 20L), ("d", 4L) }));
  }

  [Test]
  public void Load_OfAMissingFileIsEmpty_OfAFolderIsRefused() {
    Assert.That(GoldenLock.Load(Path.Combine(this._root, "none", "golden.lock.json")).Entries, Is.Empty);
    var exception = Assert.Throws<IntegrityException>(() => GoldenLock.Load(this._root))!;
    Assert.That(exception.Diagnostics.Single().Message, Does.StartWith($"golden file '{GoldenLock.RepositoryPath}' was modified: not a valid golden lock: cannot be read: "));
  }

  [TestCase("{", "not JSON: ")]
  [TestCase("[]", "the lock must be an object of exactly 'schema' and 'files'")]
  [TestCase("{\"files\":[],\"schema\":1}", "the lock must be an object of exactly 'schema' and 'files'")]
  [TestCase("{\"schema\":2,\"files\":[]}", "unsupported lock schema 2")]
  [TestCase("{\"schema\":1,\"files\":{}}", "'files' of the lock must be an array")]
  [TestCase("{\"schema\":1,\"files\":[1]}", "lock entry 1 is malformed: 1")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":\"a\",\"bytes\":1,\"sha256\":\"" + Hash + "\"}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":\"\",\"sha256\":\"" + Hash + "\",\"bytes\":1}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":1,\"sha256\":\"" + Hash + "\",\"bytes\":1}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":\"a\",\"sha256\":\"AB\",\"bytes\":1}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":\"a\",\"sha256\":1,\"bytes\":1}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":\"a\",\"sha256\":\"" + Hash + "\",\"bytes\":-1}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":\"a\",\"sha256\":\"" + Hash + "\",\"bytes\":1.5}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":\"a\",\"sha256\":\"" + Hash + "\",\"bytes\":\"1\"}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\":1,\"files\":[{\"path\":\"a\",\"sha256\":\"" + Hash + "\",\"bytes\":1},{\"path\":\"a\",\"sha256\":\"" + Hash + "\",\"bytes\":1}]}", "lock entry 'a' is listed twice")]
  public void Parse_RefusesAMalformedLock(string text, string reason)
    => Assert.That(Assert.Throws<IntegrityException>(() => GoldenLock.Parse(text))!.Diagnostics.Single().Message, Does.Contain($"not a valid golden lock: {reason}"));

  [Test]
  public void Marker_CoversItsFolderButWhatItExcludes() {
    var marker = ProvenanceMarker.Parse("""
      {"schema":1,"provenance":{"kind":"reference-run","source":"node","detail":"d","generatedBy":"node"},"exclude":["**/*.types.txt"]}
      """, "tests/fixtures/r/golden.provenance.json");
    Assert.That((marker.Path, marker.Folder, marker.Problems.Length, marker.Provenance!.Source), Is.EqualTo(("tests/fixtures/r/golden.provenance.json", "tests/fixtures/r", 0, "node")));
    Assert.That(marker.Exclude.Select(pattern => pattern.Text), Is.EqualTo(new[] { "**/*.types.txt" }));
    Assert.That(marker.Covers("tests/fixtures/r/a.txt"), Is.True);
    Assert.That(marker.Covers("tests/fixtures/r/x/a.txt"), Is.True);
    Assert.That(marker.Covers("tests/fixtures/r/x/a.types.txt"), Is.False);
    Assert.That(marker.Covers("tests/fixtures/rr/a.txt"), Is.False);
    Assert.That(ProvenanceMarker.Parse("{\"schema\":1,\"provenance\":{\"kind\":\"published\",\"source\":\"s\",\"detail\":\"d\",\"generatedBy\":\"hand\"}}", "golden.provenance.json").Folder, Is.Empty);
  }

  [TestCase("{", "not JSON: ")]
  [TestCase("1", "a provenance marker must be a JSON object")]
  [TestCase("{\"schema\":1}", "no provenance")]
  [TestCase("{\"schema\":1,\"provenance\":{\"kind\":\"guess\",\"source\":\"s\",\"detail\":\"d\",\"generatedBy\":\"hand\"}}", "provenance kind 'guess' is unknown")]
  [TestCase("{\"provenance\":{\"kind\":\"published\",\"source\":\"s\",\"detail\":\"d\",\"generatedBy\":\"hand\"}}", "'schema' must be 1")]
  [TestCase("{\"schema\":1,\"x\":1,\"provenance\":{\"kind\":\"published\",\"source\":\"s\",\"detail\":\"d\",\"generatedBy\":\"hand\"}}", "unknown property 'x'")]
  [TestCase("{\"schema\":1,\"exclude\":\"*\",\"provenance\":{\"kind\":\"published\",\"source\":\"s\",\"detail\":\"d\",\"generatedBy\":\"hand\"}}", "'exclude' must be an array of non-empty patterns")]
  [TestCase("{\"schema\":1,\"exclude\":[\"\"],\"provenance\":{\"kind\":\"published\",\"source\":\"s\",\"detail\":\"d\",\"generatedBy\":\"hand\"}}", "'exclude' must be an array of non-empty patterns")]
  [TestCase("{\"schema\":1,\"exclude\":[1],\"provenance\":{\"kind\":\"published\",\"source\":\"s\",\"detail\":\"d\",\"generatedBy\":\"hand\"}}", "'exclude' must be an array of non-empty patterns")]
  public void Marker_Malformed_HasNoProvenance(string text, string reason) {
    var marker = ProvenanceMarker.Parse(text, "tests/fixtures/golden.provenance.json");
    Assert.That(marker.Provenance, Is.Null);
    Assert.That(marker.Problems, Has.Some.StartsWith(reason));
  }

  [Test]
  public void Marker_ThatCannotBeRead_HasTheReason()
    => Assert.That(ProvenanceMarker.Load(this._root, "tests/fixtures/golden.provenance.json").Problems.Single(), Does.StartWith("cannot be read: "));

  [Test]
  public void Coverage_ByName_ByFolder_ByMarker_AndNotOtherwise() {
    var tree = new GoldenTree(this._root);
    tree.Write("tests/fixtures/x/input.txt", "x")
      .Write("tests/fixtures/x/a.vectors.json", "{}")
      .Write("tests/fixtures/x/golden/out.bin", "o")
      .Write("tests/fixtures/x/golden/deeper/out.bin", "o")
      .Write("tests/fixtures/golden.lock.json", "{\"schema\":1,\"files\":[]}")
      .Write("tests/fixtures/golden.changes.md", "")
      .Write("tests/fixtures/x/.git/HEAD", "ref")
      .Write("tests/vectors/f/golden.provenance.json", "{\"schema\":1,\"provenance\":{\"kind\":\"reference-run\",\"source\":\"mpfr\",\"detail\":\"d\",\"generatedBy\":\"mpfr\"},\"exclude\":[\"*.log\"]}")
      .Write("tests/vectors/f/sin.txt", "s")
      .Write("tests/vectors/f/run.log", "l")
      .Write("tests/vectors/f/inner/golden.provenance.json", "{")
      .Write("tests/vectors/f/inner/x.txt", "x")
      .Write("tests/vectors/loose.txt", "l")
      .Write("docs/golden/out.bin", "o");
    var coverage = GoldenCoverage.Of(this._root);
    Assert.That(coverage.Files, Is.EqualTo(new[] {
      "tests/fixtures/x/a.vectors.json", "tests/fixtures/x/golden/deeper/out.bin", "tests/fixtures/x/golden/out.bin",
      "tests/vectors/f/golden.provenance.json", "tests/vectors/f/inner/golden.provenance.json", "tests/vectors/f/inner/x.txt", "tests/vectors/f/sin.txt"
    }));
    Assert.That(coverage.Markers.Select(marker => marker.Path), Is.EqualTo(new[] { "tests/vectors/f/golden.provenance.json", "tests/vectors/f/inner/golden.provenance.json" }));
    Assert.That(coverage.MarkerOf("tests/vectors/f/inner/x.txt")!.Path, Is.EqualTo("tests/vectors/f/inner/golden.provenance.json"));
    Assert.That(coverage.MarkerOf("tests/vectors/f/sin.txt")!.Path, Is.EqualTo("tests/vectors/f/golden.provenance.json"));
    Assert.That(coverage.Covers("tests/vectors/f/deleted.txt"), Is.True);
    Assert.That(coverage.Covers("tests/fixtures/x/input.txt"), Is.False);
    Assert.That(coverage.Covers("Polypiler.Core.IR/Module.cs"), Is.False);
  }

  [Test]
  public void Coverage_OfATreeWithoutTestFolders_IsEmpty()
    => Assert.That((GoldenCoverage.Of(this._root).Files.Count, GoldenCoverage.Of(this._root).Markers.Count), Is.EqualTo((0, 0)));

}
