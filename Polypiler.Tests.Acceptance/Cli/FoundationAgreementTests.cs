using System.IO;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;

namespace Polypiler.Tests.Acceptance.Cli;

/// <summary>
/// The command-line foundation in the shipped executable and in process agree byte for byte: the
/// scenarios of <c>cli-foundation.feature</c> run in process with test-only commands, which the
/// executable does not have, so the lines here use only what both have.
/// </summary>
[TestFixture, Category("EndToEnd")]
[Layer("core")]
public sealed class FoundationAgreementTests {

  [TestCase]
  [TestCase("--help")]
  [TestCase("help")]
  [TestCase("--version")]
  [TestCase("frobnicate")]
  [TestCase("-?")]
  [TestCase("--quiet")]
  [TestCase("--quiet", "--version")]
  [TestCase("--diagnostics", "xml", "translate")]
  [TestCase("--diagnostics", "json", "frobnicate")]
  [TestCase("help", "frobnicate")]
  [TestCase("help", "translate")]
  [TestCase("translate", "--help")]
  [TestCase("translate", "--nope")]
  [TestCase("translate", "a.cs", "b.cs")]
  [TestCase("translate", "a.cs", "--from", "csharp", "--to", "javascript")]
  [TestCase("translate", "a.cs", "--from", "csharp", "--to", "javascript", "--output", "out", "--disable-opt", "O0100,")]
  [TestCase("translate", "@a.cs", "--from", "csharp", "--to", "javascript", "--output", "out")]
  [TestCase("--diagnostics=json", "translate", "my file.cs", "--from", "csharp", "--to", "javascript", "--output", "out")]
  public void TheExecutableAndTheInProcessRun_Agree(params string[] arguments) {
    var workspace = ScenarioWorkspace.Create();
    try {
      using var output = new StringWriter();
      using var error = new StringWriter();
      var exitCode = global::Polypiler.Cli.Program.Run(arguments, output, error, global::Polypiler.Cli.Program.CreateRegistry(), workspace.Path);
      Assert.That(Toolchain.Executable(workspace.Path, arguments), Is.EqualTo(new CommandResult(exitCode, output.ToString(), error.ToString())));
      Assert.That(Directory.Exists(workspace.Resolve("out")), Is.False, "nothing is written");
    } finally {
      workspace.Delete();
    }
  }

}
