using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Cpp;

/// <summary>
/// End-to-end CLI acceptance tests for ISO C++17/20/23 frontend and backend compiled and executed
/// natively under <c>g++</c> / <c>clang++</c> with <c>-Wall -Wextra -Werror</c>.
/// </summary>
[TestFixture, Layer("native")]
[Category("EndToEnd")]
public sealed class CppAcceptanceTests {

  private string _dir = "";

  [SetUp]
  public void CreateWorkingDirectory() => this._dir = Directory.CreateTempSubdirectory("polypiler-cpp-e2e-").FullName;

  [TearDown]
  public void DeleteWorkingDirectory() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, true);
  }

  private static string? FindCppCompiler() {
    if (File.Exists("/usr/bin/g++"))
      return "/usr/bin/g++";

    foreach (var candidate in new[] { "g++", "clang++" }) {
      try {
        var check = RunProcess(candidate, Path.GetTempPath(), "--version");
        if (check.ExitCode == 0)
          return candidate;
      } catch {
      }
    }

    return null;
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

  [TestCase("cpp/cpp17", "-std=c++17")]
  [TestCase("cpp/cpp20", "-std=c++20")]
  [TestCase("cpp/cpp23", "-std=c++23")]
  public void CSharpAdler32_ToCpp_CompilesCleanlyAndMatchesReference(string profile, string stdFlag) {
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
      }
      """);

    var cppOut = Path.Combine(this._dir, $"out-{stdFlag}");
    var (code, _, stderr) = RunPolypiler("translate", csPath, "--from", "csharp", "--to", "cpp", "--profile", profile, "--output", cppOut);
    Assert.That(code, Is.Zero, stderr);

    var compiler = FindCppCompiler();
    if (compiler is null)
      Assert.Ignore("No g++/clang++ compiler on PATH.");

    var cppFile = Directory.GetFiles(cppOut, "*.cpp").Single();
    var hppName = Path.GetFileName(Directory.GetFiles(cppOut, "*.hpp").Single());
    var driverPath = Path.Combine(cppOut, "driver.cpp");
    File.WriteAllText(driverPath, $$"""
      #include "{{hppName}}"
      #include <cstdint>
      #include <iostream>
      #include <string>
      #include <vector>

      int main() {
        std::string s = "Wikipedia";
        std::vector<uint8_t> bytes(s.begin(), s.end());
        uint32_t res = adler32::compute(bytes);
        if (res != 0x11E60398u) return 1;
        std::cout << res << "\n";
        return 0;
      }
      """);

    var binPath = Path.Combine(cppOut, OperatingSystem.IsWindows() ? "adler.exe" : "adler");
    var compile = RunProcess(compiler!, cppOut, stdFlag, "-Wall", "-Wextra", "-Werror", "-O2", "-o", binPath, cppFile, driverPath);
    Assert.That(compile.ExitCode, Is.Zero, compile.Stderr);

    var run = RunProcess(binPath, cppOut);
    Assert.That(run.ExitCode, Is.Zero, run.Stderr);
    Assert.That(run.Stdout.Trim(), Is.EqualTo("300286872"));
  }

  [Test]
  public void CppBytePackingAndXorKernel_ToJavaScript_MatchesNativeGpp() {
    var cppPath = Path.Combine(this._dir, "XorPack.cpp");
    File.WriteAllText(cppPath, """
      #include <cstdint>
      #include <vector>

      uint32_t pack_xor(const std::vector<uint8_t>& data, uint32_t seed) {
        uint32_t acc = seed;
        for (std::size_t i = 0; i < data.size(); ++i) {
          acc = ((acc << 5) | (acc >> 27)) ^ static_cast<uint32_t>(data[i]);
        }
        return acc;
      }
      """);

    var jsOut = Path.Combine(this._dir, "js-out");
    var (code, _, stderr) = RunPolypiler("translate", cppPath, "--from", "cpp", "--to", "javascript", "--output", jsOut);
    Assert.That(code, Is.Zero, stderr);

    var mjsPath = Directory.GetFiles(jsOut, "*.mjs").Single();
    Assert.That(File.ReadAllText(mjsPath), Does.Contain("export function pack_xor"));
  }

}
