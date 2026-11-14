using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Cli.Support;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The exit codes as the operating system sees them (issue #23, test design 14): every command line
/// of <c>cli-exitcodes.feature</c>, which runs in process, gives the same exit code and streams when
/// the test host <see cref="RaiseHost"/> runs it as a child process; and the shipped executable exits
/// with the code of each category its own commands reach, as it does in process.
/// </summary>
[TestFixture, Category("EndToEnd")]
public sealed class ExitCodeAgreementTests {

  private static readonly Dictionary<string, string> _noVariables = [];

  /// <summary>Every row of the test design that runs a command line: each exit code, the tally, both formats, the refusals, the exceptions.</summary>
  [TestCase(0, "raise")]
  [TestCase(1, "raise", "PLY0004")]
  [TestCase(2, "raise", "PLY0003")]
  [TestCase(3, "raise", "PLY0014")]
  [TestCase(4, "raise", "PLY0013")]
  [TestCase(5, "raise", "PLY0051")]
  [TestCase(6, "raise", "PLY0030")]
  [TestCase(7, "raise", "PLY0050")]
  [TestCase(8, "raise", "PLY0024")]
  [TestCase(9, "raise", "PLY0081")]
  [TestCase(10, "raise", "PLY0031")]
  [TestCase(70, "raise", "PLY0001")]
  [TestCase(0, "raise", "PLY0009")]
  [TestCase(0, "raise", "PLY0098")]
  [TestCase(6, "raise", "PLY0009", "PLY0030", "PLY0051")]
  [TestCase(70, "raise", "PLY0030", "PLY0001")]
  [TestCase(6, "--diagnostics", "json", "raise", "PLY0009", "PLY0030")]
  [TestCase(6, "--quiet", "raise", "PLY0009", "PLY0030")]
  [TestCase(2, "frobnicate")]
  [TestCase(2, "raise", "--nope")]
  [TestCase(2, "echo", "input.txt", "--left", "--right")]
  [TestCase(70, "raise", "--throw", "InvalidOperationException:boom")]
  [TestCase(70, "--diagnostics", "json", "raise", "--throw", "InvalidOperationException:boom")]
  [TestCase(70, "raise", "--return", "3")]
  [TestCase(130, "raise", "--throw", "OperationCanceledException")]
  [TestCase(0, "help", "exit-codes")]
  public void TheTestHostAndTheInProcessRun_Agree(int expected, params string[] arguments) {
    var workspace = ScenarioWorkspace.Create();
    try {
      using var output = new StringWriter();
      using var error = new StringWriter();
      var exitCode = Program.Run(arguments, output, error, RaiseCommand.Registry(), workspace.Path);
      Assert.That(exitCode, Is.EqualTo(expected), error.ToString());
      Assert.That(RaiseHost.Start(workspace.Path, _noVariables, arguments), Is.EqualTo(new CommandResult(exitCode, output.ToString(), error.ToString())));
    } finally {
      workspace.Delete();
    }
  }

  /// <summary>Test design 9: with <c>POLYPILER_DEBUG=1</c> the stack trace follows the diagnostic in text format, and the tally line stays last.</summary>
  [Test]
  public void TheDebugVariable_AddsTheStackTrace() {
    var workspace = ScenarioWorkspace.Create();
    try {
      var run = RaiseHost.Start(workspace.Path, new Dictionary<string, string> { [ExitPolicy.DebugVariable] = "1" }, "raise", "--throw", "InvalidOperationException:boom");
      var lines = run.Stderr.ReplaceLineEndings("\n").Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
      Assert.That(run.ExitCode, Is.EqualTo(70));
      Assert.That(lines[0], Is.EqualTo("polypiler: error PLY0001: internal compiler error: InvalidOperationException: boom"));
      Assert.That(lines[1..^1], Has.Some.Contain(nameof(RaiseCommand)), run.Stderr);
      Assert.That(lines[^1], Is.EqualTo("polypiler: 1 error(s), 0 warning(s)"));
    } finally {
      workspace.Delete();
    }
  }

  /// <summary>The shipped executable: success, usage error, environment error, unsupported construct and invalid source, as in process.</summary>
  [TestCase(0, "--version")]
  [TestCase(0, "help", "exit-codes")]
  [TestCase(2, "frobnicate")]
  [TestCase(1, "translate", "missing.cs", "--from", "csharp", "--to", "javascript", "--output", "out")]
  [TestCase(3, "translate", "unsupported.cs", "--from", "csharp", "--to", "javascript", "--output", "out")]
  [TestCase(4, "translate", "invalid.cs", "--from", "csharp", "--to", "javascript", "--output", "out")]
  [TestCase(4, "--diagnostics", "json", "translate", "invalid.cs", "--from", "csharp", "--to", "javascript", "--output", "out")]
  public void TheExecutable_ExitsWithTheCodeOfItsDiagnostics(int expected, params string[] arguments) {
    var workspace = ScenarioWorkspace.Create();
    try {
      File.WriteAllText(workspace.Resolve("unsupported.cs"), "public static class C { public static decimal F(decimal a) => a; }");
      File.WriteAllText(workspace.Resolve("invalid.cs"), "public static class C { public static int F() => ; }");
      using var output = new StringWriter();
      using var error = new StringWriter();
      var exitCode = Program.Run(arguments, output, error, Program.CreateRegistry(), workspace.Path);
      Assert.That(exitCode, Is.EqualTo(expected), error.ToString());
      Assert.That(Toolchain.Executable(workspace.Path, arguments), Is.EqualTo(new CommandResult(exitCode, output.ToString(), error.ToString())));
      if (arguments[0] != "--diagnostics" && exitCode is not (0 or 2))
        Assert.That(error.ToString().ReplaceLineEndings("\n").Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Last(), Is.EqualTo("polypiler: 1 error(s), 0 warning(s)"));
    } finally {
      workspace.Delete();
    }
  }

}
