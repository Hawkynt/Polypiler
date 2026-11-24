using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Batch;

[TestFixture]
public sealed class BatchTranslationTests {

  [Test]
  public void TranslatesTheCountdownToStandaloneBatch() {
    var fixture = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "countdown.cs");
    var script = Emit(File.ReadAllText(fixture));
    Assert.That(script, Does.Contain("setlocal EnableExtensions DisableDelayedExpansion").And.Contain("exit /b"));
  }

  [TestCase("int sum = 0; for (int i = 0; i < 4; i += 1) { if (i == 0) continue; if (i != 2) sum += i; else break; } return -sum;", " NEQ ")]
  [TestCase("int n = 0; do { n += 1; } while (n >= 2); return n;", " GEQ ")]
  [TestCase("int n = int.MinValue; if (n > -1) return 42; if (n <= -1) return n; return 0;", " LEQ ")]
  [TestCase("while (true) { break; } return 1;", "goto ")]
  public void EmitsClosedControlFlowUsingOnlyBuiltIns(string body, string expected) {
    var script = Emit($"public static class Kernel {{ public static int Run() {{ {body} }} }}");
    Assert.That(script, Does.Contain(expected));
    foreach (var line in script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)) {
      Assert.That(line, Does.StartWith(":").Or.EqualTo("@echo off").Or.StartWith("setlocal ")
        .Or.StartWith("set /a ").Or.StartWith("if ").Or.StartWith("goto ").Or.StartWith("exit /b "));
      Assert.That(line.IndexOfAny(['\r', '\n']), Is.EqualTo(-1));
    }
  }

  [Test]
  public void SequentialExpressionsReuseTheirTemporaryVariables() {
    var shortKernel = AssignedVariables(EmitUpdates(4));
    var longKernel = AssignedVariables(EmitUpdates(512));
    Assert.That(longKernel, Has.Length.EqualTo(shortKernel.Length));
    Assert.That(longKernel, Is.EquivalentTo(shortKernel));

    static string EmitUpdates(int count) => Emit("public static class Kernel { public static int Run() { int x = 1, y = 2;"
      + string.Concat(Enumerable.Repeat("x = (x + 1) + (y - 1);", count))
      + "if ((x - 1) * (x + 1) >= (x + 2) * (x - 2)) return -x; return x; } }");

    static string[] AssignedVariables(string script) => script.Split("\r\n")
      .Where(line => line.StartsWith("set /a \"", StringComparison.Ordinal))
      .Select(line => line[..line.IndexOf('=')]).Distinct(StringComparer.Ordinal).ToArray();
  }

  [TestCase("public static long Run() => 1L;")]
  [TestCase("public static int Run(int value) => value;")]
  [TestCase("public static int Run() { int x = 1; return checked(x + 1); }")]
  [TestCase("public static int Run() { int x = 5; return x / 2; }")]
  [TestCase("public static int Run() { int x = 5; return System.Math.Abs(x); }")]
  [TestCase("public static int Run() { bool x = true; return x ? 1 : 0; }")]
  public void RefusesConstructsOutsideTheProfile(string method) {
    var (status, error, script) = Translate($"public static class Kernel {{ {method} }}");
    Assert.That(status, Is.EqualTo(TranslateCommand.UnsupportedConstruct), error);
    Assert.That(error, Does.Contain("PLY2850"));
    Assert.That(script, Is.Null);
  }

  private static string Emit(string source) {
    var (status, error, bytes) = Translate(source);
    Assert.That(status, Is.Zero, error);
    Assert.That(bytes, Is.Not.Null);
    Assert.That(bytes!.All(value => value < 128), Is.True);
    var script = Encoding.ASCII.GetString(bytes!);
    Assert.That(script, Does.EndWith("\r\n"));
    return script;
  }

  private static (int Status, string Error, byte[]? Script) Translate(string source) {
    var workspace = Directory.CreateTempSubdirectory();
    try {
      var fixture = Path.Combine(workspace.FullName, "Kernel.cs");
      File.WriteAllText(fixture, source);
      var output = Path.Combine(workspace.FullName, "out");
      using var error = new StringWriter();
      var status = Program.Run(["translate", fixture, "--from", "csharp", "--to", "batch", "--profile", "cmd",
        "--opt", "none", "--output", output], TextWriter.Null, error);
      var script = Directory.Exists(output) ? Directory.EnumerateFiles(output, "*.bat").SingleOrDefault() : null;
      return (status, error.ToString(), script is null ? null : File.ReadAllBytes(script));
    } finally {
      workspace.Delete(recursive: true);
    }
  }

}
