using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Batch;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Compares lifted integer scripts with the system command interpreter.</summary>
[TestFixture, Layer("core"), Category("Acceptance"), Category("EndToEnd"), RequiresToolchain("cmd")]
public sealed class BatchFrontendNativeTests {

  private static string Fixture(string name)
    => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", name);

  [Test]
  public void CountdownMatchesTheNativeStatus() => Compare(File.ReadAllText(Fixture("countdown.bat")), 42);

  [Test]
  public void ForNumericFixtureMatchesTheNativeStatus() => Compare(File.ReadAllText(Fixture("for-l.bat")), 9);

  [Test]
  public void ScopedReturnFixtureMatchesTheNativeStatus() => Compare(File.ReadAllText(Fixture("scoped-return.bat")), 42);

  [Test]
  public void RoundTripsCountdownBetweenCSharpAndNativeCmd() {
    var csharpSource = File.ReadAllText(Fixture("countdown.cs"));
    var csharpIr = CSharpFrontend.Import("countdown.cs", csharpSource).Module!;
    var emitted = BatchEmitter.Emit(csharpIr).Single().Content;
    Assert.That(NativeStatus(emitted), Is.EqualTo(42));
    var relifted = BatchFrontend.Import("kernel.bat", emitted);
    Assert.That(relifted.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(relifted.Module!, "Run", [], 10000), Is.EqualTo(42));

    var batchIr = BatchFrontend.Import("countdown.bat", File.ReadAllText(Fixture("countdown.bat"))).Module!;
    var reemitted = BatchEmitter.Emit(batchIr).Single().Content;
    Assert.That(NativeStatus(reemitted), Is.EqualTo(42));
  }

  [TestCase("set /a \"x=-2147483647-1\"\r\nexit /b %x%", int.MinValue)]
  [TestCase("set /a \"x=2147483647\"\r\nset /a \"x=x+1\"\r\nexit /b %x%", int.MinValue)]
  [TestCase("set /a \"negative=-2\"\r\nif %negative% LSS -1 goto yes\r\nexit /b 0\r\n:yes\r\nexit /b 2147483647", int.MaxValue)]
  [TestCase("set /a \"n=0\"\r\n:again\r\nif %n% LEQ 0 goto done\r\nset /a \"n=n-1\"\r\ngoto again\r\n:done\r\nexit /b %n%", 0)]
  [TestCase("set /a \"total=0\"\r\nfor /l %%I in (1,3,5) do set /a \"total=total+%%I\"\r\nexit /b %total%", 5)]
  [TestCase("set /a \"total=0\"\r\nfor /l %%I in (3,1,1) do set /a \"total=total+%%I\"\r\nexit /b %total%", 0)]
  [TestCase("set /a \"number=-2147483647-1\"\r\nset /a \"sign=(number>>31)&1\"\r\nexit /b %sign%", 1)]
  [TestCase("set /a \"x=1+7>>2&1\"\r\nexit /b %x%", 0)]
  [TestCase("setlocal\r\nset /a \"n=%errorlevel%+41\"\r\nexit /b %n%", 42)]
  [TestCase("setlocal\r\nsetlocal EnableDelayedExpansion\r\nset /a \"n=!errorlevel!+41\"\r\nexit /b %n%", 41)]
  public void SignedBoundariesAndZeroIterationsMatchNative(string body, int expected)
    => Compare("@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\n" + body, expected);

  [TestCase(8190)]
  [TestCase(8191)]
  public void CommandsAtTheDocumentedLengthLimitMatchNative(int length) {
    const string assignment = "set /a \"n=42\"";
    var command = assignment.Insert(10, new string(' ', length - assignment.Length));
    Compare($"@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\nset /a \"n=7\"\r\n{command}\r\nexit /b %n%\r\n", 42);
  }

  [TestCase("value", 8190)]
  [TestCase("value", 8191)]
  [TestCase("%value%", 8190)]
  [TestCase("%value%", 8191)]
  [TestCase("!value!", 8190)]
  [TestCase("!value!", 8191)]
  public void CompoundCommandWidthAgreesWithNative(string operand, int width) {
    const string prologue = "@echo off\r\nsetlocal EnableExtensions EnableDelayedExpansion\r\nset \"value=-2147483647\"\r\nset \"result=7\"\r\n";
    var command = $"for /l %%I in (1,1,1) do (\n  set /a \"result={operand}\"\n)";
    var expansion = operand[0] is '%' or '!' ? 11 - operand.Length : 0;
    var padded = command.Insert(command.IndexOf("result=", StringComparison.Ordinal) + "result=".Length,
      new string(' ', width - command.Length - expansion)).Replace("\n", "\r\n", StringComparison.Ordinal);
    Compare(prologue + padded + "\r\nexit /b %result%\r\n", -2147483647);
  }

  [TestCase("%value%")]
  [TestCase("!value!")]
  public void ExpandedDecimalMinimumHasTheSameNativeTraceAsTheLiteral(string operand) {
    const string prologue = "@echo off\r\nsetlocal EnableExtensions EnableDelayedExpansion\r\nset /a \"value=-2147483647-1\"\r\nset \"result=7\"\r\n";
    (int ExitCode, byte[] StandardOutput, string StandardError) Observe(string expression)
      => NativeRun(prologue + $"for /l %%I in (1,1,1) do (\r\n  set /a \"result={expression}+1\"\r\n)\r\nexit /b %result%\r\n");

    var literal = Observe("-2147483648");
    var expanded = Observe(operand);
    TestContext.Out.WriteLine($"Decimal minimum expansion returned {expanded.ExitCode} with {expanded.StandardOutput.Length} stdout bytes.");
    TestContext.Out.WriteLine(expanded.StandardError);
    Assert.That(expanded.ExitCode, Is.EqualTo(literal.ExitCode));
    Assert.That(expanded.StandardOutput, Is.EqualTo(literal.StandardOutput));
    Assert.That(expanded.StandardError, Is.EqualTo(literal.StandardError));
  }

  private static void Compare(string source, int expected) {
    var lifted = BatchFrontend.Import("kernel.bat", source);
    Assert.That(lifted.Diagnostics, Is.Empty);
    var ir = IrInterpreter.Call(lifted.Module!, "Run", [], 10000);
    Assert.That(NativeStatus(source), Is.EqualTo(expected).And.EqualTo(ir));
  }

  internal static int NativeStatus(string source, bool expectEmptyOutput = false) {
    var observed = NativeRun(source);
    if (expectEmptyOutput) {
      Assert.That(observed.StandardOutput, Is.Empty, "Native standard output");
      Assert.That(observed.StandardError, Is.Empty, "Native standard error");
    }
    return observed.ExitCode;
  }

  private static (int ExitCode, byte[] StandardOutput, string StandardError) NativeRun(string source) {
    var directory = Directory.CreateTempSubdirectory();
    try {
      const string file = "kernel.bat";
      File.WriteAllText(Path.Combine(directory.FullName, file), source);
      return BatchBackendTests.NativeRun(directory.FullName, file);
    } finally {
      directory.Delete(recursive: true);
    }
  }

}
