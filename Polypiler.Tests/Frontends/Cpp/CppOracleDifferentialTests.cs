using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Cpp;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Cpp;

/// <summary>
/// Differential oracle tests: compiles real C++20 kernels with host <c>/usr/bin/g++ -std=c++20</c>,
/// executes the native binary, and asserts exact agreement with <see cref="IrInterpreter"/> on the
/// module imported by <see cref="CppFrontend"/>.
/// </summary>
[TestFixture]
public sealed class CppOracleDifferentialTests {

  private static readonly Lazy<string?> _compiler = new(FindCompiler);

  private static string? FindCompiler() {
    if (File.Exists("/usr/bin/g++"))
      return "/usr/bin/g++";

    foreach (var candidate in new[] { "g++", "clang++" }) {
      try {
        var check = RunProcess(candidate, "--version");
        if (check.ExitCode == 0)
          return candidate;
      } catch {
      }
    }

    return null;
  }

  private static string RunGppOracle(string kernelSource, string mainBody) {
    var compiler = _compiler.Value;
    if (compiler is null) {
      Assert.Ignore("No C++ compiler (g++/clang++) found on this runner.");
      return "";
    }

    var dir = Path.Combine(Path.GetTempPath(), $"polypiler-cpp-oracle-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var cppPath = Path.Combine(dir, "oracle.cpp");
      var binPath = Path.Combine(dir, OperatingSystem.IsWindows() ? "oracle.exe" : "oracle");
      var fullSource = $$"""
        #include <iostream>
        #include <iomanip>
        {{kernelSource}}

        int main() {
        {{mainBody}}
          return 0;
        }
        """;
      File.WriteAllText(cppPath, fullSource);

      var compile = RunProcess(compiler, $"-std=c++20 -O2 \"{cppPath}\" -o \"{binPath}\"");
      Assert.That(compile.ExitCode, Is.EqualTo(0), () => $"g++ failed:\n{compile.Stderr}");

      var exec = RunProcess(binPath, "");
      Assert.That(exec.ExitCode, Is.EqualTo(0), () => $"oracle binary failed:\n{exec.Stderr}");
      return exec.Stdout.Trim();
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

  private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string arguments) {
    using var proc = new Process {
      StartInfo = new ProcessStartInfo {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      }
    };
    proc.Start();
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return (proc.ExitCode, stdout, stderr);
  }

  [Test, Category("ExternalInterop")]
  public void NumericKernels_Adler32_Xor_BitPacking_MatchGppOracle() {
    const string kernel = """
      #include <bit>
      #include <cstdint>
      #include <cstddef>
      #include <span>

      uint32_t adler32(std::span<const uint8_t> data) {
        uint32_t a = 1u;
        uint32_t b = 0u;
        for (std::size_t i = 0; i < data.size(); ++i) {
          a = (a + static_cast<uint32_t>(data[i])) % 65521u;
          b = (b + a) % 65521u;
        }
        return (b << 16) | a;
      }

      uint32_t xor_fold(std::span<const uint8_t> data, uint32_t seed) {
        uint32_t acc = seed;
        for (auto b : data) {
          acc = std::rotl(acc ^ static_cast<uint32_t>(b), 5);
        }
        return acc;
      }

      uint32_t bit_pack_metrics(uint32_t x) {
        uint32_t r = std::rotr(x, 7);
        int lz = std::countl_zero(r);
        int tz = std::countr_zero(r);
        int pc = std::popcount(r);
        uint32_t fbits = std::bit_cast<uint32_t>(static_cast<float>(pc));
        return r ^ static_cast<uint32_t>(lz + tz * 31) ^ fbits;
      }
      """;

    var imported = CppFrontend.Import("kernels.cpp", kernel);
    Assert.That(imported.Diagnostics, Is.Empty, () => string.Join("\n", imported.Diagnostics));
    Assert.That(IrVerifier.Verify(imported.Module!), Is.Empty);

    var inputBytes = Encoding.ASCII.GetBytes("Polypiler C++20 Oracle!");
    var irAdler = (uint)IrInterpreter.Call(imported.Module!, "adler32", [inputBytes])!;
    var irXor = (uint)IrInterpreter.Call(imported.Module!, "xor_fold", [inputBytes, 0xDEADBEEFu])!;
    var irBits = (uint)IrInterpreter.Call(imported.Module!, "bit_pack_metrics", [0xA55A1234u])!;

    var oracleOut = RunGppOracle(kernel, """
        const uint8_t msg[] = "Polypiler C++20 Oracle!";
        std::span<const uint8_t> sp(msg, 23);
        std::cout << adler32(sp) << "," << xor_fold(sp, 0xDEADBEEFu) << "," << bit_pack_metrics(0xA55A1234u) << "\n";
      """);
    Assert.That($"{irAdler},{irXor},{irBits}", Is.EqualTo(oracleOut));
  }

  [Test, Category("ExternalInterop")]
  public void Structs_Enums_Optionals_Exceptions_Floats_Strings_MatchGppOracle() {
    const string kernel = """
      #include <array>
      #include <cmath>
      #include <cstdint>
      #include <optional>
      #include <stdexcept>
      #include <string>
      #include <algorithm>

      enum class Op : uint8_t { Add = 1, Sub = 2, Mul = 3 };

      struct Vec2 {
        int x;
        int y;
        bool operator==(const Vec2&) const = default;
      };

      template<typename T>
      T clamp_add(T a, T b, T lo, T hi) {
        return std::clamp(a + b, lo, hi);
      }

      int eval_combo(Vec2 a, Vec2 b, Op op) {
        if (a == b) return 0;
        switch (op) {
          case Op::Add: return clamp_add(a.x + b.x, a.y + b.y, -100, 100);
          case Op::Sub: return (a.x - b.x) + (a.y - b.y);
          default: return (a.x * b.x) + (a.y * b.y);
        }
      }

      int opt_and_except(int x) {
        std::optional<int> v = std::nullopt;
        if (x > 0) v = x * 3;
        try {
          if (!v.has_value()) throw std::runtime_error("empty");
          return v.value() + 1;
        } catch (const std::runtime_error&) {
          return v.value_or(-7);
        }
      }

      int float_kernel(double x) {
        double v = std::sqrt(std::abs(x)) + std::floor(x) + std::ceil(x) + std::trunc(x) + std::round(x);
        v += std::copysign(1.5, x);
        int cls = (std::isfinite(v) ? 1 : 0) + (std::isnormal(v) ? 2 : 0);
        return static_cast<int>(v * 100.0) + cls;
      }

      int string_kernel(std::string s) {
        std::string full = s + "_tail";
        int r = static_cast<int>(full.size());
        if (full.starts_with("abc")) r += 10;
        if (full.ends_with("tail")) r += 20;
        if (full.find("_") == 3) r += 30;
        return r;
      }
      """;

    var imported = CppFrontend.Import("combo.cpp", kernel);
    Assert.That(imported.Diagnostics, Is.Empty, () => string.Join("\n", imported.Diagnostics));
    Assert.That(IrVerifier.Verify(imported.Module!), Is.Empty);

    var irCombo1 = (int)IrInterpreter.Call(imported.Module!, "eval_combo", [new object[] { 10, 20 }, new object[] { 3, 4 }, (byte)1])!;
    var irCombo2 = (int)IrInterpreter.Call(imported.Module!, "eval_combo", [new object[] { 5, 5 }, new object[] { 5, 5 }, (byte)3])!;
    var irOpt1 = (int)IrInterpreter.Call(imported.Module!, "opt_and_except", [4])!;
    var irOpt2 = (int)IrInterpreter.Call(imported.Module!, "opt_and_except", [-2])!;
    var irFloat = (int)IrInterpreter.Call(imported.Module!, "float_kernel", [-6.25])!;
    var irStr = (int)IrInterpreter.Call(imported.Module!, "string_kernel", ["abc"])!;

    var oracleOut = RunGppOracle(kernel, """
        std::cout
          << eval_combo(Vec2{10, 20}, Vec2{3, 4}, Op::Add) << ","
          << eval_combo(Vec2{5, 5}, Vec2{5, 5}, Op::Mul) << ","
          << opt_and_except(4) << ","
          << opt_and_except(-2) << ","
          << float_kernel(-6.25) << ","
          << string_kernel("abc") << "\n";
      """);
    var irResult = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5}", irCombo1, irCombo2, irOpt1, irOpt2, irFloat, irStr);
    Assert.That(irResult, Is.EqualTo(oracleOut));
  }

}
