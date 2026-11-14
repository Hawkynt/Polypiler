using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-toolchains.feature</c>. The command lines run the real
/// <c>polypiler-tooling</c> executable in the scenario's workspace, which is first on <c>PATH</c>; the
/// workspace holds the repository's <c>tooling/toolchains.lock</c>, as a checkout does, until a step
/// writes a lock of its own there. The fake tool of the fixtures is copied in as <c>fake-tool</c>.
/// </summary>
[Binding, Scope(Feature = "Pinned toolchains")]
public sealed class ToolchainsSteps(ScenarioWorkspace workspace, CommandLog log) {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly string _fixtures = Path.Combine(_root, "tests", "fixtures", "infra", "toolchains");

  private string _tool = "";
  private string? _require;
  private RequiresToolchainAttribute? _attribute;
  private Exception? _outcome;

  /// <summary>Puts the repository's lock where <c>polypiler-tooling</c> looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopyRepositoryLock() => workspace.Copy(Path.Combine(_root, ToolchainLock.RepositoryPath), ToolchainLock.RepositoryPath);

  /// <summary>Copies the fake tool in and writes a lock of one entry that runs it.</summary>
  [Given("^a lock entry " + StepText.Quoted + " with version " + StepText.Quoted + ", probe " + StepText.Quoted + " and versionPattern \"(.*)\"$")]
  public void FakeToolEntry(string key, string version, string probe, string versionPattern) {
    this._tool = workspace.Copy(Path.Combine(_fixtures, OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool.sh"), OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool");
    if (!OperatingSystem.IsWindows())
      File.SetUnixFileMode(this._tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    this.WriteLock(key, version, probe, versionPattern);
  }

  /// <summary>Writes a lock of one entry whose version command names a tool that is not installed.</summary>
  [Given("^a lock entry " + StepText.Quoted + " with probe " + StepText.Quoted + "$")]
  public void AbsentToolEntry(string key, string probe) => this.WriteLock(key, "1.0.0", probe, @"^(?<version>\S+)$");

  /// <summary>Copies a malformed lock of the fixtures into the workspace under its own name.</summary>
  [Given("^the lock fixture " + StepText.Quoted + "$")]
  public void LockFixture(string fixture) => workspace.Copy(Path.Combine(_fixtures, fixture), fixture);

  /// <summary>Checks the lock against the toolchain keys the backlog's README lists.</summary>
  [Then(@"^the lock contains every one of the (\d+) toolchain keys named in " + StepText.Quoted + "$")]
  public void LockContainsTheKeys(int count, string readme) {
    var keys = BacklogKeys(readme);
    var toolchains = ToolchainLock.Load(Path.Combine(_root, ToolchainLock.RepositoryPath));
    Assert.That(keys, Has.Length.EqualTo(count));
    Assert.That(keys.Where(key => !toolchains.TryGet(key, out _)), Is.Empty);
  }

  /// <summary>Checks that the backlog's keys include the named ones.</summary>
  [Then("^the keys include (.+)$")]
  public void KeysInclude(string list) {
    var named = Regex.Matches(list, StepText.Quoted).Select(match => match.Groups[1].Value).ToArray();
    Assert.That(named, Has.Length.EqualTo(6));
    Assert.That(BacklogKeys("backlog/README.md"), Is.SupersetOf(named));
  }

  /// <summary>Checks standard output: the text of the step and the path the tool was found at.</summary>
  [Then("^the output is " + StepText.Quoted + " followed by the tool's path$")]
  public void OutputIsFollowedByThePath(string text)
    => Assert.That(log.Last.Stdout.TrimEnd(), Is.EqualTo($"{text} path={this._tool}"), log.Last.Stderr);

  /// <summary>Keeps the value the test would see in the environment, without changing the process's.</summary>
  [Given("^the environment variable POLYPILER_REQUIRE_TOOLCHAINS is " + StepText.Quoted + "$")]
  public void RequireVariable(string value) => this._require = value;

  /// <summary>Takes the attribute of a test that needs a toolchain, probed against the fixture lock of that key.</summary>
  [Given(@"^a test marked RequiresToolchain\(" + StepText.Quoted + @"\)$")]
  public void TestMarked(string key) => this._attribute = new RequiresToolchainAttribute(key) { Lock = $"tests/fixtures/infra/toolchains/{key}.lock" };

  /// <summary>Runs the attribute's check as NUnit runs it before the test.</summary>
  [When("^the test runs$")]
  public void TestRuns() => this._outcome = Assert.Catch(() => this._attribute!.Check(this._require));

  /// <summary>Checks the outcome the check gave the test and its message.</summary>
  [Then(@"^its outcome is (Failed|Inconclusive) with message containing " + StepText.Quoted + "$")]
  public void OutcomeIs(string outcome, string text) {
    Assert.That(this._outcome, outcome == "Failed" ? Is.InstanceOf<AssertionException>() : Is.InstanceOf<InconclusiveException>());
    Assert.That(this._outcome!.Message, Does.Contain(text));
  }

  private void WriteLock(string key, string version, string probe, string versionPattern) {
    var argv = string.Join(", ", CommandLine.Split(probe).Select(Yaml));
    workspace.Write(ToolchainLock.RepositoryPath, $"""
      schema: 1
      toolchains:
        {key}:
          version: "{version}"
          probe: [{argv}]
          versionPattern: {Yaml(versionPattern)}
          platforms: [linux, windows, macos]
          licence: MIT
          source: https://example.org/{key}

      """);
  }

  /// <summary>A single-quoted YAML scalar.</summary>
  private static string Yaml(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

  /// <summary>The toolchain keys listed under "Keys in use" in the backlog's README.</summary>
  private static string[] BacklogKeys(string readme) {
    var text = File.ReadAllText(Path.Combine(_root, readme));
    return [.. Regex.Matches(Regex.Match(text, @"Keys in use:(.*?)An issue that needs another", RegexOptions.Singleline).Groups[1].Value, "`([a-z0-9-]+)`")
      .Select(match => match.Groups[1].Value)];
  }

}
