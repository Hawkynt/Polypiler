using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Spec;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/spec-binary.feature</c>. Documents of
/// <c>tests/fixtures/spec/binary/</c> are copied into the scenario's workspace, which stands in for the
/// repository and so also holds the family's schema; the real <c>polypiler-tooling</c> executable
/// validates them there. A step that describes a document checks the fixture against the description.
/// </summary>
[Binding, Scope(Feature = "Schema and validator for spec/binary/")]
public sealed class SpecBinarySteps(ScenarioWorkspace workspace, CommandLog log) {

  private const string Fixtures = "tests/fixtures/spec/binary";

  private const string Schema = "spec/schemas/binary.schema.json";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private string _document = "";

  private string _fixture = "";

  /// <summary>Puts the family's schema where the tool looks for it from the workspace.</summary>
  [BeforeScenario]
  public void CopySchema() => workspace.Copy(Path.Combine(_root, Schema), Schema);

  /// <summary>Copies a fixture tree to the same path in the workspace.</summary>
  [Given("^the spec tree " + StepText.Quoted + "$")]
  public void SpecTree(string tree) {
    var source = Path.Combine(_root, tree);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
      workspace.Copy(file, $"{tree}/{Relative(source, file)}");
  }

  /// <summary>Writes <c>spec/binary/</c> with the valid documents and the variant fixture, after checking the variant against the description.</summary>
  [Given("^the spec document " + StepText.Quoted + @" with cpu \{name: ([^,]+), variant: ([^,]+), parent: ([^,]+), byteOrder: ([^}]+)\} adding opcode "
    + StepText.Quoted + @" (\S+) with mode " + StepText.Quoted + @" \(operandBytes (\d+)\) and length (\d+)$")]
  public void VariantDocument(string path, string name, string variant, string parent, string byteOrder, string opcode, string mnemonic, string mode, long operandBytes, long length) {
    this.CopyValidDocuments();
    this._document = $"spec/binary/{path}";
    workspace.Copy(Path.Combine(_root, Fixtures, "variant", "binary", path), this._document);

    var body = SpecDocument.Parse($"binary/{path}", File.ReadAllText(workspace.Resolve(this._document))).Body!;
    var cpu = body["cpu"]!;
    Assert.That(((string?)cpu["name"], (string?)cpu["variant"], (string?)cpu["parent"], (string?)cpu["byteOrder"]), Is.EqualTo((name, variant, parent, byteOrder)));
    var instruction = ((JsonArray)body["instructions"]!).Single(entry => (string?)entry!["opcode"] == opcode)!;
    Assert.That(((string?)instruction["mnemonic"], (string?)instruction["mode"], (long?)instruction["length"]), Is.EqualTo((mnemonic, mode, length)));
    var declared = ((JsonArray)body["modes"]!).Single(entry => (string?)entry!["name"] == mode)!;
    Assert.That((long?)declared["operandBytes"], Is.EqualTo(operandBytes));
  }

  /// <summary>Runs a command line over the tree the previous step wrote.</summary>
  [When("^I run " + StepText.Quoted + " on the valid tree plus that document$")]
  public void RunOnVariantTree(string line) => this.Run(line);

  /// <summary>Gives the variant another parent, validates again and checks the diagnostic and the exit code 1.</summary>
  [Then("^changing its parent to " + StepText.Quoted + " gives " + StepText.Quoted + "$")]
  public void ChangedParent(string parent, string text) {
    var full = workspace.Resolve(this._document);
    var original = File.ReadAllText(full);
    var changed = original.Replace("parent: BINARY.mos6502.instructions", $"parent: {parent}", StringComparison.Ordinal);
    Assert.That(changed, Is.Not.EqualTo(original));
    workspace.Write(this._document, changed);
    this.Run("polypiler-tooling spec validate --family binary");
    Assert.That((log.Last.ExitCode, log.Last.Stderr.Contains(StepText.Unescape(text), StringComparison.Ordinal)), Is.EqualTo((1, true)), log.Last.Output);
  }

  /// <summary>Remembers a fixture document.</summary>
  [Given("^the spec fixture " + StepText.Quoted + "$")]
  public void SpecFixture(string path) => this._fixture = File.ReadAllText(Path.Combine(_root, path));

  /// <summary>
  /// Writes the remembered fixture to <c>spec/binary/&lt;name&gt;/&lt;kind&gt;.yaml</c> as its ID
  /// <c>BINARY.&lt;name&gt;.&lt;kind&gt;</c> names, with the valid document its CPU's parent names,
  /// and runs a command line.
  /// </summary>
  [When("^I run " + StepText.Quoted + " on a tree containing only that fixture and the valid documents it refers to$")]
  public void RunOnFixtureTree(string line) {
    var document = SpecDocument.Parse("binary/fixture.yaml", this._fixture);
    workspace.Write(PathOf(document.Id!), this._fixture);
    if (document.Body?["cpu"]?["parent"] is { } parent)
      workspace.Copy(Path.Combine(_root, Fixtures, "valid", PathOf((string)parent!)["spec/".Length..]), PathOf((string)parent!));
    this.Run(line);
  }

  /// <summary>Checks the last line of standard output.</summary>
  [Then("^the last output line is " + StepText.Quoted + "$")]
  public void LastOutputLine(string text)
    => Assert.That(CommandSteps.WithoutTrailingNewline(log.Last.Stdout).Split('\n')[^1].TrimEnd('\r'), Is.EqualTo(StepText.Unescape(text)), log.Last.Output);

  /// <summary>Where a document with the ID <c>BINARY.&lt;name&gt;.&lt;kind&gt;</c> lives in the workspace.</summary>
  private static string PathOf(string id) {
    var parts = id.Split('.');
    return $"spec/binary/{parts[1]}/{parts[2]}.yaml";
  }

  private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

  private void CopyValidDocuments() {
    var source = Path.Combine(_root, Fixtures, "valid");
    foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories))
      workspace.Copy(file, $"spec/{Relative(source, file)}");
  }

  private void Run(string line) {
    var (target, arguments) = CommandSteps.Route([.. CommandLine.Split(line)]);
    Assert.That(target, Is.EqualTo(CommandTarget.Tooling), line);
    log.Add(Toolchain.Tooling(workspace.Path, arguments));
  }

}
