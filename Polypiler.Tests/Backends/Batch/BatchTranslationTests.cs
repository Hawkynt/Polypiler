using System;
using System.Collections.Generic;
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

  private static readonly string[] _forbiddenEngines = ["cscript", "wscript", "powershell", "pwsh", "mshta", ".vbs"];

  private static string BatchFixture(params string[] parts)
    => Path.Combine([PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", .. parts]);

  [Test]
  public void TranslatesTheCountdownToStandaloneBatch() {
    var script = Emit(File.ReadAllText(BatchFixture("countdown.cs")));
    Assert.That(script, Does.Contain("setlocal EnableExtensions EnableDelayedExpansion")
      .And.Contain("endlocal")
      .And.Contain("exit /b"));
    AssertPureBatch(script);
  }

  [Test]
  public void TranslatesBitwiseFixtureUsingBuiltIns() {
    var script = Emit(File.ReadAllText(BatchFixture("bitwise.cs")));
    Assert.That(script, Does.Contain("&31").And.Contain("~").And.Contain("|").And.Contain("^"));
    AssertPureBatch(script);
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
    Assert.That(script, Does.Contain("setlocal EnableDelayedExpansion").And.Contain("echo(").And.Contain("%%PATH%%").And.Contain("!_BANG!"));
    AssertPureBatch(script);
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
    AssertPureBatch(script);
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

  [TestCase(980, true)]
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
    Assert.That(script, Does.Contain("IndexOutOfRange").And.Contain("exit /b 97"));

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
    Assert.That(conditionalScript, Does.Contain("_AL_"));
    AssertPureBatch(conditionalScript);
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
  [TestCase("public static long Run() => 1L;")]
  [TestCase("public static int Run(int value) => value;")]
  [TestCase("public static int Run() { int x = 1; return checked(x + 1); }")]
  [TestCase("public static int Run() { int x = 5; return x / 2; }")]
  [TestCase("public static int Run() { int x = 5; return System.Math.Abs(x); }")]
  [TestCase("public static int Run() { bool x = true; return x ? 1 : 0; }")]
  public void TranslatesExtendedIntegerArrayAndRuntimeShapes(string method) {
    var script = Emit($"public static class Kernel {{ {method} }}");
    Assert.That(script, Does.Contain("setlocal EnableExtensions EnableDelayedExpansion"));
    AssertPureBatch(script);
  }

  [TestCase("int sum = 0; for (int i = 0; i < 4; i += 1) { if (i == 0) continue; if (i != 2) sum += i; else break; } return -sum;", "goto :")]
  [TestCase("int n = 0; do { n += 1; } while (n >= 2); return n;", " GEQ ")]
  [TestCase("int n = int.MinValue; if (n > -1) return 42; if (n <= -1) return n; return 0;", " LEQ ")]
  [TestCase("while (true) { break; } return 1;", "goto :")]
  [TestCase("int acc = 0; for (int i = -1; i <= 2; i++) { switch (i) { case -1: acc += 10; break; case 0: continue; case 1: acc += 20; break; default: acc += 3; break; } } return acc;", "_swv")]
  public void EmitsClosedControlFlowUsingOnlyBuiltIns(string body, string expected) {
    var script = Emit($"public static class Kernel {{ public static int Run() {{ {body} }} }}");
    Assert.That(script, Does.Contain(expected));
    AssertPureBatch(script);
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
    var longKernel = AssignedVariables(EmitUpdates(64));
    Assert.That(longKernel, Has.Length.EqualTo(shortKernel.Length));
    Assert.That(longKernel, Is.EquivalentTo(shortKernel));

    static string EmitUpdates(int count) => Emit("public static class Kernel { public static int Run() { int x = 1, y = 2;"
      + string.Concat(Enumerable.Repeat("x = (x + 1) + (y - 1);", count))
      + "if ((x - 1) * (x + 1) >= (x + 2) * (x - 2)) return -x; return x; } }");

    static string[] AssignedVariables(string script) => script.Split("\r\n")
      .Where(line => line.StartsWith("set \"_t", StringComparison.Ordinal) || line.StartsWith("set /a \"_t", StringComparison.Ordinal))
      .Select(line => line[..line.IndexOf('=')]).Distinct(StringComparer.Ordinal).ToArray();
  }

  [TestCase("using System.Runtime.InteropServices; public static class Kernel { [DllImport(\"geom\")] private static extern int geom_norm(int x); public static int Run() => geom_norm(1); }")]
  [TestCase("public static class Kernel { public static double Run() => 1.5; }")]
  [TestCase("public static class Kernel { public static float Run() => 1.5f; }")]
  public void RefusesConstructsOutsideTheProfile(string source) {
    var (status, error, script) = Translate(source);
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

    // LogicalNot, LogicalAnd, LogicalOr, false constant, switch without default, and nested block in cmd
    const string logicAndSwitch = """
      public static class Kernel {
        public static int Run() {
          int a = 1, b = 2;
          while (false) a = 9;
          if ((!(a == 0) && b == 2) || a == 99) {
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
    AssertPureBatch(cmdScript);

    // Crafted IR refusals for cmd (BigInt, Float, Foreign, unsupported intrinsic)
    var bigParam = new IrLocal("x", IrType.BigInt);
    var bigIntMod = new IrModule("BigIntMod", [new IrFunction("Run", true, IrType.BigInt, [bigParam], [],
      new([new IrReturn(new IrLocalRef(bigParam))]))]);
    Assert.That(BatchEmitter.Check(bigIntMod), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));

    var floatMod = new IrModule("FloatMod", [new IrFunction("Run", true, IrType.F64, [], [],
      new([new IrReturn(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1.5)))]))]);
    Assert.That(BatchEmitter.Check(floatMod), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));

    var foreignFn = new IrForeignFunction("ext", false, "geom", IrType.I32, [new IrForeignParameter("x", IrType.I32, IrMarshal.Blittable)]);
    var foreignMod = new IrModule("ForeignMod", [new IrFunction("Run", true, IrType.I32, [], [],
      new([new IrReturn(new IrForeignCall("ext", IrType.I32, [new IrConst(IrType.I32, 1)]))]))]) { ForeignFunctions = [foreignFn] };
    Assert.That(BatchEmitter.Check(foreignMod), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));

    var unsupportedIntrinsic = new IrModule("BadIntr", [new IrFunction("Run", true, IrType.Bool, [], [],
      new([new IrReturn(new IrIntrinsic(IrIntrinsicOp.RegexTest, IrType.Bool, [new IrStrConst("a"), new IrStrConst("a"), new IrStrConst("")]))]))]);
    Assert.That(BatchEmitter.Check(unsupportedIntrinsic), Has.One.Matches<Diagnostic>(d => d.Code == "PLY2850"));
  }

  private static void AssertPureBatch(string script) {
    foreach (var forbidden in _forbiddenEngines)
      Assert.That(script.ToLowerInvariant(), Does.Not.Contain(forbidden));

    foreach (var line in script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
      Assert.That(line.Length, Is.LessThanOrEqualTo(1024));
  }

  private static string Emit(string source, string profile = BatchEmitter.Profile) {
    var (status, error, bytes) = Translate(source, profile);
    Assert.That(status, Is.Zero, error);
    Assert.That(bytes, Is.Not.Null);
    Assert.That(bytes!.All(value => value < 128), Is.True);
    var script = Encoding.ASCII.GetString(bytes!);
    Assert.That(script, Does.EndWith("\r\n"));
    return script;
  }

  private static (int Status, string Error, byte[]? Script) Translate(string source, string profile = BatchEmitter.Profile) {
    var workspace = Directory.CreateTempSubdirectory();
    try {
      var fixture = Path.Combine(workspace.FullName, "Kernel.cs");
      File.WriteAllText(fixture, source);
      var output = Path.Combine(workspace.FullName, "out");
      using var error = new StringWriter();
      var status = Program.Run(["translate", fixture, "--from", "csharp", "--to", "batch", "--profile", profile,
        "--opt", "none", "--output", output], TextWriter.Null, error);
      var script = Directory.Exists(output) ? Directory.EnumerateFiles(output, "*.bat").SingleOrDefault() : null;
      return (status, error.ToString(), script is null ? null : File.ReadAllBytes(script));
    } finally {
      workspace.Delete(recursive: true);
    }
  }

}
