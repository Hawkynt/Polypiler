using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests;

[TestFixture]
public sealed class CliTests {

  private static (int ExitCode, string Output, string Error) Invoke(params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(args, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [TestCase("--help")]
  [TestCase("-h")]
  [TestCase("help")]
  public void Help_PrintsProductNameAndSucceeds(string option) {
    var (exitCode, output, _) = Invoke(option);
    Assert.That(exitCode, Is.EqualTo(Program.Success));
    Assert.That(output, Does.StartWith(ProductInfo.Name + " "));
  }

  [Test]
  public void NoArguments_BehavesLikeHelp() {
    Assert.That(Invoke().Output, Is.EqualTo(Invoke("--help").Output));
  }

  [Test]
  public void Version_PrintsTheCoreVersion() {
    var (exitCode, output, _) = Invoke("--version");
    Assert.That(exitCode, Is.EqualTo(Program.Success));
    Assert.That(output.Trim(), Is.EqualTo(ProductInfo.Version));
  }

  [Test]
  public void UnknownCommand_FailsWithUsageError() {
    var (exitCode, output, error) = Invoke("translate");
    Assert.That(exitCode, Is.EqualTo(Program.UsageError));
    Assert.That(output, Is.Empty);
    Assert.That(error, Does.Contain("unknown command 'translate'"));
  }

  [TestCase("--HELP", TestName = "Options are case-sensitive")]
  [TestCase("", TestName = "An empty argument is not help")]
  public void NearMissOptions_AreUnknownCommands(string argument)
    => Assert.That(Invoke(argument).ExitCode, Is.EqualTo(Program.UsageError));

  [Test]
  public void Main_WritesToTheConsole() {
    var originalOut = Console.Out;
    using var output = new StringWriter();
    try {
      Console.SetOut(output);
      Assert.That(Program.Main(["--version"]), Is.EqualTo(Program.Success));
    } finally {
      Console.SetOut(originalOut);
    }

    Assert.That(output.ToString().Trim(), Is.EqualTo(ProductInfo.Version));
  }

}
