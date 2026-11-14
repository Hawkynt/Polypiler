using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Licensing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-output-licence.feature</c>. The translation runs through
/// <see cref="PolypilerRunner"/>, the first one of a scenario as the real executable; the package it
/// writes is checked with the shipped policy in process. The scans run the real
/// <c>polypiler-tooling</c> executable in a workspace that holds a copy of the repository's
/// <c>tooling/licensing</c> and the tree a step writes.
/// </summary>
[Binding, Scope(Feature = "Emitted helper code is MIT")]
public sealed class OutputLicenceSteps(ScenarioWorkspace workspace, CommandLog log, PolypilerRunner polypiler) {

  private const string Tree = "tree";
  private const string Output = "out";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly Regex _helperFunction = new(@"^function (\$[a-z0-9]+)\(", RegexOptions.CultureInvariant | RegexOptions.Multiline);

  private string _file = "";
  private Manifest? _manifest;

  private void Licensing() {
    var licensing = Path.Combine(_root, "tooling", "licensing");
    foreach (var file in Directory.EnumerateFiles(licensing, "*", SearchOption.AllDirectories))
      workspace.Copy(file, Path.Combine("tooling", "licensing", Path.GetRelativePath(licensing, file)));
  }

  /// <summary>Writes a C# source with a licence header whose division needs a helper in JavaScript.</summary>
  [Given("^a C# source whose licence is " + StepText.Quoted + "$")]
  public void ACSharpSource(string licence) {
    this._file = workspace.Write("Calc.cs", $"// SPDX-License-Identifier: {licence}\npublic static class Calc {{ public static int Div(int a, int b) => a / b; }}\n");
    Assert.That(LicenceDetector.Header(File.ReadAllText(this._file)), Is.EqualTo(licence));
  }

  /// <summary>Translates the source to JavaScript and reads the manifest; at least one helper is emitted.</summary>
  [When("^it is translated to JavaScript with helpers emitted$")]
  public void Translate() {
    var result = polypiler.Run("translate", "Calc.cs", "--from", "csharp", "--to", "javascript", "--output", Output);
    Assert.That(result.ExitCode, Is.EqualTo((int)ExitCode.Success), result.Output);
    var bag = new DiagnosticBag();
    this._manifest = ManifestReader.Read(workspace.Resolve(Path.Combine(Output, Manifest.FileName)), bag);
    Assert.That((bag.Items.Count, this._manifest!.Helpers.Count > 0), Is.EqualTo((0, true)));
  }

  /// <summary>Checks that every helper function of the generated file is listed, each with an origin in the repository and the MIT licence.</summary>
  [Then("^the manifest lists every emitted helper with its origin and licence$")]
  public void EveryHelperIsListed() {
    var emitted = _helperFunction.Matches(File.ReadAllText(workspace.Resolve(Path.Combine(Output, "Calc.mjs")))).Select(match => match.Groups[1].Value);
    Assert.That(this._manifest!.Helpers.Select(helper => helper.Symbol), Is.EquivalentTo(emitted));
    Assert.That(this._manifest.Helpers.Select(helper => (helper.License, helper.Origin)).Distinct(), Is.EqualTo(new[] { ("MIT", "Hawkynt/Polypiler:Polypiler.Core.Backends.JavaScript/Helpers") }));
  }

  /// <summary>Checks the licence the manifest states and the effective one the package check computes with the shipped policy.</summary>
  [Then("^the effective licence of the output is " + StepText.Quoted + "$")]
  public void TheEffectiveLicenceIs(string licence) {
    var policy = LicencePolicy.Load(Path.Combine(_root, LicencePolicy.RepositoryPath));
    var texts = LicenceTexts.Load(Path.Combine(_root, "tooling", "licensing", LicencePolicy.TextsFolder));
    var check = new LicenceScanner(policy, texts).ScanPackage(workspace.Resolve(Output), null)!;
    Assert.That(check.Diagnostics, Is.Empty);
    Assert.That((this._manifest!.Licensing.OutputLicence, check.Effective!.Licence), Is.EqualTo((licence, licence)));
  }

  /// <summary>Writes a tree with one C file under a licence header.</summary>
  [Given("^a corpus file whose SPDX header is " + StepText.Quoted + "$")]
  public void ACorpusFile(string licence) {
    this.Licensing();
    this._file = "lib/a.c";
    workspace.Write(Path.Combine(Tree, this._file), $"/* SPDX-License-Identifier: {licence} */\nint a(void) {{ return 0; }}\n");
  }

  /// <summary>Writes a tree with a compiler project of a backend whose one file has a licence header.</summary>
  [Given("^a file under " + StepText.Quoted + " with an SPDX header " + StepText.Quoted + "$")]
  public void AProjectFile(string project, string licence) {
    this.Licensing();
    this._file = $"{project}/Derived.cs";
    workspace.Write(Path.Combine(Tree, this._file), $"// SPDX-License-Identifier: {licence}\nnamespace Polypiler.Core.Backends.CSharp;\n");
  }

  /// <summary>Runs a command line of <c>polypiler-tooling</c> on the tree the scenario wrote.</summary>
  [When("^\"polypiler-tooling (.+)\" runs on its tree$")]
  public void RunOnTheTree(string line) => log.Add(Toolchain.Tooling(workspace.Path, [.. CommandLine.Split(line), "--root", Tree]));

  /// <summary>Checks that the report lists the file as allowed.</summary>
  [Then("^it reports no violation for that file$")]
  public void NoViolation() {
    using var report = JsonDocument.Parse(File.ReadAllText(workspace.Resolve(LicenceReport.DefaultPath)));
    var entry = report.RootElement.EnumerateArray().Single(candidate => candidate.GetProperty("path").GetString() == this._file);
    Assert.That((entry.GetProperty("status").GetString(), log.Last.Stderr), Is.EqualTo(("allowed", "")));
  }

}
