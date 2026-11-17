using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Cli;

/// <summary>
/// Caller contracts through <c>polypiler translate</c> (issue #777): checked at the boundary of the
/// written code, or reported and listed in the manifest with <c>checked: false</c> — never silently trusted.
/// </summary>
[TestFixture]
public sealed class TranslateContractTests {

  private const string Sum = "public static int Sum(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }";

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-contracts-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private (int ExitCode, string Text, string Error, Manifest? Manifest) Translate(string to, params string[] further) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(Sum));
    var output = Path.Combine(this._directory, "out");
    using var standard = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", source, "--from", "csharp", "--to", to, "--output", output, .. further], standard, error);
    if (!Directory.Exists(output))
      return (exitCode, "", error.ToString(), null);

    var bag = new DiagnosticBag();
    var manifest = ManifestReader.Read(Path.Combine(output, Manifest.FileName), bag);
    Assert.That(bag.Items, Is.Empty);
    return (exitCode, File.ReadAllText(Directory.GetFiles(output).Single(path => Path.GetFileName(path) != Manifest.FileName)), error.ToString(), manifest);
  }

  private static string[] Entries(Manifest manifest)
    => [.. manifest.Assumptions!.Select(entry => $"{entry!["target"]}|{entry["contract"]}|{entry["checked"]}|{entry["origin"]}|{entry["usedBy"]}")];

  /// <summary>A checked contract is a check at the entry of the written function, and listed with checked: true.</summary>
  [TestCase("csharp", "ArgumentException")]
  [TestCase("javascript", "throw")]
  public void ACheckedContract_IsWrittenAtTheBoundary(string to, string thrown) {
    var (exitCode, text, error, manifest) = this.Translate(to, "--assume", "Sum.n: int[0, 65535]");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(text, Does.Contain("ContractViolation: Sum.n: int[0, 65535]").And.Contain(thrown));
    Assert.That(Entries(manifest!), Is.EqualTo(new[] { "Sum.n|int[0, 65535]|true|--assume|0" }));
    Assert.That(manifest!.Command, Does.Contain("Sum.n: int[0, 65535]"));
  }

  /// <summary>An unchecked contract is PLY0512, no check, and checked: false; a value fact is listed unchecked too.</summary>
  [Test]
  public void AnUncheckedContract_IsReportedAndListed() {
    var (exitCode, text, error, manifest) = this.Translate("csharp", "--assume-unchecked", "Sum.n: int[0, 65535]", "--assume", "Sum.n in [0, 9]");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(text, Does.Not.Contain("ContractViolation"));
    Assert.That(error, Does.Contain("warning PLY0512: assumption 'Sum.n: int[0, 65535]' is trusted without a run-time check"));
    Assert.That(Entries(manifest!), Is.EqualTo(new[] { "Sum.n|int[0, 65535]|false|--assume-unchecked|0", "Sum.n|in [0, 9]|false|--assume|0" }));
    Assert.That(manifest!.Warnings.Select(warning => warning.Code), Is.EquivalentTo(new[] { "PLY0009", "PLY0512" }));
  }

  /// <summary>Under --strict an unchecked contract is refused.</summary>
  [Test]
  public void Strict_RefusesAnUncheckedContract() {
    var (exitCode, text, error, _) = this.Translate("csharp", "--strict", "--assume-unchecked", "Sum.n: int[0, 65535]");
    Assert.That((exitCode, text), Is.EqualTo(((int)ExitCode.UnsupportedConstruct, "")));
    Assert.That(error, Does.Contain("error PLY0010: strict mode: PLY0512 assumption 'Sum.n: int[0, 65535]'"));
  }

  /// <summary>A contradiction, an unknown target and an uncheckable contract write nothing.</summary>
  [TestCase("Sum.n: text", ExitCode.InvalidSource, "PLY0513")]
  [TestCase("Nope.n: i32", ExitCode.UsageError, "PLY0514")]
  [TestCase("Sum.return: f32", ExitCode.UsageError, "PLY0516")]
  public void ARefusedContract_WritesNothing(string contract, ExitCode expected, string code) {
    var (exitCode, text, error, manifest) = this.Translate("csharp", "--assume", contract);
    Assert.That((exitCode, text, manifest), Is.EqualTo(((int)expected, "", (Manifest?)null)));
    Assert.That(error, Does.Contain($"error {code}: "));
  }

  /// <summary>Without any assumption the manifest has no assumptions section.</summary>
  [Test]
  public void NoAssumption_NoSection() => Assert.That(this.Translate("csharp").Manifest!.Assumptions, Is.Null);

}
