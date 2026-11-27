using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Binds the end-to-end Batch import scenarios.</summary>
[Binding, Scope(Feature = "Lift Windows Batch integer kernels")]
public sealed class BatchFrontendSteps {

  private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
  private string _source = "";
  private string _errors = "";
  private int _status;

  [Given("^the pinned Batch countdown fixture$")]
  public void Countdown() => this._source = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.bat"));

  [Given("^the pinned Batch delayed expansion fixture$")]
  public void DelayedExpansion() => this.ReadPinnedFixture("delayed-expansion.bat",
    "2b433c0dc416ae28cefd21c4e98a093b7317f48db084f45d7bc2196279ebd283");

  [Given("^the pinned Batch delayed expansion zero-trip fixture$")]
  public void ZeroTripDelayedExpansion() => this.ReadPinnedFixture("delayed-expansion-zero.bat",
    "e5323b70882da8cca631275a33e1ec5e9e42025f5f24abc8e68e1321b9a41462");

  private void ReadPinnedFixture(string name, string sha256) {
    var bytes = File.ReadAllBytes(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", name));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(sha256));
    this._source = System.Text.Encoding.ASCII.GetString(bytes);
  }

  [Given("^a Batch loop with an unbound delayed variable$")]
  public void UnboundDelayedExpansion()
    => this._source = """
      @echo off
      setlocal EnableExtensions EnableDelayedExpansion
      set /a "total=7"
      for /l %%I in (1,1,1) do (
        set /a "total=total+!neverbound!"
      )
      exit /b %total%
      """;

  [Given("^a Batch kernel containing \"([^\"]*)\"$")]
  public void WithCommand(string command)
    => this._source = "setlocal EnableExtensions DisableDelayedExpansion\r\n" + command + "\r\nexit /b 0\r\n";

  [When("^it is translated under the cmd profile$")]
  public void Translate() {
    var source = Path.Combine(this._directory.FullName, "kernel.bat");
    File.WriteAllText(source, this._source);
    using var errors = new StringWriter();
    this._status = Program.Run(["translate", source, "--from", "batch", "--to", "csharp", "--opt", "none",
      "--output", Path.Combine(this._directory.FullName, "out")], TextWriter.Null, errors);
    this._errors = errors.ToString();
  }

  [Then("^the native and translated results are both ([0-9]+)$")]
  public void SameStatus(int expected) => this.CompareStatus(expected, emptyNativeOutput: false);

  [Then("^the native and translated results are both ([0-9]+) with no native output$")]
  public void SameStatusWithNoOutput(int expected) => this.CompareStatus(expected, emptyNativeOutput: true);

  private void CompareStatus(int expected, bool emptyNativeOutput) {
    Assert.That(this._status, Is.Zero, this._errors);
    var output = Directory.EnumerateFiles(Path.Combine(this._directory.FullName, "out"), "*.cs").Single();
    var csharp = GeneratedCSharp.Compile(File.ReadAllText(output), checkOverflow: true);
    Assert.That(csharp.Method("Run").Invoke(null, null), Is.EqualTo(expected));
    new RequiresToolchainAttribute("cmd").Check(Environment.GetEnvironmentVariable("POLYPILER_REQUIRE_TOOLCHAINS"));
    Assert.That(BatchFrontendNativeTests.NativeStatus(this._source, emptyNativeOutput), Is.EqualTo(expected));
  }

  [Then("^it reports (PLY[0-9]+) and writes no output file$")]
  public void Refusal(string code) {
    Assert.That(this._status, Is.EqualTo(TranslateCommand.UnsupportedConstruct));
    Assert.That(this._errors, Does.Contain(code));
    Assert.That(Directory.Exists(Path.Combine(this._directory.FullName, "out")), Is.False);
  }

  [AfterScenario]
  public void CleanUp() => this._directory.Delete(recursive: true);

}
