using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/ir/ir-sourcemap.feature</c>: origins on the statements of the IR's
/// trees, carried by <see cref="IrOrigins"/>, checked by <see cref="IrVerifier"/> and exported by
/// <see cref="SourceMapWriter"/>. The spans of the fixture are checked against the bytes of the pinned
/// file with an index of line starts of the steps' own.
/// </summary>
[Binding, Scope(Feature = "Source maps")]
public sealed class SourceMapSteps {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  /// <summary>The schema of the map, built once: the registry of the validator refuses a second schema of the same ID.</summary>
  private static readonly Lazy<JsonSchema> _schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(_root, "spec", "ir", "sourcemap.schema.json"))));

  private static readonly IrLocal _r = new("r", IrType.U32), _c = new("c", IrType.Bool), _a = new("a", IrType.U32);

  private IReadOnlyList<SourceArtifact> _sources = [];
  private readonly List<IrStmt> _statements = [];
  private IrStmt? _result;
  private IrModule? _module;
  private IReadOnlyList<Diagnostic> _diagnostics = [];
  private List<(Origin Origin, string? Lexeme)> _expected = [];
  private string _map = "";
  private string _suffixes = "";

  private static IReadOnlyList<Origin> Origins(string text, int sources) {
    Assert.That(Origin.TryParse(text, sources, out var origins, out var diagnostic), Is.True, diagnostic?.ToString());
    return origins;
  }

  private static IrModule Module(IReadOnlyList<SourceArtifact> sources, IEnumerable<IrStmt> statements)
    => new("M", [new IrFunction("Main", true, IrType.Void, [], [], new IrBlock([.. statements]))]) { Sources = sources };

  private static IrAssign Select(long constant) => new(_r, new IrConditional(new IrLocalRef(_c), new IrLocalRef(_a), new IrConst(IrType.U32, constant)));

  /// <summary>Declares one source at the given path.</summary>
  [Given("^the module declares the source " + StepText.Quoted + "$")]
  public void Source(string path) => this._sources = [new SourceArtifact("Hawkynt/Polypiler", "main", path, new string('0', 64), "pb/3.5")];

  /// <summary>An assignment of a choice between a parameter and a constant, with origins.</summary>
  [Given("^the assignment \"%r = Select %c, %a, ([0-9]+)\" with the origins " + StepText.Quoted + "$")]
  public void Assignment(long constant, string origins) => this._statements.Add(Select(constant) with { Origins = Origins(origins, this._sources.Count) });

  /// <summary>Replaces the assignment by one with another constant.</summary>
  [When("^its constant is replaced by ([0-9]+) through IrOrigins.Replace$")]
  public void Replace(long constant) => this._result = IrOrigins.Replace(Select(constant), this._statements.Single());

  /// <summary>The origins of the result as text.</summary>
  [Then("^the printed origins of the replacement are " + StepText.Quoted + "$")]
  [Then("^the new instruction prints " + StepText.Quoted + "$")]
  public void Printed(string expected) => Assert.That(Origin.Format(this._result!.Origins), Is.EqualTo(expected));

  /// <summary>Two instructions, each at one span of source 0.</summary>
  [Given("^instructions %a at " + StepText.Quoted + " and %b at " + StepText.Quoted + "$")]
  public void Instructions(string first, string second) {
    foreach (var (span, value) in new[] { (first, 1L), (second, 2L) })
      this._statements.Add(new IrExprStmt(new IrConst(IrType.U32, value)) { Origins = Origins($"!loc({span})", 1) });
  }

  /// <summary>Merges the instructions into one.</summary>
  [When("^a rewrite replaces both by one instruction$")]
  public void Merge() => this._result = IrOrigins.Replace(new IrExprStmt(new IrConst(IrType.U32, 3)), this._statements);

  /// <summary>A module that declares a source and holds one instruction without origin.</summary>
  [Given("^a module with sources whose instruction %t has no origin$")]
  public void Unmarked() {
    this.Source("demo.bas");
    this._statements.Add(new IrExprStmt(new IrConst(IrType.U32, 1)));
    this._module = Module(this._sources, this._statements);
  }

  /// <summary>Runs the verifier.</summary>
  [When("^the module is verified$")]
  public void Verify() => this._diagnostics = IrVerifier.Verify(this._module!);

  /// <summary>The verifier refused the instruction.</summary>
  [Then("^verification fails with (PLY[0-9]{4}) naming " + StepText.Quoted + "$")]
  public void Refused(string code, string instruction)
    => Assert.That(this._diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message.Contains($"instruction {instruction} in '@Main'", StringComparison.Ordinal))),
      Is.EqualTo(new[] { (code, true) }));

  /// <summary>Marks the instruction as made by a pass and verifies again.</summary>
  [Then("^after marking %t with Synthetic\\(\"([A-Z0-9]+)\"\\) verification succeeds$")]
  public void Marked(string pass)
    => Assert.That(IrVerifier.Verify(Module(this._sources, [IrOrigins.MarkSynthetic(this._statements.Single(), pass)])), Is.Empty);

  /// <summary>Reads the fixture: the table of sources, then one origin per line with the lexeme it points at after a tab.</summary>
  [Given("^" + StepText.Quoted + " whose spans refer to the pinned FIXBCD.BAS$")]
  public void Fixture(string path) {
    var lines = File.ReadAllText(Path.Combine(_root, path)).Split('\n');
    Assert.That(SourceArtifact.TryParseTable(string.Join("\n", lines.TakeWhile(line => line != "}")) + "\n}", out var sources, out var diagnostic), Is.True, diagnostic?.ToString());
    this._sources = sources;
    this._expected = [.. lines.SkipWhile(line => line != "}").Skip(1).Where(line => line.Length > 0).Select(line => line.Split('\t'))
      .Select(parts => (Origins(parts[0], sources.Count).Single(), parts.Length > 1 ? parts[1] : null))];
    this._module = Module(sources, this._expected.Select((entry, index) => new IrExprStmt(new IrConst(IrType.U32, index)) { Origins = [entry.Origin] }));
  }

  /// <summary>Writes the map of the function.</summary>
  [When("^the source map is written$")]
  public void Write() => this._map = SourceMapWriter.Write(this._module!, this._module!.Functions[0]);

  /// <summary>Validates the map against its schema.</summary>
  [Then("^the JSON validates against spec/ir/sourcemap.schema.json$")]
  public void Valid() {
    var schema = _schema.Value;
    using var document = JsonDocument.Parse(this._map);
    Assert.That(schema.Evaluate(document.RootElement).IsValid, Is.True, this._map);
    Assert.That(document.RootElement.GetProperty("mappings").GetArrayLength(), Is.EqualTo(this._expected.Count));
  }

  /// <summary>Slices the bytes of the pinned file at every span of the map and compares the lexeme.</summary>
  [Then("^for every span the text of FIXBCD.BAS at that span equals the fixture's expected lexeme$")]
  public void Lexemes() {
    var bytes = File.ReadAllBytes(Path.Combine(_root, "tests", "fixtures", "ir", "sourcemap", "FIXBCD.BAS"));
    Assert.That(bytes, Has.All.LessThan(0x80), "the file is ASCII, so a column is a byte");
    var starts = new List<int> { 0 };
    starts.AddRange(bytes.Select((value, index) => (value, index)).Where(entry => entry.value == (byte)'\n').Select(entry => entry.index + 1));
    using var map = JsonDocument.Parse(this._map);
    var mappings = map.RootElement.GetProperty("mappings").EnumerateArray().ToList();
    for (var index = 0; index < this._expected.Count; ++index) {
      var origin = mappings[index].GetProperty("origins")[0];
      var lexeme = this._expected[index].Lexeme;
      if (origin.TryGetProperty("startLine", out var line)) {
        var from = starts[line.GetInt32() - 1] + origin.GetProperty("startColumn").GetInt32() - 1;
        var to = starts[origin.GetProperty("endLine").GetInt32() - 1] + origin.GetProperty("endColumn").GetInt32() - 1;
        Assert.That(Encoding.ASCII.GetString(bytes, from, to - from), Is.EqualTo(lexeme));
      } else if (origin.TryGetProperty("offset", out var offset)) {
        Assert.That(Encoding.ASCII.GetString(bytes, offset.GetInt32(), origin.GetProperty("length").GetInt32()), Is.EqualTo(lexeme));
      } else {
        Assert.That(lexeme, Is.Null);
      }
    }
  }

  /// <summary>A module of the given number of sources and the suffixes of one instruction.</summary>
  [Given("^a module declaring ([0-9]+) source and an instruction with " + StepText.Quoted + "$")]
  public void Declaring(int count, string suffixes) {
    this._sources = [.. Enumerable.Range(0, count).Select(index => new SourceArtifact("Hawkynt/Polypiler", "main", $"s{index}.bas", new string('0', 64), "pb/3.5"))];
    this._suffixes = suffixes;
  }

  /// <summary>Reads the suffixes against the sources.</summary>
  [When("^it is parsed$")]
  public void Parse() {
    Assert.That(Origin.TryParse(this._suffixes, this._sources.Count, out _, out var diagnostic), Is.False);
    this._diagnostics = [diagnostic!];
  }

  /// <summary>The suffixes were refused with the code.</summary>
  [Then("^parsing fails with (PLY[0-9]{4})$")]
  public void ParseRefused(string code) => Assert.That(this._diagnostics.Single().Code, Is.EqualTo(code));

}
