using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Tooling;

namespace Polypiler.Tests;

/// <summary>Checks the entry point of the <c>polypiler-tooling</c> command and its dispatch to the verbs.</summary>
[TestFixture]
public sealed class ToolingTests {

  private static (int ExitCode, string Output, string Error) Invoke(params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(args, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void NoArguments_PrintsUsageAndSucceeds() {
    var (exitCode, output, error) = Invoke();
    Assert.That(exitCode, Is.EqualTo(Program.Success));
    Assert.That(output, Does.StartWith("usage: polypiler-tooling "));
    Assert.That(error, Is.Empty);
  }

  [TestCase("corpus", TestName = "A verb that is not registered yet")]
  [TestCase("--help", TestName = "An option is not a verb")]
  [TestCase("", TestName = "An empty argument is not a verb")]
  public void AnyVerb_FailsWithUsageError(string verb) {
    var (exitCode, output, error) = Invoke(verb, "ignored");
    Assert.That(exitCode, Is.EqualTo(Program.UsageError));
    Assert.That(output, Is.Empty);
    Assert.That(error.Trim(), Is.EqualTo($"polypiler-tooling: unknown verb '{verb}'."));
  }

  // Console.Out belongs to the whole process: a parallel test that swapped it as well would take this
  // test's output, or restore the original under it.
  [Test, NonParallelizable]
  public void Main_WritesToTheConsole() {
    var originalOut = Console.Out;
    using var output = new StringWriter();
    try {
      Console.SetOut(output);
      Assert.That(Program.Main([]), Is.EqualTo(Program.Success));
    } finally {
      Console.SetOut(originalOut);
    }

    Assert.That(output.ToString(), Does.Contain("toolchains validate"));
  }

  [Test]
  public void AssemblyName_IsTheCommandName()
    => Assert.That(typeof(Program).Assembly.GetName().Name, Is.EqualTo(Program.Name));

}
