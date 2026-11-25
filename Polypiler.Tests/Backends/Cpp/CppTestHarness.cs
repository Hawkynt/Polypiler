using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>
/// Helpers for building verified IR modules, importing C# via <see cref="CSharpFrontend"/>,
/// compiling emitted C++17/C++20/C++23 code with <c>/usr/bin/g++ -Wall -Wextra -Werror</c>,
/// and verifying live native execution against <see cref="IrInterpreter.Call"/>.
/// </summary>
internal static class CppTestHarness {

  public static readonly CppProfile[] AllProfiles = [CppProfile.Cpp17, CppProfile.Cpp20, CppProfile.Cpp23];

  public static IrLocal L(string name, IrType type) => new(name, type);

  public static IrLocalRef R(IrLocal local) => new(local);

  public static IrConst C(IrType type, long value) => new(type, value);

  public static IrConst I(long value) => new(IrType.I32, value);

  public static IrConst U(ulong value) => new(IrType.U32, unchecked((long)value));

  public static IrConst I64(long value) => new(IrType.I64, value);

  public static IrConst U64(ulong value) => new(IrType.U64, unchecked((long)value));

  public static IrConst F32(float value) => new(IrType.F32, IrOperators.Bits(IrType.F32, value));

  public static IrConst F64(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

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
        case IrForEachCollection loop:
          AddLocal(loop.Element);
          AddLocal(loop.Value);
          Collect(loop.Body);
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
      }
    }

    foreach (var stmt in body)
      Collect(stmt);
    return new IrFunction(name, isPublic, result, parameters, declared, new IrBlock(body));
  }

  public static IrModule ImportCSharp(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  public static object? Eval(IrModule module, string function, params object[] arguments)
    => IrInterpreter.Call(module, function, arguments);

  public static IrModule Legalize(IrModule module, CppProfile profile)
    => CppEmitter.Legalize(module, () => PrecomputedImplementations.Module, profile, FloatMode.Accurate);

  private static readonly Lazy<string?> _compiler = new(FindCompiler);

  private static string? FindCompiler() {
    if (File.Exists("/usr/bin/g++"))
      return "/usr/bin/g++";

    foreach (var candidate in new[] { "g++", "clang++" }) {
      try {
        var check = RunProcess(candidate, ["--version"], Path.GetTempPath());
        if (check.ExitCode == 0)
          return candidate;
      } catch {
      }
    }

    return null;
  }

  /// <summary>
  /// Emits <paramref name="module"/> for <paramref name="profile"/>, compiles it together with
  /// <paramref name="driverCpp"/> using <c>g++</c>/<c>clang++ -std=c++XX -Wall -Wextra -Werror</c>,
  /// runs the resulting native executable, and returns its standard output.
  /// </summary>
  public static string CompileAndRun(IrModule module, CppProfile profile, string driverCpp, CppEmitOptions? options = null) {
    var files = CppEmitter.Emit(module, profile, options);
    Assert.That(files, Has.Count.EqualTo(2));
    var compiler = _compiler.Value;
    if (compiler is null) {
      Assert.Ignore("No C++ compiler (g++/clang++) found on this runner.");
      return "";
    }

    var dir = Path.Combine(Path.GetTempPath(), "polypiler-cpp-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      foreach (var file in files)
        File.WriteAllText(Path.Combine(dir, file.Path), file.Content);

      var cppFile = files.First(f => f.Path.EndsWith(".cpp", StringComparison.Ordinal)).Path;
      var driverPath = Path.Combine(dir, "driver.cpp");
      File.WriteAllText(driverPath, driverCpp);

      var stdFlag = profile.Standard switch {
        CppStandard.Cpp17 => "-std=c++17",
        CppStandard.Cpp20 => "-std=c++20",
        _ => "-std=c++23"
      };
      var exePath = Path.Combine(dir, OperatingSystem.IsWindows() ? "runner.exe" : "runner");
      var compile = RunProcess(compiler, [stdFlag, "-Wall", "-Wextra", "-Werror", "-O1", "-o", exePath, Path.Combine(dir, cppFile), driverPath], dir);
      if (compile.ExitCode != 0) {
        var hpp = files.First(f => f.Path.EndsWith(".hpp", StringComparison.Ordinal)).Content;
        var cpp = files.First(f => f.Path.EndsWith(".cpp", StringComparison.Ordinal)).Content;
        Assert.Fail($"g++ {stdFlag} failed (exit {compile.ExitCode}):\n{compile.StdErr}\n--- HPP ---\n{hpp}\n--- CPP ---\n{cpp}\n--- DRIVER ---\n{driverCpp}");
      }

      var run = RunProcess(exePath, [], dir);
      if (run.ExitCode != 0)
        Assert.Fail($"Native binary failed (exit {run.ExitCode}):\nSTDOUT:\n{run.StdOut}\nSTDERR:\n{run.StdErr}");

      return run.StdOut;
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

  /// <summary>Runs <see cref="CompileAndRun"/> across all three C++ standards (C++17, C++20, C++23).</summary>
  public static void CompileAndRunAllStandards(IrModule module, string driverCpp, Action<string>? assertOutput = null, CppEmitOptions? options = null) {
    foreach (var profile in AllProfiles) {
      var legalized = Legalize(module, profile);
      var stdout = CompileAndRun(legalized, profile, driverCpp, options);
      assertOutput?.Invoke(stdout);
    }
  }

  private static (int ExitCode, string StdOut, string StdErr) RunProcess(string fileName, string[] arguments, string workingDir) {
    var psi = new ProcessStartInfo(fileName) {
      WorkingDirectory = workingDir,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var arg in arguments)
      psi.ArgumentList.Add(arg);

    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return (proc.ExitCode, stdout, stderr);
  }

}
