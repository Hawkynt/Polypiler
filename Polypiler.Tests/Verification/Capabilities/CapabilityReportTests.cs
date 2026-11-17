using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Capabilities;
using Polypiler.Core.Verification.Gates;

namespace Polypiler.Tests.Verification.Capabilities;

/// <summary>
/// <see cref="CapabilityReport"/>: the pairs computed from the spec trees of
/// <c>tests/fixtures/verify/capabilities/</c> and gate records written by the real recorder, the two
/// files it writes and the reading of the JSON file.
/// </summary>
[TestFixture]
public sealed class CapabilityReportTests {

  private static readonly CapabilityLimitation _decimal = new("decimal arithmetic", "PLY1003");

  private static string States(CapabilityReport report)
    => string.Join(", ", report.Pairs.Select(pair => $"{pair.Name}={GateStates.Name(pair.State)}"));

  private static string Json(CapabilityReport report) {
    var writer = new StringWriter();
    report.WriteJson(writer);
    return writer.ToString();
  }

  private static string Markdown(CapabilityReport report) {
    var writer = new StringWriter();
    report.WriteMarkdown(writer);
    return writer.ToString();
  }

  private static CapabilityPair Planned(string from, string sourceProfile, string to, string targetProfile, params CapabilityLimitation[] limitations)
    => new(from, sourceProfile, to, targetProfile, GateState.Planned, 0, null, limitations);

  /// <summary>Scenario "Without gate records every pair is planned".</summary>
  [Test]
  public void WithoutGateRecords_EveryPairIsPlanned() {
    using var workspace = new CapabilityWorkspace("one-verified");
    var (report, diagnostics) = workspace.Build();
    Assert.That(diagnostics, Is.Empty);
    Assert.That(report.Pairs, Has.Count.EqualTo(4));
    Assert.That(report.Pairs.Select(pair => (pair.State, pair.Fixtures, pair.Evidence)), Is.All.EqualTo((GateState.Planned, 0, (string?)null)));
    Assert.That(report.GateRecords, Is.Zero);
  }

  /// <summary>Test design 1, 5 and 7: one verified pair among four, the same-language pairs rows like any other, and the limitations of 1 and 0 diagnosed constructs.</summary>
  [Test]
  public void OneVerifiedPair_IsReportedAtItsState_AndTheOthersStayPlanned() {
    using var workspace = new CapabilityWorkspace("one-verified");
    workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    var (report, diagnostics) = workspace.Build();
    Assert.That(diagnostics, Is.Empty);
    Assert.That(report.Pairs, Is.EqualTo(new[] {
      Planned("csharp", "net10", "csharp", "net10", _decimal),
      new CapabilityPair("csharp", "net10", "javascript", "node-es2022", GateState.Verified, 1, "artifacts/gates/csharp--javascript--net10--node-es2022.json", [_decimal]),
      Planned("javascript", "node-es2022", "csharp", "net10"),
      Planned("javascript", "node-es2022", "javascript", "node-es2022")
    }).Using<CapabilityPair>((left, right) => left.SameAs(right)));
    Assert.That(report.StateOf("csharp", "net10", "javascript", "node-es2022"), Is.EqualTo(GateState.Verified));
    Assert.That(report.StateOf("javascript", "node-es2022", "csharp", "net10"), Is.EqualTo(GateState.Planned));
    Assert.That(report.StateOf("cobol", "x", "csharp", "net10"), Is.EqualTo(GateState.Planned));
    Assert.That(report.GateRecords, Is.EqualTo(1));
  }

  /// <summary>Test design 3 and 12: each of the ten evidenced states, the lowest and the highest included, is reported verbatim.</summary>
  [Test]
  public void EveryState_IsReportedVerbatim([ValueSource(typeof(GateStates), nameof(GateStates.Evidenced))] GateState state) {
    using var workspace = new CapabilityWorkspace("one-verified");
    workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", state);
    Assert.That(workspace.Build().Report.StateOf("csharp", "net10", "javascript", "node-es2022"), Is.EqualTo(state));
  }

  /// <summary>Test design 4: the optional states skipped or present, the chain up to verified.</summary>
  [TestCase(false)]
  [TestCase(true)]
  public void OptionalStates_MayBeSkipped(bool optional) {
    using var workspace = new CapabilityWorkspace("one-verified");
    workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified, optional);
    Assert.That(workspace.Build().Report.StateOf("csharp", "net10", "javascript", "node-es2022"), Is.EqualTo(GateState.Verified));
  }

  /// <summary>Test design 10 and 11, and the scenario "The weakest fixture decides the pair".</summary>
  [TestCase(new GateState[0], GateState.Planned, 0)]
  [TestCase(new[] { GateState.Executed }, GateState.Executed, 1)]
  [TestCase(new[] { GateState.Verified, GateState.Emitted }, GateState.Emitted, 2)]
  [TestCase(new[] { GateState.Compiled, GateState.Executed }, GateState.Compiled, 2)]
  public void TheWeakestFixture_DecidesThePair(GateState[] fixtures, GateState expected, int count) {
    using var workspace = new CapabilityWorkspace("one-verified");
    for (var index = 0; index < fixtures.Length; ++index)
      workspace.Record(CapabilityWorkspace.CSharpToJavaScript, index == 0 ? "add32" : "mul64", fixtures[index]);
    var pair = workspace.Build().Report.Find("csharp", "net10", "javascript", "node-es2022")!;
    Assert.That((pair.State, pair.Fixtures), Is.EqualTo((expected, count)));
  }

  /// <summary>Test design 7: the limitations of 0, 1 and 3 diagnosed constructs, in document order, on every pair of the source profile.</summary>
  [Test]
  public void Limitations_AreTheDiagnosedConstructs_InDocumentOrder() {
    using var workspace = new CapabilityWorkspace("mixed");
    var report = workspace.Build().Report;
    Assert.That(report.Pairs, Has.Count.EqualTo(9));
    Assert.That(report.Pairs.Where(pair => pair.SourceProfile == "net8").Select(pair => pair.Limitations), Has.All.EqualTo(new[] {
      new CapabilityLimitation("goto statement", "PLY1002"), new CapabilityLimitation("lambda", "PLY1002"), new CapabilityLimitation("string", "PLY1002")
    }));
    Assert.That(report.Find("csharp", "net10", "csharp", "net8")!.Limitations, Is.EqualTo(new[] { _decimal }));
    Assert.That(report.Find("javascript", "node-es2022", "csharp", "net8")!.Limitations, Is.Empty);
  }

  /// <summary>Test design 8: no language, no pair.</summary>
  [Test]
  public void AnEmptySpecTree_HasNoPair() {
    using var workspace = new CapabilityWorkspace("empty");
    var (report, diagnostics) = workspace.Build();
    Assert.That((report.Pairs, diagnostics), Is.EqualTo((Array.Empty<CapabilityPair>(), Array.Empty<string>())));
    Assert.That(Json(report), Does.Contain("\"pairs\": []"));
    Assert.That(Markdown(report), Does.Contain("so the report has no pair"));
  }

  /// <summary>Test design 9: one source profile and one target profile give one pair; duplicates count once.</summary>
  [Test]
  public void OneSourceAndOneTarget_GiveOnePair() {
    var source = new CapabilitySource("csharp", "net10", []);
    var target = new CapabilityTarget("javascript", "node-es2022");
    var report = CapabilityReport.Build([source, source], [target, target], GateStore.Load("nowhere", new DiagnosticBag()), "artifacts/gates", CapabilityWorkspace.Commit, new DiagnosticBag());
    Assert.That(report.Pairs.Select(pair => pair.Name), Is.EqualTo(new[] { "csharp/net10 -> javascript/node-es2022" }));
  }

  /// <summary>A profile whose name cannot name a gate record is planned, and so is its pair.</summary>
  [Test]
  public void AProfileThatCannotNameARecord_IsPlanned() {
    var report = CapabilityReport.Build([new("C#", "net10", [])], [new("javascript", "Node")], GateStore.Load("nowhere", new DiagnosticBag()), "artifacts\\gates\\", CapabilityWorkspace.Commit, new DiagnosticBag());
    Assert.That(report.Pairs.Single().State, Is.EqualTo(GateState.Planned));
  }

  /// <summary>Test design 13: a record of a pair without spec documents is a PLY0061, not a row.</summary>
  [Test]
  public void ARecordWithoutSpecDocuments_IsPly0061() {
    using var workspace = new CapabilityWorkspace("one-verified");
    workspace.Record(new("cobol", "csharp", "x", "net10"), "add32", GateState.Parsed);
    var (report, diagnostics) = workspace.Build();
    Assert.That(diagnostics, Is.EqualTo(new[] { "PLY0061 gate record 'artifacts/gates/cobol--csharp--x--net10.json' names a pair with no language or target document" }));
    Assert.That(report.Pairs.Select(pair => pair.State), Is.All.EqualTo(GateState.Planned));
    Assert.That(report.GateRecords, Is.Zero);
  }

  /// <summary>Test design 14: an invalid record (missing evidence file, wrong hash, kind mismatch) gives PLY0058 and leaves its pair planned.</summary>
  [TestCase("missing")]
  [TestCase("hash")]
  [TestCase("kind")]
  public void AnInvalidRecord_LeavesItsPairPlanned(string damage) {
    using var workspace = new CapabilityWorkspace("one-verified");
    workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    var record = Path.Combine(workspace.Gates, CapabilityWorkspace.CSharpToJavaScript.FileName);
    var manifest = workspace.PathOf("artifacts/evidence/manifests/verified.json");
    if (damage == "missing")
      File.Delete(manifest);
    else if (damage == "hash")
      File.AppendAllText(manifest, " ");
    else
      File.WriteAllText(record, File.ReadAllText(record).Replace("\"kind\": \"frontend-log\"", "\"kind\": \"ir-dump\"", StringComparison.Ordinal));

    var (report, diagnostics) = workspace.Build();
    Assert.That(diagnostics, Is.Not.Empty.And.All.StartWith("PLY0058 "));
    Assert.That(report.StateOf("csharp", "net10", "javascript", "node-es2022"), Is.EqualTo(GateState.Planned));
  }

  /// <summary>The JSON file: the shape of the specification, two-space indentation, LF and one final line break.</summary>
  [Test]
  public void TheJson_HasTheSpecifiedShape() {
    using var workspace = new CapabilityWorkspace("one-verified");
    workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    var json = Json(workspace.Build().Report);
    Assert.That(json, Does.StartWith("{\n  \"schemaVersion\": 1,\n  \"generatedFrom\": {\n    \"commit\": \"" + CapabilityWorkspace.Commit + "\",\n    \"gateRecords\": 1\n  },\n  \"states\": [\n    \"planned\",\n    \"parsed\","));
    Assert.That(json, Does.Contain("""
          "from": "csharp",
          "sourceProfile": "net10",
          "to": "javascript",
          "targetProfile": "node-es2022",
          "state": "verified",
          "fixtures": 1,
          "evidence": "artifacts/gates/csharp--javascript--net10--node-es2022.json",
          "limitations": [
            {
              "construct": "decimal arithmetic",
              "diagnostic": "PLY1003"
            }
          ]
    """.Replace("\r\n", "\n", StringComparison.Ordinal)));
    Assert.That(json, Does.Contain("\"evidence\": null"));
    Assert.That(json, Does.EndWith("]\n}\n").And.Not.Contain("\r"));
  }

  /// <summary>The Markdown file: the header line, one table per source language, the limitations and the legend of eleven states.</summary>
  [Test]
  public void TheMarkdown_HasOneTablePerSourceLanguage_AndTheLegend() {
    using var workspace = new CapabilityWorkspace("one-verified");
    workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    var markdown = Markdown(workspace.Build().Report);
    Assert.That(markdown, Does.StartWith("<!-- generated by polypiler-tooling report capabilities; do not edit -->\n# Capabilities\n"));
    Assert.That(markdown, Does.Contain($"commit `{CapabilityWorkspace.Commit}` and 1 gate record."));
    Assert.That(markdown, Does.Contain("""

      ## csharp

      | Source profile | csharp/net10 | javascript/node-es2022 |
      | --- | --- | --- |
      | net10 | planned | verified |

      Known limitations, the constructs a source profile diagnoses instead of translating:

      - `net10`: decimal arithmetic (PLY1003)

      ## javascript

      | Source profile | csharp/net10 | javascript/node-es2022 |
      | --- | --- | --- |
      | node-es2022 | planned | planned |

      ## States

      """.Replace("\r\n", "\n", StringComparison.Ordinal)));
    var legend = markdown[markdown.IndexOf("## States", StringComparison.Ordinal)..].Split('\n').Where(line => line.Length > 0).Skip(1).ToList();
    Assert.That(legend, Has.Count.EqualTo(11));
    Assert.That(legend[0], Is.EqualTo("1. `planned`: no gate record proves anything yet"));
    Assert.That(legend[^1], Is.EqualTo("11. `performance-validated`: the target code met its benchmark"));
  }

  /// <summary>The Markdown of pairs that are not every combination: a missing cell is a dash; bars are escaped; a limitation without a code names none.</summary>
  [Test]
  public void TheMarkdown_OfAnIncompleteReport() {
    var report = new CapabilityReport(CapabilityWorkspace.Commit, 0, [
      Planned("a|b", "p", "x", "1", new CapabilityLimitation("line\nbreak", "")),
      Planned("a|b", "q", "x", "2")
    ]);
    var markdown = Markdown(report);
    Assert.That(markdown, Does.Contain("## a\\|b\n\n| Source profile | x/1 | x/2 |\n| --- | --- | --- |\n| p | planned | — |\n| q | — | planned |\n"));
    Assert.That(markdown, Does.Contain("- `p`: line break\n"));
    Assert.That(markdown, Does.Contain("and 0 gate records."));
  }

  /// <summary>Scenario "The same inputs give the same bytes": two builds of the tree "mixed" write the same JSON and Markdown.</summary>
  [Test]
  public void TheSameInputs_GiveTheSameBytes() {
    using var workspace = new CapabilityWorkspace("mixed");
    workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.Verified);
    workspace.Record(new("csharp", "csharp", "net8", "net10"), "add32", GateState.Lifted);
    var (first, second) = (workspace.Build().Report, workspace.Build().Report);
    Assert.That(Json(first), Is.EqualTo(Json(second)));
    Assert.That(Markdown(first), Is.EqualTo(Markdown(second)));
  }

  /// <summary>Load reads back what WriteJson wrote.</summary>
  [Test]
  public void Load_ReadsBackWhatWasWritten() {
    using var workspace = new CapabilityWorkspace("mixed");
    workspace.Record(CapabilityWorkspace.CSharpToJavaScript, "add32", GateState.PerformanceValidated);
    var report = workspace.Build().Report;
    var loaded = CapabilityReport.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json(report))));
    Assert.That((loaded.Commit, loaded.GateRecords), Is.EqualTo((report.Commit, report.GateRecords)));
    Assert.That(loaded.Pairs, Is.EqualTo(report.Pairs).Using<CapabilityPair>((left, right) => left.SameAs(right)));
    Assert.That(Json(loaded), Is.EqualTo(Json(report)));
  }

  /// <summary>Test design 17 in the reader, and every other way a file is no report.</summary>
  [TestCase("\"state\": \"planned\"", "\"state\": \"supported\"", "/pairs/0/state: ")]
  [TestCase("\"fixtures\": 0", "\"fixtures\": 2", "/pairs/0/fixtures: ")]
  [TestCase("\"schemaVersion\": 1", "\"schemaVersion\": 2", "/schemaVersion: ")]
  [TestCase("\"schemaVersion\": 1,", "\"schemaVersion\": 1,,", "not JSON: ")]
  public void Parse_NamesWhatIsWrong(string original, string replacement, string error) {
    var text = Json(new CapabilityReport(CapabilityWorkspace.Commit, 0, [Planned("csharp", "net10", "csharp", "net10")]));
    Assert.That(CapabilityReport.Parse(text.Replace(original, replacement, StringComparison.Ordinal), out var errors), Is.Null);
    Assert.That(errors, Has.Some.StartWith(error));
    Assert.That(() => CapabilityReport.Load(new MemoryStream(Encoding.UTF8.GetBytes(text.Replace(original, replacement, StringComparison.Ordinal)))),
      Throws.TypeOf<InvalidDataException>().With.Message.StartWith("no capability report: "));
  }

  [Test]
  public void Parse_RejectsAPairListedTwice() {
    var text = Json(new CapabilityReport(CapabilityWorkspace.Commit, 0, [Planned("csharp", "net10", "csharp", "net10"), Planned("csharp", "net10", "csharp", "net8")]))
      .Replace("\"targetProfile\": \"net8\"", "\"targetProfile\": \"net10\"", StringComparison.Ordinal);
    Assert.That(CapabilityReport.Parse(text, out var errors), Is.Null);
    Assert.That(errors, Is.EqualTo(new[] { "/pairs/1: the pair csharp/net10 -> csharp/net10 is listed twice" }));
  }

  [Test]
  public void TheConstructor_RefusesWhatTheFileCannotHold() {
    Assert.That(() => new CapabilityReport("abc", 0, []), Throws.ArgumentException.With.Message.Contains("40 lower-case hexadecimal digits"));
    Assert.That(() => new CapabilityReport(CapabilityWorkspace.Commit, -1, []), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new CapabilityReport(CapabilityWorkspace.Commit, 0, [Planned("a", "b", "c", "d"), Planned("a", "b", "c", "d")]),
      Throws.ArgumentException.With.Message.Contains("the pair a/b -> c/d is given twice"));
    Assert.That(CapabilityReport.UnknownCommit, Is.EqualTo(new string('0', 40)));
  }

  [Test]
  public void SameAs_ComparesEveryField() {
    var pair = Planned("a", "b", "c", "d", _decimal);
    Assert.That(pair.SameAs(Planned("a", "b", "c", "d", new CapabilityLimitation("decimal arithmetic", "PLY1003"))), Is.True);
    Assert.That(pair.SameAs(Planned("a", "b", "c", "d")), Is.False);
    Assert.That(pair.SameAs(pair with { Fixtures = 1 }), Is.False);
    Assert.That(pair.Key, Is.EqualTo(("a", "b", "c", "d")));
  }

}
