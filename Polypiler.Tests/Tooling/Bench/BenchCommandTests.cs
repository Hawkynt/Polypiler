using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Tooling.Toolchains;
using Polypiler.Tests.Verification.Benchmarks;
using Polypiler.Tooling;
using Polypiler.Tooling.Bench;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Verification.Benchmarks.BenchTesting;

namespace Polypiler.Tests.Tooling.Bench;

/// <summary><c>polypiler-tooling bench run</c> and <c>bench compare</c> on a scratch repository with a scripted runner.</summary>
[TestFixture]
public sealed class BenchCommandTests {

  private const string Expected = "499999500000\n";

  private sealed class FixedResolver : IToolchainResolver {

    public ToolchainResolution Resolve(string toolchain) => ToolchainResolution.Found("/usr/bin/" + toolchain);

  }

  private ScratchFolder _repository = null!;
  private ScriptedBenchRunner _runner = null!;
  private IToolchainResolver? _seenResolver;

  [SetUp]
  public void CreateRepository() {
    this._repository = new ScratchFolder();
    this._repository.File("tooling/toolchains.lock");
    File.Copy(System.IO.Path.Combine(FixtureFolder, "sum-loop.js"), this._repository.File("tests/fixtures/bench/sum-loop.js"), true);
    File.Copy(System.IO.Path.Combine(FixtureFolder, "sum-loop.bench.yaml"), this._repository.File("tests/benchmarks/sum-loop.bench.yaml"), true);
    this._runner = new ScriptedBenchRunner(Completed(Expected, 10));
  }

  [TearDown]
  public void DeleteRepository() => this._repository.Dispose();

  private string Path(string relative) => System.IO.Path.Combine(this._repository.Path, relative);

  private (int ExitCode, string Output, string Error) RunIn(string currentDirectory, params string[] args) {
    var command = new BenchCommand(currentDirectory,
      _ => new BenchToolchains(new FixedResolver(), key => key == "node" ? "24.22.2" : null),
      resolver => {
        this._seenResolver = resolver;
        return this._runner;
      },
      _ => "abc123",
      toolchains => Machine(node: toolchains["node"]));
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = command.Run(args, output, error);
    return (exitCode, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
  }

  private (int ExitCode, string Output, string Error) Run(params string[] args) => this.RunIn(this._repository.Path, args);

  /// <summary>Scenario "A program is measured and its numbers are recorded, not judged".</summary>
  [Test]
  public void Run_WritesTheResultAndTheSummary() {
    var (exitCode, output, error) = this.Run("run", "--id", "sum-loop");
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output, Is.EqualTo("bench 'sum-loop/default': 5 samples, wall median 10000000 ns, MAD 0 ns: artifacts/bench/sum-loop/default.json\n"));
    var result = BenchmarkResult.Load(this.Path("artifacts/bench/sum-loop/default.json"));
    Assert.That((result.Samples.Count, result.Commit, result.Environment.Toolchains["node"]), Is.EqualTo((5, "abc123", "24.22.2")));
    Assert.That(File.ReadAllText(this.Path("artifacts/bench/summary.md")), Does.Contain("| sum-loop | default | 5 |").And.Contain("| record only |"));
    Assert.That(this._seenResolver, Is.InstanceOf<FixedResolver>());
    Assert.That(this._runner.Plans, Has.Count.EqualTo(6));
  }

  [Test]
  public void Run_FromASubdirectory_FindsTheRoot_AndWritesWhereOutSays() {
    var below = this._repository.Folder("tests/deep");
    var (exitCode, output, _) = this.RunIn(below, "run", "--all", "--out", "results");
    Assert.That(exitCode, Is.Zero);
    Assert.That(File.Exists(System.IO.Path.Combine(below, "results", "sum-loop", "default.json")), Is.True, output);
    Assert.That(output, Does.EndWith(": results/sum-loop/default.json\n"));
  }

  [Test]
  public void Run_OutsideTheCurrentDirectory_NamesTheResultInFull() {
    using var elsewhere = new ScratchFolder();
    var (_, output, _) = this.Run("run", "--id", "sum-loop", "--out", elsewhere.Path);
    Assert.That(output, Does.EndWith($": {elsewhere.Path.Replace('\\', '/')}/sum-loop/default.json\n"));
  }

  [Test]
  public void Run_All_MeasuresEveryDefinition_AndReportsTheInvalidOnes() {
    File.WriteAllText(this.Path("tests/benchmarks/broken.bench.yaml"), "id: broken\n");
    File.WriteAllText(this.Path("tests/benchmarks/misnamed.bench.yaml"), File.ReadAllText(this.Path("tests/benchmarks/sum-loop.bench.yaml")));
    var (exitCode, output, error) = this.Run("run", "--all");
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(output, Does.Contain("bench 'sum-loop/default'"));
    Assert.That(error, Is.EqualTo(
      "polypiler-tooling: tests/benchmarks/broken.bench.yaml(1,1): error PLY0078: missing property 'kind'\n" +
      "polypiler-tooling: tests/benchmarks/misnamed.bench.yaml: error PLY0078: 'id' is 'sum-loop', but the file is named 'misnamed.bench.yaml'\n"));
  }

  [Test]
  public void Run_WithoutALockAbove_TakesTheCurrentDirectoryAsTheRoot() {
    using var bare = new ScratchFolder();
    var (exitCode, _, _) = this.RunIn(bare.Path, "run", "--all");
    Assert.That(exitCode, Is.Zero);
    Assert.That(File.Exists(System.IO.Path.Combine(bare.Path, "artifacts", "bench", "summary.md")), Is.True);
  }

  [Test]
  public void Run_All_WithoutDefinitions_WritesAnEmptySummary() {
    Directory.Delete(this.Path("tests/benchmarks"), true);
    var (exitCode, _, _) = this.Run("run", "--all");
    Assert.That(exitCode, Is.Zero);
    Assert.That(File.ReadAllText(this.Path("artifacts/bench/summary.md")).Split('\n'), Has.Length.EqualTo(3));
  }

  /// <summary>Scenario "A run that prints the wrong result is not a measurement"; a stale result is removed.</summary>
  [Test]
  public void Run_AWrongOutput_WritesNoResult_AndExitsWith5() {
    this.Run("run", "--id", "sum-loop");
    this._runner = new ScriptedBenchRunner(Completed("0\n", 10));
    var (exitCode, _, error) = this.Run("run", "--id", "sum-loop");
    Assert.That(exitCode, Is.EqualTo(5));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0051: benchmark 'sum-loop/default': the program printed '0'"));
    Assert.That(File.Exists(this.Path("artifacts/bench/sum-loop/default.json")), Is.False);
  }

  [Test]
  public void Run_AFailingProgram_PrintsTheCause() {
    this._runner = new ScriptedBenchRunner(Completed("", 10, exitCode: 2));
    var (exitCode, _, error) = this.Run("run", "--id", "sum-loop");
    Assert.That((exitCode, error), Is.EqualTo((1, "polypiler-tooling: error PLY0079: benchmark 'sum-loop/default': the program exited with 2; nothing is recorded\n  cause: it broke\n")));
  }

  /// <summary>Test design 20: an unknown ID is a usage error.</summary>
  [TestCase("nope")]
  [TestCase("../sum-loop")]
  public void Run_AnUnknownId_IsAUsageError(string id) {
    var (exitCode, _, error) = this.Run("run", "--id", id);
    Assert.That((exitCode, error), Is.EqualTo((2, $"polypiler-tooling: error PLY0003: unknown benchmark '{id}': there is no tests/benchmarks/{id}.bench.yaml\n")));
  }

  [TestCase(new string[0], "bench needs 'run' or 'compare'")]
  [TestCase(new[] { "walk" }, "unknown bench command 'walk'")]
  [TestCase(new[] { "run" }, "bench run needs either '--id <id>' or '--all'")]
  [TestCase(new[] { "run", "--id", "a", "--all" }, "bench run needs either '--id <id>' or '--all'")]
  [TestCase(new[] { "run", "--all", "--all" }, "option '--all' is given more than once")]
  [TestCase(new[] { "run", "--fast" }, "unknown option '--fast'")]
  [TestCase(new[] { "run", "--id" }, "option '--id' requires a value")]
  [TestCase(new[] { "compare", "--current", "." }, "bench compare needs '--baseline <dir>'")]
  [TestCase(new[] { "compare", "--baseline", "nowhere", "--current", "." }, "option '--baseline': directory 'nowhere' does not exist")]
  public void AnInvalidCommandLine_IsAUsageError(string[] args, string message) {
    var (exitCode, _, error) = this.Run(args);
    Assert.That((exitCode, error), Is.EqualTo((2, $"polypiler-tooling: error PLY0003: {message}\n")));
  }

  [Test]
  public void Program_DispatchesTheVerb_AndListsItsUsage() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run([BenchCommand.Verb], output, error), Is.EqualTo(Program.UsageError));
    Assert.That(Program.Run([], output, error), Is.EqualTo(Program.Success));
    Assert.That(output.ToString(), Does.Contain("bench run (--id <id> | --all)").And.Contain("bench compare --baseline <dir> --current <dir>"));
  }

  private static string Results(string name) => System.IO.Path.Combine(FixtureFolder, "results", name);

  /// <summary>Scenario outline "A budget set by an ADR gates a regression, equality passes" on the fixture results.</summary>
  [Test]
  public void Compare_WithTheTestBudget_PassesOnTheLimit_AndWritesTheTable() {
    var (exitCode, output, error) = this.Run("compare", "--baseline", Results("baseline"), "--current", Results("current"), "--budgets", System.IO.Path.Combine(FixtureFolder, "budgets-test.yaml"), "--out", "out/compare.md");
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output, Does.Contain("| sum-loop | default | wallNs | 100000000 | 110000000 | +10 % | within budget |"));
    Assert.That(File.ReadAllText(this.Path("out/compare.md")), Is.EqualTo(output));
  }

  [Test]
  public void Compare_ARegression_IsPly0073() {
    File.WriteAllText(this.Path("budgets.yaml"), "schemaVersion: 1\nbudgets:\n  - { id: sum-loop, variant: default, metric: wallNs, maxRegressionPercent: 9.99 }\n");
    var (exitCode, _, error) = this.Run("compare", "--baseline", Results("baseline"), "--current", Results("current"), "--budgets", "budgets.yaml");
    Assert.That((exitCode, error), Is.EqualTo((1, "polypiler-tooling: error PLY0073: benchmark 'sum-loop/default': wallNs regressed by 10 % (baseline 100000000, current 110000000), budget 9.99 % from budgets.yaml\n")));
  }

  /// <summary>Scenario "Results from different environments are not compared".</summary>
  [Test]
  public void Compare_DifferentEnvironments_IsPly0072() {
    var (exitCode, output, error) = this.Run("compare", "--baseline", Results("baseline"), "--current", Results("cores-4"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Is.EqualTo("polypiler-tooling: error PLY0072: benchmark 'sum-loop/default': environment differs from the baseline (logicalCores: '8' vs '4')\n"));
    Assert.That(output, Does.Contain("| sum-loop | default |  |  |  |  | not compared: environment differs (logicalCores) |"));
  }

  [Test]
  public void Compare_AnInvalidBudgetFile_IsPly0078() {
    File.WriteAllText(this.Path("budgets.yaml"), "schemaVersion: 1\nbudgets:\n  - { id: a, variant: b, metric: c, maxRegressionPercent: -1 }\n");
    var (exitCode, output, error) = this.Run("compare", "--baseline", Results("baseline"), "--current", Results("current"), "--budgets", "budgets.yaml");
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Is.EqualTo("polypiler-tooling: budgets.yaml(3,59): error PLY0078: 'maxRegressionPercent' must not be negative, not -1\n"));
  }

  [Test]
  public void ALockThatCannotBeRead_IsReported() {
    var command = new BenchCommand(this._repository.Path, BenchCommand.LockToolchains, _ => this._runner, _ => "x", toolchains => Machine());
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(command.Run(["run", "--id", "sum-loop"], output, error), Is.EqualTo(1));
    Assert.That(error.ToString(), Does.Contain("PLY0020"));
  }

  [Test]
  public void LockToolchains_ProbeTheVersionOnce_AndKnowNoVersionOfAnUnknownKey() {
    var toolchains = BenchCommand.LockToolchains(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot);
    Assert.That(toolchains.Version("no-such-toolchain"), Is.Null);
    Assert.That(toolchains.Version("dotnet-sdk"), Does.Match(@"^\d+\.\d+\.\d+$"), "a known key is probed");
    Assert.That(toolchains.Resolver.Resolve("no-such-toolchain").Problem!.Code, Is.EqualTo("PLY0020"));
  }

  [Test]
  public void Commit_IsWhatGitPrints_OrUnknown() {
    Assert.That(BenchCommand.Commit(new ScriptedRunner("abc\n"), "/repo"), Is.EqualTo("abc"));
    Assert.That(BenchCommand.Commit(new ScriptedRunner(_ => new(128, "", "fatal", false)), "/repo"), Is.EqualTo("unknown"));
    Assert.That(BenchCommand.Commit(new ScriptedRunner(_ => new(0, "  \n", "", false)), "/repo"), Is.EqualTo("unknown"));
    Assert.That(BenchCommand.Commit(new ScriptedRunner(_ => throw new System.ComponentModel.Win32Exception(2)), "/repo"), Is.EqualTo("unknown"));
    Assert.That(BenchCommand.GitCommit(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot), Is.Not.Empty);
  }

  [Test]
  public void Configuration_IsTheBuildConfiguration_OrRelease() {
    Assert.That(BenchCommand.Configuration(typeof(BenchCommand).Assembly), Is.AnyOf("Debug", "Release"));
    Assert.That(BenchCommand.Configuration(typeof(object).Assembly), Is.Not.Empty);
    var bare = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("bare"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
    Assert.That(BenchCommand.Configuration(bare), Is.EqualTo("Release"));
  }

  [Test]
  public void TheDefaultCommand_WorksInTheCurrentDirectory() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(new BenchCommand().Run(["compare", "--baseline", Results("baseline"), "--current", Results("baseline")], output, error), Is.Zero);
  }

}
