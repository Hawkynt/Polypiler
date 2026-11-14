using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Rebuild;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Toolchains;

namespace Polypiler.Tests.Verification.Rebuild;

/// <summary><see cref="RebuildIgnore"/>: the file's schema, the globs, and the canonical manifest without the ignored pointers (RFC 6901).</summary>
[TestFixture]
public sealed class RebuildIgnoreTests {

  private static string Canonical(RebuildIgnore ignore, string json) => Encoding.UTF8.GetString(ignore.Canonicalize(Encoding.UTF8.GetBytes(json))!.Value.Span);

  private static RebuildIgnore Pointers(params string[] pointers) => new([], [.. Array.ConvertAll(pointers, pointer => new IgnoredPointer(pointer, "test"))]);

  private static RebuildIgnoreException Refused(string text) => Assert.Throws<RebuildIgnoreException>(() => RebuildIgnore.Parse(text))!;

  /// <summary>The repository's list: schema 1, and empty because the manifest documents nothing run-dependent.</summary>
  [Test]
  public void TheRepositoryList_IsValidAndEmpty() {
    var ignore = RebuildIgnore.Load(Path.Combine(PrototypeFixture.RepositoryRoot, RebuildIgnore.RepositoryPath));
    Assert.That((ignore.Files.Count, ignore.ManifestPointers.Count), Is.EqualTo((0, 0)));
  }

  [Test]
  public void AFullList_IsRead() {
    var ignore = RebuildIgnore.Parse("""
      schema: 1
      files:
        - glob: "**/*.log"
          reason: build log
      manifestPointers:
        - pointer: /verification/durationMs
          reason: wall-clock time
      """);
    Assert.That(ignore.Files, Is.EqualTo(new[] { new IgnoredFile("**/*.log", "build log") }));
    Assert.That(ignore.ManifestPointers, Is.EqualTo(new[] { new IgnoredPointer("/verification/durationMs", "wall-clock time") }));
  }

  [Test]
  public void ListsMayBeLeftOut() {
    var ignore = RebuildIgnore.Parse("schema: 1\n");
    Assert.That((ignore.Files.Count, ignore.ManifestPointers.Count), Is.EqualTo((0, 0)));
  }

  /// <summary>Test design 18 and the scenario "An ignore entry without a reason is refused".</summary>
  [TestCase("schema: 1\nfiles:\n  - glob: \"*.log\"\n", "the file glob '*.log' has no 'reason'", 3)]
  [TestCase("schema: 1\nfiles:\n  - glob: \"*.log\"\n    reason: \" \"\n", "the file glob '*.log' has no 'reason'", 3)]
  [TestCase("schema: 1\nfiles:\n  - reason: r\n", "an entry of 'files' has no 'glob'", 3)]
  [TestCase("schema: 1\nmanifestPointers:\n  - pointer: /a\n", "the manifest pointer '/a' has no 'reason'", 3)]
  [TestCase("schema: 1\nmanifestPointers:\n  - reason: r\n", "an entry of 'manifestPointers' has no 'pointer'", 3)]
  [TestCase("schema: 1\nmanifestPointers:\n  - pointer: verification\n    reason: r\n", "'verification' is not a JSON pointer", 3)]
  [TestCase("schema: 1\nmanifestPointers:\n  - pointer: /a~2b\n    reason: r\n", "'/a~2b' is not a JSON pointer", 3)]
  [TestCase("schema: 1\nmanifestPointers:\n  - pointer: /a~\n    reason: r\n", "'/a~' is not a JSON pointer", 3)]
  [TestCase("schema: 1\nfiles:\n  - glob: a\n    reason: r\n    extra: x\n", "unknown key 'extra' in an entry of 'files'", 5)]
  [TestCase("schema: 1\nfiles:\n  - [a]\n", "an entry of 'files' must be a mapping", 3)]
  [TestCase("schema: 1\nfiles: a\n", "'files' must be a list", 2)]
  [TestCase("schema: 1\nother: 1\n", "unknown key 'other'", 2)]
  [TestCase("schema: 1\n? [a]\n: 1\n", "unknown key '[ a ]'", 2)]
  [TestCase("schema: 1\nfiles:\n  - ? [a]\n    : 1\n", "unknown key '[ a ]' in an entry of 'files'", 3)]
  [TestCase("schema: 1\nfiles:\n  - glob: [a]\n    reason: r\n", "an entry of 'files' has no 'glob'", 3)]
  [TestCase("schema: 2\n", "expected 'schema: 1'", 1)]
  [TestCase("files: []\n", "expected 'schema: 1'", 1)]
  [TestCase("- a\n", "expected a mapping with 'schema: 1'", 1)]
  [TestCase("", "expected a mapping with 'schema: 1'", 1)]
  [TestCase("schema: [1\n", "", 2)]
  public void AnInvalidList_IsRefusedWithItsLine(string text, string message, int line) {
    var exception = Refused(text);
    Assert.That(exception.Line, Is.EqualTo(line));
    Assert.That(exception.Message, Does.Contain(message));
  }

  [Test]
  public void AFileThatCannotBeRead_IsRefused() {
    var exception = Assert.Throws<RebuildIgnoreException>(() => RebuildIgnore.Load(Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"), "x.yaml")))!;
    Assert.That(exception.Line, Is.Zero);
    Assert.That(exception.Message, Does.Contain("cannot be read"));
  }

  [Test]
  public void AFile_IsLoaded() {
    using var folder = new ScratchFolder();
    var file = folder.File("rebuild-ignore.yaml");
    File.WriteAllText(file, "schema: 1\nfiles:\n  - glob: x\n    reason: y\n");
    Assert.That(RebuildIgnore.Load(file).Files, Has.Count.EqualTo(1));
  }

  [Test]
  public void TheConstructor_ChecksEveryEntry() {
    Assert.That(() => new RebuildIgnore([new("", "r")], []), Throws.ArgumentException);
    Assert.That(() => new RebuildIgnore([], [new("x", "r")]), Throws.ArgumentException);
    Assert.That(() => new RebuildIgnore(null!, []), Throws.ArgumentNullException);
    Assert.That(() => new RebuildIgnore([], null!), Throws.ArgumentNullException);
    Assert.That(() => RebuildIgnore.Parse(null!), Throws.ArgumentNullException);
  }

  [TestCase("*.log", "run.log", true)]
  [TestCase("*.log", "b/run.log", false)]
  [TestCase("**/*.log", "run.log", true)]
  [TestCase("**/*.log", "b/c/run.log", true)]
  [TestCase("b/**", "b/c/d.txt", true)]
  [TestCase("b/**", "a/b/c", false)]
  [TestCase("b/?.txt", "b/c.txt", true)]
  [TestCase("b/?.txt", "b/cd.txt", false)]
  [TestCase("b/?.txt", "b//.txt", false)]
  [TestCase("a.txt", "a.txt", true)]
  [TestCase("a.txt", "aXtxt", false)]
  [TestCase("a.txt", "A.txt", false)]
  [TestCase("**", "x/y", true)]
  public void AGlob_MatchesTheWholePath(string glob, string path, bool expected)
    => Assert.That(new RebuildIgnore([new(glob, "r")], []).IsIgnored(path), Is.EqualTo(expected));

  /// <summary>Test design 7: the canonical form sorts keys at every depth and drops insignificant whitespace.</summary>
  [Test]
  public void TheCanonicalForm_SortsKeysAndDropsWhitespace()
    => Assert.That(Canonical(RebuildIgnore.Empty, """{ "z": [ { "b": null, "a": true } , 2.5, "s" ], "a": { "y": false, "x": 1 } }"""),
      Is.EqualTo("""{"a":{"x":1,"y":false},"z":[{"a":true,"b":null},2.5,"s"]}"""));

  [TestCase("/verification/durationMs", """{"verification":{"durationMs":5,"oracle":"x"}}""", """{"verification":{"oracle":"x"}}""")]
  [TestCase("/outputs/1", """{"outputs":["a","b","c"]}""", """{"outputs":["a","c"]}""")]
  [TestCase("/outputs/0/sha256", """{"outputs":[{"sha256":"x","path":"a"}]}""", """{"outputs":[{"path":"a"}]}""")]
  [TestCase("/a~1b/c~0d", """{"a/b":{"c~d":1,"e":2}}""", """{"a/b":{"e":2}}""")]
  [TestCase("/missing/deeper", """{"a":1}""", """{"a":1}""")]
  [TestCase("/a/b", """{"a":1}""", """{"a":1}""")]
  [TestCase("/outputs/3", """{"outputs":[1]}""", """{"outputs":[1]}""")]
  [TestCase("/outputs/01", """{"outputs":[1,2]}""", """{"outputs":[1,2]}""")]
  [TestCase("/outputs/-", """{"outputs":[1]}""", """{"outputs":[1]}""")]
  [TestCase("/outputs/0/x", """{"outputs":[{"x":1,"y":2}]}""", """{"outputs":[{"y":2}]}""")]
  [TestCase("/outputs/9/x", """{"outputs":[{"x":1}]}""", """{"outputs":[{"x":1}]}""")]
  [TestCase("/0", """[1,2]""", """[2]""")]
  [TestCase("/a/b/c", """{"a":1}""", """{"a":1}""")]
  [TestCase("/outputs/x/y", """{"outputs":[{"y":1}]}""", """{"outputs":[{"y":1}]}""")]
  public void APointer_RemovesWhatItNames(string pointer, string json, string expected)
    => Assert.That(Canonical(Pointers(pointer), json), Is.EqualTo(expected));

  /// <summary>Test design 19: bytes that are not JSON have no canonical form.</summary>
  [TestCase("""{"a": 1""")]
  [TestCase("not json")]
  [TestCase("""{"a":1,"a":2}""")]
  public void BytesThatAreNotJson_HaveNoCanonicalForm(string text)
    => Assert.That(Pointers("/a").Canonicalize(Encoding.UTF8.GetBytes(text)), Is.Null);

  [TestCase("/a", true)]
  [TestCase("/a~0~1", true)]
  [TestCase("/", true)]
  [TestCase("a", false)]
  [TestCase("", false)]
  [TestCase("/~", false)]
  public void IsPointer(string text, bool expected) => Assert.That(RebuildIgnore.IsPointer(text), Is.EqualTo(expected));

}
