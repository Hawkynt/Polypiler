using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Tooling.Corpus;

/// <summary><see cref="CorpusLock"/>: canonical order and formatting, and every malformed lock.</summary>
[TestFixture]
public sealed class CorpusLockTests {

  private const string Commit = "0123456789abcdef0123456789abcdef01234567";
  private const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

  private static CorpusLockEntry Entry(string repository, string path) => new(repository, Commit, path, Empty, 0);

  [Test]
  public void Entries_AreSortedByRepositoryThenOrdinalPath() {
    var lockFile = new CorpusLock([Entry("o/r", "dir/b.txt"), Entry("a/z", "x"), Entry("o/r", "a.txt"), Entry("o/r", "LICENSE")]);
    Assert.That(lockFile.Entries.Select(entry => entry.Name), Is.EqualTo(new[] { "a/z:x", "o/r:LICENSE", "o/r:a.txt", "o/r:dir/b.txt" }));
  }

  [Test]
  public void ToJson_IsTheCanonicalText() {
    var text = new CorpusLock([Entry("o/r", "a")]).ToJson();
    Assert.That(text, Is.EqualTo($$"""
      {
        "schema": 1,
        "files": [
          {
            "repository": "o/r",
            "commit": "{{Commit}}",
            "path": "a",
            "sha256": "{{Empty}}",
            "bytes": 0
          }
        ]
      }

      """.Replace("\r\n", "\n")));
  }

  [Test]
  public void EmptyLock_IsWrittenWithAnEmptyArray()
    => Assert.That(new CorpusLock([]).ToJson(), Is.EqualTo("{\n  \"schema\": 1,\n  \"files\": []\n}\n"));

  [Test]
  public void Parse_ReadsWhatToJsonWrote() {
    var written = new CorpusLock([Entry("o/r", "b"), Entry("o/r", "a")]);
    var read = CorpusLock.Parse(written.ToJson(), "lock");
    Assert.That(read.Entries, Is.EqualTo(written.Entries));
    Assert.That(read.Find("o/r", Commit, "a"), Is.EqualTo(Entry("o/r", "a")));
    Assert.That(read.Find("o/r", Commit, "c"), Is.Null);
    Assert.That(read.Find("o/x", Commit, "a"), Is.Null);
    Assert.That(read.Find("o/r", new string('f', 40), "a"), Is.Null);
  }

  [Test]
  public void Hash_OfNoBytes_IsTheEmptySha256() {
    var entry = CorpusLockEntry.For(new CorpusRepository("o/r", "u", Commit, "MIT", []), "empty.txt", []);
    Assert.That((entry.Sha256, entry.Bytes, entry.Location), Is.EqualTo((Empty, 0L, $"o/r@{Commit}/empty.txt")));
  }

  [Test]
  public void Hash_OfHello_IsTheOneTheScenarioNames()
    => Assert.That(CorpusLockEntry.Hash(Encoding.ASCII.GetBytes("hello\n")), Is.EqualTo("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03"));

  private const string Valid = "\"repository\": \"o/r\", \"commit\": \"" + Commit + "\", \"path\": \"a\", \"sha256\": \"" + Empty + "\", \"bytes\": 0";

  [TestCase("not json", "not JSON")]
  [TestCase("[]", "the lock must be an object of exactly 'schema' and 'files'")]
  [TestCase("{\"files\": [], \"schema\": 1}", "the lock must be an object of exactly 'schema' and 'files'")]
  [TestCase("{\"schema\": 2, \"files\": []}", "unsupported lock schema 2")]
  [TestCase("{\"schema\": \"1\", \"files\": []}", "unsupported lock schema \"1\"")]
  [TestCase("{\"schema\": 1, \"files\": {}}", "'files' of the lock must be an array")]
  [TestCase("{\"schema\": 1, \"files\": [1]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\": 1, \"files\": [{" + Valid + ", \"x\": 1}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\": 1, \"files\": [{\"repository\": 1, \"commit\": \"" + Commit + "\", \"path\": \"a\", \"sha256\": \"" + Empty + "\", \"bytes\": 0}]}", "lock entry 1 is malformed")]
  [TestCase("{\"schema\": 1, \"files\": [{" + Valid + "}, {" + Valid + "}]}", "lock entry 'o/r@" + Commit + "/a' is listed twice")]
  public void Parse_OfAMalformedLock_IsRefused(string text, string reason) {
    var exception = Assert.Throws<CorpusException>(() => CorpusLock.Parse(text, "lock"))!;
    Assert.That(exception.Diagnostics.Single().Message, Does.StartWith($"corpus manifest invalid: {reason}"));
  }

  [TestCase("\"o\"", Commit, "a", Empty, "0")]
  [TestCase("\"o/r\"", "0123", "a", Empty, "0")]
  [TestCase("\"o/r\"", Commit, "../a", Empty, "0")]
  [TestCase("\"o/r\"", Commit, "a", "E3B0", "0")]
  [TestCase("\"o/r\"", Commit, "a", Empty, "-1")]
  [TestCase("\"o/r\"", Commit, "a", Empty, "1.5")]
  public void Parse_OfAnEntryWithAnInvalidField_IsRefused(string repository, string commit, string path, string sha256, string bytes) {
    var text = $"{{\"schema\": 1, \"files\": [{{\"repository\": {repository}, \"commit\": \"{commit}\", \"path\": \"{path}\", \"sha256\": \"{sha256}\", \"bytes\": {bytes}}}]}}";
    Assert.That(() => CorpusLock.Parse(text, "lock"), Throws.InstanceOf<CorpusException>().With.Message.Contains("lock entry 1 is malformed"));
  }

  [Test]
  public void Load_OfAMissingFile_IsAnEmptyLock()
    => Assert.That(CorpusLock.Load(Path.Combine(Path.GetTempPath(), "polypiler-no-such-lock.json")).Entries, Is.Empty);

  [Test]
  public void Load_OfAnUnreadableFile_IsRefused() {
    var folder = Scratch.Create("polypiler-lock-");
    try {
      var path = Scratch.Write(folder, "lock.json", "{}");
      using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
      var exception = Assert.Throws<CorpusException>(() => CorpusLock.Load(path))!;
      Assert.That(exception.Diagnostics.Single().Message, Does.StartWith("corpus manifest invalid: cannot read '"));
    } finally {
      Scratch.Delete(folder);
    }
  }

  /// <summary>The committed lock is in canonical form: reading and writing it gives the same bytes.</summary>
  [Test]
  public void RepositoryLock_IsCanonical() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, CorpusLock.RepositoryPath);
    var text = File.ReadAllText(path);
    Assert.That(CorpusLock.Parse(text, path).ToJson(), Is.EqualTo(text));
  }

}
