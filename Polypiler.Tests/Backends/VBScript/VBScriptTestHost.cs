using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.VBScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VBScript;

/// <summary>IR construction, C# import, emission and live <c>wine cscript</c> execution helpers.</summary>
internal static class VBScriptTestHost {

  public static IrLocal L(string name, IrType type) => new(name, type);

  public static IrLocalRef R(IrLocal local) => new(local);

  public static IrConst C(IrType type, long value) => new(type, value);

  public static IrConst I(long value) => new(IrType.I32, value);

  public static IrConst U(uint value) => new(IrType.U32, value);

  public static IrConst F64(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  public static IrConst F32(float value) => new(IrType.F32, IrOperators.Bits(IrType.F32, value));

  public static IrConst B(bool value) => new(IrType.Bool, value ? 1 : 0);

  public static IrBinary Bin(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  public static IrBlock Block(params IrStmt[] statements) => new(statements);

  public static IrFunction Function(string name, IrType result, IrLocal[] parameters, params IrStmt[] body)
    => Function(name, true, result, parameters, body);

  public static IrFunction Function(string name, bool isPublic, IrType result, IrLocal[] parameters, params IrStmt[] body) {
    var declared = new List<IrLocal>();
    void AddLocal(IrLocal? local) {
      if (local is not null && !declared.Contains(local))
        declared.Add(local);
    }

    void Collect(IrStmt statement) {
      switch (statement) {
        case IrDeclare declaration:
          AddLocal(declaration.Local);
          break;
        case IrBlock block:
          foreach (var child in block.Statements)
            Collect(child);
          break;
        case IrIf conditional:
          Collect(conditional.Then);
          if (conditional.Else is { } otherwise)
            Collect(otherwise);
          break;
        case IrWhile loop:
          Collect(loop.Body);
          break;
        case IrDoWhile loop:
          Collect(loop.Body);
          break;
        case IrFor loop:
          Collect(loop.Body);
          Collect(loop.Step);
          break;
        case IrSwitch selection:
          foreach (var section in selection.Sections)
            Collect(section.Body);
          break;
        case IrTry trying:
          Collect(trying.Body);
          foreach (var handler in trying.Catches) {
            AddLocal(handler.Local);
            Collect(handler.Body);
          }
          if (trying.Finally is { } cleanup)
            Collect(cleanup);
          break;
        case IrUsing @using:
          AddLocal(@using.Resource);
          Collect(@using.Body);
          break;
        case IrForEachCollection feCol:
          AddLocal(feCol.Element);
          AddLocal(feCol.Value);
          Collect(feCol.Body);
          break;
        case IrForEachSequence feSeq:
          AddLocal(feSeq.Element);
          Collect(feSeq.Body);
          break;
      }
    }

    foreach (var stmt in body)
      Collect(stmt);
    return new IrFunction(name, isPublic, result, parameters, declared, new IrBlock(body));
  }

  public static IrModule Returning(IrType result, IrLocal[] parameters, IrExpr value)
    => new("M", [Function("f", result, parameters, new IrReturn(value))]);

  public static string Emit(IrModule module, VBScriptProfile? profile = null, VBScriptEmitOptions? options = null)
    => VBScriptEmitter.Emit(module, profile, options)[0].Content;

  public static string LegalizedEmit(IrModule module, VBScriptProfile? profile = null, VBScriptEmitOptions? options = null) {
    var lowered = VBScriptEmitter.Legalize(module, () => PrecomputedImplementations.Module, profile, FloatMode.Accurate);
    return VBScriptEmitter.Emit(lowered, profile, options)[0].Content;
  }

  public static IrModule ImportCSharp(string methods) {
    var imported = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(methods));
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    return imported.Module!;
  }

  public static IrModule ImportCSharpRaw(string source) {
    var imported = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    return imported.Module!;
  }

  public static IrModule ImportShowcase(string fileName) {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Polypiler.slnx")))
      dir = dir.Parent;
    Assert.That(dir, Is.Not.Null, "Could not locate repository root containing Polypiler.slnx.");
    var path = Path.Combine(dir!.FullName, "docs", "showcase", fileName);
    var imported = CSharpFrontend.Import(fileName, File.ReadAllText(path));
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    return imported.Module!;
  }

  /// <summary>Whether a VBScript 5.8 host (<c>cscript.exe</c> on Windows, or <c>wine cscript</c> on POSIX) is available.</summary>
  public static bool HasHost { get; } = FindHost() is not null;

  private static (string FileName, string Prefix)? FindHost() {
    if (OperatingSystem.IsWindows()) {
      var sys = Path.Combine(Environment.SystemDirectory, "cscript.exe");
      return File.Exists(sys) ? (sys, "//Nologo") : ("cscript.exe", "//Nologo");
    }

    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
      var candidate = Path.Combine(dir, "wine");
      if (File.Exists(candidate))
        return (candidate, "cscript //Nologo");
    }

    return null;
  }

  public static IReadOnlyList<string> RunLines(string vbsModule, string driverCode) {
    var host = FindHost();
    if (host is null)
      Assert.Ignore("No VBScript host (cscript or wine) is available on this runner.");

    const string fmtHelper = """
      Function test_fmt(ByVal v)
        If VarType(v) = 11 Then
          If v Then test_fmt = "True" Else test_fmt = "False"
        Else
          test_fmt = CStr(v)
        End If
      End Function
      """;
    var script = vbsModule + "\n" + fmtHelper + "\n" + driverCode + "\n";
    var tempPath = Path.Combine(Path.GetTempPath(), "polypiler-vbs-" + Guid.NewGuid().ToString("N") + ".vbs");
    try {
      File.WriteAllText(tempPath, script);
      var psi = new ProcessStartInfo(host.Value.FileName, $"{host.Value.Prefix} \"{tempPath}\"") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      };
      if (!OperatingSystem.IsWindows())
        psi.Environment["WINEDEBUG"] = "-all";
      using var process = Process.Start(psi)!;
      var stdout = process.StandardOutput.ReadToEnd();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit(30000);
      var vbsError = stderr.Contains("VBScript runtime error", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("VBScript compilation error", StringComparison.OrdinalIgnoreCase);
      Assert.That(process.ExitCode, Is.Zero, $"cscript failed (exit {process.ExitCode}):\nSTDERR:\n{stderr}\nSTDOUT:\n{stdout}\nSCRIPT:\n{script}");
      Assert.That(vbsError, Is.False, $"cscript reported a VBScript error:\nSTDERR:\n{stderr}\nSTDOUT:\n{stdout}\nSCRIPT:\n{script}");
      return stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    } finally {
      if (File.Exists(tempPath))
        File.Delete(tempPath);
    }
  }

  public static string FormatExpected(object? value) => value switch {
    bool b => b ? "True" : "False",
    double d => d.ToString("G15", CultureInfo.InvariantCulture),
    float f => f.ToString("G7", CultureInfo.InvariantCulture),
    long l => l.ToString(CultureInfo.InvariantCulture),
    string s => s,
    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
  };

}
