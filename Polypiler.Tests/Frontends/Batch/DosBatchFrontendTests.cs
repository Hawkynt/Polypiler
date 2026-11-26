using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Batch;

[TestFixture]
public sealed class DosBatchFrontendTests {

  private static string DosFixture(params string[] parts)
    => Path.Combine([PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "dos622", .. parts]);

  [Test]
  public void ThePinnedEarlierLabelWinsOnItsEightCharacterPrefix() {
    var source = DosFixture("lift.bat");
    var result = Translate(source);
    Assert.That(result.Status, Is.Zero, result.Errors);
    Assert.That(GeneratedCSharp.Compile(result.Source!, checkOverflow: true).Method("Run").Invoke(null, null), Is.EqualTo(42));
  }

  [Test]
  public void LiftsPinnedDosCallShiftAndDirectTransferFixtures() {
    var options = new BatchImportOptions {
      EntryInvocation = @"B:\CASE.BAT",
      CompanionFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["CHILD.BAT"] = File.ReadAllText(DosFixture("CHILD.BAT")),
        ["LEX.BAT"] = File.ReadAllText(DosFixture("LEX.BAT")),
        ["CHAIN.BAT"] = File.ReadAllText(DosFixture("CHAIN.BAT"))
      },
      StatusFixtures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
        ["PLYRC.COM"] = 42
      }
    };
    var imported = BatchFrontend.Import("CALL6.BAT", File.ReadAllText(DosFixture("CALL6.BAT")), BatchDialect.Dos622, options);
    Assert.That(imported.Diagnostics, Is.Empty);

    var output = new List<string>();
    var status = (int)IrInterpreter.Instantiate(imported.Module!, output.Add).Call("Run")!;
    Assert.That(status, Is.EqualTo(42));

    var trace = string.Join("\r\n", output) + "\r\nPLY_DONE\r\n";
    var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(trace)));
    Assert.That(sha, Is.EqualTo("976b2c9ef033431a66173b7a32c082845aa510748bb79b496e12973107a1838a"));
  }

  [Test]
  public void LiftsPinnedDosNestedFrameAndDirectTransferReturnFixtures() {
    var options = new BatchImportOptions {
      EntryInvocation = @"B:\CASE.BAT",
      EntryArguments = ["Alpha", "Beta", "Gamma"],
      CompanionFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["FIRST.BAT"] = File.ReadAllText(DosFixture("frames", "FIRST.BAT")),
        ["CHAIN.BAT"] = File.ReadAllText(DosFixture("frames", "CHAIN.BAT")),
        ["REL.BAT"] = File.ReadAllText(DosFixture("frames", "REL.BAT"))
      }
    };
    var imported = BatchFrontend.Import("CASE.BAT", File.ReadAllText(DosFixture("frames", "CASE.BAT")), BatchDialect.Dos622, options);
    Assert.That(imported.Diagnostics, Is.Empty);

    var output = new List<string>();
    var status = (int)IrInterpreter.Instantiate(imported.Module!, output.Add).Call("Run")!;
    Assert.That(status, Is.Zero);

    var trace = string.Join("\r\n", output) + "\r\nPLY_DONE\r\n";
    var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(trace)));
    Assert.That(sha, Is.EqualTo("4f152ec0488a1c610ae4f70efaad83f44b118d5e44b65a15c6d560f2a26fe9fb"));
  }

  [Test]
  public void LiftsDosIfEqualWithPositionalSlotsAndLiteralPercent() {
    var source = "@ECHO OFF\r\nIF \"%1\"==\"alpha\" ECHO HIT=%%1=%1\r\nIF NOT \"%2\"==\"skip\" GOTO done\r\nECHO SKIPPED\r\n:done\r\n";
    var options = new BatchImportOptions { EntryArguments = ["alpha"] };
    var imported = BatchFrontend.Import("CASE.BAT", source, BatchDialect.Dos622, options);
    Assert.That(imported.Diagnostics, Is.Empty);

    var output = new List<string>();
    Assert.That(IrInterpreter.Instantiate(imported.Module!, output.Add).Call("Run"), Is.EqualTo(0));
    Assert.That(output, Is.EqualTo(new[] { "HIT=%1=alpha" }));
  }

  [Test]
  public void CoversDosShapeMergingEmptyScriptsAndTemplateEdgeCases() {
    Assert.That(BatchFrontend.Import("empty.bat", "", BatchDialect.Dos622).Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2800"));
    Assert.That(BatchFrontend.Import("setexp.bat", "SET X=%Y%\r\nECHO PLYRESULT=%X%\r\n", BatchDialect.Dos622).Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));
    Assert.That(BatchFrontend.Import("ifeof.bat", "SET X=a\r\nIF \"%X%\"==\"a\" GOTO :EOF\r\nECHO PLYRESULT=%X%\r\n", BatchDialect.Dos622).Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));
    Assert.That(BatchFrontend.Import("ifspace.bat", "IF \"a b\"==\"a b\" GOTO d\r\n:d\r\nSET X=00000000\r\nECHO PLYRESULT=%X%\r\n", BatchDialect.Dos622).Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));
    Assert.That(BatchFrontend.Import("ifempty.bat", "SET X=\r\nIF %X%==a GOTO d\r\n:d\r\nSET R=00000000\r\nECHO PLYRESULT=%R%\r\n", BatchDialect.Dos622).Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));

    // Shape merge diamond in scalar DOS (distinct paths writing different valid hex values)
    const string diamond = "SET F=1\r\nIF \"%F%\"==\"1\" GOTO left\r\nSET R=00000001\r\nGOTO join\r\n:left\r\nSET R=0000002A\r\n:join\r\nECHO PLYRESULT=%R%\r\n";
    var merged = BatchFrontend.Import("diamond.bat", diamond, BatchDialect.Dos622);
    Assert.That(merged.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(merged.Module!, "Run", []), Is.EqualTo(42));

    // Trailing argument delimiter + empty ECHO template + nested subdir key + tab + %VAR% call arg + unquoted %1 + delimited IF
    var trailing = BatchFrontend.Import("main.bat", "@ECHO OFF\r\n\tSET EMPTY=\r\nSET ARG=val\r\nCALL sub\\child.bat %ARG%, \r\nECHO %EMPTY%\r\n", BatchDialect.Dos622,
      new BatchImportOptions {
        CompanionFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          [@"sub\child.bat"] = "@ECHO OFF\r\nIF %1==val ECHO %1\r\n"
        }
      });
    Assert.That(trailing.Diagnostics, Is.Empty);
    var outLines = new List<string>();
    Assert.That(IrInterpreter.Instantiate(trailing.Module!, outLines.Add).Call("Run"), Is.EqualTo(0));
    Assert.That(outLines, Is.EqualTo(new[] { "val", "" }));

    Assert.That(BatchFrontend.Import("delim.bat", "SET X=a b\r\nIF \"%X%\"==\"a b\" GOTO d\r\n:d\r\nSET R=00000000\r\nECHO PLYRESULT=%R%\r\n", BatchDialect.Dos622).Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));
    Assert.That(BatchFrontend.Import("pctcmd.bat", "%CMD%\r\n", BatchDialect.Dos622).Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));
    Assert.That(BatchFrontend.Import("badlabel.bat", ":bad-label\r\n", BatchDialect.Dos622).Diagnostics,
      Has.One.Matches<Diagnostic>(d => d.Code == "PLY2800"));
    Assert.That(BatchFrontend.Import("echoempty.bat", "@ECHO OFF\r\nECHO \r\n", BatchDialect.Dos622).Diagnostics, Is.Empty);

    // Shape merge adding a brand-new local on a second incoming edge
    const string asymmetric = "SET F=1\r\nSET R=00000001\r\nIF \"%F%\"==\"1\" GOTO join\r\nSET EXTRA=00000002\r\nSET R=00000003\r\n:join\r\nECHO PLYRESULT=%R%\r\n";
    Assert.That(BatchFrontend.Import("asym.bat", asymmetric, BatchDialect.Dos622).Diagnostics, Is.Empty);
  }

  [TestCase("CALL MISSING.BAT\r\n", "PLY2802", 1)]
  [TestCase("MISSING.BAT\r\n", "PLY2802", 1)]
  [TestCase("CALL :sub\r\n", "PLY2802", 1)]
  [TestCase("CALL %TARGET%\r\n", "PLY2802", 1)]
  [TestCase("SHIFT /1\r\n", "PLY2802", 1)]
  [TestCase("GOTO :EOF\r\n", "PLY2802", 1)]
  [TestCase("ECHO hi > out.txt\r\n", "PLY2802", 1)]
  [TestCase("SET X=a|b\r\n", "PLY2802", 1)]
  [TestCase("ECHO %UNCLOSED\r\n", "PLY2802", 1)]
  [TestCase("ECHO %123%\r\n", "PLY2802", 1)]
  [TestCase("CALL A.BAT > out.txt\r\n", "PLY2802", 1)]
  public void RejectsUnsupportedDosCallShiftAndTransferForms(string source, string code, int line) {
    var options = new BatchImportOptions {
      CompanionFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["A.BAT"] = "@ECHO OFF\r\nECHO A\r\n"
      }
    };
    var imported = BatchFrontend.Import("ENTRY.BAT", source, BatchDialect.Dos622, options);
    Assert.That(imported.Module, Is.Null);
    Assert.That(imported.Diagnostics, Has.Count.EqualTo(1));
    Assert.That((imported.Diagnostics[0].Code, imported.Diagnostics[0].Line), Is.EqualTo((code, line)));
  }

  [Test]
  public void RejectsDosCallAndTransferCycles() {
    var callCycle = BatchFrontend.Import("A.BAT", "@ECHO OFF\r\nCALL B.BAT\r\n", BatchDialect.Dos622, new BatchImportOptions {
      CompanionFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["A.BAT"] = "@ECHO OFF\r\nCALL B.BAT\r\n",
        ["B.BAT"] = "@ECHO OFF\r\nCALL A.BAT\r\n"
      }
    });
    Assert.That(callCycle.Module, Is.Null);
    Assert.That(callCycle.Diagnostics, Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));

    var transferCycle = BatchFrontend.Import("A.BAT", "@ECHO OFF\r\nB.BAT\r\n", BatchDialect.Dos622, new BatchImportOptions {
      CompanionFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["A.BAT"] = "@ECHO OFF\r\nB.BAT\r\n",
        ["B.BAT"] = "@ECHO OFF\r\nA.BAT\r\n"
      }
    });
    Assert.That(transferCycle.Module, Is.Null);
    Assert.That(transferCycle.Diagnostics, Has.One.Matches<Diagnostic>(d => d.Code == "PLY2802"));
  }

  [TestCase("00000000", 0)]
  [TestCase("7FFFFFFF", int.MaxValue)]
  [TestCase("80000000", int.MinValue)]
  [TestCase("FFFFFFFF", -1)]
  public void AResultUsesAllThirtyTwoBits(string hex, int expected) {
    var result = TranslateText($"SET value={hex}\r\nECHO PLYRESULT=%VALUE%\r\n");
    Assert.That(result.Status, Is.Zero, result.Errors);
    Assert.That(GeneratedCSharp.Compile(result.Source!, checkOverflow: true).Method("Run").Invoke(null, null), Is.EqualTo(expected));
  }

  [TestCase("SET /A X=1\r\nECHO PLYRESULT=%X%\r\n", "PLY2802", 1)]
  [TestCase("GOTO UNKNOWN\r\nECHO PLYRESULT=%X%\r\n", "PLY2800", 1)]
  [TestCase(":spin\r\nGOTO spin\r\n", "PLY2800", 2)]
  [TestCase("SET X=00000001\r\nIF \"%X%\"==\"00000002\" GOTO done\r\n:spin\r\nGOTO spin\r\n:done\r\nECHO PLYRESULT=%X%\r\n", "PLY2800", 4)]
  [TestCase("ECHO PLYRESULT=%X%\r\n", "PLY2802", 1)]
  [TestCase("SET X=00000000\r\n", "PLY2800", 1)]
  [TestCase("SET X=0000002a\r\nECHO PLYRESULT=%X%\r\n", "PLY2800", 2)]
  [TestCase("SET X=000000002\r\nECHO PLYRESULT=%X%\r\n", "PLY2800", 2)]
  [TestCase("SET X=\r\nECHO PLYRESULT=%X%\r\n", "PLY2800", 2)]
  [TestCase("SET X=00000000\nECHO PLYRESULT=%X%\n", "PLY2800", 1)]
  public void InvalidScriptsHaveLocatedDiagnosticsAndNoOutput(string source, string code, int line) {
    var result = TranslateText(source);
    Assert.That(result.Status, Is.EqualTo(code == "PLY2800" ? TranslateCommand.InvalidSource : TranslateCommand.UnsupportedConstruct));
    Assert.That(result.Errors, Does.Contain($"({line},1): error {code}"));
    Assert.That(result.Source, Is.Null);
  }

  [TestCase(126, true)]
  [TestCase(127, true)]
  [TestCase(128, false)]
  public void ChecksPhysicalSourceWidths(int length, bool accepted) {
    var result = TranslateText($"SET PAD={new string('a', length - 8)}\r\nSET X=0000002A\r\nECHO PLYRESULT=%X%\r\n");
    Assert.That(result.Status, Is.EqualTo(accepted ? 0 : TranslateCommand.UnsupportedConstruct), result.Errors);
    Assert.That(result.Source is not null, Is.EqualTo(accepted));
  }

  [TestCase(126, true)]
  [TestCase(127, true)]
  [TestCase(128, false)]
  public void ChecksExpandedIfWidthsSeparately(int length, bool accepted) {
    const string conditional = "IF \"%LEFT%\" == \"%RIGHT%\" GOTO hit";
    var word = new string('a', 44);
    var baseline = conditional.Replace("%LEFT%", word, StringComparison.Ordinal).Replace("%RIGHT%", word, StringComparison.Ordinal);
    var command = conditional.Insert(2, new string(' ', length - baseline.Length));
    var result = TranslateText($"SET LEFT={word}\r\nSET RIGHT={word}\r\n{command}\r\nSET X=00000000\r\nGOTO done\r\n:hit\r\nSET X=0000002A\r\n:done\r\nECHO PLYRESULT=%X%\r\n");
    Assert.That(result.Status, Is.EqualTo(accepted ? 0 : TranslateCommand.UnsupportedConstruct), result.Errors);
    if (accepted)
      Assert.That(GeneratedCSharp.Compile(result.Source!, checkOverflow: true).Method("Run").Invoke(null, null), Is.EqualTo(42));
    else
      Assert.That(result.Errors, Does.Contain("(3,1): error PLY2802").And.Contain("expanded IF"));
  }

  [Test]
  public void ClearingAKnownNameExpandsToEmptyAndAssignmentsReplacePriorValues() {
    var result = TranslateText("SET X=outer\r\nSET X=\r\nSET RESULT=stale\r\nIF \"%x%\" == \"\" GOTO cleared\r\nSET RESULT=00000000\r\nGOTO finish\r\n:cleared\r\nSET RESULT=0000002A\r\n:finish\r\nECHO PLYRESULT=%result%\r\n");
    Assert.That(result.Status, Is.Zero, result.Errors);
    Assert.That(GeneratedCSharp.Compile(result.Source!, checkOverflow: true).Method("Run").Invoke(null, null), Is.EqualTo(42));
  }

  [Test]
  public void TheSourceDialectRejectsUnknownNames() {
    var result = TranslateText("SET X=00000000\r\nECHO PLYRESULT=%X%\r\n", "dos6x");
    Assert.That(result.Status, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(result.Errors, Does.Contain("dos6x"));
    Assert.That(result.Source, Is.Null);
  }

  private static (int Status, string Errors, string? Source) TranslateText(string text, string dialect = "dos622") {
    var input = Directory.CreateTempSubdirectory();
    try {
      var source = Path.Combine(input.FullName, "test.bat");
      File.WriteAllText(source, text);
      return Translate(source, dialect);
    } finally {
      input.Delete(recursive: true);
    }
  }

  private static (int Status, string Errors, string? Source) Translate(string source, string dialect = "dos622") {
    var output = Directory.CreateTempSubdirectory();
    try {
      using var errors = new StringWriter();
      var status = Program.Run(["translate", source, "--from", "batch", "--batch-dialect", dialect, "--to", "csharp", "--opt", "none",
        "--output", output.FullName], TextWriter.Null, errors);
      var file = output.EnumerateFiles("*.cs").SingleOrDefault();
      return (status, errors.ToString(), file is null ? null : File.ReadAllText(file.FullName));
    } finally {
      output.Delete(recursive: true);
    }
  }

}
