using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Msil;

/// <summary>Helpers for building IR modules, loading emitted MSIL assemblies in an isolated ALC, and running console executables.</summary>
internal static class MsilTestHarness {

  public static IrLocal L(string name, IrType type) => new(name, type);

  public static IrLocalRef R(IrLocal local) => new(local);

  public static IrConst C(IrType type, long value) => new(type, value);

  public static IrConst I(int value) => new(IrType.I32, value);

  public static IrConst I64(long value) => new(IrType.I64, value);

  public static IrConst U32(uint value) => new(IrType.U32, value);

  public static IrConst U64(ulong value) => new(IrType.U64, unchecked((long)value));

  public static IrConst F32(float value) => new(IrType.F32, IrOperators.Bits(IrType.F32, value));

  public static IrConst F64(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  public static IrConst B(bool value) => new(IrType.Bool, value ? 1 : 0);

  public static IrBinary Bin(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  public static IrBlock Block(params IrStmt[] statements) => new(statements);

  /// <summary>Builds a public IR function collecting every declared local automatically.</summary>
  public static IrFunction Function(string name, IrType result, IrLocal[] parameters, params IrStmt[] body)
    => Function(name, true, result, parameters, body);

  /// <summary>Builds an IR function with explicit visibility collecting every declared local automatically.</summary>
  public static IrFunction Function(string name, bool isPublic, IrType result, IrLocal[] parameters, params IrStmt[] body) {
    var declared = new List<IrLocal>();
    void AddLocal(IrLocal? local) {
      if (local is not null && !declared.Contains(local))
        declared.Add(local);
    }

    void Collect(IrStmt stmt) {
      switch (stmt) {
        case IrDeclare d:
          AddLocal(d.Local);
          break;
        case IrBlock b:
          foreach (var child in b.Statements)
            Collect(child);
          break;
        case IrIf i:
          Collect(i.Then);
          if (i.Else is not null)
            Collect(i.Else);
          break;
        case IrWhile w:
          Collect(w.Body);
          break;
        case IrDoWhile dw:
          Collect(dw.Body);
          break;
        case IrFor f:
          Collect(f.Body);
          Collect(f.Step);
          break;
        case IrSwitch sw:
          foreach (var s in sw.Sections)
            Collect(s.Body);
          break;
        case IrTry t:
          Collect(t.Body);
          foreach (var c in t.Catches) {
            AddLocal(c.Local);
            Collect(c.Body);
          }
          if (t.Finally is not null)
            Collect(t.Finally);
          break;
        case IrForEachCollection fe:
          AddLocal(fe.Element);
          AddLocal(fe.Value);
          Collect(fe.Body);
          break;
        case IrForEachSequence fes:
          AddLocal(fes.Element);
          Collect(fes.Body);
          break;
        case IrUsing u:
          AddLocal(u.Resource);
          Collect(u.Body);
          break;
      }
    }

    foreach (var stmt in body)
      Collect(stmt);
    return new IrFunction(name, isPublic, result, parameters, declared, new IrBlock(body));
  }

  /// <summary>A module with a single public function <c>f</c> returning one expression.</summary>
  public static IrModule Returning(IrType result, IrLocal[] parameters, IrExpr value)
    => new("M", [Function("f", result, parameters, new IrReturn(value))]);

  /// <summary>Legalizes a module against the precomputed library implementations.</summary>
  public static IrModule Legalize(IrModule module, MsilProfile? profile = null, FloatMode mode = FloatMode.Accurate)
    => MsilEmitter.Legalize(module, () => PrecomputedImplementations.Module, profile, mode);

  /// <summary>Emits <paramref name="module"/>, loads it into a collectible <see cref="AssemblyLoadContext"/>, invokes <paramref name="methodName"/>, and unloads.</summary>
  public static object? Invoke(IrModule module, string methodName, params object?[] args)
    => Invoke(module, null, null, methodName, args);

  /// <summary>Emits <paramref name="module"/> with options, loads it into a collectible <see cref="AssemblyLoadContext"/>, invokes <paramref name="methodName"/>, and unloads.</summary>
  public static object? Invoke(IrModule module, MsilProfile? profile, MsilEmitOptions? options, string methodName, params object?[] args) {
    var files = MsilEmitter.Emit(module, profile, options);
    var dll = files[0];
    Assert.That(dll.BinaryContent, Is.Not.Null);
    var alc = new AssemblyLoadContext("MsilTest-" + Guid.NewGuid().ToString("N"), isCollectible: true);
    try {
      using var stream = new MemoryStream(dll.BinaryContent!);
      var assembly = alc.LoadFromStream(stream);
      var type = assembly.GetType(module.Name) ?? assembly.GetTypes()[0];
      var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
      Assert.That(method, Is.Not.Null, $"Method '{methodName}' not found on '{type.FullName}'.");
      try {
        return method!.Invoke(null, args);
      } catch (TargetInvocationException ex) when (ex.InnerException is not null) {
        throw ex.InnerException;
      }
    } finally {
      alc.Unload();
    }
  }

  /// <summary>Emits <paramref name="module"/> and executes a callback against the loaded <see cref="Assembly"/> inside a collectible ALC.</summary>
  public static void WithLoadedAssembly(IrModule module, Action<Assembly> action, MsilProfile? profile = null, MsilEmitOptions? options = null) {
    var files = MsilEmitter.Emit(module, profile, options);
    var alc = new AssemblyLoadContext("MsilTest-" + Guid.NewGuid().ToString("N"), isCollectible: true);
    try {
      using var stream = new MemoryStream(files[0].BinaryContent!);
      action(alc.LoadFromStream(stream));
    } finally {
      alc.Unload();
    }
  }

  /// <summary>Compares the result of invoking the emitted MSIL method against <see cref="IrInterpreter.Call"/>.</summary>
  public static void AssertMatchesInterpreter(IrModule module, string functionName, params object[] args) {
    var interpArgs = CloneArgs(args);
    var msilArgs = CloneArgs(args);
    var expected = IrInterpreter.Call(module, functionName, interpArgs);
    var actual = Invoke(module, functionName, msilArgs);
    Assert.That(actual, Is.EqualTo(expected));
  }

  private static object[] CloneArgs(object[] args) {
    var copy = new object[args.Length];
    for (var i = 0; i < args.Length; i++)
      copy[i] = args[i] is Array arr ? arr.Clone() : args[i];
    return copy;
  }

  /// <summary>Writes emitted ConsoleExe files to a temporary directory and runs <c>dotnet &lt;asm&gt;.dll</c>.</summary>
  public static (int ExitCode, string StdOut, string StdErr) RunConsoleExe(IReadOnlyList<GeneratedFile> files, params string[] cliArgs) {
    var dir = Path.Combine(Path.GetTempPath(), "polypiler-msil-exe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      string? dllPath = null;
      foreach (var file in files) {
        var fullPath = Path.Combine(dir, file.Path);
        File.WriteAllBytes(fullPath, file.GetBytes());
        if (file.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
          dllPath = fullPath;
      }
      Assert.That(dllPath, Is.Not.Null);
      var psi = new ProcessStartInfo("dotnet") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      };
      psi.ArgumentList.Add(dllPath!);
      foreach (var arg in cliArgs)
        psi.ArgumentList.Add(arg);
      using var process = Process.Start(psi)!;
      var stdout = process.StandardOutput.ReadToEnd();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit(15000);
      return (process.ExitCode, stdout, stderr);
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

}
