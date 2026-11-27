using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Batch;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Batch;

[TestFixture]
public sealed class BatchTranslationTests {

  private static string BatchFixture(params string[] parts)
    => Path.Combine([PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", .. parts]);

  [Test]
  public void TranslatesTheCountdownToStandaloneBatch() {
    var script = Emit(File.ReadAllText(BatchFixture("countdown.cs")));
    Assert.That(script, Does.Contain("setlocal EnableExtensions DisableDelayedExpansion").And.Contain("exit /b"));
  }

  [Test]
  public void TranslatesBitwiseFixtureUsingBuiltIns() {
    var script = Emit(File.ReadAllText(BatchFixture("bitwise.cs")));
    Assert.That(script, Does.Contain("&31").And.Contain("~").And.Contain("|").And.Contain("^"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void TranslatesThePinnedMapWithoutEmbeddingKeyText(bool optimize) {
    var bytes = File.ReadAllBytes(BatchFixture("map-alias.cs"));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)),
      Is.EqualTo("6c733c8067656f5584a68425c13b8f7adb4030069d8c8e40054f2f8250ef8bda"));
    var source = Encoding.ASCII.GetString(bytes);
    var imported = CSharpFrontend.Import("map-alias.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", []), Is.EqualTo(42));

    var script = Emit(source, optimize: optimize);
    Assert.That(script, Does.Contain("if defined").And.Contain("setlocal"));
    Assert.That(script, Does.Not.Contain("Alice").And.Not.Contain("alice").And.Not.Contain("a% !&|<>^[]"));
    Assert.That(script.All(char.IsAscii), Is.True);
  }

  [Test]
  public void TranslatesSelectedOrdinalKeysAndCountsTheirEntries() {
    const string source = """
      using System.Collections.Generic;
      public static class Kernel {
        public static int Run() {
          var map = new Dictionary<string, int> { ["\u00e9"] = 0, ["e\u0301"] = 2, ["\0"] = 3 };
          int selector = 1;
          string key = selector > 0 ? "\u00e9" : "e\u0301";
          bool present = map.ContainsKey(key);
          map[key] = 40;
          if (!present) return -1;
          return map[key] + map.Count - 1;
        }
      }
      """;
    var imported = CSharpFrontend.Import("ordinal.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(imported.Module!, "Run", []), Is.EqualTo(42));

    var script = Emit(source);
    Assert.That(script.All(char.IsAscii), Is.True);
    Assert.That(script, Does.Contain("if defined"));
  }

  [TestCase("return map[\"missing\"];", "KeyNotFound")]
  [TestCase("map.Add(\"key\", 0); map.Add(\"key\", 1); return 0;", "DuplicateKey")]
  public void EmitsDistinctTrapsForDictionaryOperations(string body, string trap) {
    var source = "using System.Collections.Generic; public static class Kernel { public static int Run() {"
      + "var map = new Dictionary<string, int>(); " + body + " } }";
    var script = Emit(source);
    Assert.That(script, Does.Contain($"PLY_BATCH_TRAP={trap}").And.Contain("exit /b 97"));
  }

  [TestCase("var map = new Dictionary<string, long>(); return map.Count;")]
  [TestCase("int n = 0; var map = new Dictionary<string, int>(); return map[$\"key{n}\"];")]
  [TestCase("var map = new Dictionary<string, int>(); map.Clear(); return 0;")]
  [TestCase("int n = 0; while (n < 2) { var map = new Dictionary<string, int>(); n += map.Count + 1; } return n;")]
  public void RefusesUnsupportedDictionaryShapes(string body) {
    var source = "using System.Collections.Generic; public static class Kernel { public static int Run() { " + body + " } }";
    var imported = CSharpFrontend.Import("unsupported.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(BatchEmitter.Check(imported.Module!), Has.One.Matches<Diagnostic>(problem => problem.Code == "PLY2850"));
  }

  [Test]
  public void RefusesANullMapKeyInVerifiedIr() {
    var type = IrType.Map(IrType.String, IrType.I32, MapOrder.SlotReuse);
    var map = new IrLocal("map", type);
    var module = new IrModule("NullKey", [
      new("Run", true, IrType.I32, [], [map], new([
        new IrDeclare(map, new IrCollection(IrCollectionOp.MapNew, type, [])),
        new IrReturn(new IrCollection(IrCollectionOp.MapGet, IrType.I32, [new IrLocalRef(map), new IrNull(IrType.String)]))
      ]))
    ]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(BatchEmitter.Check(module), Has.One.Matches<Diagnostic>(problem => problem.Code == "PLY2850"));
  }

  [TestCase(509, true)]
  [TestCase(510, true)]
  [TestCase(511, false)]
  public void CountsMapSlotsAndArrayElementsInTheSameStorageBudget(int arrayLength, bool accepted) {
    var source = $$"""
      using System.Collections.Generic;
      public static class Kernel {
        public static int Run() {
          int[] values = new int[{{arrayLength}}];
          var map = new Dictionary<string, int> { ["key"] = 42 };
          return values[0] + map.Count;
        }
      }
      """;
    var imported = CSharpFrontend.Import("budget.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    var diagnostics = BatchEmitter.Check(imported.Module!);
    if (accepted)
      Assert.That(diagnostics, Is.Empty);
    else
      Assert.That(diagnostics, Has.One.Matches<Diagnostic>(problem
        => problem.Code == "PLY2850" && problem.Message.Contains("512-slot collection budget", StringComparison.Ordinal)));
  }

  [Test]
  public void TranslatesConsoleOutputFixtureAndLiftsExactStdout() {
    var source = File.ReadAllText(BatchFixture("console-output.cs"));
    var imported = CSharpFrontend.Import("console-output.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    var output = new List<string>();
    var status = (int)IrInterpreter.Instantiate(imported.Module!, output.Add).Call("Run")!;
    Assert.That(status, Is.EqualTo(42));
    var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\r\n", output) + "\r\n")));
    Assert.That(sha, Is.EqualTo("7fe52560bc51f640ece2d6c204838c52497bfc2e5d560894a1496ee71e4526ff"));

    var script = Emit(source);
    Assert.That(script, Does.Contain("setlocal EnableDelayedExpansion").And.Contain("echo(").And.Contain("%%PATH%%"));
  }

  [Test]
  public void TranslatesConsoleWriteLineWithNullEmptyAndExtrema() {
    const string source = """
      using System;
      public static class Kernel {
        public static int Run() {
          int min = int.MinValue, max = int.MaxValue;
          Console.WriteLine((string)null);
          Console.WriteLine("");
          Console.WriteLine($"min={min} max={max}");
          return 0;
        }
      }
      """;
    var imported = CSharpFrontend.Import("NullOut.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    var lines = new List<string>();
    Assert.That(IrInterpreter.Instantiate(imported.Module!, lines.Add).Call("Run"), Is.EqualTo(0));
    Assert.That(lines, Is.EqualTo(new[] { "", "", $"min={int.MinValue} max={int.MaxValue}" }));

    var script = Emit(source);
    Assert.That(script, Does.Contain("echo(\r\n"));
  }

  [TestCase("Console.WriteLine(\"\\u00E9\");", "PLY2850")]
  [TestCase("Console.WriteLine(\"a\\nb\");", "PLY2850")]
  [TestCase("int x = 5; Console.WriteLine($\"{x:X}\");", "PLY2850")]
  public void RefusesUnsupportedConsoleOutputTextInBatch(string body, string code) {
    var (status, error, script) = Translate($"using System; public static class Kernel {{ public static int Run() {{ {body} return 0; }} }}");
    Assert.That(status, Is.EqualTo(TranslateCommand.UnsupportedConstruct), error);
    Assert.That(error, Does.Contain(code));
    Assert.That(script, Is.Null);
  }

  [TestCase("Console.WriteLine();")]
  [TestCase("Console.WriteLine(42);")]
  [TestCase("int x = 5; Console.WriteLine($\"{x,5}\");")]
  public void RefusesUnsupportedConsoleOverloadsAndAlignments(string body) {
    var (status, _, script) = Translate($"using System; public static class Kernel {{ public static int Run() {{ {body} return 0; }} }}");
    Assert.That(status, Is.EqualTo(TranslateCommand.UnsupportedConstruct));
    Assert.That(script, Is.Null);
  }

  [TestCase(1000, true)]
  [TestCase(1020, false)]
  public void EnforcesExpandedEchoCommandLengthBound(int literalLength, bool accepted) {
    var text = new string('a', literalLength);
    var (status, error, script) = Translate($"using System; public static class Kernel {{ public static int Run() {{ Console.WriteLine(\"{text}\"); return 0; }} }}");
    Assert.That(status == 0, Is.EqualTo(accepted), error);
    Assert.That(script is not null, Is.EqualTo(accepted));
    if (!accepted)
      Assert.That(error, Does.Contain("PLY2850"));
  }

  [Test]
  public void TranslatesArrayAliasFixtureAndEmptyArrayAndConditionalAlias() {
    var script = Emit(File.ReadAllText(BatchFixture("array-alias.cs")));
    Assert.That(script, Does.Contain("PLY_BATCH_TRAP=IndexOutOfRange").And.Contain("exit /b 97"));

    const string conditional = """
      public static class Kernel {
        public static int Run() {
          int[] a = [1, 2];
          int[] b = new int[0];
          int[] target;
          if (b.Length == 0) target = a; else target = b;
          return target[1];
        }
      }
      """;
    var conditionalScript = Emit(conditional);
    Assert.That(conditionalScript, Does.Contain("_ply_al"));
  }

  [TestCase(BatchEmitter.CmdArrayStorageBudget, true)]
  [TestCase(BatchEmitter.CmdArrayStorageBudget + 1, false)]
  public void EnforcesCmdArrayStorageBudget(int size, bool accepted) {
    var (status, error, script) = Translate($"public static class Kernel {{ public static int Run() {{ var a = new int[{size}]; return a.Length; }} }}");
    Assert.That(status == 0, Is.EqualTo(accepted), error);
    Assert.That(script is not null, Is.EqualTo(accepted));
    if (!accepted)
      Assert.That(error, Does.Contain("PLY2850"));
  }

  [TestCase("public static int Run() { long[] values = [1L, 2L]; return unchecked((int)values[0]); }")]
  [TestCase("public static int Run() { int n = 3; var a = new int[n]; return a.Length; }")]
  [TestCase("public static int Run() { int total = 0; for (int i = 0; i < 2; i++) { int[] a = [i]; total += a[0]; } return total; }")]
  [TestCase("public static int Run() { long x = -1; return (int)(x >>> 1); }")]
  [TestCase("public static int Run() { uint x = 1; return (int)(x >> 1); }")]
  public void RefusesUnsupportedArrayAndBitwiseShapes(string method) {
    var (status, error, script) = Translate($"public static class Kernel {{ {method} }}");
    Assert.That(status, Is.EqualTo(TranslateCommand.UnsupportedConstruct), error);
    Assert.That(error, Does.Contain("PLY2850"));
    Assert.That(script, Is.Null);
  }

  [TestCase("int sum = 0; for (int i = 0; i < 4; i += 1) { if (i == 0) continue; if (i != 2) sum += i; else break; } return -sum;", " NEQ ")]
  [TestCase("int n = 0; do { n += 1; } while (n >= 2); return n;", " GEQ ")]
  [TestCase("int n = int.MinValue; if (n > -1) return 42; if (n <= -1) return n; return 0;", " LEQ ")]
  [TestCase("while (true) { break; } return 1;", "goto ")]
  [TestCase("int acc = 0; for (int i = -1; i <= 2; i++) { switch (i) { case -1: acc += 10; break; case 0: continue; case 1: acc += 20; break; default: acc += 3; break; } } return acc;", "_ply_swm")]
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
  public void RejectsUnknownBatchProfile() {
    var (badProfileStatus, badProfileError, _) = Translate("public static class Kernel { public static int Run() => 0; }", "dos50");
    Assert.That(badProfileStatus, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(badProfileError, Does.Contain("dos50"));
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

  [Test]
  public void CoversEmitterApiGuardsBranchesAndDiagnostics() {
    var empty = new IrModule("Empty", [new IrFunction("Run", true, IrType.I32, [], [], new([new IrReturn(new IrConst(IrType.I32, 0))]))]);
    Assert.That(BatchEmitter.Check(empty), Is.Empty);

    var unverified = new IrModule("Bad", [new IrFunction("Run", true, IrType.I32, [], [], new([new IrWriteLine(new IrConst(IrType.I32, 1)), new IrReturn(new IrConst(IrType.I32, 0))]))]);
    Assert.That(BatchEmitter.Check(unverified), Is.Not.Empty);
    Assert.That(() => BatchEmitter.Emit(unverified), Throws.ArgumentException);

    // LogicalNot, LogicalAnd, false constant, switch without default, and nested block in cmd
    const string logicAndSwitch = """
      public static class Kernel {
        public static int Run() {
          int a = 1, b = 2;
          while (false) a = 9;
          if (!(a == 0) && b == 2) {
            switch (a) {
              case 1: a = 10; break;
            }
          }
          return a;
        }
      }
      """;
    var cmdScript = Emit(logicAndSwitch);
    Assert.That(cmdScript, Does.Contain("exit /b"));

    // Crafted IR refusals for cmd
    var longSwitch = new IrModule("LongSwitch", [new IrFunction("Run", true, IrType.I32, [], [],
      new([new IrSwitch(new IrConst(IrType.I64, 1), []), new IrReturn(new IrConst(IrType.I32, 0))]))]);
    Assert.That(BatchEmitter.Check(longSwitch), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));

    var strWrite = new IrModule("BadWrite", [new IrFunction("Run", true, IrType.I32, [], [],
      new([new IrWriteLine(new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [new IrConst(IrType.Bool, 1)])), new IrReturn(new IrConst(IrType.I32, 0))]))]);
    Assert.That(BatchEmitter.Check(strWrite), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));

    var badCond = new IrModule("BadCond", [new IrFunction("Run", true, IrType.I32, [], [],
      new([new IrIf(new IrBinary(IrBinaryOp.Equal, new IrConst(IrType.I64, 1), new IrConst(IrType.I64, 1)), new([new IrReturn(new IrConst(IrType.I32, 1))]), null),
        new IrReturn(new IrConst(IrType.I32, 0))]))]);
    Assert.That(BatchEmitter.Check(badCond), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));

    var badBinaryBranch = new IrModule("BadBin", [new IrFunction("Run", true, IrType.I32, [], [],
      new([new IrIf(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [new IrStrConst("a"), new IrStrConst("b")]), new([new IrReturn(new IrConst(IrType.I32, 1))]), null),
        new IrReturn(new IrConst(IrType.I32, 0))]))]);
    Assert.That(BatchEmitter.Check(badBinaryBranch), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));
  }

  private static string Emit(string source, string profile = BatchEmitter.Profile, bool optimize = false) {
    var (status, error, bytes) = Translate(source, profile, optimize);
    Assert.That(status, Is.Zero, error);
    Assert.That(bytes, Is.Not.Null);
    Assert.That(bytes!.All(value => value < 128), Is.True);
    var script = Encoding.ASCII.GetString(bytes!);
    Assert.That(script, Does.EndWith("\r\n"));
    return script;
  }

  private static (int Status, string Error, byte[]? Script) Translate(string source, string profile = BatchEmitter.Profile, bool optimize = false) {
    var workspace = Directory.CreateTempSubdirectory();
    try {
      var fixture = Path.Combine(workspace.FullName, "Kernel.cs");
      File.WriteAllText(fixture, source);
      var output = Path.Combine(workspace.FullName, "out");
      using var error = new StringWriter();
      List<string> arguments = ["translate", fixture, "--from", "csharp", "--to", "batch", "--profile", profile, "--output", output];
      if (!optimize)
        arguments.AddRange(["--opt", "none"]);
      var status = Program.Run([.. arguments], TextWriter.Null, error);
      var script = Directory.Exists(output) ? Directory.EnumerateFiles(output, "*.bat").SingleOrDefault() : null;
      return (status, error.ToString(), script is null ? null : File.ReadAllBytes(script));
    } finally {
      workspace.Delete(recursive: true);
    }
  }

}
