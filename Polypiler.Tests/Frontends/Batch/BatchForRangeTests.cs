using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Batch;

[TestFixture]
public sealed class BatchForRangeTests {

  private const string _header = "@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\n";
  private const string _initialize = "set /a \"total=0\"\r\n";
  private const string _return = "\r\nexit /b %total%\r\n";

  [TestCase("1,2,5", "total+%%I", 9)]
  [TestCase("5,-2,1", "TOTAL+%%I", 9)]
  [TestCase("1,3,5", "total+%%I", 5)]
  [TestCase("3,1,1", "total+%%I", 0)]
  [TestCase("1,-1,3", "total+%%I", 0)]
  [TestCase("-1,1,-1", "total-%%I", 1)]
  [TestCase("-1,1,-1", "-%%I", 1)]
  [TestCase("2147483646,1,2147483646", "total+1", 1)]
  [TestCase("-2147483647,-1,-2147483647", "total+1", 1)]
  [TestCase("-2147483648,1,-2147483648", "total+1", 1)]
  [TestCase("0,-2147483648,0", "total+1", 1)]
  [TestCase("1,1,1", "2147483647+%%I", int.MinValue)]
  public void SignedRangesMatchBothTypedExecutions(string range, string expression, int expected)
    => Check(_initialize + Command(range, expression) + _return, expected);

  [Test]
  public void IteratorAndEnvironmentVariablesHaveSeparateLifetimes() => Check(
    "set /a \"i=3\"\r\n" + Command("4,1,4", "i+%%I") + "\r\n"
    + "for /l %%i in (1,1,1) do set /a \"total=total+%%i+i\"" + _return, 11);

  [TestCase("3,1,1", "PLY2802", "read of total before assignment", 4)]
  [TestCase("1,1,1", "PLY2802", "read of missing before assignment", 3)]
  public void ALoopOnlyEstablishesAssignmentsItActuallyExecutes(string range, string code, string message, int line) {
    var value = range == "3,1,1" ? "42" : "missing+%%I";
    Refused(Command(range, value) + _return, code, line, message);
  }

  [Test]
  public void ANonemptyLoopCanInitializeItsDestination() => Check(Command("7,2,7", "%%I") + _return, 7);

  [TestCase("1,0,3", "total+%%I", "PLY2802", "zero")]
  [TestCase("2147483647,1,2147483647", "total+1", "PLY2802", "increment")]
  [TestCase("-2147483648,-1,-2147483648", "total+1", "PLY2802", "increment")]
  [TestCase("-2147483648,1,-2147483648", "total+%%I", "PLY2802", "decimal")]
  [TestCase("1,1,1", "total+%%i", "PLY2802", "%%i")]
  [TestCase("1,1,1", "total+%%~I", "PLY2802", "%%~I")]
  [TestCase("1,1,1", "total+%%I0", "PLY2802", "%%I0")]
  [TestCase("1,1,2147483648", "total+%%I", "PLY2800", "Int32")]
  [TestCase("01,1,7", "total+%%I", "PLY2800", "canonical")]
  [TestCase("1,1", "total+%%I", "PLY2800", "range")]
  public void RefusesInvalidOrUnmodeledRanges(string range, string expression, string code, string message)
    => Refused(_initialize + Command(range, expression) + _return, code, 4, message);

  [TestCase("for /d %%I in (*) do set /a \"total=1\"")]
  [TestCase("for /l %%I in (1,1,2) do echo %%I")]
  [TestCase("for /l %%I in (1,1,2) do (set /a \"total=1\")")]
  [TestCase("for /l %%I in (%total%,1,2) do set /a \"total=1\"")]
  public void OtherForFormsAreRefused(string command) => Refused(_initialize + command + _return, "PLY2802", 4, command);

  [TestCase(8183, true)]
  [TestCase(8184, false)]
  public void BoundsTheExpandedIteratorCommand(int length, bool fits) {
    var command = Command("-2147483647,1,-2147483647", "%%I");
    var padded = command.Insert(command.IndexOf("%%I\"", StringComparison.Ordinal), new string(' ', length - command.Length));
    if (fits)
      Check(padded + _return, -2147483647);
    else
      Refused(padded + _return, "PLY2802", 3, "expanded FOR /L command");
  }

  [Test]
  public void CliRefusesTheProhibitedStepWithoutCreatingOutput() {
    var workspace = Directory.CreateTempSubdirectory();
    try {
      var source = Path.Combine(workspace.FullName, "kernel.bat");
      File.WriteAllText(source, _header + _initialize + Command("1,0,3", "total+%%I") + _return);
      var target = Path.Combine(workspace.FullName, "out");
      using var errors = new StringWriter();
      var status = Program.Run(["translate", source, "--from", "batch", "--to", "csharp",
        "--opt", "none", "--output", target], TextWriter.Null, errors);
      Assert.That(status, Is.EqualTo(TranslateCommand.UnsupportedConstruct), errors.ToString());
      Assert.That(errors.ToString(), Does.Contain("(4,1): error PLY2802"));
      Assert.That(Directory.Exists(target), Is.False);
    } finally {
      workspace.Delete(recursive: true);
    }
  }

  private static string Command(string range, string expression) => $"for /l %%I in ({range}) do set /a \"total={expression}\"";

  private static void Check(string body, int expected) {
    var imported = BatchFrontend.Import("kernel.bat", _header + body);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", [], 1000), Is.EqualTo(expected));
    var generated = CSharpEmitter.Emit(imported.Module!).Single().Content;
    Assert.That(GeneratedCSharp.Compile(generated, checkOverflow: true).Method("Run").Invoke(null, null), Is.EqualTo(expected));
  }

  private static void Refused(string body, string code, int line, string detail) {
    var imported = BatchFrontend.Import("kernel.bat", _header + body);
    Assert.That(imported.Module, Is.Null);
    var diagnostic = imported.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Line), Is.EqualTo((code, line)));
    Assert.That(diagnostic.Message, Does.Contain(detail));
  }

}
