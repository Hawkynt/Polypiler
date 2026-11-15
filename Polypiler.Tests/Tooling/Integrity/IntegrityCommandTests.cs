using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Integrity;

namespace Polypiler.Tests.Tooling.Integrity;

/// <summary>
/// <c>polypiler-tooling integrity</c> in process: <c>check</c> and <c>seal</c> on synthetic trees, the
/// pull-request rule over git histories built in the test (test design 10, 11, 19 to 21), and every
/// command line that is refused.
/// </summary>
[TestFixture]
public sealed class IntegrityCommandTests {

  private const string Line = "- " + GoldenTree.Adler32 + ": add 'a' vector (source: python)";

  private GoldenTree _tree = null!;

  [SetUp]
  public void CreateTree() => this._tree = new(Scratch.Create("polypiler-integrity-command-"));

  [TearDown]
  public void DeleteTree() => Scratch.Delete(this._tree.Root);

  private (int ExitCode, string Output, string Error) Run(params string[] args) => this.RunIn(this._tree.Root, args);

  private (int ExitCode, string Output, string Error) RunIn(string directory, params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = new IntegrityCommand(new GitProcess(), directory).Run(args, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private static string Usage(string message) => $"polypiler-tooling: error PLY0003: {message}{Environment.NewLine}";

  [Test]
  public void SealThenCheck_FromAFolderBelowTheRoot() {
    this._tree.Write(GoldenTree.Adler32, GoldenTree.RepositoryAdler32);
    var below = this._tree.Resolve("tests/fixtures/verify");
    Assert.That(this.RunIn(below, "seal", "adler32.vectors.json", "./adler32.vectors.json"), Is.EqualTo((0, $"1 golden files sealed in {GoldenLock.RepositoryPath}{Environment.NewLine}", "")));
    Assert.That(this.RunIn(below, "check"), Is.EqualTo((0, $"1 golden files verified{Environment.NewLine}", "")));
  }

  [Test]
  public void Check_ReportsEveryProblem_WithExitCode1() {
    this._tree.Write(GoldenTree.Adler32, GoldenTree.RepositoryAdler32).Seal(GoldenTree.Adler32);
    this._tree.Write(GoldenTree.Adler32, GoldenTree.RepositoryAdler32.Replace("024D0127", "024D0128", StringComparison.Ordinal))
      .Write("tests/fixtures/new.vectors.json", GoldenTree.RepositoryAdler32.Replace("\"python\",\n    \"detail\"", "\"nodejs\",\n    \"detail\"", StringComparison.Ordinal));
    var (exitCode, output, error) = this.Run("check");
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Is.EqualTo(string.Join(Environment.NewLine,
      $"polypiler: error PLY0027: golden file 'tests/fixtures/new.vectors.json' was modified: not in the golden lock",
      $"polypiler: error PLY0027: golden file '{GoldenTree.Adler32}' was modified: hash differs from the golden lock",
      "polypiler: error PLY0028: vectors 'tests/fixtures/new.vectors.json' lack independent provenance: source is not a toolchain key of tooling/toolchains.lock: 'nodejs'",
      "")));
  }

  [Test]
  public void Seal_ThatFails_ExitsWith1_AndWritesNothing() {
    this._tree.Write("tests/fixtures/p.vectors.json", GoldenTree.RepositoryAdler32.Replace("\"generatedBy\": \"python\"", "\"generatedBy\": \"polypiler\"", StringComparison.Ordinal));
    var (exitCode, _, error) = this.Run("seal", "tests/fixtures/p.vectors.json");
    Assert.That((exitCode, error), Is.EqualTo((1, $"polypiler: error PLY0028: vectors 'tests/fixtures/p.vectors.json' lack independent provenance: generatedBy 'polypiler' names Polypiler itself{Environment.NewLine}")));
    Assert.That(File.Exists(this._tree.Resolve(GoldenLock.RepositoryPath)), Is.False);
  }

  [TestCase(new string[0], "integrity needs 'check' or 'seal'")]
  [TestCase(new[] { "verify" }, "unknown integrity command 'verify'")]
  [TestCase(new[] { "check", "--force" }, "unknown option '--force'")]
  [TestCase(new[] { "check", "--base" }, "option '--base' requires a value")]
  [TestCase(new[] { "check", "--base", "a", "--base", "b" }, "option '--base' is given more than once")]
  [TestCase(new[] { "seal" }, "integrity seal needs the files to seal")]
  [TestCase(new[] { "seal", "tests/fixtures/x/input.txt" }, "'tests/fixtures/x/input.txt' does not exist and is not in the golden lock")]
  public void CommandLine_IsRefused(string[] args, string message) => Assert.That(this.Run(args), Is.EqualTo((2, "", Usage(message))));

  [Test]
  public void Seal_OutsideTheRoot_IsRefused() {
    this._tree.Write("tests/fixtures/.keep", "");
    Assert.That(this.Run("seal", "../elsewhere.vectors.json"), Is.EqualTo((2, "", Usage($"'../elsewhere.vectors.json' is outside the repository root '{this._tree.Root}'"))));
    Assert.That(this.Run("seal", this._tree.Root + "/.."), Is.EqualTo((2, "", Usage($"'{this._tree.Root}/..' is outside the repository root '{this._tree.Root}'"))));
  }

  [Test]
  public void WithoutTestFolders_TheCurrentDirectoryIsTheRoot()
    => Assert.That(this.Run("check"), Is.EqualTo((0, $"0 golden files verified{Environment.NewLine}", "")));

  [Test]
  public void Base_ThatNamesNoCommit_IsAUsageError() {
    this._tree.History(changeCompiler: false);
    Assert.That(this.Run("check", "--base", "nope"), Is.EqualTo((2, "", Usage("'nope' does not name a commit of this repository"))));
  }

  [Test]
  public void Base_OutsideAGitRepository_IsAUsageError()
    => Assert.That(this.Run("check", "--base", "c1").ExitCode, Is.EqualTo(2));

  [Test]
  public void Base_EqualToHead_ChecksNoCommit() {
    this._tree.History(changeCompiler: false).Seal(GoldenTree.Adler32);
    this._tree.Declare(Line).Commit("c2");
    Assert.That(this.Run("check", "--base", "HEAD"), Is.EqualTo((0, $"1 golden files verified, 0 commits since HEAD checked{Environment.NewLine}", "")));
  }

  [Test]
  public void IsolatedDeclaredChange_Passes() {
    this._tree.History(changeCompiler: false).Seal(GoldenTree.Adler32);
    this._tree.Declare(Line).Commit("c2");
    Assert.That(this.Run("check", "--base", "c1"), Is.EqualTo((0, $"1 golden files verified, 1 commits since c1 checked{Environment.NewLine}", "")));
  }

  [Test]
  public void ChangeWithFeatureAndBacklogCompanions_Passes() {
    this._tree.History(changeCompiler: false).Seal(GoldenTree.Adler32);
    this._tree.Declare(Line).Write("tests/features/infra/x.feature", "Feature: x\n").Write("backlog/infra/X.md", "x\n").Commit("c2");
    Assert.That(this.Run("check", "--base", "c1").ExitCode, Is.Zero);
  }

  [Test]
  public void ChangeTogetherWithCompilerCode_IsRefused() {
    this._tree.History(changeCompiler: true).Seal(GoldenTree.Adler32);
    var commit = this._tree.Declare(Line).Commit("c2");
    var (exitCode, _, error) = this.Run("check", "--base", "c1");
    Assert.That((exitCode, error), Is.EqualTo((1,
      $"polypiler: error PLY0027: golden file '{GoldenTree.Adler32}' was modified: changed together with '{GoldenTree.CompilerFile}' in commit {commit[..12]}{Environment.NewLine}")));
  }

  [Test]
  public void ChangeWithoutItsLine_IsRefused() {
    this._tree.History(changeCompiler: false).Seal(GoldenTree.Adler32);
    this._tree.Commit("c2");
    Assert.That(this.Run("check", "--base", "c1").Error, Is.EqualTo($"polypiler: error PLY0027: golden file '{GoldenTree.Adler32}' was modified: no entry in {GoldenLock.ChangeLogPath}{Environment.NewLine}"));
  }

  [Test]
  public void LineThatNamesAnotherSource_IsRefused() {
    this._tree.History(changeCompiler: false).Seal(GoldenTree.Adler32);
    this._tree.Declare(Line.Replace("(source: python)", "(source: guess)", StringComparison.Ordinal)).Declare("not an entry").Commit("c2");
    Assert.That(this.Run("check", "--base", "c1").Error,
      Is.EqualTo($"polypiler: error PLY0027: golden file '{GoldenTree.Adler32}' was modified: the entry in {GoldenLock.ChangeLogPath} does not name the source 'python'{Environment.NewLine}"));
  }

  [Test]
  public void LineThatWasAlreadyThere_DoesNotCount() {
    this._tree.History(changeCompiler: false).Seal(GoldenTree.Adler32);
    this._tree.Commit("c2");
    var line = $"- {GoldenTree.Adler32}: add the Adler-32 vectors (source: python)";
    Assert.That(this._tree.Read(GoldenLock.ChangeLogPath), Does.Contain(line));
    Assert.That(this.Run("check", "--base", "c1").Error, Does.Contain("no entry in"));
  }

  [Test]
  public void FirstCommit_AgainstARootBase_NeedsNoEarlierLog_AndDeletedFilesNeedAnyLine() {
    this._tree.Init().Write("README.md", "r\n").Commit("c0");
    this._tree.History(changeCompiler: false);
    Assert.That(this.Run("check", "--base", "c0").ExitCode, Is.EqualTo(1), "c1 changes the compiler file and the vector file together");
    this._tree.Git("-C", this._tree.Root, "reset", "--quiet", "--hard", "c1");
    File.Delete(this._tree.Resolve(GoldenTree.Adler32));
    this._tree.Seal(GoldenTree.Adler32).Declare($"- {GoldenTree.Adler32}: superseded (source: none)").Commit("c2");
    Assert.That(this.Run("check", "--base", "c1"), Is.EqualTo((0, $"0 golden files verified, 1 commits since c1 checked{Environment.NewLine}", "")));
  }

  [Test]
  public void UnbornHead_IsAUsageError() {
    this._tree.History(changeCompiler: false);
    this._tree.Git("-C", this._tree.Root, "checkout", "--quiet", "--orphan", "empty");
    var (exitCode, _, error) = this.Run("check", "--base", "c1");
    Assert.That(exitCode, Is.EqualTo(2));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0003: git rev-list --reverse c1..HEAD failed: "));
  }

  [Test]
  public void Program_RoutesTheVerb() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["integrity"], output, error), Is.EqualTo(Program.UsageError));
    Assert.That(error.ToString(), Is.EqualTo(Usage("integrity needs 'check' or 'seal'")));
  }

}
