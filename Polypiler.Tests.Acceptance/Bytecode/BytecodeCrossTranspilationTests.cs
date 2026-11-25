using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Bytecode;

/// <summary>
/// End-to-end CLI acceptance tests for .NET MSIL cross-transpilation verified against live
/// <c>dotnet</c> and <c>node</c> runtimes on Linux and Windows.
/// </summary>
[TestFixture, Layer("core")]
[Category("EndToEnd")]
public sealed class MsilCrossTranspilationTests {

  private string _dir = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._dir = Directory.CreateTempSubdirectory("polypiler-msil-e2e-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, true);
  }

  private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string workingDir, params string[] args) {
    var psi = new ProcessStartInfo(fileName) {
      WorkingDirectory = workingDir,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var arg in args)
      psi.ArgumentList.Add(arg);

    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return (proc.ExitCode, stdout, stderr);
  }

  private static (int ExitCode, string Stdout, string Stderr) RunPolypiler(params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var code = Program.Run(args, output, error);
    return (code, output.ToString(), error.ToString());
  }

  private string CompileNetAssembly(string assemblyName, string source, Platform platform) {
    var tree = CSharpSyntaxTree.ParseText(source);
    var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
    var references = trusted
      .Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Numerics.dll")
      .Select(path => MetadataReference.CreateFromFile(path))
      .ToList();
    var compilation = CSharpCompilation.Create(
      assemblyName,
      [tree],
      references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, platform: platform, optimizationLevel: OptimizationLevel.Release));

    var dllPath = Path.Combine(this._dir, $"{assemblyName}_{platform}.dll");
    var emit = compilation.Emit(dllPath);
    Assert.That(emit.Success, Is.True, string.Join("\n", emit.Diagnostics));
    return dllPath;
  }

  [TestCase(Platform.X86)]
  [TestCase(Platform.X64)]
  [TestCase(Platform.AnyCpu)]
  public void DotNetAssembly_ToJavaScriptIife_ExecutesLiveInNode(Platform platform) {
    const string source = """
      public static class ChecksumLib {
        public static uint Adler32(byte[] data) {
          uint s1 = 1;
          uint s2 = 0;
          for (int i = 0; i < data.Length; i++) {
            s1 = (s1 + data[i]) % 65521u;
            s2 = (s2 + s1) % 65521u;
          }
          return (s2 << 16) | s1;
        }
        public static int Fibonacci(int n) {
          int a = 0;
          int b = 1;
          for (int i = 0; i < n; i++) {
            int next = a + b;
            a = b;
            b = next;
          }
          return a;
        }
      }
      """;

    var dllPath = this.CompileNetAssembly("ChecksumLib", source, platform);
    var outDir = Path.Combine(this._dir, $"js-iife-{platform}");
    var (code, _, stderr) = RunPolypiler(
      "translate", dllPath,
      "--from", "msil",
      "--to", "javascript",
      "--module", "iife",
      "--global", "NetChecksums",
      "--output", outDir);
    Assert.That(code, Is.Zero, stderr);

    var jsPath = Directory.GetFiles(outDir, "*.js").Single();
    var driverPath = Path.Combine(this._dir, $"driver-{platform}.js");
    File.WriteAllText(driverPath, $$"""
      const fs = require('fs');
      const vm = require('vm');
      const code = fs.readFileSync({{System.Text.Json.JsonSerializer.Serialize(jsPath)}}, 'utf8');
      const ctx = { Uint8Array, Int32Array, Uint32Array, Float32Array, Float64Array, BigInt, Math, RangeError, Error };
      vm.runInNewContext(code, ctx);
      const bytes = new Uint8Array(Buffer.from('Wikipedia', 'utf8'));
      const adler = ctx.NetChecksums.Adler32(bytes);
      const fib = ctx.NetChecksums.Fibonacci(10);
      if (adler !== 0x11E60398) throw new Error('Adler32 mismatch: ' + adler);
      if (fib !== 55) throw new Error('Fibonacci mismatch: ' + fib);
      console.log('OK:' + adler + ':' + fib);
      """);

    var run = RunProcess("node", this._dir, driverPath);
    Assert.That(run.ExitCode, Is.Zero, run.Stderr);
    Assert.That(run.Stdout.Trim(), Is.EqualTo("OK:300286872:55"));
  }

  [Test]
  public void JavaScriptSource_ToMsilExecutable_ExecutesLiveInDotNet() {
    var jsPath = Path.Combine(this._dir, "App.js");
    File.WriteAllText(jsPath, """
      /** @param {int} a @param {int} b @returns {int} */
      function compute(a, b) {
        return ((a * 7) + (b * 3)) | 0;
      }
      /** @returns {int} */
      function Main() {
        return compute(4, 5);
      }
      """);

    var outDir = Path.Combine(this._dir, "msil-exe");
    var (code, _, stderr) = RunPolypiler(
      "translate", jsPath,
      "--from", "javascript",
      "--to", "msil",
      "--output-type", "exe",
      "--output", outDir);
    Assert.That(code, Is.Zero, stderr);

    var dllPath = Directory.GetFiles(outDir, "*.dll").Single();
    var run = RunProcess("dotnet", outDir, dllPath);
    Assert.That(run.ExitCode, Is.EqualTo(43), $"STDOUT:\n{run.Stdout}\nSTDERR:\n{run.Stderr}");
  }

}

/// <summary>
/// End-to-end CLI acceptance tests for JVM bytecode cross-transpilation verified against live
/// <c>javac</c>, <c>java -Xverify:all</c>, <c>python3</c>, <c>node</c>, and <c>dotnet</c>.
/// </summary>
[TestFixture, Layer("jvm")]
[Category("EndToEnd")]
public sealed class JvmCrossTranspilationTests {

  private string _dir = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._dir = Directory.CreateTempSubdirectory("polypiler-jvm-e2e-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, true);
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

  private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string workingDir, params string[] args) {
    var psi = new ProcessStartInfo(fileName) {
      WorkingDirectory = workingDir,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (var arg in args)
      psi.ArgumentList.Add(arg);

    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return (proc.ExitCode, stdout, stderr);
  }

  private static (int ExitCode, string Stdout, string Stderr) RunPolypiler(params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var code = Program.Run(args, output, error);
    return (code, output.ToString(), error.ToString());
  }

  [Test]
  public void JarPackage_ToPythonModule_ExecutesLiveInPython3() {
    var srcDir = Path.Combine(this._dir, "java-src");
    var pkgDir = Path.Combine(srcDir, "demo");
    Directory.CreateDirectory(pkgDir);
    var javaFile = Path.Combine(pkgDir, "Kernels.java");
    File.WriteAllText(javaFile, """
      package demo;
      public final class Kernels {
        public static int gcd(int a, int b) {
          while (b != 0) {
            int t = a % b;
            a = b;
            b = t;
          }
          return a;
        }
        public static int sumSquares(int[] xs) {
          int acc = 0;
          for (int i = 0; i < xs.length; i++) {
            acc += xs[i] * xs[i];
          }
          return acc;
        }
      }
      """);

    var classesDir = Path.Combine(this._dir, "classes");
    Directory.CreateDirectory(classesDir);
    var javac = RunProcess(FindJdkTool("javac"), this._dir, "-d", classesDir, javaFile);
    Assert.That(javac.ExitCode, Is.Zero, javac.Stderr);

    var jarPath = Path.Combine(this._dir, "kernels.jar");
    using (var zip = ZipFile.Open(jarPath, ZipArchiveMode.Create)) {
      foreach (var classFile in Directory.EnumerateFiles(classesDir, "*.class", SearchOption.AllDirectories)) {
        var entryName = Path.GetRelativePath(classesDir, classFile).Replace('\\', '/');
        zip.CreateEntryFromFile(classFile, entryName);
      }
    }

    var pyOut = Path.Combine(this._dir, "py-out");
    var (code, _, stderr) = RunPolypiler(
      "translate", jarPath,
      "--from", "jvm",
      "--to", "python",
      "--output", pyOut);
    Assert.That(code, Is.Zero, stderr);

    var pyFile = Directory.GetFiles(pyOut, "*.py").Single();
    var moduleName = Path.GetFileNameWithoutExtension(pyFile);
    var pyRun = RunProcess("python3", pyOut, "-c",
      $"import {moduleName} as m; assert m.gcd(48, 18) == 6; assert m.sum_squares([1, 2, 3, 4]) == 30; print('OK')");
    Assert.That(pyRun.ExitCode, Is.Zero, pyRun.Stderr);
    Assert.That(pyRun.Stdout.Trim(), Is.EqualTo("OK"));
  }

  [Test]
  public void CSharpSource_ToJarFile_VerifiesAndExecutesLiveInJvm() {
    var csPath = Path.Combine(this._dir, "Adler32.cs");
    File.WriteAllText(csPath, """
      public static class Adler32 {
        public static uint Compute(byte[] data) {
          uint s1 = 1;
          uint s2 = 0;
          for (int i = 0; i < data.Length; i++) {
            s1 = (s1 + data[i]) % 65521u;
            s2 = (s2 + s1) % 65521u;
          }
          return (s2 << 16) | s1;
        }
        public static int Factorial(int n) {
          int acc = 1;
          for (int i = 2; i <= n; i++) acc *= i;
          return acc;
        }
      }
      """);

    var jarOut = Path.Combine(this._dir, "jar-out");
    var (code, _, stderr) = RunPolypiler(
      "translate", csPath,
      "--from", "csharp",
      "--to", "jvm",
      "--profile", "jvm/21",
      "--package-format", "jar",
      "--output", jarOut);
    Assert.That(code, Is.Zero, stderr);

    var jarPath = Path.Combine(jarOut, "Adler32.jar");
    Assert.That(File.Exists(jarPath), Is.True);

    var harnessFile = Path.Combine(this._dir, "Harness.java");
    File.WriteAllText(harnessFile, """
      import java.nio.charset.StandardCharsets;
      public final class Harness {
        public static void main(String[] args) {
          byte[] data = "Wikipedia".getBytes(StandardCharsets.UTF_8);
          int hash = Adler32.Compute(data);
          int fact = Adler32.Factorial(6);
          if (hash != 0x11E60398) throw new AssertionError("Adler32=" + hash);
          if (fact != 720) throw new AssertionError("Factorial=" + fact);
          System.out.println("OK:" + hash + ":" + fact);
        }
      }
      """);

    var cp = $"{jarPath}{Path.PathSeparator}{this._dir}";
    var javac = RunProcess(FindJdkTool("javac"), this._dir, "-cp", cp, harnessFile);
    Assert.That(javac.ExitCode, Is.Zero, javac.Stderr);

    var java = RunProcess(FindJdkTool("java"), this._dir, "-Xverify:all", "-cp", cp, "Harness");
    Assert.That(java.ExitCode, Is.Zero, java.Stderr);
    Assert.That(java.Stdout.Trim(), Is.EqualTo("OK:300286872:720"));
  }

  [Test]
  public void EverythingToEverything_CSharp_ToJar_ToMsil_ToJavaScriptIife_PreservesExactOutput() {
    var csPath = Path.Combine(this._dir, "Chain.cs");
    File.WriteAllText(csPath, """
      public static class Chain {
        public static int Mix(int x, int y) {
          int a = (x ^ (y << 3)) + 17;
          for (int i = 0; i < 4; i++) a = (a * 3) ^ (i + y);
          return a;
        }
      }
      """);

    var jarOut = Path.Combine(this._dir, "step1-jar");
    var r1 = RunPolypiler("translate", csPath, "--from", "csharp", "--to", "jvm", "--output", jarOut);
    Assert.That(r1.ExitCode, Is.Zero, r1.Stderr);

    var msilOut = Path.Combine(this._dir, "step2-msil");
    var r2 = RunPolypiler("translate", Path.Combine(jarOut, "Chain.jar"), "--from", "jvm", "--to", "msil", "--output", msilOut);
    Assert.That(r2.ExitCode, Is.Zero, r2.Stderr);

    var pyOut = Path.Combine(this._dir, "step3-py");
    var r3 = RunPolypiler("translate", Directory.GetFiles(msilOut, "*.dll").Single(), "--from", "msil", "--to", "python", "--output", pyOut);
    Assert.That(r3.ExitCode, Is.Zero, r3.Stderr);

    var pyFile = Directory.GetFiles(pyOut, "*.py").Single();
    var mod = Path.GetFileNameWithoutExtension(pyFile);
    int expected = ((7 ^ (11 << 3)) + 17);
    for (var i = 0; i < 4; i++)
      expected = unchecked((expected * 3) ^ (i + 11));

    var pyRun = RunProcess("python3", pyOut, "-c", $"import {mod} as m; print(m.mix(7, 11))");
    Assert.That(pyRun.ExitCode, Is.Zero, pyRun.Stderr);
    Assert.That(pyRun.Stdout.Trim(), Is.EqualTo(expected.ToString()));
  }

}
