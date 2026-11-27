using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Batch;

[TestFixture]
public sealed class BatchCompoundForTests {

  private const string _prologue = "setlocal EnableExtensions DisableDelayedExpansion\n";
  private const string _enabled = "setlocal EnableDelayedExpansion\n";
  private const string _counter = "set /a \"n=7\"\n";
  private const string _loop = "for /l %%I in (1,1,3) do (\n";
  private const string _zeroLoop = "for /l %%I in (3,1,1) do (\n";
  private const string _return = "exit /b %n%";

  [TestCase(_enabled + _counter + _loop + "  set /a \"n=n+%%I\"\n  set /a \"n=!N!+%N%+n\"\n)\n" + _return, 127)]
  [TestCase(_enabled + _counter + "for /l %%I in (1,1,1) do (set /a \"n=n+%%I\")\n" + _return, 8)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"m=%%I\"\n  set /a \"n=!m!\"\n)\n" + _return, 3)]
  [TestCase(_counter + _zeroLoop + "  set /a \"n=!unbound!\"\n)\n" + _return, 7)]
  [TestCase(_enabled + _counter + "setlocal\nset /a \"n=!N!+1\"\n" + _return, 8)]
  [TestCase(_enabled + _counter + "call :update\nset /a \"n=!n!+1\"\n" + _return
    + "\n:update\nsetlocal DisableDelayedExpansion\nset /a \"n=30\"\ngoto :eof", 8)]
  [TestCase(_enabled + _counter + "set /a \"n=-7\"\n" + _loop
    + "  set /a \"n=n-%n%+!n!\"\n)\n" + _return, -7)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"m=%%I\"\n)\nexit /b %m%", 3)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"n=n+1\" & set /a \"n=!n!+%n%\"\n)\n" + _return, 31)]
  [TestCase("goto finish\n" + _loop + "  setlocal\n)\n:finish\nexit /b 42", 42)]
  public void EvaluatesOperandsAtTheirExpansionPhase(string body, int expected) {
    var imported = BatchFrontend.Import("compound.bat", _prologue + body);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", []), Is.EqualTo(expected));
  }

  [TestCase(_enabled + _counter + _zeroLoop + "  set /a \"n=%unbound%\"\n)\n" + _return, "PLY2802", 5)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"n=1\"\n  set /a \"n=!unbound!\"\n)\n" + _return, "PLY2802", 6)]
  [TestCase(_counter + _loop + "  set /a \"n=!n!\"\n)\n" + _return, "PLY2802", 4)]
  [TestCase(_enabled + _counter + "endlocal\n" + _loop + "  set /a \"n=!n!\"\n)\n" + _return, "PLY2802", 6)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"n=1\"\n", "PLY2800", 4)]
  [TestCase(_enabled + _counter + _loop + ")\n" + _return, "PLY2800", 4)]
  [TestCase(_enabled + _counter + _loop + "  setlocal\n)\n" + _return, "PLY2802", 5)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"n=%%i\"\n)\n" + _return, "PLY2802", 5)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"n=!n\"\n)\n" + _return, "PLY2802", 5)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"n=1%n%\"\n)\n" + _return, "PLY2802", 5)]
  [TestCase(_enabled + _counter + _loop + "  set /a \"n=1\"\n" + _return, "PLY2800", 4)]
  [TestCase(_counter + "if %n% EQU 7 goto enable\nsetlocal DisableDelayedExpansion\ngoto merge\n:enable\n"
    + _enabled + ":merge\n" + _loop + "  set /a \"n=!n!\"\n)\n" + _return, "PLY2802", 10)]
  public void RefusesUnsupportedCompoundOperands(string body, string code, int line) {
    var imported = BatchFrontend.Import("compound.bat", _prologue + body);
    Assert.That(imported.Module, Is.Null);
    Assert.That(imported.Diagnostics, Has.Count.EqualTo(1));
    Assert.That((imported.Diagnostics[0].Code, imported.Diagnostics[0].Line), Is.EqualTo((code, line)));
  }

  [TestCase("%n%")]
  [TestCase("!n!")]
  public void RefusesTextExpansionOfTheDecimalMinimum(string operand) {
    var body = _enabled + "set /a \"n=-2147483647-1\"\nset /a \"result=7\"\n" + _loop
      + $"  set /a \"result={operand}+1\"\n)\nexit /b %result%";
    var imported = BatchFrontend.Import("minimum.bat", _prologue + body);
    Assert.That(imported.Module, Is.Null);
    Assert.That(imported.Diagnostics, Has.One.Matches<Polypiler.Core.Diagnostic>(problem
      => problem.Code == "PLY2802" && problem.Line == 6));
  }

  [Test]
  public void ChecksDelayedExpansionOnLaterIterations() {
    const string body = _enabled + "set /a \"n=2147483646\"\nset /a \"result=7\"\n" + _loop
      + "  set /a \"n=n+1\"\n  set /a \"result=!n!\"\n)\nexit /b %result%";
    var imported = BatchFrontend.Import("minimum.bat", _prologue + body);
    Assert.That(imported.Module, Is.Null);
    Assert.That((imported.Diagnostics.Single().Code, imported.Diagnostics.Single().Line), Is.EqualTo(("PLY2802", 7)));
  }

  [Test]
  public void KeepsTheRepresentableSnapshotWhenTheLoopChangesTheVariableToTheMinimum() {
    const string body = _enabled + "set /a \"n=2147483647\"\nset /a \"result=7\"\n" + _loop
      + "  set /a \"n=n+1\"\n  set /a \"result=%n%\"\n)\nexit /b %result%";
    var imported = BatchFrontend.Import("snapshot.bat", _prologue + body);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", []), Is.EqualTo(int.MaxValue));
  }

  [TestCase("setlocal", 42)]
  [TestCase("setlocal EnableExtensions EnableDelayedExpansion", 41)]
  [TestCase("setlocal DisableDelayedExpansion", 41)]
  public void ExpandedStatusReflectsTheSetlocalOptions(string command, int expected) {
    var body = "call :status\n" + command + "\nset /a \"result=%errorlevel%+41\"\n"
      + "exit /b %result%\n:status\nexit /b 7";
    var imported = BatchFrontend.Import("status.bat", _prologue + body);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", []), Is.EqualTo(expected));
  }

  [Test]
  public void ProvesTheSafeBranchAtTheExpansionSite() {
    const string body = "set /a \"n=0\"\nif %n% EQU 0 goto safe\n"
      + "set /a \"value=-2147483647-1\"\ngoto emit\n"
      + ":safe\nset /a \"value=42\"\n:emit\nset /a \"n=%value%\"\n" + _return;
    var imported = BatchFrontend.Import("branch.bat", _prologue + body);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", []), Is.EqualTo(42));
  }

  [Test]
  public void AcceptsAProvenInvariantOverALargeRange() {
    const string body = _enabled + "set /a \"n=21*2\"\nfor /l %%I in (1,1,10000000) do (\n"
      + "  set /a \"n=!n!\"\n)\n" + _return;
    Assert.That(BatchFrontend.Import("large.bat", _prologue + body).Diagnostics, Is.Empty);
  }

  [Test]
  public void RefusesAnUnprovenExpansionWhenTheAnalysisBudgetRunsOut() {
    const string body = _enabled + _counter + "for /l %%I in (1,1,10000000) do (\n"
      + "  set /a \"n=!n!+1\"\n)\n" + _return;
    var imported = BatchFrontend.Import("large.bat", _prologue + body);
    Assert.That(imported.Module, Is.Null);
    Assert.That(imported.Diagnostics.Single().Message, Does.Contain("analysis budget"));
  }

  [TestCase("n", 8190, false)]
  [TestCase("n", 8191, false)]
  [TestCase("n", 8192, true)]
  [TestCase("%n%", 8190, false)]
  [TestCase("%n%", 8191, false)]
  [TestCase("%n%", 8192, true)]
  [TestCase("!n!", 8190, false)]
  [TestCase("!n!", 8191, false)]
  [TestCase("!n!", 8192, true)]
  [TestCase("%%I", 8190, false)]
  [TestCase("%%I", 8191, false)]
  [TestCase("%%I", 8192, true)]
  public void GuardsTheCompoundCommandWidth(string operand, int width, bool refused) {
    var command = _loop + "  set /a \"n= " + operand + "\"\n)";
    var growth = operand.Length == 1 ? 0 : 11 - operand.Length;
    command = command.Insert(command.IndexOf("n= ", StringComparison.Ordinal) + 2, new string(' ', width - command.Length - growth));
    var imported = BatchFrontend.Import("wide.bat", _prologue + _enabled + _counter + command + "\n" + _return);
    if (refused) {
      Assert.That(imported.Module, Is.Null);
      Assert.That(imported.Diagnostics.Single().Code, Is.EqualTo("PLY2802"));
    } else {
      Assert.That(imported.Diagnostics, Is.Empty);
      Assert.That(IrInterpreter.Call(imported.Module!, "Run", []), Is.EqualTo(operand == "%%I" ? 3 : 7));
    }
  }

}
