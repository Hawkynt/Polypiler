using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Frontends.VisualBasic;

namespace Polypiler.Tests.Cli;

/// <summary>
/// The static-typing report (#1590, ADR 0300 decision 7): the counts of a module built by hand, the
/// boundaries of the ratio, the reasons of the variants, the schema every fixture's report satisfies,
/// and <c>translate --type-report</c> to a file, to standard output and to a path it cannot write.
/// </summary>
[TestFixture]
public sealed class TypeReportTests {

  private static readonly JsonSchema _schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "type-report.schema.json")));

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-type-report-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static IrModule Module(params IrLocal[] locals)
    => new("M", [new IrFunction("F", true, IrType.Void, [], locals, new IrBlock([new IrReturn(null)]))]);

  private static JsonElement Summary(string json) => JsonDocument.Parse(json).RootElement.GetProperty("summary");

  [Test]
  public void TheReport_CountsThePlaces() {
    var module = Module(new("a", IrType.I32), new("b", IrType.I32), new("c", IrType.I32), new("v", IrType.Variant));
    var json = TypeReport.Json(module, null);
    var summary = Summary(json);
    Assert.That(summary.GetProperty("places").GetInt32(), Is.EqualTo(4));
    Assert.That(summary.GetProperty("levels").GetProperty("exact").GetInt32(), Is.EqualTo(3));
    Assert.That(summary.GetProperty("levels").GetProperty("variant").GetInt32(), Is.EqualTo(1));
    Assert.That(summary.GetProperty("certainty").GetProperty("proven").GetInt32(), Is.EqualTo(3));
    Assert.That(summary.GetProperty("certainty").GetProperty("dynamic").GetInt32(), Is.EqualTo(1));
    Assert.That(summary.GetProperty("staticRatio").GetRawText(), Is.EqualTo("0.75"));
    Assert.That(TypeReport.Places(module, VisualBasicFrontend.Semantics).Single(place => place.Name == "v").Reason, Is.EqualTo("never assigned nor read"));
    var variant = JsonDocument.Parse(json).RootElement.GetProperty("functions")[0].GetProperty("variants")[0];
    Assert.That(variant.GetProperty("name").GetString(), Is.EqualTo("v"));
    Assert.That(variant.GetProperty("reason").GetString(), Does.Contain("no semantics table"));
    Assert.That(_schema.Evaluate(JsonDocument.Parse(json).RootElement).IsValid, Is.True);
  }

  [Test]
  public void TheRatio_OfNoPlaces_AllTyped_AndAllVariants() {
    Assert.That(Summary(TypeReport.Json(new IrModule("M", []), null)).GetProperty("staticRatio").GetRawText(), Is.EqualTo("1.00"), "no places count as typed");
    Assert.That(Summary(TypeReport.Json(Module(new IrLocal("a", IrType.I32)), null)).GetProperty("staticRatio").GetRawText(), Is.EqualTo("1.00"));
    Assert.That(Summary(TypeReport.Json(Module(new IrLocal("v", IrType.Variant)), null)).GetProperty("staticRatio").GetRawText(), Is.EqualTo("0.00"));
    Assert.That(TypeReport.Ratio([]), Is.EqualTo(1m));
    Assert.That(() => TypeReport.Ratio(null!), Throws.ArgumentNullException);
    Assert.That(() => TypeReport.Places(null!, null), Throws.ArgumentNullException);
  }

  [Test]
  public void Levels_FollowTheTypeOfTheIr() {
    var point = new IrStructType("Point");
    point.Define([new IrField("x", IrType.I32, false)]);
    Assert.That(TypeReport.LevelOf(IrType.I32), Is.EqualTo(TypeLevel.Exact));
    Assert.That(TypeReport.LevelOf(IrType.Array(IrType.F64)), Is.EqualTo(TypeLevel.Exact));
    Assert.That(TypeReport.LevelOf(IrType.Struct(point)), Is.EqualTo(TypeLevel.Nominal));
    Assert.That(TypeReport.LevelOf(IrType.Optional(IrType.I32)), Is.EqualTo(TypeLevel.Optional));
    Assert.That(TypeReport.LevelOf(IrType.Union(IrType.I32, IrType.String)), Is.EqualTo(TypeLevel.Union));
    Assert.That(TypeReport.LevelOf(IrType.Variant), Is.EqualTo(TypeLevel.Variant));
    Assert.That(() => TypeReport.LevelOf(null!), Throws.ArgumentNullException);
    var places = TypeReport.Places(new IrModule("M", [new IrFunction("G", true, IrType.Optional(IrType.I32), [new("p", IrType.Struct(point))], [], new IrBlock([new IrReturn(new IrNone(IrType.Optional(IrType.I32)))]))]) { Types = [point] }, null);
    Assert.That(places.Select(place => (place.Name, place.Kind, place.Level, place.Certainty)), Is.EqualTo(new[] {
      ("p", DynamicPlaceKind.Parameter, TypeLevel.Nominal, Certainty.Proven), ("G", DynamicPlaceKind.Result, TypeLevel.Optional, Certainty.Proven)
    }));
  }

  [TestCase("Inference")]
  [TestCase("Variants")]
  [TestCase("Numeric")]
  public void EveryFixturesReport_SatisfiesTheSchema_AndNamesWhyAVariantStaysOne(string fixture) {
    var module = ClassicCases.Import(fixture + ".bas", fixture + ".cases.txt");
    var json = TypeReport.Json(module, VisualBasicFrontend.Semantics);
    var result = _schema.Evaluate(JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
    Assert.That(result.IsValid, Is.True, json);
    var places = TypeReport.Places(module, VisualBasicFrontend.Semantics);
    Assert.That(places.Select(place => place.Function), Has.None.StartWith("Ply"), "the helpers are not counted");
    Assert.That(places.Where(place => place.Level == TypeLevel.Variant).Select(place => place.Reason), Has.All.Not.Null.And.All.Not.Empty);
  }

  private (int ExitCode, string Output, string Error) Translate(params string[] arguments) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var source = Path.Combine(this._directory, "m.bas");
    File.WriteAllText(source, "Public Function F(v)\nDim a, b, c, d\na = CDbl(1)\nb = a * 2\nc = b + a\nd = v\nF = c\nEnd Function\n");
    var exitCode = Program.Run(["translate", source, "--from", "visualbasic", "--to", "csharp", "--output", Path.Combine(this._directory, "out"), .. arguments], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void Translate_WritesTheReportToAFile() {
    var path = Path.Combine(this._directory, "r.json");
    var (exitCode, _, error) = this.Translate("--type-report", path);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success), error);
    var report = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    Assert.That(_schema.Evaluate(report).IsValid, Is.True);
    var f = report.GetProperty("functions").EnumerateArray().Single(function => function.GetProperty("name").GetString() == "F");
    Assert.That(f.GetProperty("counts").GetProperty("levels").GetProperty("exact").GetInt32(), Is.EqualTo(4), "a, b, c and the result local");
    Assert.That(f.GetProperty("variants").EnumerateArray().Select(variant => variant.GetProperty("name").GetString()), Is.EquivalentTo(new[] { "v", "d", "F" }));
    var manifest = File.ReadAllText(Path.Combine(this._directory, "out", "polypiler.manifest.json"));
    Assert.That(manifest, Does.Not.Contain("--type-report"), "the report is no part of the output");
  }

  [Test]
  public void Translate_WritesTheReportToStandardOutput() {
    var (exitCode, output, error) = this.Translate("--type-report", "-");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success), error);
    Assert.That(output, Does.StartWith("{\n  \"schemaVersion\": 1,"));
  }

  [Test]
  public void Translate_ToAPathItCannotWrite_FailsWithoutOutput() {
    var path = Path.Combine(this._directory, "missing", "r.json");
    var (exitCode, _, error) = this.Translate("--type-report", path);
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.EnvironmentError));
    Assert.That(error, Does.Contain("PLY0005"));
    Assert.That(File.Exists(path), Is.False);
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False, "no output is written");
  }

}
