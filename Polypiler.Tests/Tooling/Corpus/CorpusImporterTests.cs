using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Tooling.Corpus;

/// <summary>
/// <see cref="CorpusImporter"/> and <see cref="CorpusVerifier"/> on the mini-upstream repository,
/// built once with git: import from a local checkout and by fetch, the lock compared and rewritten,
/// and every way an import or a verification fails.
/// </summary>
[TestFixture]
public sealed class CorpusImporterTests {

  private const string Hello = "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03";
  private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

  private static MiniUpstream _mini = null!;
  private static MiniUpstream _extended = null!;
  private string _root = "";

  [OneTimeSetUp]
  public static void BuildUpstreams() {
    _mini = MiniUpstream.Create();
    _extended = MiniUpstream.Create(("crlf.txt", "a\r\nb\r\n"), ("empty.txt", ""), ("vectors/a.json", "[1]\n"));
  }

  [OneTimeTearDown]
  public static void DeleteUpstreams() {
    _mini.Dispose();
    _extended.Dispose();
  }

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-corpus-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private CorpusImporter Importer(MiniUpstream? source, Action<string, byte[]>? writeFile = null, GitProcess? git = null)
    => new(git ?? new GitProcess(), this._root, source is null ? [] : [source.Directory], writeFile);

  private static CorpusManifest Manifest(string text) => CorpusManifest.Parse(text, "corpus.yaml");

  private string Upstream(MiniUpstream mini, string path) => CorpusImporter.FilePath(this._root, MiniUpstream.Name, mini.Commit, path);

  private CorpusLock ImportAll(MiniUpstream mini, string fixtures, bool fromSource = true)
    => this.Importer(fromSource ? mini : null).Import(Manifest(mini.Manifest(fixtures)), new([]), [], true).Lock;

  private string LockText() => File.ReadAllText(Path.Combine(this._root, CorpusLock.RepositoryPath));

  private static string Text(Polypiler.Core.Diagnostic diagnostic) => $"{diagnostic.Code}: {diagnostic.Message}";

  [Test]
  public void Import_FromALocalCheckout_WritesTheFilesReadOnlyAndTheLock() {
    var imported = this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt", "[dir/b.txt]"));
    Assert.That(imported.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "LICENSE", "a.txt", "dir/b.txt" }));
    Assert.That(File.ReadAllText(this.Upstream(_mini, "a.txt")), Is.EqualTo("hello\n"));
    Assert.That(imported.Find(MiniUpstream.Name, _mini.Commit, "a.txt")!.Sha256, Is.EqualTo(Hello));
    Assert.That(File.GetAttributes(this.Upstream(_mini, "a.txt")).HasFlag(FileAttributes.ReadOnly), Is.True);
    Assert.That(Scratch.Files(Path.Combine(this._root, "corpus")), Has.None.Contains(".corpus-import"));
    Assert.That(LockText(), Is.EqualTo(imported.ToJson()));
  }

  [Test]
  public void Import_Twice_GivesByteIdenticalLocks() {
    var fixtures = MiniUpstream.Fixture("mini.a", "a.txt") + MiniUpstream.Fixture("mini.b", "dir/b.txt");
    this.ImportAll(_mini, fixtures);
    var first = LockText();
    this.ImportAll(_mini, fixtures);
    Assert.That(LockText(), Is.EqualTo(first));
    Assert.That(CorpusLock.Parse(first, "lock").Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "LICENSE", "a.txt", "dir/b.txt" }));
  }

  [Test]
  public void Import_ByFetch_GivesTheSameBytesAsGitShow() {
    var fetched = this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt"), fromSource: false);
    Assert.That(Directory.Exists(Path.Combine(CorpusImporter.Folder(this._root, Manifest(_mini.Manifest()).Repositories[0]), ".git")), Is.True);
    var shown = new GitProcess().ReadFile(_mini.Directory, _mini.Commit, "a.txt").Output;
    Assert.That(File.ReadAllBytes(this.Upstream(_mini, "a.txt")), Is.EqualTo(shown));
    Assert.That(fetched.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "LICENSE", "a.txt" }));

    var again = this.Importer(null).Import(Manifest(_mini.Manifest(MiniUpstream.Fixture("mini.a", "a.txt"))), fetched, [], false);
    Assert.That(again.Files, Is.EqualTo(2), "the second import reuses the fetched commit");
  }

  [Test]
  public void Import_OfAnUnknownCommit_ReportsGitsStderrAsTheCause() {
    var manifest = Manifest(_mini.Manifest(commit: new string('0', 40)));
    var exception = Assert.Throws<CorpusException>(() => this.Importer(_mini).Import(manifest, new([]), [], true))!;
    var diagnostic = exception.Diagnostics.Single();
    Assert.That(diagnostic.Code, Is.EqualTo(CorpusDiagnostics.PathInvalid));
    Assert.That(diagnostic.Message, Does.StartWith($"{MiniUpstream.Name} at {new string('0', 40)} cannot be fetched from {_mini.Url}: "));
    Assert.That(diagnostic.Cause, Is.Not.Empty);
    Assert.That(diagnostic.Message, Does.EndWith(diagnostic.Cause));
    Assert.That(File.Exists(Path.Combine(this._root, CorpusLock.RepositoryPath)), Is.False);
  }

  [Test]
  public void Import_OfAPathMissingAtThePin_IsRefusedAndLeavesTheLockAlone() {
    this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt"));
    var before = LockText();
    var manifest = Manifest(_mini.Manifest(MiniUpstream.Fixture("mini.a", "a.txt") + MiniUpstream.Fixture("mini.c", "c.txt")));
    var exception = Assert.Throws<CorpusException>(() => this.Importer(_mini).Import(manifest, CorpusLock.Parse(before, "lock"), [], true))!;
    Assert.That(exception.Diagnostics.Select(Text), Is.EqualTo(new[] {
      $"PLY0025: 'c.txt' does not exist in {MiniUpstream.Name} at {_mini.Commit}: {exception.Diagnostics[0].Cause}"
    }));
    Assert.That(exception.Diagnostics[0].Cause, Does.Contain("c.txt"));
    Assert.That(LockText(), Is.EqualTo(before));
  }

  [Test]
  public void Import_OfAPathOnlyInALaterCommit_IsMissingAtAnEarlierPin() {
    var manifest = Manifest(_mini.Manifest(MiniUpstream.Fixture("mini.b", "dir/b.txt"), commit: _mini.FirstCommit));
    var exception = Assert.Throws<CorpusException>(() => this.Importer(_mini).Import(manifest, new([]), [], true))!;
    Assert.That(exception.Diagnostics.Single().Message, Does.StartWith($"'dir/b.txt' does not exist in {MiniUpstream.Name} at {_mini.FirstCommit}"));
  }

  [Test]
  public void Fixture_WithADependency_ImportsIt_WithoutOne_OnlyItsFile() {
    var with = this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt", "[dir/b.txt]"));
    Assert.That(with.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "LICENSE", "a.txt", "dir/b.txt" }));
    Scratch.Delete(this._root);
    var without = this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt"));
    Assert.That(without.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "LICENSE", "a.txt" }));
  }

  [Test]
  public void VectorKinds_OnlyAFileKindImportsTheVectorFile() {
    var fixtures = MiniUpstream.Fixture("mini.embedded", "a.txt")
      + MiniUpstream.Fixture("mini.published", "a.txt", vectors: "{ kind: published, source: 'RFC 1950' }")
      + MiniUpstream.Fixture("mini.file", "crlf.txt", vectors: "{ kind: file, source: vectors/a.json }");
    var imported = this.ImportAll(_extended, fixtures);
    Assert.That(imported.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "LICENSE", "a.txt", "crlf.txt", "vectors/a.json" }));
  }

  [Test]
  public void CrlfContent_IsKeptByteForByte() {
    this.ImportAll(_extended, MiniUpstream.Fixture("mini.crlf", "crlf.txt"));
    Assert.That(File.ReadAllBytes(this.Upstream(_extended, "crlf.txt")), Is.EqualTo(Encoding.ASCII.GetBytes("a\r\nb\r\n")));
  }

  [Test]
  public void EmptyFile_IsLockedWithTheEmptyHashAndNoBytes() {
    var entry = this.ImportAll(_extended, MiniUpstream.Fixture("mini.empty", "empty.txt")).Find(MiniUpstream.Name, _extended.Commit, "empty.txt")!;
    Assert.That((entry.Sha256, entry.Bytes), Is.EqualTo((EmptySha256, 0L)));
  }

  [Test]
  public void EmptyFixtureList_LocksOnlyTheLicenceFiles() {
    var imported = this.ImportAll(_mini, "");
    Assert.That(imported.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "LICENSE" }));
  }

  [Test]
  public void Import_WithoutUpdateLock_ComparesWithTheLock() {
    var fixtures = MiniUpstream.Fixture("mini.a", "a.txt");
    var locked = this.ImportAll(_mini, fixtures);
    var before = LockText();
    var result = this.Importer(_mini).Import(Manifest(_mini.Manifest(fixtures)), locked, [], false);
    Assert.That((result.Files, result.Lock), Is.EqualTo((2, locked)));
    Assert.That(LockText(), Is.EqualTo(before));
  }

  [Test]
  public void Import_OfBytesTheLockDoesNotRecord_IsRefusedBeforeAnythingIsWritten() {
    var fixtures = MiniUpstream.Fixture("mini.a", "a.txt");
    var stale = new CorpusLock(this.ImportAll(_mini, fixtures).Entries.Select(entry => entry.Path == "a.txt" ? entry with { Sha256 = EmptySha256, Bytes = 0 } : entry));
    Scratch.Delete(this._root);
    var exception = Assert.Throws<CorpusException>(() => this.Importer(_mini).Import(Manifest(_mini.Manifest(fixtures)), stale, [], false))!;
    Assert.That(exception.Diagnostics.Select(Text), Is.EqualTo(new[] {
      $"PLY0023: corpus file '{MiniUpstream.Name}:a.txt' has SHA-256 {Hello}, the lock records {EmptySha256} (6 bytes, the lock records 0 bytes)"
    }));
    Assert.That(Directory.Exists(this._root), Is.False, "nothing was written");
  }

  [Test]
  public void Import_OfAFileNotInTheLock_IsRefused() {
    var exception = Assert.Throws<CorpusException>(() => this.Importer(_mini).Import(Manifest(_mini.Manifest()), new([]), [], false))!;
    Assert.That(exception.Diagnostics.Select(Text), Is.EqualTo(new[] {
      $"PLY0026: corpus manifest invalid: '{MiniUpstream.Name}@{_mini.Commit}/LICENSE' is not in the lock; import it with --update-lock"
    }));
  }

  [Test]
  public void Import_OfOneFixture_KeepsTheOtherLockEntries() {
    var fixtures = MiniUpstream.Fixture("mini.a", "a.txt") + MiniUpstream.Fixture("mini.b", "dir/b.txt");
    var all = this.ImportAll(_mini, fixtures);
    var one = this.Importer(_mini).Import(Manifest(_mini.Manifest(fixtures)), all, ["mini.b"], true);
    Assert.That((one.Files, one.Lock.ToJson()), Is.EqualTo((2, all.ToJson())));
  }

  [Test]
  public void Import_OfAnUnknownFixture_IsRefused() {
    var exception = Assert.Throws<CorpusException>(() => this.Importer(_mini).Import(Manifest(_mini.Manifest()), new([]), ["no.such"], true))!;
    Assert.That(exception.Diagnostics.Select(Text), Is.EqualTo(new[] { "PLY0026: corpus manifest invalid: no fixture 'no.such' in corpus.yaml" }));
  }

  [Test]
  public void Import_ReplacesAModifiedReadOnlyFile() {
    var fixtures = MiniUpstream.Fixture("mini.a", "a.txt");
    var locked = this.ImportAll(_mini, fixtures);
    Scratch.Write(this._root, $"corpus/upstream/{MiniUpstream.Name}@{_mini.Commit}/a.txt", "hellO\n");
    this.Importer(_mini).Import(Manifest(_mini.Manifest(fixtures)), locked, [], false);
    Assert.That(File.ReadAllText(this.Upstream(_mini, "a.txt")), Is.EqualTo("hello\n"));
  }

  [Test]
  public void Import_InterruptedByAnIoFailure_LeavesNoFileAndNoLock() {
    var calls = 0;
    void FailSecond(string path, byte[] content) {
      if (++calls == 2)
        throw new IOException("disk full");
      File.WriteAllBytes(path, content);
    }

    var manifest = Manifest(_mini.Manifest(MiniUpstream.Fixture("mini.a", "a.txt", "[dir/b.txt]")));
    var exception = Assert.Throws<CorpusException>(() => this.Importer(_mini, FailSecond).Import(manifest, new([]), [], true))!;
    var diagnostic = exception.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Cause), Is.EqualTo((CorpusDiagnostics.PathInvalid, "disk full")));
    Assert.That(diagnostic.Message, Does.StartWith("corpus path '").And.Contains("a.txt' cannot be written: disk full"));
    Assert.That(Scratch.Files(this._root), Is.Empty);
  }

  [Test]
  public void Import_WithoutGit_ReportsPly0021() {
    var exception = Assert.Throws<CorpusException>(() => this.Importer(_mini, git: new GitProcess("polypiler-no-such-git", GitProcess.DefaultTimeout))
      .Import(Manifest(_mini.Manifest()), new([]), [], true))!;
    Assert.That(Text(exception.Diagnostics.Single()), Does.StartWith("PLY0021: toolchain 'git' is not installed (probe: polypiler-no-such-git --version): "));
    Assert.That(exception.Diagnostics[0].ExitCode, Is.EqualTo(Polypiler.Core.ExitCode.EnvironmentError));
  }

  [Test]
  public void Verify_OfAnUntouchedWorkspace_FindsNothing() {
    var locked = this.ImportAll(_extended, MiniUpstream.Fixture("mini.a", "a.txt", "[empty.txt, crlf.txt]"));
    Assert.That(CorpusVerifier.Verify(this._root, Manifest(_extended.Manifest()), locked), Is.Empty);
  }

  [Test]
  public void Verify_OfAChangedByte_ReportsBothHashes() {
    var locked = this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt"));
    Scratch.Write(this._root, $"corpus/upstream/{MiniUpstream.Name}@{_mini.Commit}/a.txt", "hellO\n");
    var changed = CorpusLockEntry.Hash(Encoding.ASCII.GetBytes("hellO\n"));
    Assert.That(CorpusVerifier.Verify(this._root, Manifest(_mini.Manifest()), locked).Select(Text), Is.EqualTo(new[] {
      $"PLY0023: corpus file '{MiniUpstream.Name}:a.txt' has SHA-256 {changed}, the lock records {Hello}"
    }));
  }

  [Test]
  public void Verify_OfATruncatedFile_NamesBothLengths() {
    var locked = this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt"));
    Scratch.Write(this._root, $"corpus/upstream/{MiniUpstream.Name}@{_mini.Commit}/a.txt", "hell");
    Assert.That(CorpusVerifier.Verify(this._root, Manifest(_mini.Manifest()), locked).Single().Message, Does.EndWith("(4 bytes, the lock records 6 bytes)"));
  }

  [Test]
  public void Verify_OfAMissingFile_ReportsPly0023() {
    var locked = this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt"));
    var file = this.Upstream(_mini, "a.txt");
    File.SetAttributes(file, FileAttributes.Normal);
    File.Delete(file);
    var diagnostic = CorpusVerifier.Verify(this._root, Manifest(_mini.Manifest()), locked).Single();
    Assert.That(Text(diagnostic), Does.StartWith($"PLY0023: corpus file '{MiniUpstream.Name}:a.txt' cannot be read, the lock records {Hello}: "));
  }

  [Test]
  public void Verify_OfAFileOnDiskButNotInTheLock_ReportsPly0026() {
    var locked = this.ImportAll(_mini, MiniUpstream.Fixture("mini.a", "a.txt"), fromSource: false);
    Scratch.Write(this._root, $"corpus/upstream/{MiniUpstream.Name}@{_mini.Commit}/extra.txt", "x");
    Assert.That(CorpusVerifier.Verify(this._root, Manifest(_mini.Manifest()), locked).Select(Text), Is.EqualTo(new[] {
      $"PLY0026: corpus manifest invalid: 'corpus/upstream/{MiniUpstream.Name}@{_mini.Commit}/extra.txt' is on disk but not in the lock"
    }), "the fetched .git folder is not reported");
  }

  [Test]
  public void Verify_OfALockEntryOutsideThePins_ReportsPly0026() {
    var commit = _mini.Commit;
    var entries = new CorpusLock([
      new("other/repo", commit, "a.txt", Hello, 6),
      new(MiniUpstream.Name, _mini.FirstCommit, "a.txt", Hello, 6)
    ]);
    Assert.That(CorpusVerifier.Verify(this._root, Manifest(_mini.Manifest()), entries).Select(Text), Is.EqualTo(new[] {
      $"PLY0026: corpus manifest invalid: lock entry 'other/repo@{commit}/a.txt' names undeclared repository 'other/repo'",
      $"PLY0026: corpus manifest invalid: lock entry '{MiniUpstream.Name}@{_mini.FirstCommit}/a.txt' is not at the pin {commit}"
    }));
  }

  [Test]
  public void Locate_PrefersASourceHoldingTheCommit_AndSkipsOneThatDoesNot() {
    var repository = Manifest(_mini.Manifest()).Repositories[0];
    var importer = new CorpusImporter(new GitProcess(), this._root, [this._root, _extended.Directory, _mini.Directory]);
    Assert.That(importer.Locate(repository), Is.EqualTo(_mini.Directory));
  }

}
