using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Rebuild;
using Polypiler.Tests.Tooling.Toolchains;

namespace Polypiler.Tests.Verification.Rebuild;

/// <summary>
/// <see cref="TreeHash"/>: the listing and tree hash, checked against <c>sha256sum</c>. The expected
/// hashes were computed with <c>printf 'hello\n' &gt; a.txt; : &gt; b/c.txt; find . -type f | sort |
/// xargs sha256sum | sha256sum</c>, independently of the implementation.
/// </summary>
[TestFixture]
public sealed class TreeHashTests {

  /// <summary>The tree hash of <c>a.txt</c> = <c>hello\n</c> and an empty <c>b/c.txt</c>.</summary>
  public const string StableHash = "7be606a71e0da97c2698969020a6b713ef3d0d64927ea1bb4ab4c3e0c4885443";

  /// <summary>The SHA-256 of nothing: of an empty file, and of the listing of an empty tree.</summary>
  public const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

  private const string HelloHash = "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03";

  internal static Dictionary<string, ReadOnlyMemory<byte>> Tree(params (string Path, string Content)[] files)
    => files.ToDictionary(file => file.Path, file => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(file.Content), StringComparer.Ordinal);

  /// <summary>Test design 1: the stable generator's tree.</summary>
  [Test]
  public void TheStableTree_HasTheHashSha256sumGives() {
    var digest = TreeHash.Compute(Tree(("b/c.txt", ""), ("a.txt", "hello\n")), RebuildIgnore.Empty);
    Assert.That(digest.Sha256, Is.EqualTo(StableHash));
    Assert.That(digest.Listing, Is.EqualTo($"{HelloHash}  a.txt\n{EmptyHash}  b/c.txt\n"));
    Assert.That(digest.Files.Select(file => file.Path), Is.EqualTo(new[] { "a.txt", "b/c.txt" }));
  }

  /// <summary>Test designs 1 and 6: the same tree on disk, read with the host's separators.</summary>
  [Test]
  public void ADirectory_IsHashedWithSlashesOnEveryHost() {
    using var folder = new ScratchFolder();
    File.WriteAllBytes(System.IO.Path.Combine(folder.Path, "a.txt"), "hello\n"u8.ToArray());
    folder.File(System.IO.Path.Combine("b", "c.txt"));
    folder.Folder("empty/below");
    var digest = TreeHash.Compute(folder.Path, RebuildIgnore.Empty);
    Assert.That((digest.Sha256, digest.Files.Count), Is.EqualTo((StableHash, 2)));
  }

  /// <summary>Test design 6: a path written with backslashes lists with slashes.</summary>
  [Test]
  public void BackslashesInAPath_ListAsSlashes()
    => Assert.That(TreeHash.Compute(Tree(("b\\c.txt", ""), ("a.txt", "hello\n")), RebuildIgnore.Empty).Sha256, Is.EqualTo(StableHash));

  [Test]
  public void AMissingDirectory_IsRefused()
    => Assert.That(() => TreeHash.Compute(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N")), RebuildIgnore.Empty), Throws.TypeOf<DirectoryNotFoundException>());

  /// <summary>Test design 4: a binary file is hashed as its bytes, with no newline normalization.</summary>
  [Test]
  public void ABinaryFile_IsHashedAsItsBytes() {
    var digest = TreeHash.Compute(new Dictionary<string, ReadOnlyMemory<byte>> { ["bin"] = new byte[] { 0x00, 0xFF, 0x0A, 0x0D } }, RebuildIgnore.Empty);
    Assert.That(digest.Files[0].Sha256, Is.EqualTo(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([0x00, 0xFF, 0x0A, 0x0D]))));
    Assert.That(digest.Files[0].Content.ToArray(), Is.EqualTo(new byte[] { 0x00, 0xFF, 0x0A, 0x0D }));
    Assert.That(TreeHash.Compute(Tree(("t", "a\r\n")), RebuildIgnore.Empty).Sha256, Is.Not.EqualTo(TreeHash.Compute(Tree(("t", "a\n")), RebuildIgnore.Empty).Sha256));
  }

  /// <summary>Test design 8: an empty tree hashes the empty listing.</summary>
  [Test]
  public void AnEmptyTree_HashesTheEmptyListing() {
    var digest = TreeHash.Compute(Tree(), RebuildIgnore.Empty);
    Assert.That((digest.Sha256, digest.Files.Count, digest.Listing), Is.EqualTo((EmptyHash, 0, "")));
  }

  /// <summary>Test design 9: a tree of one empty file.</summary>
  [Test]
  public void ASingleEmptyFile_IsListed()
    => Assert.That(TreeHash.Compute(Tree(("b/c.txt", "")), RebuildIgnore.Empty).Listing, Is.EqualTo($"{EmptyHash}  b/c.txt\n"));

  /// <summary>Test design 5: a file a glob ignores is left out of the listing.</summary>
  [Test]
  public void AnIgnoredFile_IsLeftOut() {
    var ignore = new RebuildIgnore([new("**/*.log", "build log")], []);
    Assert.That(TreeHash.Compute(Tree(("a.txt", "hello\n"), ("b/c.txt", ""), ("run.log", "1"), ("b/x/run.log", "2")), ignore).Sha256, Is.EqualTo(StableHash));
  }

  /// <summary>Test designs 5 and 7: a manifest is hashed in its canonical form without the ignored pointers.</summary>
  [Test]
  public void AManifest_IsHashedCanonically() {
    var ignore = new RebuildIgnore([], [new("/verification/durationMs", "wall-clock time")]);
    var first = TreeHash.Compute(Tree(("polypiler.manifest.json", """{ "b": 1, "verification": { "durationMs": 10, "oracle": "x" } }""")), ignore);
    var second = TreeHash.Compute(Tree(("polypiler.manifest.json", """{"verification":{"oracle":"x","durationMs":99},"b":1}""")), ignore);
    Assert.That(first.Sha256, Is.EqualTo(second.Sha256));
    Assert.That(Encoding.UTF8.GetString(first.Files[0].Content.Span), Is.EqualTo("""{"b":1,"verification":{"oracle":"x"}}"""));
  }

  /// <summary>A manifest below the root is a manifest too; a file merely ending in the name is not.</summary>
  [TestCase("polypiler.manifest.json", true)]
  [TestCase("pair/polypiler.manifest.json", true)]
  [TestCase("old-polypiler.manifest.json", false)]
  [TestCase("polypiler.manifest.json.bak", false)]
  public void IsManifest(string path, bool expected) => Assert.That(TreeHash.IsManifest(path), Is.EqualTo(expected));

  /// <summary>Test design 19: a manifest that is not JSON is hashed as its bytes.</summary>
  [Test]
  public void AManifestThatIsNotJson_IsHashedAsItsBytes() {
    var digest = TreeHash.Compute(Tree(("polypiler.manifest.json", """{"b": 1""")), RebuildIgnore.Empty);
    Assert.That(Encoding.UTF8.GetString(digest.Files[0].Content.Span), Is.EqualTo("""{"b": 1"""));
  }

  [Test]
  public void NullArguments_AreRefused() {
    Assert.That(() => TreeHash.Compute((string)null!, RebuildIgnore.Empty), Throws.ArgumentNullException);
    Assert.That(() => TreeHash.Compute((IReadOnlyDictionary<string, ReadOnlyMemory<byte>>)null!, RebuildIgnore.Empty), Throws.ArgumentNullException);
    Assert.That(() => TreeHash.Compute(Tree(), null!), Throws.ArgumentNullException);
  }

}
