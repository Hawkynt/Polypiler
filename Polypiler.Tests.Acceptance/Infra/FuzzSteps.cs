using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Verification.Fuzzing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-fuzz.feature</c>. Each run starts the real
/// <c>polypiler-tooling fuzz run</c> in the scenario's workspace, which holds the target's corpus
/// under <c>tests/fixtures/fuzz/</c> as a checkout does; the targets run in supervised worker
/// processes. The demo targets are compiled into this assembly, which the run names with
/// <c>--assembly</c>; <c>cli-args</c> comes from the built CLI assembly.
/// </summary>
[Binding, Scope(Feature = "Fuzzing harness with corpus mutation, budgets and minimization")]
public sealed partial class FuzzSteps(ScenarioWorkspace workspace, CommandLog log) {

  private string _target = "";
  private string _assembly = typeof(DemoMarkerTarget).Assembly.Location;
  private string[] _arguments = [];

  [GeneratedRegex(@"error PLY0070: fuzz target '(?<target>[^']+)': (?<kind>\w+) '(?<signature>[^']*)' at iteration (?<iteration>\d+) \(seed (?<seed>\d+)\); minimized to (?<length>\d+) bytes: (?<path>\S+)")]
  private static partial Regex FindingLine();

  [GeneratedRegex(@"^fuzz target '[^']+': (?<executed>\d+) iterations, (?<findings>\d+) findings, (?<starts>\d+) worker starts, ")]
  private static partial Regex ReportLine();

  private sealed record Reported(string Kind, long Iteration, string Path);

  private static List<Reported> Findings(CommandResult result)
    => [.. FindingLine().Matches(result.Stderr).Select(match => new Reported(match.Groups["kind"].Value, long.Parse(match.Groups["iteration"].Value, CultureInfo.InvariantCulture), match.Groups["path"].Value))];

  private Match Report() {
    var match = ReportLine().Match(log.Last.Stdout);
    Assert.That(match.Success, Is.True, log.Last.Output);
    return match;
  }

  private static byte[] Hex(string bytes) => Convert.FromHexString(bytes.Replace(" ", "", StringComparison.Ordinal));

  /// <summary>Writes the target's one corpus entry into the workspace.</summary>
  [Given("^the fuzz target " + StepText.Quoted + " with the corpus entry " + StepText.Quoted + @" containing the (?:bytes|byte) ([0-9A-F]{2}(?: [0-9A-F]{2})*)$")]
  public void TargetWithEntry(string target, string entry, string bytes) {
    this._target = target;
    var path = workspace.Resolve($"tests/fixtures/fuzz/{target}/corpus/{entry}");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, Hex(bytes));
  }

  /// <summary>Copies the repository's corpus of <c>cli-args</c> into the workspace and names the CLI assembly.</summary>
  [Given("^the fuzz target " + StepText.Quoted + @" from the built CLI assembly with its corpus of (\d+) entries$")]
  public void TargetFromTheCli(string target, int entries) {
    this._target = target;
    this._assembly = typeof(Polypiler.Cli.Fuzzing.CliArgsFuzzTarget).Assembly.Location;
    var source = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "fuzz", target, "corpus");
    var files = Directory.GetFiles(source, "*.bin");
    Assert.That(files, Has.Length.EqualTo(entries));
    foreach (var file in files)
      workspace.Copy(file, $"tests/fixtures/fuzz/{target}/corpus/{Path.GetFileName(file)}");
  }

  /// <summary>Runs the target with the default budget.</summary>
  [When(@"^the in-process engine runs (\d+) iterations? with seed (\d+)$")]
  public void EngineRuns(long iterations, ulong seed) {
    this._arguments = ["fuzz", "run", "--target", this._target, "--iterations", iterations.ToString(CultureInfo.InvariantCulture),
      "--seed", seed.ToString(CultureInfo.InvariantCulture), "--assembly", this._assembly];
    log.Add(Toolchain.Tooling(workspace.Path, this._arguments));
  }

  /// <summary>Runs the target with a time budget, which is the default one the run uses.</summary>
  [When(@"^the in-process engine runs (\d+) iterations? with seed (\d+) and a per-input budget of (\d+) seconds?$")]
  public void EngineRunsWithTime(long iterations, ulong seed, int seconds) {
    Assert.That(FuzzBudget.Default.PerInput, Is.EqualTo(TimeSpan.FromSeconds(seconds)));
    this.EngineRuns(iterations, seed);
  }

  /// <summary>Runs the target with an allocation budget, which is the default one the run uses.</summary>
  [When(@"^the in-process engine runs (\d+) iterations? with seed (\d+) and an allocation budget of (\d+) MiB$")]
  public void EngineRunsWithAllocation(long iterations, ulong seed, long mebibytes) {
    Assert.That(FuzzBudget.Default.MaxAllocatedBytes, Is.EqualTo(mebibytes * 1024 * 1024));
    this.EngineRuns(iterations, seed);
  }

  /// <summary>Runs a command line of the tooling as it is.</summary>
  [When("^" + StepText.Quoted + " is invoked$")]
  public void Invoked(string line) => log.Add(Toolchain.Tooling(workspace.Path, [.. CommandLine.Split(line)]));

  /// <summary>Checks the one finding: its kind, iteration and diagnostic, and the exit code of a finding.</summary>
  [Then(@"^one finding of kind (\w+) is reported at iteration (\d+) with diagnostic (PLY\d{4})$")]
  public void OneFindingWithDiagnostic(string kind, long iteration, string code) {
    Assert.That(code, Is.EqualTo("PLY0070"));
    this.FindingAt(kind, iteration);
    Assert.That(Findings(log.Last), Has.Count.EqualTo(1));
  }

  /// <summary>Checks that a finding of a kind was reported at an iteration, and that the run failed for it.</summary>
  [Then(@"^a finding of kind (\w+) is reported at iteration (\d+)$")]
  public void FindingAt(string kind, long iteration) {
    Assert.That(log.Last.ExitCode, Is.EqualTo(1), log.Last.Output);
    Assert.That(Findings(log.Last).Select(finding => (finding.Kind, finding.Iteration)), Does.Contain((kind, iteration)), log.Last.Output);
  }

  /// <summary>Checks that exactly one finding was reported, of a kind.</summary>
  [Then(@"^exactly one finding of kind (\w+) is reported$")]
  public void ExactlyOneFinding(string kind) {
    Assert.That(Findings(log.Last).Select(finding => finding.Kind), Is.EqualTo(new[] { kind }), log.Last.Output);
    Assert.That(this.Report().Groups["findings"].Value, Is.EqualTo("1"));
  }

  /// <summary>Checks the bytes of the minimized input the first finding names.</summary>
  [Then(@"^the minimized input is the (?:\d+ bytes|single byte) ([0-9A-F]{2}(?: [0-9A-F]{2})*)$")]
  public void MinimizedInput(string bytes)
    => Assert.That(File.ReadAllBytes(workspace.Resolve(Findings(log.Last)[0].Path)), Is.EqualTo(Hex(bytes)));

  /// <summary>Checks that the record of the finding exists beside its input.</summary>
  [Then("^the finding record is " + StepText.Quoted + "$")]
  public void FindingRecord(string path) {
    Assert.That(File.Exists(workspace.Resolve(path)), Is.True, path);
    Assert.That(Path.ChangeExtension(Findings(log.Last)[0].Path, ".json"), Is.EqualTo(path));
  }

  /// <summary>Checks a field of the record against the SHA-256 of a corpus entry.</summary>
  [Then("^the record's " + StepText.Quoted + " is the SHA-256 of " + StepText.Quoted + "$")]
  public void RecordFieldIsTheHash(string field, string entry) {
    using var json = JsonDocument.Parse(File.ReadAllText(workspace.Resolve(Path.ChangeExtension(Findings(log.Last)[0].Path, ".json"))));
    var bytes = File.ReadAllBytes(workspace.Resolve($"tests/fixtures/fuzz/{this._target}/corpus/{entry}"));
    Assert.That(json.RootElement.GetProperty(field).GetString(), Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(bytes))));
  }

  /// <summary>Runs the same command line again and compares the iteration of the finding.</summary>
  [Then(@"^a second run with seed (\d+) reports the same iteration number$")]
  public void SecondRun(ulong seed) {
    Assert.That(this._arguments[Array.IndexOf(this._arguments, "--seed") + 1], Is.EqualTo(seed.ToString(CultureInfo.InvariantCulture)));
    var first = Findings(log.Last).Single();
    var second = log.Add(Toolchain.Tooling(workspace.Path, this._arguments));
    Assert.That(Findings(second).Single(), Is.EqualTo(first));
  }

  /// <summary>Checks that every iteration ran and that the worker was started again after the hang.</summary>
  [Then(@"^iterations (\d+) and (\d+) were executed by a restarted worker$")]
  public void RestartedWorker(int first, int second) {
    var report = this.Report();
    Assert.That(long.Parse(report.Groups["executed"].Value, CultureInfo.InvariantCulture), Is.EqualTo(Math.Max(first, second) + 1));
    Assert.That(int.Parse(report.Groups["starts"].Value, CultureInfo.InvariantCulture), Is.GreaterThanOrEqualTo(2));
  }

  /// <summary>Checks a clean run.</summary>
  [Then(@"^no finding is reported and the tooling exit code is (\d+)$")]
  public void NoFinding(int exitCode) {
    Assert.That(log.Last.ExitCode, Is.EqualTo(exitCode), log.Last.Output);
    Assert.That(Findings(log.Last), Is.Empty);
    Assert.That(this.Report().Groups["findings"].Value, Is.EqualTo("0"));
  }

  /// <summary>Checks the count of executed iterations in the report.</summary>
  [Then(@"^the report counts (\d+) executed iterations$")]
  public void ReportCounts(long executed)
    => Assert.That(long.Parse(this.Report().Groups["executed"].Value, CultureInfo.InvariantCulture), Is.EqualTo(executed));

  /// <summary>Checks the diagnostic of a refused command line and the exit code.</summary>
  [Then(@"^diagnostic (PLY\d{4}) is reported and the tooling exit code is (\d+)$")]
  public void DiagnosticReported(string code, int exitCode) {
    Assert.That(log.Last.ExitCode, Is.EqualTo(exitCode), log.Last.Output);
    Assert.That(log.Last.Stderr, Does.StartWith($"polypiler-tooling: error {code}: "));
  }

}
