using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Jvm;

/// <summary>IR builder helpers and live <c>javac</c> / <c>java -Xverify:all</c> execution harness for JVM backend tests.</summary>
internal static class JvmTestRunner {

  public static IrLocal L(string name, IrType type) => new(name, type);

  public static IrLocalRef R(IrLocal local) => new(local);

  public static IrConst C(IrType type, long value) => new(type, value);

  public static IrConst I(int value) => new(IrType.I32, value);

  public static IrConst U32(uint value) => new(IrType.U32, value);

  public static IrConst I64(long value) => new(IrType.I64, value);

  public static IrConst U64(ulong value) => new(IrType.U64, unchecked((long)value));

  public static IrConst F32(float value) => new(IrType.F32, IrOperators.Bits(IrType.F32, value));

  public static IrConst F64(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  public static IrConst B(bool value) => new(IrType.Bool, value ? 1 : 0);

  public static IrBinary Bin(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  public static IrBlock Block(params IrStmt[] statements) => new(statements);

  /// <summary>Builds a public IR function automatically collecting all declared locals.</summary>
  public static IrFunction Function(string name, IrType result, IrLocal[] parameters, params IrStmt[] body)
    => Function(name, true, result, parameters, body);

  /// <summary>Builds an IR function with explicit visibility automatically collecting all declared locals.</summary>
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
      }
    }

    foreach (var stmt in body)
      Collect(stmt);
    return new IrFunction(name, isPublic, result, parameters, declared, new IrBlock(body));
  }

  /// <summary>
  /// Writes generated <c>.jar</c>/<c>.class</c> files to a temporary directory, compiles <paramref name="harnessSource"/>
  /// with <c>javac</c>, runs <c>java -Xverify:all</c>, asserts exit code 0, and returns trimmed stdout.
  /// </summary>
  public static string RunHarness(IReadOnlyList<GeneratedFile> files, string harnessSource, string harnessClass = "Harness") {
    var dir = Path.Combine(Path.GetTempPath(), "polypiler-jvm-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var classpathEntries = new List<string> { dir };
      foreach (var file in files) {
        var fullPath = Path.Combine(dir, file.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, file.GetBytes());
        if (file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
          classpathEntries.Add(fullPath);
      }

      var harnessFile = Path.Combine(dir, harnessClass + ".java");
      File.WriteAllText(harnessFile, harnessSource);
      var cp = string.Join(Path.PathSeparator, classpathEntries);

      var compile = RunProcess("javac", ["-cp", cp, harnessFile], dir);
      Assert.That(compile.ExitCode, Is.EqualTo(0), $"javac failed:\nSTDOUT:\n{compile.Stdout}\nSTDERR:\n{compile.Stderr}");

      var run = RunProcess("java", ["-Xverify:all", "-cp", cp, harnessClass], dir);
      Assert.That(run.ExitCode, Is.EqualTo(0), $"java -Xverify:all failed (exit {run.ExitCode}):\nSTDOUT:\n{run.Stdout}\nSTDERR:\n{run.Stderr}");
      return run.Stdout.Replace("\r\n", "\n").TrimEnd('\n');
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

  /// <summary>Executes an emitted executable <c>.jar</c> directly via <c>java -Xverify:all -jar</c>.</summary>
  public static (int ExitCode, string Stdout, string Stderr) RunJar(GeneratedFile jarFile, params string[] args) {
    var dir = Path.Combine(Path.GetTempPath(), "polypiler-jvm-jar-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var jarPath = Path.Combine(dir, jarFile.Path);
      File.WriteAllBytes(jarPath, jarFile.GetBytes());
      return RunProcess("java", ["-Xverify:all", "-jar", jarPath, .. args], dir);
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

  private static string FindJdkTool(string tool) {
    var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
    foreach (var envVar in new[] { "JAVA_HOME_21_X64", "JAVA_HOME_21_ARM64", "JAVA_HOME_25_X64", "JAVA_HOME" }) {
      var home = Environment.GetEnvironmentVariable(envVar);
      if (string.IsNullOrWhiteSpace(home))
        continue;
      var candidate = Path.Combine(home, "bin", exe);
      if (File.Exists(candidate))
        return candidate;
    }

    return tool;
  }

  private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, IReadOnlyList<string> arguments, string workingDir) {
    var psi = new ProcessStartInfo(FindJdkTool(fileName)) {
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
