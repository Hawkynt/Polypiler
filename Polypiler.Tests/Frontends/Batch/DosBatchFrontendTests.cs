using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Batch;

[TestFixture]
public sealed class DosBatchFrontendTests {

  [Test]
  public void ThePinnedEarlierLabelWinsOnItsEightCharacterPrefix() {
    var source = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "dos622", "lift.bat");
    var result = Translate(source);
    Assert.That(result.Status, Is.Zero, result.Errors);
    Assert.That(GeneratedCSharp.Compile(result.Source!, checkOverflow: true).Method("Run").Invoke(null, null), Is.EqualTo(42));
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
