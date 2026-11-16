using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling;
using Polypiler.Tooling.Cipher;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Cipher;

/// <summary>
/// <c>polypiler-tooling cipher kat</c> (#1365) with a runner that stands in for polypiler and dotnet:
/// the golden files, every stage a target can end in, the test project, the table and every command
/// line that is refused. Test design 3 (no, one and many vectors) and 4 (an altered expected value, a
/// build failure and a vector that throws are failures, never passes) are here; the real toolchains
/// run in the acceptance scenarios.
/// </summary>
[TestFixture]
public sealed class CipherKatTests {

  private const string Commit = "48928fc1013dc7d3d42f76b05d0f200dbae23dd1";

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-cipher-kat-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private static string Bytes(string hex) => $$"""{"t":"bytes","v":"{{hex}}"}""";

  private static string Vector(string expected = "0001", string outcome = "passed", string steps = "[]", string input = "", bool deterministic = true, string? output = null, string text = "v")
    => "{\"text\":\"" + text + "\",\"inverse\":false,\"steps\":" + steps + ",\"input\":" + (input.StartsWith('{') ? input : Bytes(input)) + ","
      + (expected.Length == 0 ? "" : "\"expected\":" + Bytes(expected) + ",")
      + "\"node\":{\"outcome\":\"" + outcome + "\"," + (output is null ? "" : "\"output\":" + output + ",") + "\"deterministic\":" + (deterministic ? "true" : "false") + "}}";

  private static string Golden(string file, string algorithms, string? error = null)
    => $$"""{"schema":1,"file":"algorithms/{{file}}","algorithms":[{{algorithms}}]{{(error is null ? "" : $",\"error\":\"{error}\"")}}}""";

  private static string Algorithm(string name, params string[] vectors) => $$"""{"name":"{{name}}","vectors":[{{string.Join(",", vectors)}}]}""";

  // --- the golden files -------------------------------------------------------------------------

  [Test]
  public void Golden_IsRead_WithStepsOfBothKindsAndNodesOutcome() {
    var file = KatFile.Parse(Golden("checksum/x.js", Algorithm("X",
      Vector(steps: """[{"set":"key","value":{"t":"bytes","v":"00ff"}},{"call":"setIV","args":[{"t":"number","v":1}]}]"""),
      Vector(expected: "", outcome: "threw", output: null).Replace("\"deterministic\"", "\"error\":\"boom\",\"deterministic\""))));
    Assert.That(file.Relative, Is.EqualTo("checksum/x.js"));
    var (_, first) = file.Vectors.First();
    Assert.That(first.Steps.Select(step => (step.Name, step.IsCall, step.Values[0].Kind)), Is.EqualTo(new[] { ("key", false, "bytes"), ("setIV", true, "number") }));
    Assert.That(first.Steps[0].Values[0].Bytes, Is.EqualTo(new byte[] { 0, 255 }));
    Assert.That(first.Steps[1].Values[0].Bytes, Is.Null);
    var second = file.Algorithms[0].Vectors[1];
    Assert.That((second.Expected, second.Node.Outcome, second.Node.Error, second.Node.Output), Is.EqualTo(((KatValue?)null, "threw", "boom", (KatValue?)null)));
    Assert.That(KatFile.Parse(Golden("a/b.js", "", "cannot load")).Error, Is.EqualTo("cannot load"));
    Assert.That(new KatFile("other.js", [], null).Relative, Is.EqualTo("other.js"));
  }

  [TestCase("not json", "not JSON")]
  [TestCase("""{"schema":2}""", "not a golden file of schema 1")]
  [TestCase("[]", "not a golden file of schema 1")]
  [TestCase("""{"schema":"1"}""", "not a golden file of schema 1")]
  [TestCase("""{"schema":1}""", "lacks a field")]
  [TestCase("""{"schema":1,"file":"f","algorithms":[{"name":"X","vectors":[{"text":1}]}]}""", "lacks a field")]
  public void Golden_ThatIsNone_IsRefused(string text, string reason)
    => Assert.That(() => KatFile.Parse(text), Throws.TypeOf<FormatException>().With.Message.Contains(reason));

  [Test]
  public void RecordedValue_WithoutAKind_IsRefused() {
    using var document = JsonDocument.Parse("""{"v":1}""");
    Assert.That(() => KatValue.Of(document.RootElement), Throws.TypeOf<FormatException>());
  }

  [TestCase("""{"t":"object","v":"Foo"}""", false)]
  [TestCase("""{"t":"function","v":"f"}""", false)]
  [TestCase("""{"t":"array","v":[{"t":"number","v":1},{"t":"object","v":"Foo"}]}""", false)]
  [TestCase("""{"t":"array","v":[{"t":"number","v":1}]}""", true)]
  [TestCase("""{"t":"string","v":"a"}""", true)]
  public void RecordedValue_IsDataUnlessItHoldsAnObject(string json, bool data) {
    using var document = JsonDocument.Parse(json);
    Assert.That(KatValue.Of(document.RootElement).IsData, Is.EqualTo(data));
  }

  [TestCase("passed", true, "", null)]
  [TestCase("failed", true, "", "node failed")]
  [TestCase("threw", true, "", "node threw")]
  [TestCase("passed", true, """{"t":"object","v":"Cipher"}""", "a value that is an object")]
  public void Vector_IsCheckedOnlyWhereNodePassesItWithData(string outcome, bool deterministic, string step, string? reason) {
    var steps = step.Length == 0 ? "[]" : $$"""[{"set":"cipher","value":{{step}}}]""";
    var vector = KatFile.Parse(Golden("a.js", Algorithm("A", Vector(outcome: outcome, steps: steps, deterministic: deterministic)))).Algorithms[0].Vectors[0];
    Assert.That(vector.Unchecked, Is.EqualTo(reason));
  }

  [Test]
  public void Vector_WithoutAnExpectedResult_IsCheckedOnNodesOwnWhereItIsTheSameEveryRun() {
    var same = KatFile.Parse(Golden("a.js", Algorithm("A", Vector(expected: "", output: Bytes("0a"))))).Algorithms[0].Vectors[0];
    Assert.That((same.Unchecked, same.Answer!.Bytes), Is.EqualTo(((string?)null, new byte[] { 10 })));
    var varying = KatFile.Parse(Golden("a.js", Algorithm("A", Vector(expected: "", output: Bytes("0a"), deterministic: false)))).Algorithms[0].Vectors[0];
    Assert.That(varying.Unchecked, Is.EqualTo("a result that differs from run to run"));
    var text = KatFile.Parse(Golden("a.js", Algorithm("A", Vector(expected: "", output: """{"t":"string","v":"x"}""")))).Algorithms[0].Vectors[0];
    Assert.That(text.Unchecked, Is.EqualTo("a result that is no bytes"));
    var input = KatFile.Parse(Golden("a.js", Algorithm("A", Vector(input: """{"t":"object","v":"X"}""")))).Algorithms[0].Vectors[0];
    Assert.That(input.Unchecked, Is.EqualTo("a value that is an object"));
    var nothing = KatFile.Parse(Golden("a.js", Algorithm("A", Vector(expected: "")))).Algorithms[0].Vectors[0];
    Assert.That((nothing.Answer, nothing.Unchecked), Is.EqualTo(((KatValue?)null, "a result that is no bytes")));
  }

  [Test]
  public void Golden_IsLoadedPlainOrCompressed() {
    var folder = Path.Combine(this._root, "goldens");
    Scratch.Write(folder, "checksum/plain.kat.json", Golden("checksum/plain.js", Algorithm("P", Vector())));
    var compressed = KatFile.PathOf(folder, "hash/big.js") + ".gz";
    Directory.CreateDirectory(Path.GetDirectoryName(compressed)!);
    using (var file = File.Create(compressed))
    using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
      gzip.Write(Encoding.UTF8.GetBytes(Golden("hash/big.js", Algorithm("B", Vector(), Vector()))));
    Assert.That(KatFile.Load(folder, "checksum/plain.js").Algorithms[0].Name, Is.EqualTo("P"));
    Assert.That(KatFile.Load(folder, "hash/big.js").Vectors.Count(), Is.EqualTo(2));
    Assert.That(() => KatFile.Load(folder, "none/such.js"), Throws.InstanceOf<IOException>());
  }

  // --- the harness ------------------------------------------------------------------------------

  /// <summary>Stands in for polypiler and dotnet: translation succeeds unless a target is refused, and a test run writes the TRX it is given.</summary>
  private sealed class FakeRunner : IProcessRunner {

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Func<IReadOnlyList<string>, ProcessResult> Translate { get; set; } = _ => new(0, "", "", false);

    public Func<IReadOnlyList<string>, string?> Trx { get; set; } = _ => null;

    public ProcessResult Test { get; set; } = new(0, "", "", false);

    public ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout) {
      lock (this.Calls)
        this.Calls.Add(argv);
      if (argv.Contains("translate"))
        return this.Translate(argv);

      var results = argv[argv.ToList().IndexOf("--results-directory") + 1];
      if (this.Trx(argv) is { } trx) {
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "kat.trx"), trx);
      }

      return this.Test;
    }

  }

  private static string Trx(params (string Name, string Outcome, string Message)[] results) => $$"""
    <?xml version="1.0" encoding="utf-8"?>
    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
    {{string.Join("\n", results.Select(result => $"<UnitTestResult testName=\"{result.Name}\" outcome=\"{result.Outcome}\">"
      + (result.Message.Length == 0 ? "" : $"<Output><ErrorInfo><Message>{result.Message}\nat line 2</Message></ErrorInfo></Output>") + "</UnitTestResult>"))}}
    </Results></TestRun>
    """;

  private KatHarness Harness(FakeRunner runner, params (string File, string Golden)[] goldens) {
    foreach (var (file, golden) in goldens)
      Scratch.Write(Path.Combine(this._root, "goldens"), Path.ChangeExtension(file, null) + ".kat.json", golden);
    return new KatHarness(runner, ["polypiler"], Path.Combine(this._root, "corpus"), Path.Combine(this._root, "goldens"), Path.Combine(this._root, "work"));
  }

  [Test]
  public void Refusal_IsTheFirstErrorOfTheTranslation() {
    var runner = new FakeRunner { Translate = _ => new(4, "", "note js-oob-read: …\nx.js(1,1): error PLY1102: 'a thing' is not supported yet\nx.js(2,1): error PLY1101: other\n", false) };
    var row = this.Harness(runner, ("checksum/a.js", Golden("checksum/a.js", Algorithm("A", Vector())))).Check("checksum/a.js", [KatTarget.CSharp]);
    Assert.That(row, Is.EqualTo(row with { File = "checksum/a.js", Vectors = 1, Checkable = 1, Note = null }));
    Assert.That((row.Targets[0].Stage, row.Targets[0].Reason, row.Targets[0].AllPassed), Is.EqualTo((KatStage.Refused, "PLY1102: 'a thing' is not supported yet", false)));
    Assert.That(runner.Calls.Single(), Is.EqualTo(new[] {
      "polypiler", "translate", Path.Combine(this._root, "corpus", "algorithms", "checksum", "a.js"), "--from", "javascript", "--to", "csharp",
      "--output", Path.Combine(this._root, "work", "checksum", "a", "csharp", "src"), "--profile", "net10.0"
    }));
  }

  [Test]
  public void TranslationThatTimesOut_IsARefusal() {
    var runner = new FakeRunner { Translate = _ => new(-1, "", "", true) };
    var result = this.Harness(runner, ("a.js", Golden("a.js", Algorithm("A", Vector())))).Check("a.js", [KatTarget.Rust]).Targets[0];
    Assert.That((result.Stage, result.Reason), Is.EqualTo((KatStage.Refused, "the translation timed out")));
    Assert.That(runner.Calls[0], Does.Not.Contain("--profile"), "Rust has no profile of its own");
  }

  /// <summary>Test design 3: a file with no vector is not checkable, translated or not; it never counts as passing.</summary>
  [Test]
  public void FileWithoutAVector_IsTranslatedAndNotRun() {
    var harness = this.Harness(new FakeRunner(),
      ("a.js", Golden("a.js", Algorithm("A"))), ("b.js", Golden("b.js", "")), ("c.js", Golden("c.js", "", "it throws")));
    var a = harness.Check("a.js", [KatTarget.CSharp]);
    Assert.That((a.Note, a.Targets[0].Stage, a.Targets[0].AllPassed), Is.EqualTo(("has no known-answer test", KatStage.Translated, false)));
    Assert.That(harness.Check("b.js", [KatTarget.CSharp]).Note, Is.EqualTo("registers no algorithm"));
    Assert.That(harness.Check("c.js", [KatTarget.CSharp]).Note, Is.EqualTo("node cannot load it: it throws"));
  }

  [Test]
  public void TargetWithoutATestProject_IsTranslatedAndNotRun() {
    var result = this.Harness(new FakeRunner(), ("a.js", Golden("a.js", Algorithm("A", Vector())))).Check("a.js", [new KatTarget("python", "python", null)]).Targets[0];
    Assert.That((result.Stage, result.Reason), Is.EqualTo((KatStage.Translated, "no test project for python yet")));
  }

  /// <summary>Test design 3 and 4: one vector passes, an altered one fails with its first differing byte, one that never ran fails too.</summary>
  [Test]
  public void TestRun_CountsEveryVectorThatPassed_AndNamesEveryOneThatDidNot() {
    var runner = new FakeRunner {
      Trx = _ => Trx(("A #1", "Passed", ""), ("A #2", "Failed", "first differing byte at 0: expected 0x01, got 0x00 (2 bytes expected, 2 given)")),
      Test = new(1, "", "", false)
    };
    var harness = this.Harness(runner, ("a.js", Golden("a.js", Algorithm("A", Vector(), Vector(expected: "0101"), Vector(text: "third")))));
    var result = harness.Check("a.js", [KatTarget.CSharp]).Targets[0];
    Assert.That((result.Stage, result.Passed, result.AllPassed), Is.EqualTo((KatStage.Ran, 1, false)));
    Assert.That(result.Failures, Is.EqualTo(new[] { "A #2: first differing byte at 0: expected 0x01, got 0x00 (2 bytes expected, 2 given)", "A #3: did not run" }));
    var project = Path.Combine(this._root, "work", "a", "csharp");
    Assert.That(File.ReadAllText(Path.Combine(project, "Kat.Tests.csproj")), Does.Contain("NUnit3TestAdapter"));
    Assert.That(File.ReadAllText(Path.Combine(project, "KatTests.cs")), Does.Contain("[TestCaseSource(nameof(Vectors))]"));
    Assert.That(File.ReadAllText(Path.Combine(project, "Directory.Build.props")), Is.EqualTo("<Project />\n"));
    using var kats = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "kats.json")));
    Assert.That(kats.RootElement.EnumerateArray().Select(vector => (vector.GetProperty("id").GetString(), vector.GetProperty("answer").GetString())),
      Is.EqualTo(new[] { ("A #1", "0001"), ("A #2", "0101"), ("A #3", "0001") }));
    Assert.That(runner.Calls[1].Take(3), Is.EqualTo(new[] { "dotnet", "test", Path.Combine(project, "Kat.Tests.csproj") }));
  }

  [Test]
  public void TestRun_WherenEveryVectorPasses_Passes() {
    var runner = new FakeRunner { Trx = _ => Trx(("A #1", "Passed", "")) };
    var inverse = Vector(steps: """[{"set":"key","value":{"t":"bytes","v":"0102"}},{"call":"setRounds","args":[{"t":"number","v":12}]}]""").Replace("\"inverse\":false", "\"inverse\":true");
    var harness = this.Harness(runner, ("a.js", Golden("a.js", Algorithm("A", inverse, Vector(outcome: "failed")))));
    var row = harness.Check("a.js", [KatTarget.CSharp]);
    Assert.That((row.Vectors, row.Checkable, row.Targets[0].AllPassed), Is.EqualTo((2, 1, true)));
    using (var kats = JsonDocument.Parse(File.ReadAllText(Path.Combine(this._root, "work", "a", "csharp", "kats.json")))) {
      var vector = kats.RootElement[0];
      Assert.That(vector.GetProperty("inverse").GetRawText().Replace(" ", "").Replace("\n", ""), Is.EqualTo("""{"t":"bool","v":true}"""));
      Assert.That(vector.GetProperty("steps").GetRawText().Replace(" ", "").Replace("\n", ""),
        Is.EqualTo("""[{"name":"key","call":false,"values":[{"t":"bytes","v":"0102"}]},{"name":"setRounds","call":true,"values":[{"t":"number","v":12}]}]"""));
    }

    Directory.CreateDirectory(Path.Combine(this._root, "work", "a", "csharp", "stale"));
    Assert.That(harness.Check("a.js", [KatTarget.CSharp]).Targets[0].AllPassed, Is.True, "a second run starts afresh");
  }

  /// <summary>Test design 4: a build failure is reported by its first error, never as a pass.</summary>
  [Test]
  public void BuildFailure_IsTheFirstCompilerError() {
    var runner = new FakeRunner { Test = new(1, "  src/a.cs(3,5): error CS0103: The name 'x' does not exist [/w/Kat.Tests.csproj]\n", "", false) };
    var result = this.Harness(runner, ("a.js", Golden("a.js", Algorithm("A", Vector())))).Check("a.js", [KatTarget.CSharp]).Targets[0];
    Assert.That((result.Stage, result.Reason, result.AllPassed), Is.EqualTo((KatStage.BuildFailed, "CS0103: The name 'x' does not exist", false)));
    runner.Test = new(-1, "", "", true);
    Assert.That(this.Harness(runner).Check("a.js", [KatTarget.CSharp]).Targets[0].Reason, Is.EqualTo("the build and the tests timed out"));
  }

  [TestCase("first\nsecond", ": error ", "first")]
  [TestCase("\n  \n", ": error ", "no output")]
  [TestCase("a.js(1,1): error PLY1102: x [y]", ": error ", "PLY1102: x [y]")]
  [TestCase("a.js(1,1): error PLY1102: x [/p/q.csproj]", ": error ", "PLY1102: x")]
  public void FirstProblem_IsTheFirstLineWithTheMarker(string text, string marker, string problem)
    => Assert.That(KatHarness.FirstProblem(text, marker), Is.EqualTo(problem));

  [Test]
  public void Trx_IsReadByTestName_WithTheFirstLineOfTheMessage() {
    var outcomes = KatHarness.ReadTrx(Trx(("A #1", "Passed", ""), ("A #2", "Failed", "differs")) + "<!-- -->");
    Assert.That(outcomes["A #1"], Is.EqualTo((true, "")));
    Assert.That(outcomes["A #2"], Is.EqualTo((false, "differs")));
    Assert.That(KatHarness.ReadTrx("<TestRun><Results><UnitTestResult outcome=\"Passed\" /></Results></TestRun>")[""], Is.EqualTo((true, "")));
  }

  // --- the table --------------------------------------------------------------------------------

  private static KatRow Row(string file, int vectors, int checkable, string? note, params KatTargetResult[] targets) => new(file, vectors, checkable, note, targets);

  [Test]
  public void Table_SummarisesEveryTarget_AndListsTheRefusalsByHowManyFilesTheyBlock() {
    var rows = new[] {
      Row("a.js", 3, 3, null, new("csharp", KatStage.Ran, null, 3, []), new("rust", KatStage.Refused, "PLY0003: unknown target language 'rust'", 0, [])),
      Row("b.js", 2, 2, null, new("csharp", KatStage.Ran, null, 1, ["B #2: differs"]), new("rust", KatStage.Refused, "PLY0003: unknown target language 'rust'", 0, [])),
      Row("c.js", 1, 1, null, new("csharp", KatStage.Refused, "PLY1101: parameter 'x' of 'C.ctor' has no supported JSDoc type", 0, []), new("rust", KatStage.Refused, "PLY0003: unknown target language 'rust'", 0, [])),
      Row("d.js", 0, 0, "registers no algorithm", new("csharp", KatStage.Translated, "no known-answer test a translation can be checked on", 0, []), new("rust", KatStage.BuildFailed, "E0308|x", 0, []))
    };
    var text = KatReport.Markdown(rows, [KatTarget.CSharp, KatTarget.Rust], "# Title\n\nhow\n");
    Assert.That(text, Does.StartWith("# Title\n\nhow\n\n## Summary\n"));
    Assert.That(text, Does.Contain("| csharp | 4 | 3 | 2 | 1 | 1 |"));
    Assert.That(text, Does.Contain("| rust | 4 | 1 | 0 | 0 | 0 |"));
    Assert.That(text, Does.Contain("6 known-answer tests in 4 files, 6 of them checkable"));
    Assert.That(text, Does.Contain("### csharp\n\n| Files | First diagnostic |\n| ---: | --- |\n| 1 | PLY1101: parameter '…' of '…' has no supported JSDoc type |"));
    Assert.That(text, Does.Contain("| 3 | PLY0003: unknown target language '…' |"));
    Assert.That(text, Does.Contain("| `b.js` | 2 / 2 | 1 / 2 pass; B #2: differs |"));
    Assert.That(text, Does.Contain("| `d.js` | 0 / 0 (registers no algorithm) | translated; no known-answer test a translation can be checked on | does not build: E0308\\|x |"));
    Assert.That(text, Does.Contain("| `a.js` | 3 / 3 | 3 / 3 pass |"));
  }

  [Test]
  public void Table_WithoutRefusals_SaysSo_AndFoldsTheReasonsBeyondTheTop() {
    var refused = Enumerable.Range(0, KatReport.TopReasons + 2).Select(index => Row($"r{index}.js", 1, 1, null, new KatTargetResult("csharp", KatStage.Refused, $"PLY1102: 'reason {index}' is not supported yet", 0, []))).ToList();
    var text = KatReport.Markdown(refused, [KatTarget.CSharp], "# T");
    Assert.That(text, Does.Contain("| 2 | 2 other reasons |"));
    Assert.That(KatReport.Markdown([Row("a.js", 1, 1, null, new KatTargetResult("csharp", KatStage.Ran, null, 1, []))], [KatTarget.CSharp], "# T"), Does.Contain("No file is refused."));
  }

  [TestCase("PLY1102: 'a call of OpCodes.Unpack64BE(qword).reverse' is not supported yet", "PLY1102: 'a call of ….reverse' is not supported yet")]
  [TestCase("PLY1102: 'typeof' is not supported yet", "PLY1102: 'typeof' is not supported yet")]
  [TestCase("PLY1105: 'RotL128' holds both uint8[] and uint8[]", "PLY1105: 'RotL128' holds both uint8[] and uint8[]")]
  [TestCase("PLY1101: parameter 'value' of 'RotL64n' has no supported JSDoc type", "PLY1101: parameter '…' of '…' has no supported JSDoc type")]
  [TestCase("the translation timed out", "the translation timed out")]
  [TestCase("PLY1102: 'unclosed is not supported", "PLY1102: 'unclosed is not supported")]
  public void Reason_KeepsTheConstructAndLeavesOutTheNames(string diagnostic, string reason)
    => Assert.That(KatReport.Reason(diagnostic), Is.EqualTo(reason));

  // --- the command ------------------------------------------------------------------------------

  private string Corpus => CorpusImporter.FilePath(this._root, "Hawkynt/Hawkynt.github.io", Commit, "Cipher");

  private void Repository(bool fixture = true, bool corpus = true) {
    var entry = fixture ? $"""
        fixtures:
          - id: {CipherCommand.Fixture}
            repository: Hawkynt/Hawkynt.github.io
            commit: {Commit}
            path: Cipher/AlgorithmFramework.js
            export: RegisterAlgorithm
            dependencies: []
            run: [node, '{"{file}"}']
            vectors: {"{ kind: embedded, source: x }"}
            licence: LGPL-3.0-or-later
            determinism: deterministic
            tolerance: exact
            profiles: []
        """ : "fixtures: []";
    Scratch.Write(this._root, CorpusManifest.RepositoryPath, $"""
      schema: 1
      repositories:
        - name: Hawkynt/Hawkynt.github.io
          url: https://example.org/x.git
          commit: {new string('b', 40)}
          licence: LGPL-3.0-or-later
          licenceFiles: [LICENSE]
      {entry}
      """);
    if (!corpus)
      return;

    Scratch.Write(this.Corpus, "algorithms/checksum/a.js", "//");
    Scratch.Write(this.Corpus, "algorithms/hash/b.js", "//");
    Scratch.Write(Path.Combine(this._root, CipherCommand.Goldens), "checksum/a.kat.json", Golden("checksum/a.js", Algorithm("A", Vector())));
    Scratch.Write(Path.Combine(this._root, CipherCommand.Goldens), "hash/b.kat.json", Golden("hash/b.js", Algorithm("B", Vector())));
  }

  private (int ExitCode, string Output, string Error) Run(FakeRunner runner, params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var nested = Directory.CreateDirectory(Path.Combine(this._root, "some")).FullName;
    var exitCode = new CipherCommand(runner, nested).Run(args, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [TestCase(new string[0], "cipher needs 'kat'")]
  [TestCase(new[] { "run" }, "unknown cipher command 'run'")]
  [TestCase(new[] { "kat", "--fast" }, "unknown option '--fast'")]
  [TestCase(new[] { "kat", "a.js", "--targets" }, "option '--targets' requires a value")]
  [TestCase(new[] { "kat" }, "cipher kat needs the files to check or '--all', not both")]
  [TestCase(new[] { "kat", "a.js", "--all" }, "cipher kat needs the files to check or '--all', not both")]
  [TestCase(new[] { "kat", "--all", "--targets", "csharp,go" }, "unknown target 'go'")]
  [TestCase(new[] { "kat", "--all", "--targets", "csharp", "--require", "rust" }, "the required target 'rust' is not checked")]
  [TestCase(new[] { "kat", "--all", "--jobs", "0" }, "--jobs needs a number of at least 1, not '0'")]
  [TestCase(new[] { "kat", "--all", "--jobs", "many" }, "--jobs needs a number of at least 1, not 'many'")]
  public void CommandLine_ThatIsWrong_IsAUsageError(string[] args, string message) {
    var (exitCode, _, error) = this.Run(new FakeRunner(), args);
    Assert.That((exitCode, error.Trim()), Is.EqualTo((Program.UsageError, $"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}")));
  }

  [Test]
  public void Command_ChecksTheNamedFiles_PrintsARowEach_AndWritesTheTable() {
    this.Repository();
    var runner = new FakeRunner { Trx = argv => Trx(("A #1", "Passed", ""), ("B #1", "Passed", "")) };
    var report = Path.Combine(this._root, "cipher.md");
    var (exitCode, output, error) = this.Run(runner, "kat", "checksum\\a.js", "--targets", "csharp", "--require", "csharp", "--report", report, "--polypiler", "tools/polypiler.dll", "--work", Path.Combine(this._root, "w"));
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(output.Trim(), Is.EqualTo("checksum/a.js: 1 / 1 KATs checkable; csharp 1 / 1 pass"));
    Assert.That(runner.Calls[0].Take(2), Is.EqualTo(new[] { "dotnet", Path.GetFullPath("tools/polypiler.dll") }));
    Assert.That(File.ReadAllText(report), Does.StartWith("# Cipher corpus").And.Contain($"`Hawkynt/Hawkynt.github.io` at `{Commit}`").And.Contain("| `checksum/a.js` | 1 / 1 | 1 / 1 pass |"));
    Assert.That(Directory.Exists(Path.Combine(this._root, "w", "checksum", "a", "csharp")), Is.True);
  }

  [Test]
  public void Command_OverAll_FailsWhereARequiredTargetDoesNotPassEveryVector() {
    this.Repository();
    var runner = new FakeRunner { Translate = argv => argv.Any(argument => argument.EndsWith("b.js", StringComparison.Ordinal)) ? new(4, "", "b.js(1,1): error PLY1102: no\n", false) : new(0, "", "", false), Trx = _ => Trx(("A #1", "Passed", "")) };
    var (exitCode, output, error) = this.Run(runner, "kat", "--all", "--require", "csharp", "--jobs", "2");
    Assert.That(exitCode, Is.EqualTo(Program.Failure));
    Assert.That(output, Does.Contain("checksum/a.js: 1 / 1 KATs checkable; csharp 1 / 1 pass; rust translated; the tests cannot reach the translation: ").And.Contain("hash/b.js: 1 / 1 KATs checkable; csharp refused: PLY1102: no"));
    Assert.That(error.Trim(), Is.EqualTo($"{Program.Name}: hash/b.js does not pass every checked KAT on csharp"));
    Assert.That(runner.Calls[0].Take(2), Is.EqualTo(new[] { "dotnet", Path.Combine(this._root, "Polypiler.Cli", "bin", "Release", "net10.0", "polypiler.dll") }));
  }

  [Test]
  public void Command_WithoutTheFixture_TheCorpus_TheFile_OrItsGolden_Fails() {
    Assert.That(this.Run(new FakeRunner(), "kat", "--all").Error, Does.Contain("PLY0026"), "no manifest at all");
    this.Repository(fixture: false, corpus: false);
    Assert.That(this.Run(new FakeRunner(), "kat", "--all").Error.Trim(), Is.EqualTo($"{Program.Name}: {CorpusManifest.RepositoryPath} has no fixture '{CipherCommand.Fixture}'"));
    this.Repository(corpus: false);
    Assert.That(this.Run(new FakeRunner(), "kat", "--all").Error, Does.Contain("the Cipher corpus is not imported"));
    this.Repository();
    Assert.That(this.Run(new FakeRunner(), "kat", "none.js"), Is.EqualTo((Program.Failure, "", $"{Program.Name}: 'none.js' is no algorithm file of the corpus{Environment.NewLine}")));
    Scratch.Write(this.Corpus, "algorithms/x.js", "//");
    Assert.That(this.Run(new FakeRunner(), "kat", "x.js").Error, Does.StartWith($"{Program.Name}: the golden file of 'x.js' cannot be read: "));
  }

  [Test]
  public void PolypilerCommand_IsAnAssemblyThroughDotnet_OrAnExecutable() {
    Assert.That(CipherCommand.Command("p.DLL"), Is.EqualTo(new[] { "dotnet", Path.GetFullPath("p.DLL") }));
    Assert.That(CipherCommand.Command("polypiler"), Is.EqualTo(new[] { Path.GetFullPath("polypiler") }));
  }

  [Test]
  public void Templates_AreEmbedded() {
    Assert.That(KatHarness.Template("KatTests.cs.txt"), Does.Contain("namespace Polypiler.CipherKat;"));
    Assert.That(KatHarness.Template("Kat.Tests.csproj.txt"), Does.Contain("<TargetFramework>net10.0</TargetFramework>"));
  }

  [Test]
  public void Program_RunsTheVerb() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["cipher"], output, error), Is.EqualTo(Program.UsageError));
    Assert.That(error.ToString(), Does.Contain("cipher needs 'kat'"));
  }

}
