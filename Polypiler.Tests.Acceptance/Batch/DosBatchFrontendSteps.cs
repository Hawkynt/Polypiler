using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Compares DOS input with the independently booted COMMAND.COM and generated C#.</summary>
[Binding, Scope(Feature = "Lift MS-DOS 6.22 scalar kernels")]
public sealed class DosBatchFrontendSteps {

  private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory();
  private string _source = "";
  private string _errors = "";
  private string _trace = "";
  private int _status;

  private string Output => Path.Combine(this._work.FullName, "out");

  [Given("the pinned DOS fixture whose GOTO operand shares an eight-character prefix with two labels")]
  public void EarlierLabel() => this._source = DosCommandOracle.Fixture("lift.bat");

  [Given("^a DOS kernel containing SET /A$")]
  public void WindowsArithmetic() {
    this._source = Path.Combine(this._work.FullName, "kernel.bat");
    File.WriteAllText(this._source, "@ECHO OFF\r\nSET /A X=1\r\nECHO PLYRESULT=%X%\r\n");
  }

  [When("it runs on MS-DOS 6.22 and is separately lifted under dos622")]
  public void NativeAndLifted() {
    var guest = DosCommandOracleTests.Guest();
    var result = guest.Run(DosCommandOracle.Fixture("LIFTDRV.BAT"), [new(this._source, "LIFT.BAT")], TimeSpan.FromSeconds(20));
    Assert.That(result.TimedOut, Is.False);
    this._trace = result.Trace;
    TestContext.Out.Write(result.Trace);
    this.Lift();
  }

  [When("it is lifted under dos622")]
  public void Lift() {
    using var errors = new StringWriter();
    this._status = Program.Run(["translate", this._source, "--from", "batch", "--batch-dialect", "dos622", "--to", "csharp",
      "--opt", "none", "--output", this.Output], TextWriter.Null, errors);
    this._errors = errors.ToString();
  }

  [Then("^the two results match the selected label and return ([0-9]+)$")]
  public void CheckResult(int expected) {
    Assert.That(this._status, Is.Zero, this._errors);
    var source = File.ReadAllText(Directory.EnumerateFiles(this.Output, "*.cs").Single());
    var result = GeneratedCSharp.Compile(source, checkOverflow: true).Method("Run").Invoke(null, null);
    Assert.That(result, Is.EqualTo(expected));
    var imported = BatchFrontend.Import(this._source, File.ReadAllText(this._source), BatchDialect.Dos622);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", [], 1000), Is.EqualTo(expected));
    Assert.That(this._trace, Is.EqualTo($"PLYRESULT={expected:X8}\r\nPLY_DONE\r\n"));
  }

  [Then("a source diagnostic identifies the unsupported command and no output is written")]
  public void Refused() {
    Assert.That(this._status, Is.EqualTo(TranslateCommand.UnsupportedConstruct));
    Assert.That(this._errors, Does.Contain("(2,1): error PLY2802").And.Contain("SET /A"));
    Assert.That(Directory.Exists(this.Output), Is.False);
  }

  [AfterScenario]
  public void CleanUp() => this._work.Delete(recursive: true);

}
