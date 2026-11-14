using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Tooling.Toolchains.ToolchainTesting;

namespace Polypiler.Tests.Tooling.Toolchains;

/// <summary><c>polypiler-tooling toolchains validate|probe</c>: the command line, the output formats, the diagnostics and the exit codes.</summary>
[TestFixture]
public sealed class ToolchainsCommandTests {

  private ScratchFolder _folder = null!;
  private Dictionary<string, string> _environment = null!;

  [SetUp]
  public void CreateFolder() {
    this._folder = new();
    this._environment = new() { ["PATH"] = Path.Combine(this._folder.Path, "path") };
  }

  [TearDown]
  public void DeleteFolder() => this._folder.Dispose();

  private (int ExitCode, string Output, string Error) Invoke(IProcessRunner runner, string currentDirectory, params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var command = new ToolchainsCommand(runner, name => this._environment.GetValueOrDefault(name), "linux", currentDirectory, TimeSpan.FromSeconds(30));
    var exitCode = command.Run(args, output, error);
    return (exitCode, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
  }

  private (int ExitCode, string Output, string Error) Invoke(params string[] args) => this.Invoke(new ScriptedRunner("fake 1.2.3"), this._folder.Path, args);

  private string WriteLock(params string[] entries) {
    var path = Path.Combine(this._folder.Path, "test.lock");
    File.WriteAllText(path, Lock(entries));
    return path;
  }

  [Test]
  public void Validate_RepositoryLock_Succeeds() {
    var (exitCode, output, error) = this.Invoke("validate", "--lock", RepositoryLock);
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output, Does.Match(@"^.*toolchains\.lock: \d+ toolchains, valid\n$"));
  }

  [Test]
  public void Validate_WithoutLock_FindsTheRepositoryLockAboveTheCurrentDirectory() {
    var (exitCode, output, _) = this.Invoke(new ScriptedRunner(""), Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures"), "validate");
    Assert.That(exitCode, Is.EqualTo(0));
    Assert.That(output, Does.StartWith(RepositoryLock + ": "));
  }

  [Test]
  public void Validate_WithoutLockOutsideARepository_IsAnEnvironmentError() {
    var directory = this._folder.Folder("outside");
    var (exitCode, output, error) = this.Invoke(new ScriptedRunner(""), directory, "validate");
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Does.StartWith($"{Path.Combine(directory, "tooling", "toolchains.lock")}: error PLY0004: "));
  }

  [TestCase("no-version.lock", "missing property 'version' in 'node'")]
  [TestCase("extra-property.lock", "unknown property 'channel' in 'node'")]
  [TestCase("no-group.lock", "versionPattern of 'node' has no group 'version'")]
  [TestCase("unsorted.lock", "keys are not in ordinal order: 'gcc' after 'node'")]
  [TestCase("schema-2.lock", "unsupported schema 2")]
  [TestCase("duplicate.lock", "duplicate key 'node'")]
  [TestCase("invalid-key.lock", "invalid key 'Node'")]
  [TestCase("unknown-host.lock", "unknown toolchain key 'python'")]
  [TestCase("not-yaml.lock", "not a YAML mapping")]
  [TestCase("core-missing-z3.lock", "missing core toolchain 'z3'")]
  [TestCase("one.lock", "missing core toolchain 'z3'")]
  public void Validate_MalformedLock_FailsWithItsReason(string fixture, string reason) {
    var (exitCode, output, error) = this.Invoke("validate", "--lock", Fixture(fixture));
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Does.Contain("error PLY0020: " + reason));
  }

  [TestCase("core-only.lock", TestName = "A lock with exactly the core toolchains validates")]
  [TestCase("core-plus-one.lock", TestName = "A lock with an optional toolchain more validates")]
  public void Validate_CompleteLock_Succeeds(string fixture)
    => Assert.That(this.Invoke("validate", "--lock", Fixture(fixture)).ExitCode, Is.EqualTo(0));

  [Test]
  public void Probe_MatchingVersion_PrintsOkAndSucceeds() {
    var tool = this._folder.File(Path.Combine("path", "fake"));
    var (exitCode, output, error) = this.Invoke("probe", "--lock", this.WriteLock(Entry("fake")), "--only", "fake");
    Assert.That((exitCode, output, error), Is.EqualTo((0, $"fake Ok expected=1.2.3 actual=1.2.3 path={tool}\n", "")));
  }

  [Test]
  public void Probe_MismatchAndMissing_ReportTheirDiagnosticsAtTheEntry() {
    var tool = this._folder.File(Path.Combine("path", "fake"));
    var path = this.WriteLock(Entry("fake", "1.2.4"), Entry("ghost", probe: "[no-such-binary-polypiler, --version]"));
    var (exitCode, output, error) = this.Invoke("probe", "--lock", path, "--only", "ghost,fake");
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(output, Is.EqualTo($"fake Mismatch expected=1.2.4 actual=1.2.3 path={tool}\nghost Missing expected=1.2.3 actual=- path=-\n"));
    Assert.That(error, Is.EqualTo(
      $"{path}(3,3): error PLY0022: toolchain 'fake' is 1.2.3, the lock pins 1.2.4\n" +
      $"{path}(10,3): error PLY0021: toolchain 'ghost' is not installed (probe: no-such-binary-polypiler --version): 'no-such-binary-polypiler' was not found on PATH\n"));
  }

  [Test]
  public void Probe_Json_PrintsOneObjectPerToolchain() {
    var tool = this._folder.File(Path.Combine("path", "fake"));
    var path = this.WriteLock(Entry("fake"), Entry("ghost", probe: "[no-such-binary-polypiler]"));
    var (exitCode, output, _) = this.Invoke("probe", "--lock", path, "--only", "fake,ghost", "--format", "json");
    Assert.That(exitCode, Is.EqualTo(1));
    var lines = output.TrimEnd('\n').Split('\n').Select(line => JsonDocument.Parse(line).RootElement).ToArray();
    Assert.That(lines.Select(line => (line.GetProperty("key").GetString(), line.GetProperty("status").GetString())), Is.EqualTo(new[] { ("fake", "Ok"), ("ghost", "Missing") }));
    Assert.That(lines[0].GetProperty("path").GetString(), Is.EqualTo(tool));
    Assert.That(lines[0].GetProperty("cause").ValueKind, Is.EqualTo(JsonValueKind.Null));
    Assert.That((lines[1].GetProperty("actual").ValueKind, lines[1].GetProperty("expected").GetString()), Is.EqualTo((JsonValueKind.Null, "1.2.3")));
    Assert.That(lines[1].GetProperty("cause").GetString(), Is.EqualTo("'no-such-binary-polypiler' was not found on PATH"));
  }

  [Test]
  public void Probe_Text_IsTheDefaultFormat() {
    this._folder.File(Path.Combine("path", "fake"));
    var path = this.WriteLock(Entry("fake"));
    Assert.That(this.Invoke("probe", "--lock", path, "--only", "fake", "--format", "text").Output, Is.EqualTo(this.Invoke("probe", "--lock", path, "--only", "fake").Output));
  }

  [Test]
  public void Probe_WithoutOnly_ProbesTheCoreToolchainsInKeyOrder() {
    foreach (var key in ToolchainLock.CoreKeys.Append("xz-utils"))
      this._folder.File(Path.Combine("path", key));
    var runner = new ScriptedRunner("1.0.0");
    var (exitCode, output, _) = this.Invoke(runner, this._folder.Path, "probe", "--lock", Fixture("core-plus-one.lock"));
    var lines = output.TrimEnd('\n').Split('\n');
    Assert.That(exitCode, Is.EqualTo(0));
    Assert.That(lines.Select(line => line.Split(' ')[0]), Is.EqualTo(ToolchainLock.CoreKeys));
    Assert.That(lines, Is.All.Contains(" Ok expected=1.0.0 actual=1.0.0 "));
  }

  [Test]
  public void Probe_OnlyOptionalKey_ProbesIt() {
    this._folder.File(Path.Combine("path", "xz-utils"));
    var (exitCode, output, _) = this.Invoke(new ScriptedRunner("1.0.0"), this._folder.Path, "probe", "--lock", Fixture("core-plus-one.lock"), "--only", "xz-utils,xz-utils");
    Assert.That((exitCode, output.Split(' ')[0], output.Count(character => character == '\n')), Is.EqualTo((0, "xz-utils", 1)));
  }

  [Test]
  public void Probe_ZeroToolchains_PrintsNothingAndSucceeds()
    => Assert.That(this.Invoke("probe", "--lock", Fixture("empty.lock")), Is.EqualTo((0, "", "")));

  [Test]
  public void Probe_NotOnThisPlatform_Succeeds() {
    var (exitCode, output, error) = this.Invoke("probe", "--lock", this.WriteLock(Entry("fake", platforms: "[windows]")), "--only", "fake");
    Assert.That((exitCode, output, error), Is.EqualTo((0, "fake NotOnThisPlatform expected=1.2.3 actual=- path=-\n", "")));
  }

  [Test]
  public void Probe_UnknownKey_IsALockError() {
    var path = this.WriteLock(Entry("fake"));
    var (exitCode, output, error) = this.Invoke("probe", "--lock", path, "--only", "fake,nope");
    Assert.That((exitCode, output, error), Is.EqualTo((1, "", $"{path}: error PLY0020: unknown toolchain key 'nope'\n")));
  }

  [Test]
  public void Probe_InvalidLock_FailsBeforeProbing() {
    var runner = new ScriptedRunner("");
    var (exitCode, _, error) = this.Invoke(runner, this._folder.Path, "probe", "--lock", Fixture("schema-2.lock"));
    Assert.That((exitCode, runner.Calls.Count), Is.EqualTo((1, 0)));
    Assert.That(error, Does.Contain("error PLY0020: unsupported schema 2"));
  }

  [TestCase(new string[0], "toolchains needs 'validate' or 'probe'", TestName = "toolchains needs a command")]
  [TestCase(new[] { "check" }, "unknown toolchains command 'check'", TestName = "An unknown toolchains command is a usage error")]
  [TestCase(new[] { "probe", "--only", "" }, "option '--only' has an empty list element", TestName = "An empty --only is a usage error")]
  [TestCase(new[] { "probe", "--only", "node," }, "option '--only' has an empty list element", TestName = "An empty key in --only is a usage error")]
  [TestCase(new[] { "probe", "--format", "xml" }, "option '--format' does not allow 'xml'", TestName = "An unknown format is a usage error")]
  [TestCase(new[] { "probe", "--lock" }, "option '--lock' requires a value", TestName = "An option without its value is a usage error")]
  [TestCase(new[] { "probe", "--lock", "a", "--lock", "b" }, "option '--lock' is given more than once", TestName = "An option given twice is a usage error")]
  [TestCase(new[] { "probe", "--verbose" }, "unknown option '--verbose'", TestName = "An unknown option is a usage error")]
  [TestCase(new[] { "validate", "--only", "node" }, "unknown option '--only'", TestName = "validate does not take --only")]
  [TestCase(new[] { "validate", "--format", "json" }, "unknown option '--format'", TestName = "validate does not take --format")]
  public void UsageError_ExitsWith2(string[] args, string message) {
    var runner = new ScriptedRunner("");
    var (exitCode, output, error) = this.Invoke(runner, this._folder.Path, args);
    Assert.That((exitCode, output, error), Is.EqualTo((2, "", $"polypiler-tooling: error PLY0003: {message}\n")));
    Assert.That(runner.Calls, Is.Empty);
  }

  [Test]
  public void Program_DispatchesTheVerb() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["toolchains", "probe", "--lock", Fixture("empty.lock")], output, error), Is.EqualTo(0));
    Assert.That(Program.Run(["toolchains", "validate", "--lock", RepositoryLock], output, error), Is.EqualTo(0));
    Assert.That(error.ToString(), Is.Empty);
    Assert.That(output.ToString(), Does.Contain("toolchains, valid"));
  }

}
