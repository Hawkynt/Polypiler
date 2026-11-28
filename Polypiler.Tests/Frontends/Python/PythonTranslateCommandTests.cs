using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>The command line with <c>--from python</c>: the version the source is parsed for, and CPython's errors as exit code 4.</summary>
[TestFixture]
public sealed class PythonTranslateCommandTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._directory = Directory.CreateTempSubdirectory("polypiler-python-cli-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private (int ExitCode, string Output, string Error) Translate(string source, params string[] options) {
    var path = Path.Combine(this._directory, "m.py");
    File.WriteAllText(path, source);
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", path, "--from", "python", "--to", "csharp", "--output", Path.Combine(this._directory, "out"), .. options], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private const string Match = "def f(x: int) -> int:\n    match x:\n        case 1:\n            return 2\n        case _:\n            return 0\n";

  [Test]
  public void TheDefaultVersion_Is314() => Assert.That(this.Translate(Match).ExitCode, Is.EqualTo((int)ExitCode.Success));

  [TestCase("3.10")]
  [TestCase("3.14")]
  public void AVersionWithTheConstruct_Translates(string version) => Assert.That(this.Translate(Match, "--python-version", version).ExitCode, Is.EqualTo((int)ExitCode.Success));

  [Test]
  public void AVersionBeforeTheConstruct_IsExitCode4WithCPythonsMessage() {
    var (exitCode, _, error) = this.Translate(Match, "--python-version", "3.9");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.InvalidSource));
    Assert.That(error, Does.Contain("error PLY1800: SyntaxError: Pattern matching is only supported in Python 3.10 and greater"));
  }

  [Test]
  public void ACompileTimeError_IsExitCode4() {
    var (exitCode, _, error) = this.Translate("def f() -> int:\n    return 1\n\nreturn 2\n");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.InvalidSource));
    Assert.That(error, Does.Contain("m.py(4,1): error PLY1800: SyntaxError: 'return' outside function"));
  }

  [TestCase("3.7")]
  [TestCase("3.15")]
  [TestCase("2.7")]
  [TestCase("3.x")]
  [TestCase("3")]
  public void AnUnknownVersion_IsAMisuse(string version) {
    var (exitCode, _, error) = this.Translate(Match, "--python-version", version);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error, Does.Contain($"unknown Python version '{version}'; the versions are 3.8 to 3.14"));
  }

}
