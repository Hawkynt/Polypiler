using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Frontends.VBScript;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.VBScript;

/// <summary>
/// Differential oracle tests comparing lifted PolyIR execution (<see cref="IrInterpreter"/>)
/// against live Windows Script Host VBScript 5.8 (<c>wine cscript //Nologo</c>).
/// </summary>
[TestFixture]
public sealed class VBScriptOracleTests {

  private static readonly Lazy<bool> _cscriptAvailable = new(CheckCScriptAvailable);

  private static bool CheckCScriptAvailable() {
    try {
      var temp = Path.Combine(Path.GetTempPath(), $"vbs-probe-{Guid.NewGuid():N}.vbs");
      File.WriteAllText(temp, "WScript.Echo \"ok\"\r\n");
      try {
        var output = RunCScript(temp);
        return output.Trim() == "ok";
      } finally {
        if (File.Exists(temp))
          File.Delete(temp);
      }
    } catch {
      return false;
    }
  }

  private static string RunCScript(string scriptPath) {
    var psi = OperatingSystem.IsWindows()
      ? new ProcessStartInfo("cscript.exe", $"//Nologo \"{scriptPath}\"") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      }
      : new ProcessStartInfo("wine", $"cscript //Nologo \"{scriptPath}\"") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      };
    if (!OperatingSystem.IsWindows())
      psi.Environment["WINEDEBUG"] = "-all";
    using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start cscript.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(30_000);
    if (process.ExitCode != 0)
      throw new InvalidOperationException($"cscript exited with {process.ExitCode}: {stderr}");
    return stdout.Replace("\r\n", "\n").TrimEnd('\n');
  }

  private static void AssertMatchesOracle(string vbsModule, string entryFunction, string vbsCallExpr) {
    var result = VBScriptFrontend.Import("Oracle.vbs", vbsModule);
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join(Environment.NewLine, result.Diagnostics));
    var module = result.Module!;
    Assert.That(IrVerifier.Verify(module), Is.Empty);

    var inst = IrInterpreter.Instantiate(module);
    var irVal = inst.Call(entryFunction);
    var irText = irVal switch {
      bool b => b ? "True" : "False",
      double d => d.ToString("G15", CultureInfo.InvariantCulture),
      IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
      _ => irVal?.ToString() ?? ""
    };

    if (!_cscriptAvailable.Value)
      Assert.Ignore("cscript is not available on this host.");

    var temp = Path.Combine(Path.GetTempPath(), $"vbs-oracle-{Guid.NewGuid():N}.vbs");
    File.WriteAllText(temp, vbsModule + "\r\nWScript.Echo CStr(" + vbsCallExpr + ")\r\n");
    try {
      var oracleText = RunCScript(temp).Trim();
      Assert.That(irText, Is.EqualTo(oracleText), $"IR vs cscript mismatch for {entryFunction}");
    } finally {
      if (File.Exists(temp))
        File.Delete(temp);
    }
  }

  [Test]
  [Category("ExternalInterop")]
  public void Oracle_ArithmeticHexPrecedenceAndBitwise_MatchesCScript() {
    var code = """
      Option Explicit
      Function RunAll()
        Dim a, b, c, d, e, f, g
        a = &HFFFF + &HFFFF& + &HFF
        b = (2 ^ 3 ^ 2) + (-2 ^ 2)
        c = (7 \ 2) + (-7 \ 2) + (7 Mod 3) + (-7 Mod 3)
        d = (5 And 3) + (5 Or 3) + (5 Xor 3) + (Not 5)
        e = Int(2.7) + Int(-2.7) + Fix(2.7) + Fix(-2.7)
        f = Round(2.5) + Round(3.5) + Abs(-12) + Sgn(-9)
        g = CInt(True) + CInt(False) + CInt(2.5) + CInt(3.5)
        RunAll = a & "|" & b & "|" & c & "|" & d & "|" & e & "|" & f & "|" & g
      End Function
      """;
    AssertMatchesOracle(code, "RunAll", "RunAll()");
  }

  [Test]
  [Category("ExternalInterop")]
  public void Oracle_StringsArraysLoopsAndClasses_MatchesCScript() {
    var code = """
      Option Explicit

      Class Accumulator
        Private m_sum
        Private Sub Class_Initialize()
          m_sum = 0
        End Sub
        Public Property Get Total()
          Total = m_sum
        End Property
        Public Property Let Total(v)
          m_sum = v
        End Property
        Public Sub Push(x)
          m_sum = m_sum + x
        End Sub
      End Class

      Function RunAll()
        Dim a(), i, acc, s, parts
        ReDim a(2)
        a(0) = 3 : a(1) = 6 : a(2) = 9
        ReDim Preserve a(4)
        a(3) = 12 : a(4) = 15

        Set acc = New Accumulator
        For Each i In a
          acc.Push i
        Next

        s = Trim("  hello world  ")
        parts = Split("red,green,blue", ",")
        RunAll = CStr(acc.Total) & "|" & UCase(Left(s, 5)) & "|" & LCase(Right(s, 5)) & "|" _
          & Mid(s, 3, 4) & "|" & CStr(InStr(3, "banana", "an")) & "|" _
          & Replace("aba", "a", "z") & "|" & Join(parts, "-") & "|" _
          & Chr(66) & "|" & CStr(Asc("B")) & "|" & CStr(IsNumeric("42")) & "|" & CStr(IsNumeric("no"))
      End Function
      """;
    AssertMatchesOracle(code, "RunAll", "RunAll()");
  }

  [Test]
  [Category("ExternalInterop")]
  public void Oracle_ErrorHandling_MatchesCScript() {
    var code = """
      Function RunAll()
        On Error Resume Next
        Dim x, a, b
        x = 10 \ 0
        a = Err.Number
        Err.Clear
        Err.Raise 777, "Oracle", "custom"
        b = Err.Number & ":" & Err.Description
        On Error GoTo 0
        RunAll = CStr(a) & "|" & b
      End Function
      """;
    AssertMatchesOracle(code, "RunAll", "RunAll()");
  }

}
