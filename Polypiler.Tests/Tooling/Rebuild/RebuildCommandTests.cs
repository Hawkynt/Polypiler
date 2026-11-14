using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Rebuild;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Tooling.Toolchains;
using Polypiler.Tests.Verification.Rebuild;
using Polypiler.Tooling;
using Polypiler.Tooling.Rebuild;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Verification.Rebuild.TreeHashTests;

namespace Polypiler.Tests.Tooling.Rebuild;

/// <summary><c>polypiler-tooling rebuild check</c> and <c>rebuild compare</c> on a scratch repository with a scripted runner.</summary>
[TestFixture]
public sealed class RebuildCommandTests {

  private const string Usage = "polypiler-tooling: error PLY0003: ";

  private sealed class FixedResolver : IToolchainResolver {

    public ToolchainResolution Resolve(string toolchain) => ToolchainResolution.Found("/usr/bin/" + toolchain);

  }

  private ScratchFolder _repository = null!;
  private ScriptedRebuildRunner _runner = null!;
  private string? _root;

  [SetUp]
  public void CreateRepository() {
    this._repository = new ScratchFolder();
    this._repository.File(ToolchainLock.RepositoryPath);
    this._runner = Stable(2);
  }

  [TearDown]
  public void DeleteRepository() => this._repository.Dispose();

  private static ScriptedRebuildRunner Stable(int runs)
    => new([.. Enumerable.Repeat(ScriptedRebuildRunner.Wrote(("out/a.txt", "hello\n"), ("out/b/c.txt", "")), runs)]);

  private (int ExitCode, string Output, string Error) RunIn(string currentDirectory, params string[] args) {
    var command = new RebuildCommand(currentDirectory, root => {
      this._root = root;
      return new FixedResolver();
    }, _ => this._runner);
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = command.Run(args, output, error);
    return (exitCode, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
  }

  private (int ExitCode, string Output, string Error) Run(params string[] args) => this.RunIn(this._repository.Path, args);

  private static string[] Check(params string[] options) => ["check", .. options, "--", "dotnet-sdk", "gen.dll", "stable", "{output}"];

  /// <summary>Scenario "A deterministic generator passes and its tree hash is stable".</summary>
  [Test]
  public void Check_PrintsTheHashOfEveryRunAndTheTreeHash() {
    var (exitCode, output, error) = this.Run(Check("--output", "out"));
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output, Is.EqualTo($"run 1: {StableHash}\nrun 2: {StableHash}\nrebuild deterministic over 2 runs: {StableHash}\n"));
    Assert.That(this._root, Is.EqualTo(this._repository.Path));
    Assert.That(this._runner.Plans[0].Execute.Arguments, Is.EqualTo(new[] { "gen.dll", "stable", "out" }));
    Assert.That(this._runner.Plans[0].Limits, Is.EqualTo(RunLimits.Default));
  }

  [Test]
  public void Check_FromASubdirectory_FindsTheRootAndReadsInputsFromTheCurrentDirectory() {
    var below = this._repository.Folder("deep/er");
    File.WriteAllText(Path.Combine(below, "gen.dll"), "g");
    var tool = this._repository.Folder("deep/er/tool");
    File.WriteAllText(Path.Combine(tool, "x.dll"), "x");
    File.WriteAllText(this._repository.File("deep/er/tool/sub/y.json"), "y");
    var (exitCode, _, error) = this.RunIn(below, Check("--output", "out", "--input", "gen.dll", "--input", "tool" + Path.DirectorySeparatorChar));
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(this._root, Is.EqualTo(this._repository.Path));
    Assert.That(this._runner.Plans[0].Files.Keys.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "gen.dll", "tool/sub/y.json", "tool/x.dll" }));
  }

  [Test]
  public void Check_OutsideARepository_UsesTheCurrentDirectory() {
    using var elsewhere = new ScratchFolder();
    this.RunIn(elsewhere.Path, Check("--output", "out"));
    Assert.That(this._root, Is.EqualTo(elsewhere.Path));
  }

  /// <summary>Test design 10 at the boundary: <c>--runs 2</c> and more are accepted.</summary>
  [TestCase("2", 2)]
  [TestCase("3", 3)]
  public void Check_RunsAsOftenAsAsked(string runs, int expected) {
    this._runner = Stable(expected);
    var (exitCode, output, _) = this.Run(Check("--output", "out", "--runs", runs));
    Assert.That((exitCode, this._runner.Plans.Count), Is.EqualTo((0, expected)));
    Assert.That(output, Does.Contain($"over {expected} runs"));
  }

  [Test]
  public void Check_TakesTheLimitsFromTheOptions() {
    this.Run(Check("--output", "out", "--wall-time", "7", "--memory", "3"));
    var limits = this._runner.Plans[0].Limits;
    Assert.That((limits.WallTime, limits.MemoryBytes, limits.OutputBytes, limits.Processes), Is.EqualTo((TimeSpan.FromSeconds(7), 3L << 20, RunLimits.Default.OutputBytes, RunLimits.Default.Processes)));
  }

  /// <summary>Test designs 10 and the usage errors: every one is PLY0003 with exit code 2, before anything runs.</summary>
  [TestCase(new[] { "check", "--output", "out", "--runs", "1", "--", "t" }, "option '--runs' needs a whole number of at least 2, not '1'")]
  [TestCase(new[] { "check", "--output", "out", "--runs", "0", "--", "t" }, "option '--runs' needs a whole number of at least 2, not '0'")]
  [TestCase(new[] { "check", "--output", "out", "--runs", "two", "--", "t" }, "option '--runs' needs a whole number of at least 2, not 'two'")]
  [TestCase(new[] { "check", "--output", "out", "--runs", "-3", "--", "t" }, "option '--runs' needs a whole number of at least 2, not '-3'")]
  [TestCase(new[] { "check", "--output", "out", "--wall-time", "0", "--", "t" }, "option '--wall-time' needs a whole number of at least 1, not '0'")]
  [TestCase(new[] { "check", "--output", "out", "--memory", "x", "--", "t" }, "option '--memory' needs a whole number of at least 1, not 'x'")]
  [TestCase(new[] { "check", "--output", "out" }, "rebuild check needs '-- <toolchain key> <args>' after its options")]
  [TestCase(new[] { "check", "--output", "out", "--" }, "rebuild check needs '-- <toolchain key> <args>' after its options")]
  [TestCase(new[] { "check", "--", "t" }, "rebuild check needs '--output <dir>'")]
  [TestCase(new[] { "check", "--output", "/abs", "--", "t" }, "option '--output': '/abs' must be a relative directory without '..'")]
  [TestCase(new[] { "check", "--output", "../up", "--", "t" }, "option '--output': '../up' must be a relative directory without '..'")]
  [TestCase(new[] { "check", "--output", "a", "--output", "b", "--", "t" }, "option '--output' is given more than once")]
  [TestCase(new[] { "check", "--output", "--", "t" }, "option '--output' requires a value")]
  [TestCase(new[] { "check", "--output", "out", "--runs", "--", "t" }, "option '--runs' requires a value")]
  [TestCase(new[] { "check", "--verbose", "--output", "out", "--", "t" }, "unknown option '--verbose'")]
  [TestCase(new[] { "check", "--output", "out", "--input", "missing", "--", "t" }, "option '--input': 'missing' does not exist")]
  [TestCase(new[] { "compare", "a" }, "rebuild compare needs two directories")]
  [TestCase(new[] { "compare", "a", "b" }, "directory 'a' does not exist")]
  [TestCase(new[] { "build" }, "unknown rebuild command 'build'")]
  [TestCase(new string[0], "rebuild needs 'check' or 'compare'")]
  public void AnInvalidCommandLine_IsAUsageError(string[] args, string message) {
    var (exitCode, output, error) = this.Run(args);
    Assert.That((exitCode, output, error), Is.EqualTo((Program.UsageError, "", Usage + message + "\n")));
    Assert.That(this._runner.Plans, Is.Empty);
  }

  /// <summary>The option without a value after <c>--output</c> takes <c>--</c> as its value: the separator is then missing.</summary>
  [Test]
  public void TheSeparator_IsFoundBeforeTheOptionsAreRead() {
    var (exitCode, _, error) = this.Run("check", "--output", "out", "--runs", "2", "--", "t", "--output");
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(this._runner.Plans[0].Execute.Arguments, Is.EqualTo(new[] { "--output" }));
  }

  /// <summary>Scenario "A timestamp in the output is detected": one PLY0036 per difference, exit code 1.</summary>
  [Test]
  public void Check_ReportsEveryDifference() {
    this._runner = new(ScriptedRebuildRunner.Wrote(("out/a.txt", "hello\n"), ("out/stamp.txt", "1")), ScriptedRebuildRunner.Wrote(("out/a.txt", "hello\n"), ("out/stamp.txt", "2")));
    var (exitCode, output, error) = this.Run(Check("--output", "out"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(output.Split('\n').Where(line => line.Length > 0).Select(line => line[..7]), Is.EqualTo(new[] { "run 1: ", "run 2: " }));
    Assert.That(error, Is.EqualTo("polypiler-tooling: error PLY0036: rebuild not deterministic: 'stamp.txt' differs at byte 0\n"));
  }

  /// <summary>Test designs 16 and 17: a runner diagnostic passes through with its own exit code.</summary>
  [Test]
  public void Check_PassesARunnerDiagnosticThrough() {
    this._runner = new(ScriptedRebuildRunner.Ended(RunOutcome.TimedOut, -1, diagnostics: Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 1000L)));
    var (exitCode, output, error) = this.Run(Check("--output", "out"));
    Assert.That((exitCode, output, error), Is.EqualTo((10, "", "polypiler-tooling: error PLY0031: time limit of 1000 ms exceeded\n")));
    this._runner = new(ScriptedRebuildRunner.Ended(RunOutcome.Completed, 1, stderr: "nope"));
    (exitCode, _, error) = this.Run(Check("--output", "out"));
    Assert.That((exitCode, error), Is.EqualTo((6, "polypiler-tooling: error PLY0030: target build failed: 'dotnet-sdk gen.dll stable out' exited with 1\n  cause: nope\n")));
  }

  /// <summary>The repository's ignore list is applied.</summary>
  [Test]
  public void Check_AppliesTheIgnoreList() {
    File.WriteAllText(this._repository.File(RebuildIgnore.RepositoryPath), "schema: 1\nfiles:\n  - glob: stamp.txt\n    reason: tick count\n");
    this._runner = new(ScriptedRebuildRunner.Wrote(("out/stamp.txt", "1")), ScriptedRebuildRunner.Wrote(("out/stamp.txt", "2")));
    Assert.That(this.Run(Check("--output", "out")).ExitCode, Is.Zero);
  }

  /// <summary>Test design 18 and the scenario "An ignore entry without a reason is refused": PLY0026, exit code 1, nothing run.</summary>
  [Test]
  public void AnInvalidIgnoreList_IsPly0026() {
    File.WriteAllText(this._repository.File(RebuildIgnore.RepositoryPath), "schema: 1\nfiles:\n  - glob: \"*.log\"\n");
    var (exitCode, _, error) = this.Run(Check("--output", "out"));
    Assert.That((exitCode, error), Is.EqualTo((1, "polypiler-tooling: tooling/ci/rebuild-ignore.yaml(3,1): error PLY0026: corpus manifest invalid: rebuild ignore: the file glob '*.log' has no 'reason'\n")));
    Assert.That(this._runner.Plans, Is.Empty);
    File.WriteAllText(this._repository.File(RebuildIgnore.RepositoryPath), "");
    Assert.That(this.Run("compare", ".", ".").ExitCode, Is.EqualTo(1));
  }

  [Test]
  public void AnInvalidToolchainLock_IsAFailure() {
    var command = new RebuildCommand(this._repository.Path, _ => throw new ToolchainLockException([new("tooling/toolchains.lock", 1, 1, ToolchainDiagnostic.LockInvalid, "bad lock")]), _ => this._runner);
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(command.Run(Check("--output", "out"), output, error), Is.EqualTo(Program.Failure));
    Assert.That(error.ToString(), Does.Contain("PLY0020").And.Contain("bad lock"));
  }

  /// <summary><c>rebuild compare</c> on two trees on disk: equal, and different in each kind.</summary>
  [Test]
  public void Compare_ComparesTwoDirectories() {
    foreach (var run in new[] { "one", "two" }) {
      File.WriteAllText(this._repository.File($"{run}/a.txt"), "hello\n");
      this._repository.File($"{run}/b/c.txt");
    }

    var (exitCode, output, error) = this.Run("compare", "one", Path.Combine(this._repository.Path, "two"));
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output, Does.EndWith($"rebuild deterministic over 2 runs: {StableHash}\n"));
    File.WriteAllText(this._repository.File("one/only.txt"), "");
    File.WriteAllText(this._repository.File("two/a.txt"), "hellO\n");
    File.WriteAllText(this._repository.File("two/new.txt"), "");
    (exitCode, _, error) = this.Run("compare", "one", "two");
    Assert.That((exitCode, error), Is.EqualTo((1, string.Concat(
      "polypiler-tooling: error PLY0036: rebuild not deterministic: 'a.txt' differs at byte 4\n",
      "polypiler-tooling: error PLY0036: rebuild not deterministic: 'new.txt' exists only in run 2\n",
      "polypiler-tooling: error PLY0036: rebuild not deterministic: 'only.txt' exists only in run 1\n"))));
  }

  [Test]
  public void TheVerb_IsDispatchedByTheProgram() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run([RebuildCommand.Verb], output, error), Is.EqualTo(Program.UsageError));
    Assert.That(error.ToString(), Does.Contain("rebuild needs 'check' or 'compare'"));
    Assert.That(() => new RebuildCommand().Run(null!, output, error), Throws.ArgumentNullException);
  }

}
