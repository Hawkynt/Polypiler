using System;
using System.IO;
using System.Linq;
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
  public void SameStatus(int expected) {
    Assert.That(this._status, Is.Zero, this._errors);
    var output = Directory.EnumerateFiles(Path.Combine(this._directory.FullName, "out"), "*.cs").Single();
    var csharp = GeneratedCSharp.Compile(File.ReadAllText(output), checkOverflow: true);
    Assert.That(csharp.Method("Run").Invoke(null, null), Is.EqualTo(expected));
    new RequiresToolchainAttribute("cmd").Check(Environment.GetEnvironmentVariable("POLYPILER_REQUIRE_TOOLCHAINS"));
    Assert.That(BatchFrontendNativeTests.NativeStatus(this._source), Is.EqualTo(expected));
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
