using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests.Cli;

/// <summary><c>polypiler</c> with its real commands: help, version and the refusal of an unknown command.</summary>
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
    var (exitCode, output, error) = Invoke(option);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(output, Does.StartWith(ProductInfo.Name + " "));
    Assert.That(error, Is.Empty);
  }

  [Test]
  public void NoArguments_BehavesLikeHelp() {
    Assert.That(Invoke().Output, Is.EqualTo(Invoke("--help").Output));
  }

  [Test]
  public void RootHelp_ListsTheRegisteredCommands()
    => Assert.That(Invoke("--help").Output, Does.Contain(Environment.NewLine + "  translate  "));

  [Test]
  public void Version_PrintsTheCoreVersion() {
    var (exitCode, output, _) = Invoke("--version");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(output.Trim(), Is.EqualTo(ProductInfo.Version));
  }

  [Test]
  public void UnknownCommand_FailsWithUsageError() {
    var (exitCode, output, error) = Invoke("recover");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(output, Is.Empty);
    Assert.That(error, Is.EqualTo("polypiler: error PLY0002: unknown command 'recover'; run 'polypiler --help'" + Environment.NewLine));
  }

  [TestCase("--HELP", "PLY0003: unknown option '--HELP'", TestName = "Options are case-sensitive")]
  [TestCase("", "PLY0002: unknown command ''", TestName = "An empty argument is not help")]
  public void NearMissOptions_AreRefused(string argument, string message) {
    var (exitCode, _, error) = Invoke(argument);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(error, Does.Contain(message));
  }

  [Test]
  public void CreateRegistry_ListsTranslate()
    => Assert.That(Program.CreateRegistry().Find("translate"), Is.InstanceOf<TranslateCommand>());

  // Console.Out belongs to the whole process: a parallel test that swapped it as well would take this
  // test's output, or restore the original under it.
  [Test, NonParallelizable]
  public void Main_WritesToTheConsole() {
    var originalOut = Console.Out;
    using var output = new StringWriter();
    try {
      Console.SetOut(output);
      Assert.That(Program.Main(["--version"]), Is.EqualTo((int)ExitCode.Success));
    } finally {
      Console.SetOut(originalOut);
    }

    Assert.That(output.ToString().Trim(), Is.EqualTo(ProductInfo.Version));
  }

}
