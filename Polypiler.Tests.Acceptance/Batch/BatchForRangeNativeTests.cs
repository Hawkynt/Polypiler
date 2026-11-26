using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Checks the admitted literal range and arithmetic forms against native cmd.exe.</summary>
[TestFixture, Layer("core"), Category("Acceptance"), Category("EndToEnd"), RequiresToolchain("cmd")]
public sealed class BatchForRangeNativeTests {

  private const string _header = "@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\n";

  [Test]
  public void NativeCmdRunsThePinnedDescendingRange() {
    var fixture = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "for-l.bat");
    Compare(File.ReadAllText(fixture), 9);
  }

  [TestCase("1,2,5", "total+%%I", 9)]
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
  public void RangePartitionsPreserveNativeResults(string triple, string expression, int expected) =>
    Compare(_header + $"set /a \"total=0\"\r\nfor /l %%I in ({triple}) do set /a \"total={expression}\"\r\nexit /b %total%\r\n", expected);

  [TestCase("2147483647,1,2147483647")]
  [TestCase("-2147483648,-1,-2147483648")]
  public void WinCmdTakesAnotherIterationAfterPostBodyOverflow(string triple) {
    // The literal profile refuses this range. The oracle stops itself after the second trip.
    var source = $"@echo off\r\nsetlocal EnableExtensions EnableDelayedExpansion\r\n"
      + $"set /a \"trips=0\"\r\nfor /l %%I in ({triple}) do (\r\n"
      + "  set /a \"trips=trips+1\"\r\n  if !trips! EQU 2 exit /b 2\r\n)\r\nexit /b 1\r\n";
    Assert.That(BatchFrontendNativeTests.NativeStatus(source), Is.EqualTo(2));
  }

  [Test]
  public void TheExpandedMinimumFailsInNativeSetArithmetic() {
    var source = _header + "set /a \"total=17\"\r\n"
      + "for /l %%I in (-2147483648,1,-2147483648) do set /a \"total=total+%%I\"\r\n"
      + "exit /b %total%\r\n";
    Assert.That(BatchFrontendNativeTests.NativeStatus(source), Is.EqualTo(17));
  }

  private static void Compare(string source, int expected) {
    var imported = BatchFrontend.Import("range.bat", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", [], 1000), Is.EqualTo(expected));
    var generated = CSharpEmitter.Emit(imported.Module!).Single().Content;
    Assert.That(GeneratedCSharp.Compile(generated, checkOverflow: true).Method("Run").Invoke(null, null), Is.EqualTo(expected));
    Assert.That(BatchFrontendNativeTests.NativeStatus(source), Is.EqualTo(expected));
  }

}
