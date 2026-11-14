using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Tests.Support;
using Polypiler.Tooling;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Tooling.Corpus;

/// <summary>
/// <c>polypiler-tooling corpus</c> in process: the three verbs on the mini-upstream, discovery and its
/// stubs, <see cref="CorpusFixture.Open(string, string)"/>, <see cref="GitProcess"/> on its own, and
/// every command line that is refused.
/// </summary>
[TestFixture]
public sealed class CorpusCommandTests {

  private static MiniUpstream _mini = null!;
  private string _root = "";

  [OneTimeSetUp]
  public static void BuildUpstream() => _mini = MiniUpstream.Create();

  [OneTimeTearDown]
  public static void DeleteUpstream() => _mini.Dispose();

  [SetUp]
  public void CreateRoot() {
    this._root = Scratch.Create("polypiler-corpus-command-");
    Scratch.Write(this._root, CorpusManifest.RepositoryPath, _mini.Manifest(MiniUpstream.Fixture("mini.a", "a.txt")));
  }

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private (int ExitCode, string Output, string Error) Run(params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var nested = Directory.CreateDirectory(Path.Combine(this._root, "some", "folder")).FullName;
    var exitCode = new CorpusCommand(new GitProcess(), nested).Run(args, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private string[] Source => ["--source", _mini.Directory];

  [Test]
  public void ImportThenVerify_Succeeds_FromAFolderBelowTheRoot() {
    var import = this.Run(["import", "--update-lock", .. this.Source]);
    Assert.That(import, Is.EqualTo((0, $"2 corpus files imported, {CorpusLock.RepositoryPath} rewritten{Environment.NewLine}", "")));
    Assert.That(this.Run(["import", .. this.Source]), Is.EqualTo((0, $"2 corpus files imported{Environment.NewLine}", "")));
    Assert.That(this.Run("verify"), Is.EqualTo((0, $"2 corpus files verified{Environment.NewLine}", "")));
  }

  [Test]
  public void ImportOfOneFixture_WritesItsFiles() {
    Assert.That(this.Run(["import", "--update-lock", "--fixture", "mini.a", .. this.Source]).ExitCode, Is.Zero);
    Assert.That(CorpusFixture.Open("mini.a", this._root), Is.EqualTo(CorpusImporter.FilePath(this._root, MiniUpstream.Name, _mini.Commit, "a.txt")));
  }

  [Test]
  public void Verify_OfAChangedByte_ExitsWithOne() {
    this.Run(["import", "--update-lock", .. this.Source]);
    Scratch.Write(this._root, $"corpus/upstream/{MiniUpstream.Name}@{_mini.Commit}/a.txt", "hellO\n");
    var (exitCode, output, error) = this.Run("verify");
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Does.Contain("error PLY0023: ").And.Contains("the lock records 5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03"));
  }

  [Test]
  public void Verify_OfAnInvalidManifest_ExitsWithOne() {
    Scratch.Write(this._root, CorpusManifest.RepositoryPath, _mini.Manifest(MiniUpstream.Fixture("x.y", "a.txt").Replace(MiniUpstream.Name, "Hawkynt/Unknown")));
    var (exitCode, _, error) = this.Run("verify");
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.Contain("PLY0026: corpus manifest invalid: fixture 'x.y' names undeclared repository 'Hawkynt/Unknown'"));
  }

  [Test]
  public void Verify_OfAnInvalidLock_ExitsWithOne() {
    Scratch.Write(this._root, CorpusLock.RepositoryPath, "[]");
    Assert.That(this.Run("verify").Error, Does.Contain("error PLY0026: corpus manifest invalid: the lock must be an object"));
  }

  [Test]
  public void Verify_WithoutAManifestAnywhere_ExitsWithOne() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var empty = Scratch.Create("polypiler-corpus-empty-");
    try {
      Assert.That(new CorpusCommand(new GitProcess(), empty).Run(["verify"], output, error), Is.EqualTo(1));
      Assert.That(error.ToString(), Does.Contain("PLY0026: corpus manifest invalid: cannot read '"));
    } finally {
      Scratch.Delete(empty);
    }
  }

  [Test]
  public void Discover_ListsStubsSortedByOrdinalPath() {
    var (exitCode, output, error) = this.Run(["discover", "--repository", MiniUpstream.Name, "--glob", "**/*", .. this.Source]);
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(Regex.Matches(output, "^  path: '(.*)'$", RegexOptions.Multiline).Select(match => match.Groups[1].Value), Is.EqualTo(new[] { "LICENSE", "a.txt", "dir/b.txt" }));
    Assert.That(output, Does.StartWith($"- id: ''\n  repository: '{MiniUpstream.Name}'\n  path: 'LICENSE'\n  export: ''\n"));
    Assert.That(File.ReadAllText(Path.Combine(this._root, CorpusManifest.RepositoryPath)), Is.EqualTo(_mini.Manifest(MiniUpstream.Fixture("mini.a", "a.txt"))));
  }

  [TestCase("dir/*.txt", 1)]
  [TestCase("**/*.txt", 2)]
  [TestCase("nothing/*.zzz", 0)]
  public void Discover_CountsTheMatches(string glob, int count)
    => Assert.That(Regex.Matches(this.Run(["discover", "--repository", MiniUpstream.Name, "--glob", glob, .. this.Source]).Output, "^- id:", RegexOptions.Multiline), Has.Count.EqualTo(count));

  [Test]
  public void Discover_OfAnUndeclaredRepository_ExitsWithOne()
    => Assert.That(this.Run("discover", "--repository", "x/y", "--glob", "*").Error, Does.Contain("PLY0026: corpus manifest invalid: repository 'x/y' is not declared in "));

  [Test]
  public void Discover_WhenGitCannotListTheCommit_ReportsPly0025() {
    var repository = new CorpusRepository("o/r", "u", new string('0', 40), "MIT", []);
    var exception = Assert.Throws<CorpusException>(() => FixtureDiscovery.Discover(new GitProcess(), _mini.Directory, repository, GlobPattern.Parse("*")))!;
    Assert.That(exception.Diagnostics.Single().Message, Does.StartWith($"the files of o/r at {new string('0', 40)} cannot be listed: "));
  }

  [Test]
  public void Stub_QuotesItsValues()
    => Assert.That(FixtureDiscovery.Stub(new CorpusRepository("o/r", "u", new string('0', 40), "MIT", []), "it's.js"), Does.Contain("  path: 'it''s.js'\n  export: ''\n"));

  [TestCase(new string[0], "corpus needs 'import', 'verify' or 'discover'")]
  [TestCase(new[] { "fetch" }, "unknown corpus command 'fetch'")]
  [TestCase(new[] { "verify", "--source", "x" }, "unknown option '--source'")]
  [TestCase(new[] { "import", "--glob", "x" }, "unknown option '--glob'")]
  [TestCase(new[] { "import", "--fixture" }, "option '--fixture' requires a value")]
  [TestCase(new[] { "discover", "--glob", "a", "--glob", "b", "--repository", "r" }, "option '--glob' is given more than once")]
  [TestCase(new[] { "discover", "--glob", "a" }, "corpus discover needs '--repository' and '--glob'")]
  public void CommandLine_NotUnderstood_ExitsWithTwo(string[] args, string reason)
    => Assert.That(this.Run(args), Is.EqualTo((2, "", $"{Program.Name}: error PLY0003: {reason}{Environment.NewLine}")));

  [Test]
  public void Program_RoutesTheVerb() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["corpus"], output, error), Is.EqualTo(2));
    Assert.That(error.ToString(), Does.Contain("corpus needs 'import', 'verify' or 'discover'"));
    Assert.That(new CorpusCommand(), Is.Not.Null);
  }

  [Test]
  public void CorpusFixture_WithAStaleLock_FailsQuotingPly0023() {
    this.Run(["import", "--update-lock", .. this.Source]);
    Scratch.Write(this._root, $"corpus/upstream/{MiniUpstream.Name}@{_mini.Commit}/a.txt", "hellO\n");
    var failure = Assert.Throws<AssertionException>(() => CorpusFixture.Open("mini.a", this._root))!;
    Assert.That(failure.Message, Does.Contain("error PLY0023: corpus file 'test/mini:a.txt' has SHA-256"));
  }

  [Test]
  public void CorpusFixture_NotInTheLock_Fails() {
    var failure = Assert.Throws<AssertionException>(() => CorpusFixture.Open("mini.a", this._root))!;
    Assert.That(failure.Message, Does.Contain("PLY0026: corpus manifest invalid: '").And.Contains("/a.txt' is not in the lock"));
  }

  [Test]
  public void CorpusFixture_OfAnUnknownId_Fails()
    => Assert.That(() => CorpusFixture.Open("no.such", this._root), Throws.InstanceOf<AssertionException>().With.Message.Contains("no fixture 'no.such'"));

  [Test]
  public void CorpusFixture_OfAnInvalidManifest_Fails() {
    Scratch.Write(this._root, CorpusManifest.RepositoryPath, "schema: 2\nrepositories: []\nfixtures: []\n");
    Assert.That(() => CorpusFixture.Open("mini.a", this._root), Throws.InstanceOf<AssertionException>().With.Message.Contains("unsupported schema 2"));
  }

  [Test]
  public void GitProcess_OutlivingItsTime_IsKilled() {
    var result = new GitProcess("git", TimeSpan.Zero).Run(null, "--version");
    Assert.That((result.ExitCode, result.Succeeded, result.Output), Is.EqualTo((-1, false, Array.Empty<byte>())));
    Assert.That(result.Error, Does.Contain("did not end within"));
  }

  [Test]
  public void GitProcess_DefaultsToGitOnThePath() => Assert.That(new GitProcess().Executable, Is.EqualTo("git"));

  [Test]
  public void GitProcess_ThatCannotInitTheFetchFolder_ReportsTheFailure() {
    var blocker = Scratch.Write(this._root, "blocker", "a file where a folder is needed");
    var failure = new GitProcess().Fetch(Path.Combine(blocker, "repo"), _mini.Url, _mini.Commit);
    Assert.That(failure, Is.Not.Null);
  }

}
