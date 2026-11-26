using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.Batch;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Batch;

[TestFixture]
public sealed class DosBatchTranslationTests {

  [Test]
  public void TranslatesThePinnedWrapKernelToStandaloneDosBatch() {
    var fixture = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "dos622", "DosWrap.cs");
    var (status, errors, bytes, manifest) = Translate(File.ReadAllText(fixture));
    Assert.That(status, Is.Zero, errors);
    Assert.That(bytes, Is.Not.Null);
    using var parsed = JsonDocument.Parse(manifest!);
    var target = parsed.RootElement.GetProperty("target");
    Assert.That(target.GetProperty("profile").GetString(), Is.EqualTo("dos622"));
    Assert.That(target.GetProperty("runtime").GetString(), Is.EqualTo("command.com"));
    Assert.That(bytes!.All(value => value < 128), Is.True);
    var script = Encoding.ASCII.GetString(bytes!);
    Assert.That(script, Does.EndWith("\r\n"));
    var lines = script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    var labels = lines.Where(line => line.StartsWith(':')).Select(line => line[1..]).ToArray();
    Assert.That(labels, Is.All.Length.LessThanOrEqualTo(8));
    Assert.That(labels.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(labels.Length));
    Assert.That(lines.Count(line => line.StartsWith("ECHO PLYRESULT=", StringComparison.Ordinal)), Is.EqualTo(1));
    foreach (var line in lines) {
      Assert.That(line, Does.StartWith(":").Or.EqualTo("@ECHO OFF").Or.StartWith("SET ")
        .Or.StartWith("IF ").Or.StartWith("GOTO ").Or.StartWith("ECHO PLYRESULT="));
      Assert.That(line.Length, Is.LessThanOrEqualTo(127));
      Assert.That(line.IndexOfAny(['\r', '\n']), Is.EqualTo(-1));
    }
  }

  [TestCase("public static int Run() { int x = 1; return checked(x + 1); }", "AddChecked")]
  [TestCase("public static int Run() { int x = 1; return x * 2; }", "Multiply")]
  [TestCase("public static int Run() { int x = 1; if (x > 0) return x; return 0; }", "Greater")]
  [TestCase("public static int Run() { int x = 1; return System.Math.Abs(x); }", "IrIntrinsic")]
  [TestCase("public static int Run(int value) => value;", "parameterless Int32 kernel")]
  [TestCase("public static long Run() => 1L;", "parameterless Int32 kernel")]
  public void RefusesUnsupportedOperationsBeforeOpeningTheOutputDirectory(string method, string detail) {
    var (status, errors, bytes, _) = Translate($"public static class Kernel {{ {method} }}");
    Assert.That(status, Is.EqualTo(TranslateCommand.UnsupportedConstruct), errors);
    Assert.That(errors, Does.Contain("PLY2851").And.Contain(detail));
    Assert.That(bytes, Is.Null);
  }

  [Test]
  public void ReportIsSharedAndAdderCoversEveryDigitAndCarry() {
    var (_, _, output, _) = Translate("public static class Kernel { public static int Run() { int x = 42; return x + 1; } }");
    Assert.That(output, Is.Not.Null);
    var lines = Encoding.ASCII.GetString(output!).Split("\r\n");
    var table = lines.Select((line, index) => (line, index))
      .Where(entry => entry.line.StartsWith(":A", StringComparison.Ordinal)).ToDictionary(entry => entry.line, entry => entry.index);
    Assert.That(table.Count, Is.EqualTo(512));
    for (var left = 0; left < 16; ++left)
      for (var right = 0; right < 16; ++right)
        for (var carry = 0; carry <= 1; ++carry) {
          var index = table[$":A{left:X}{right:X}{carry}"];
          var quotient = Math.DivRem(left + right + carry, 16, out var remainder);
          Assert.That(lines[(index + 1)..(index + 4)], Is.EqualTo(new[] { $"SET PLYD={remainder:X}", $"SET PLYC={quotient}", "GOTO %PLYN%" }));
        }
  }

  [Test]
  public void PreservesVerificationDiagnosticsAndRefusesToEmitInvalidIr() {
    var body = new IrBlock([new IrReturn(new IrConst(IrType.Bool, 1))]);
    var module = new IrModule("Kernel", [new("Run", true, IrType.I32, [], [], body)], []);
    var expected = IrVerifier.Verify(module);
    Assert.That(expected, Is.Not.Empty);
    Assert.That(DosBatchEmitter.Check(module).Select(item => item.Message), Is.EqualTo(expected.Select(item => item.Message)));
    Assert.That(() => DosBatchEmitter.Emit(module), Throws.ArgumentException.With.Message.Contains(expected[0].Message));
  }

  [Test]
  public void RefusesAnIrKernelWithNoResult() {
    var module = new IrModule("Kernel", [new("Run", true, IrType.I32, [], [], new([]))], []);
    Assert.That(DosBatchEmitter.Check(module).Single().Message, Does.Contain("kernel with no Int32 return"));
  }

  [TestCase(494, true)]
  [TestCase(495, true)]
  [TestCase(496, false)]
  public void BudgetsGeneratedEntriesWithRoomForTheCaller(int count, bool fits) {
    var locals = string.Concat(Enumerable.Range(0, count).Select(i => $"int x{i} = {i};"));
    var (status, errors, bytes, _) = Translate($"public static class Kernel {{ public static int Run() {{ {locals} return x{count - 1}; }} }}");
    Assert.That(status, Is.EqualTo(fits ? 0 : TranslateCommand.UnsupportedConstruct), errors);
    if (fits)
      Assert.That(bytes, Is.Not.Null);
    else {
      Assert.That(bytes, Is.Null);
      Assert.That(errors, Does.Contain("PLY2851").And.Contain("generated environment exceeds 32768 bytes"));
    }
  }

  private static (int Status, string Errors, byte[]? Script, string? Manifest) Translate(string source) {
    var workspace = Directory.CreateTempSubdirectory();
    try {
      var fixture = Path.Combine(workspace.FullName, "Kernel.cs");
      File.WriteAllText(fixture, source);
      var output = Path.Combine(workspace.FullName, "out");
      using var errors = new StringWriter();
      var status = Program.Run(["translate", fixture, "--from", "csharp", "--to", "batch", "--profile", "dos622",
        "--opt", "none", "--output", output], TextWriter.Null, errors);
      var script = Directory.Exists(output) ? File.ReadAllBytes(Directory.EnumerateFiles(output, "*.bat").Single()) : null;
      return (status, errors.ToString(), script, script is null ? null : File.ReadAllText(Path.Combine(output, "polypiler.manifest.json")));
    } finally {
      workspace.Delete(recursive: true);
    }
  }

}
