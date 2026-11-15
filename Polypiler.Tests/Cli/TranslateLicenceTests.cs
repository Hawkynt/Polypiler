using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Licensing;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Cli;

/// <summary>
/// What <c>translate</c> records about licences (owner decision of 2026-11-14): every helper the
/// backend emitted, with its origin and licence, in the manifest; the licence of the output from the
/// input's <c>SPDX-License-Identifier</c> header and the MIT of the helpers; the header line of the
/// generated file; and agreement with the effective licence <c>licenses scan</c> computes for the
/// package.
/// </summary>
[TestFixture]
public sealed class TranslateLicenceTests {

  private const string HeaderLine = "// Helpers added by polypiler: MIT (https://github.com/Hawkynt/Polypiler/blob/main/LICENSE-OUTPUT.md); the translated code keeps the licence of its source.";
  private const string JavaScriptHelpers = "Hawkynt/Polypiler:Polypiler.Core.Backends.JavaScript/Helpers";

  private static readonly LicencePolicy _shipped = LicencePolicy.Load(LicencePolicyTests.Shipped);

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-licence-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Translates a snippet whose source starts with a licence header, or none, and reads the manifest.</summary>
  private Manifest Translate(string? licence, string methods, params string[] options) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, (licence is null ? "" : $"// SPDX-License-Identifier: {licence}\n") + PrototypeFixture.Wrap(methods));
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", source, "--from", "csharp", .. options, "--output", this._out], output, error);
    Assert.That((exitCode, error.ToString()), Is.EqualTo(((int)ExitCode.Success, "")));
    var bag = new DiagnosticBag();
    var manifest = ManifestReader.Read(Path.Combine(this._out, Manifest.FileName), bag);
    Assert.That(bag.Items, Is.Empty);
    return manifest!;
  }

  private const string Division = "public static int F(int a, int b) => a / b;";

  // Scenario: A translated MIT program stays MIT.
  [Test]
  public void AnMitProgram_ListsEveryHelperWithItsOriginAndLicence_AndStaysMit() {
    var manifest = this.Translate("MIT", Division, "--to", "javascript");
    Assert.That(manifest.Helpers, Is.EqualTo(new[] { new ManifestHelper("$div", "MIT", JavaScriptHelpers, 1, manifest.Helpers.Single().Size, []) })
      .Using<ManifestHelper>((left, right) => left.Symbol == right.Symbol && left.License == right.License && left.Origin == right.Origin && left.CallSites == right.CallSites && left.DependsOn.Count == right.DependsOn.Count));
    Assert.That(manifest.Helpers.Single().Size, Is.GreaterThan(50));
    Assert.That((manifest.Licensing.OutputLicence, manifest.Licensing.Obligations.Single()), Is.EqualTo(("MIT",
      "MIT: keep the copyright and licence notice of the helpers under it (https://github.com/Hawkynt/Polypiler/blob/main/LICENSE-OUTPUT.md and the notices of their origins)")));
    Assert.That(File.ReadAllLines(Path.Combine(this._out, "Snippet.mjs")).Take(4), Has.Member(HeaderLine));
  }

  // Test design 1: the input licence MIT, LGPL, GPL and unknown.
  [TestCase("MIT", "MIT")]
  [TestCase("LGPL-2.1-only", "LGPL-3.0-or-later")]
  [TestCase("GPL-3.0-only", "GPL-3.0-or-later")]
  [TestCase(null, "NOASSERTION")]
  public void TheOutputLicence_FollowsTheInputs(string? licence, string expected)
    => Assert.That(this.Translate(licence, Division, "--to", "javascript").Licensing.OutputLicence, Is.EqualTo(expected));

  // Test design 3: a translation that emits no helper.
  [Test]
  public void WithoutHelpers_TheManifestListsNone_AndTheLicenceIsTheInputs() {
    var manifest = this.Translate("Apache-2.0", "public static int F(int a) => a + 1;", "--to", "javascript");
    Assert.That((manifest.Helpers.Count, manifest.Licensing.OutputLicence, manifest.Licensing.Obligations.Count), Is.EqualTo((0, "Apache-2.0", 0)));
    Assert.That(File.ReadAllText(Path.Combine(this._out, "Snippet.mjs")), Does.Not.Contain("Helpers added by"));
  }

  [Test]
  public void APortableImplementation_IsAHelperUnderTheLicenceOfItsFile() {
    var manifest = this.Translate("MIT", "public static double F(double a) => System.Math.Cbrt(a);", "--to", "csharp", "--profile", "net2.0");
    var cbrt = manifest.Helpers.Single(helper => helper.Symbol == "FloatCbrt");
    Assert.That((cbrt.License, cbrt.Origin, cbrt.CallSites), Is.EqualTo(("MIT", "Hawkynt/Polypiler:Polypiler.Core.IR/Float/Operations/Roots.Cbrt.cs", 1)));
    Assert.That(manifest.Helpers.All(helper => helper.License == "MIT" && helper.Origin.StartsWith("Hawkynt/Polypiler:Polypiler.Core.", StringComparison.Ordinal)), Is.True);
    Assert.That((manifest.Licensing.OutputLicence, File.ReadAllLines(Path.Combine(this._out, "Snippet.cs"))[2]), Is.EqualTo(("MIT", HeaderLine)));
  }

  [Test]
  public void TheFusedMultiplyAddOfFloat_AddsTheBsdOfFreeBsd() {
    var manifest = this.Translate("MIT", "public static float F(float a, float b, float c) => System.MathF.FusedMultiplyAdd(a, b, c);", "--to", "javascript");
    Assert.That(manifest.Helpers.Single(helper => helper.Symbol == "FloatFusedMultiplyAddF").License, Is.EqualTo("MIT AND BSD-2-Clause"));
    Assert.That((manifest.Licensing.OutputLicence, manifest.Licensing.Obligations.Count), Is.EqualTo(("BSD-2-Clause AND MIT", 2)));
  }

  // The licence translate states is what licenses scan computes for the package, never less.
  [TestCase("MIT", Division, "MIT")]
  [TestCase("Apache-2.0", Division, "Apache-2.0 AND MIT")]
  [TestCase("LGPL-2.1-only", Division, "LGPL-3.0-or-later")]
  [TestCase("GPL-3.0-only", Division, "GPL-3.0-or-later")]
  [TestCase("MIT", "public static float F(float a, float b, float c) => System.MathF.FusedMultiplyAdd(a, b, c);", "BSD-2-Clause AND MIT")]
  public void TheStatedLicence_IsTheEffectiveLicenceOfThePackage(string licence, string methods, string effective) {
    var manifest = this.Translate(licence, methods, "--to", "javascript");
    var check = new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanPackage(this._out, null)!;
    Assert.That(check.Diagnostics.Select(diagnostic => diagnostic.Message), Is.Empty);
    Assert.That((check.Stated, check.Effective!.Licence), Is.EqualTo((manifest.Licensing.OutputLicence, effective)));
    Assert.That(manifest.Licensing.OutputLicence, Is.EqualTo(effective));
  }

}
