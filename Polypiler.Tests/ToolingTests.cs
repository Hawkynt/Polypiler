using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;
using Polypiler.Tests.Precomputed.Support;
using Polypiler.Tooling;

namespace Polypiler.Tests;

/// <summary>Checks the entry point of the <c>polypiler-tooling</c> shell and its verbs.</summary>
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
    Assert.That(output, Does.Contain($"polypiler-tooling {Program.FloatImplementations} <file>"));
    Assert.That(error, Is.Empty);
  }

  [TestCase("no-such-verb", TestName = "A verb that is not registered")]
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

    Assert.That(output.ToString(), Does.Contain(Program.FloatImplementations));
    Assert.That(output.ToString(), Does.Contain("toolchains validate"));
  }

  [TestCase(TestName = "float-implementations without a file")]
  [TestCase("a", "b", TestName = "float-implementations with two files")]
  public void FloatImplementations_TakesExactlyOneFile(params string[] files) {
    var (exitCode, output, error) = Invoke([Program.FloatImplementations, .. files]);
    Assert.That(exitCode, Is.EqualTo(Program.UsageError));
    Assert.That(output, Is.Empty);
    Assert.That(error, Does.Contain("takes one argument"));
  }

  /// <summary>What the build embeds: the portable implementations imported from their source, as a module in bytes.</summary>
  [Test, Category("Slow")]
  public void FloatImplementations_WritesTheImportedModule() {
    var file = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N") + ".irm");
    try {
      var (exitCode, output, error) = Invoke(Program.FloatImplementations, file);
      Assert.That((exitCode, output.Trim(), error), Is.EqualTo((Program.Success, file, "")));
      Assert.That(IrGraph.Difference(IrModuleFormat.Read(File.ReadAllBytes(file)), FloatImplementations.Module), Is.Null);
    } finally {
      File.Delete(file);
    }
  }

  [Test]
  public void SourcesThatDoNotImport_WriteNoFile() {
    var file = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N") + ".irm");
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.WriteFloatImplementations([new CSharpSource("Broken.cs", "public static class Broken { public static int F() => ; }")], file, output, error);
    Assert.That(exitCode, Is.EqualTo(Program.Failure));
    Assert.That(error.ToString(), Does.Contain(CSharpFrontend.CompilerError));
    Assert.That(File.Exists(file), Is.False);
  }

  [Test]
  public void AFileThatCannotBeWritten_IsAFailure() {
    var file = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"), "missing", "out.irm");
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.WriteFloatImplementations([new CSharpSource("Small.cs", "public static class Small { public static int F() => 1; }")], file, output, error);
    Assert.That(exitCode, Is.EqualTo(Program.Failure));
    Assert.That(error.ToString(), Does.Contain("cannot be written"));
    Assert.That(output.ToString(), Is.Empty);
  }

  [Test]
  public void ASmallModule_IsWrittenAndReadsBack() {
    var file = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N") + ".irm");
    try {
      using var output = new StringWriter();
      using var error = new StringWriter();
      var exitCode = Program.WriteFloatImplementations([new CSharpSource("Small.cs", "public static class Small { public static int F(int x) => x + 1; }")], file, output, error);
      Assert.That(exitCode, Is.EqualTo(Program.Success));
      Assert.That(IrModuleFormat.Read(File.ReadAllBytes(file)).Functions[0].Name, Is.EqualTo("F"));
    } finally {
      File.Delete(file);
    }
  }

  [Test]
  public void AssemblyName_IsTheCommandName()
    => Assert.That(typeof(Program).Assembly.GetName().Name, Is.EqualTo(Program.Name));

}
