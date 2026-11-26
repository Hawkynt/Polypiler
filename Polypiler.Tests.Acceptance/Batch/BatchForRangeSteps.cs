using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Batch;

[Binding, Scope(Feature = "Batch FOR L integer ranges")]
public sealed class BatchForRangeSteps {

  private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory();
  private string _prefix = "@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\n";
  private string _source = "";
  private string _errors = "";
  private int _status;
  private string Output => Path.Combine(this._work.FullName, "out");

  [Given("a scalar Batch kernel initializes total to 0")]
  public void Initialize() => this._prefix += "set /a \"total=0\"\r\n";

  [Given("""^(?:the kernel runs|a scalar Batch kernel runs) (FOR /L %%I in \([^)]+\) DO SET /A "total=total\+%%I")$""")]
  public void Command(string command) => this._source = this._prefix + command + "\r\nexit /b %total%\r\n";

  [When("it is lifted in the cmd profile")]
  public void Lift() {
    var input = Path.Combine(this._work.FullName, "kernel.bat");
    File.WriteAllText(input, this._source);
    using var error = new StringWriter();
    this._status = Program.Run(["translate", input, "--from", "batch", "--batch-dialect", "cmd", "--to", "csharp",
      "--opt", "none", "--output", this.Output], TextWriter.Null, error);
    this._errors = error.ToString();
  }

  [Then("^native cmd.exe and the translated kernel both return ([0-9]+)$")]
  public void Matches(int expected) {
    Assert.That(this._status, Is.Zero, this._errors);
    var module = BatchFrontend.Import("kernel.bat", this._source);
    Assert.That(module.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(module.Module!, "Run", [], 1000), Is.EqualTo(expected));
    var source = File.ReadAllText(Directory.EnumerateFiles(this.Output, "*.cs").Single());
    Assert.That(GeneratedCSharp.Compile(source, checkOverflow: true).Method("Run").Invoke(null, null), Is.EqualTo(expected));
    new RequiresToolchainAttribute("cmd").Check(Environment.GetEnvironmentVariable("POLYPILER_REQUIRE_TOOLCHAINS"));
    Assert.That(BatchFrontendNativeTests.NativeStatus(this._source), Is.EqualTo(expected));
  }

  [Then("it reports PLY2802 and emits no output")]
  public void Refused() {
    Assert.That(this._status, Is.EqualTo(TranslateCommand.UnsupportedConstruct), this._errors);
    Assert.That(this._errors, Does.Contain("PLY2802").And.Contain("zero"));
    Assert.That(Directory.Exists(this.Output), Is.False);
  }

  [AfterScenario]
  public void CleanUp() => this._work.Delete(recursive: true);

}
