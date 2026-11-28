using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Runners;

/// <summary>
/// The JavaScript runner without node (#234): the plans it makes and what it makes of every kind of
/// answer, given by a runner that replays a recorded one, the reading of node's syntax errors and TAP
/// reports, and the explanation of failed vectors. The acceptance project runs the same on node.
/// </summary>
[TestFixture]
public sealed class NodeRunnerUnitTests {

  /// <summary>Answers each plan with the next result and keeps the plans.</summary>
  private sealed class Replay(params RunResult[] results) : ICrossLanguageRunner {

    private int _next;

    public List<RunPlan> Plans { get; } = [];

    public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
      this.Plans.Add(plan);
      return Task.FromResult(results[Math.Min(this._next++, results.Length - 1)]);
    }

  }

  private static RunResult Result(RunOutcome outcome, int exitCode, string stdout = "", string stderr = "", params Diagnostic[] diagnostics)
    => new(outcome, exitCode, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics);

  private static readonly CallSignature _add = new([new CallParameter("a", ImageType.I32, ParameterMode.In), new CallParameter("b", ImageType.I32, ParameterMode.In)], ImageType.I32);

  private static RunnerInput Input(params (string Name, string Text)[] files)
    => new(files.ToDictionary(file => file.Name, file => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(file.Text), StringComparer.Ordinal), "F", _add, "node", TrustLevel.Trusted);

  private DirectoryInfo? _workspace;

  [SetUp]
  public void CreateWorkspace() => this._workspace = Directory.CreateTempSubdirectory("polypiler-node-unit-");

  [TearDown]
  public void DeleteWorkspace() => this._workspace?.Delete(true);

  private NodeRunner Runner(Replay replay) => new(replay, this._workspace!.FullName);

  [Test]
  public void TheRunner_IsJavaScriptOnNode_AndRegistersWithItsHelperCapability() {
    var runner = this.Runner(new Replay(Result(RunOutcome.Completed, 0)));
    var runners = new LanguageRunners();
    runners.Register(runner);

    Assert.That((runner.Language, runner.Toolchains.Single()), Is.EqualTo(("javascript", "node")));
    Assert.That(runners.TryGet<IHelperRunner>("javascript", out var helper), Is.True);
    Assert.That(helper, Is.SameAs(runner));
  }

  [Test]
  public void ACheck_IsNodeCheckOfEveryScript_AndKeepsTheFiles() {
    var replay = new Replay(Result(RunOutcome.Completed, 0));
    var result = this.Runner(replay).Compile(Input(("b.mjs", "export const F = 1;"), ("a.cjs", ""), ("lib/c.js", ""), ("data.json", "{}")));
    var plan = replay.Plans.Single();

    Assert.That((result.Outcome, result.Diagnostics.Count), Is.EqualTo((RunOutcome.Completed, 0)));
    Assert.That(plan.Build.Select(step => step.ToString()), Is.EqualTo(new[] { "node --check a.cjs", "node --check b.mjs", "node --check lib/c.js" }));
    Assert.That(File.ReadAllText(Path.Combine(result.Directory, "b.mjs")), Is.EqualTo("export const F = 1;"));
    Assert.That(File.Exists(Path.Combine(result.Directory, "lib", "c.js")), Is.True);
  }

  [Test]
  public void ARejectedScript_IsPLY0030WithTheSyntaxError() {
    var failed = Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, "node --check broken.js", 1)
      with { Cause = "/tmp/polypiler-run-1/broken.js:2\n  return a +;\n            ^\n\nSyntaxError: Unexpected token ';'\n    at wrapSafe\n" };
    var result = this.Runner(new Replay(Result(RunOutcome.BuildFailed, 1, diagnostics: failed))).Compile(Input(("broken.js", "")));

    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1191" }));
    Assert.That(result.Diagnostics[1].Message, Is.EqualTo("node rejected the input: broken.js:2: SyntaxError: Unexpected token ';'"));
  }

  [TestCase(RunOutcome.TimedOut)]
  [TestCase(RunOutcome.Refused)]
  public void ACheckThatDidNotEnd_KeepsTheRunnerDiagnostics(RunOutcome outcome) {
    var result = this.Runner(new Replay(Result(outcome, -1, diagnostics: Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 1000)))).Compile(Input(("a.mjs", "")));

    Assert.That((result.Outcome, result.Diagnostics.Single().Code), Is.EqualTo((outcome, "PLY0031")));
  }

  [TestCase("C:\\run\\lib\\a.mjs:7\nx\n^\nSyntaxError: Unexpected identifier 'x'", "lib/a.mjs:7: SyntaxError: Unexpected identifier 'x'")]
  [TestCase("/r/a.mjs:7\n/r/other.mjs:3\nSyntaxError: bad", "other.mjs:3: SyntaxError: bad")]
  [TestCase("/r/unknown.mjs:1\nUncaught SyntaxError: bad", "unknown.mjs:1: Uncaught SyntaxError: bad")]
  [TestCase("SyntaxError: no location", "SyntaxError: no location")]
  [TestCase("a.mjs:3\nSyntaxError: here", "a.mjs:3: SyntaxError: here")]
  [TestCase("  Error: something else  \n", "Error: something else")]
  public void TheSyntaxErrorOfACheck_NamesFileLineAndText(string output, string text)
    => Assert.That(NodeRunner.SyntaxError(output, ["a.mjs", "lib/a.mjs", "other.mjs"]), Is.EqualTo(text));

  private string Program(params string[] names) {
    var directory = Path.Combine(this._workspace!.FullName, "program");
    foreach (var name in names) {
      var path = Path.Combine(directory, name);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, "export const F = (a, b) => a + b;");
    }

    return directory;
  }

  [Test]
  public void ADriverRun_PassesTheExportTheImagesAndTheGeneratedDriver() {
    var replay = new Replay(Result(RunOutcome.Completed, 0, "05000000\n!other:RangeError RangeError\n"));
    var outcomes = this.Runner(replay).ExecuteFunction(this.Program("add.mjs", "lib/x.json"), "F", _add, [[2, 0, 0, 0, 3, 0, 0, 0], new byte[8]]);
    var plan = replay.Plans.Single();

    Assert.That(outcomes[0].Image, Is.EqualTo(new byte[] { 5, 0, 0, 0 }));
    Assert.That(outcomes[1].Trap, Is.EqualTo(TrapClass.Parse("other:RangeError")));
    Assert.That(plan.Execute.ToString(), Is.EqualTo("node __driver.mjs F"));
    Assert.That(Encoding.UTF8.GetString(plan.Execute.Stdin.Span), Is.EqualTo("0200000003000000\n0000000000000000\n"));
    Assert.That(plan.Files.Keys, Is.EquivalentTo(new[] { "add.mjs", "lib/x.json", "__driver.mjs" }));
    Assert.That(Encoding.UTF8.GetString(plan.Files["__driver.mjs"].Span), Does.Contain("const ENTRY = \"./add.mjs\";"));
  }

  [TestCase(2, "PLY1195", "call 1: the function under test wrote to standard output")]
  [TestCase(3, "PLY1192", "the JavaScript driver exited with code 3: the export 'F' does not exist")]
  [TestCase(1, "PLY1192", "the JavaScript driver exited with code 1: the export 'F' does not exist")]
  public void ADriverThatEndsWithAnExitCode_IsAProtocolErrorOrACrash(int exitCode, string code, string message) {
    var stderr = exitCode == 2 ? "call 1: the function under test wrote to standard output\n" : "the export 'F' does not exist\n";
    var error = Assert.Throws<LanguageRunnerException>(() => this.Runner(new Replay(Result(RunOutcome.Completed, exitCode, "", stderr)))
      .ExecuteFunction(this.Program("add.mjs"), "F", _add, [new byte[8]]));

    Assert.That(error!.Diagnostics.Single().Code, Is.EqualTo(code));
    Assert.That(error.Diagnostics.Single().Message, Is.EqualTo(message));
  }

  [Test]
  public void ADriverThatTimesOut_KeepsTheTimeLimitDiagnostic() {
    var replay = new Replay(Result(RunOutcome.TimedOut, -1, diagnostics: Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 5000)));
    var error = Assert.Throws<LanguageRunnerException>(() => this.Runner(replay).ExecuteFunction(this.Program("add.mjs"), "F", _add, [new byte[8]]));

    Assert.That(error!.Diagnostics.Single().Code, Is.EqualTo("PLY0031"));
  }

  [Test]
  public void AProgramWithoutAnEntry_IsAProtocolErrorWithoutARun() {
    var replay = new Replay(Result(RunOutcome.Completed, 0));
    var error = Assert.Throws<LanguageRunnerException>(() => this.Runner(replay).ExecuteFunction(this.Program("a.mjs", "b.mjs"), "F", _add, [new byte[8]]));

    Assert.That(error!.Diagnostics.Single().Code, Is.EqualTo("PLY1195"));
    Assert.That(error.Diagnostics.Single().Message, Does.StartWith("there is no entry module among 2 JavaScript file(s)").And.Not.Contain("Parameter"));
    Assert.That(replay.Plans, Is.Empty);
  }

  [TestCase("05000000\nnonsense\n", "line 2: not an image in lower-case hexadecimal")]
  [TestCase("05000000\n", "the driver printed 1 line(s) for 2 calls")]
  public void OutputThatDoesNotFollowTheProtocol_IsPLY1195(string stdout, string reason) {
    var error = Assert.Throws<LanguageRunnerException>(() => this.Runner(new Replay(Result(RunOutcome.Completed, 0, stdout)))
      .ExecuteFunction(this.Program("add.mjs"), "F", _add, [new byte[8], new byte[8]]));

    Assert.That((error!.Diagnostics.Single().Code, error.Diagnostics.Single().Message), Is.EqualTo(("PLY1195", reason)));
  }

  [Test]
  public void Execute_RunsTheEntryWithTheArguments() {
    var replay = new Replay(Result(RunOutcome.Completed, 0, "x"));
    var compiled = new CompileResult(RunOutcome.Completed, this.Program("index.mjs"), [], Result(RunOutcome.Completed, 0));
    var result = this.Runner(replay).Execute(compiled, ["a", "b"]);

    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Is.EqualTo("x"));
    Assert.That(replay.Plans.Single().Execute.ToString(), Is.EqualTo("node index.mjs a b"));
  }

  [Test]
  public void AHelper_RunsUnderItsFileNameWithItsStandardInput() {
    var replay = new Replay(Result(RunOutcome.Completed, 0, "ok"));
    var helper = Path.Combine(this._workspace!.FullName, "bridge.mjs");
    File.WriteAllText(helper, "process.stdin.pipe(process.stdout);");
    var result = this.Runner(replay).RunHelper(helper, ["--parse"], "x"u8.ToArray());
    var plan = replay.Plans.Single();

    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Is.EqualTo("ok"));
    Assert.That((plan.Execute.ToString(), plan.Trust, Encoding.UTF8.GetString(plan.Execute.Stdin.Span)), Is.EqualTo(("node bridge.mjs --parse", TrustLevel.Trusted, "x")));
    Assert.That(plan.Files.Keys, Is.EqualTo(new[] { "bridge.mjs" }));
  }

  private const string _tap = """
    TAP version 13
    # Subtest: adds two numbers
    ok 1 - adds two numbers
      ---
      duration_ms: 0.5
      ...
    # Subtest: fails on purpose
    not ok 2 - fails on purpose
      ---
      duration_ms: 0.7
      failureType: 'testCodeFailure'
      error: |-
        this test fails on purpose

        4 !== 5
      code: 'ERR_ASSERTION'
      ...
    not ok 3 - quoted
      ---

      error: 'a quoted message'
      ...
    not ok 4 - silent # TODO later
      ---
      duration_ms: 1
      ...
    not ok 5 - last
      error: |-
    1..5
    # tests 5
    # suites 0
    # pass 1
    # fail 4
    # cancelled 0
    # skipped 1
    # todo 1
    """;

  [Test]
  public void ATapReport_GivesTheCountsAndTheFailures() {
    var report = NodeRunner.ReadTap(_tap.ReplaceLineEndings("\r\n"))!;

    Assert.That((report.Total, report.Passed, report.Failed, report.Skipped), Is.EqualTo((5, 1, 4, 2)));
    Assert.That(report.Failures, Is.EqualTo(new[] {
      new TestFailure("fails on purpose", "this test fails on purpose"), new TestFailure("quoted", "a quoted message"),
      new TestFailure("silent", ""), new TestFailure("last", "")
    }));
  }

  [Test]
  public void ATapReportWithoutCounts_IsNone() {
    Assert.That(NodeRunner.ReadTap("TAP version 13\nok 1 - x\n"), Is.Null);
    Assert.That(NodeRunner.ReadTap("# tests 1\nnot ok 1 - end\n  error: |-")!.Failures.Single(), Is.EqualTo(new TestFailure("end", "")));
    Assert.That(NodeRunner.ReadTap("# tests 1\nnot ok 1 - final")!.Failures.Single(), Is.EqualTo(new TestFailure("final", "")));
    var bare = NodeRunner.ReadTap("# tests 2\n")!;
    Assert.That((bare.Total, bare.Passed, bare.Failed, bare.Skipped, bare.Failures.Count), Is.EqualTo((2, 0, 0, 0, 0)));
  }

  private string Suite() {
    var directory = Path.Combine(this._workspace!.FullName, "suite");
    Directory.CreateDirectory(Path.Combine(directory, "node_modules", "dep"));
    File.WriteAllText(Path.Combine(directory, "a.test.mjs"), "");
    File.WriteAllText(Path.Combine(directory, "node_modules", "dep", "index.js"), "");
    return directory;
  }

  [Test]
  public void ATestRun_IsNodeTestWithTheTapReporter() {
    var replay = new Replay(Result(RunOutcome.Completed, 1, _tap));
    var report = this.Runner(replay).RunTests(this.Suite());
    var plan = replay.Plans.Single();

    Assert.That(report.Total, Is.EqualTo(5));
    Assert.That(plan.Execute.ToString(), Is.EqualTo("node --test --test-reporter=tap"));
    Assert.That(plan.Files.Keys, Is.EqualTo(new[] { "a.test.mjs" }));
  }

  [Test]
  public void ATestRunWithoutReport_IsACrash() {
    var error = Assert.Throws<LanguageRunnerException>(() => this.Runner(new Replay(Result(RunOutcome.Completed, 134, "", "aborted"))).RunTests(this.Suite()));

    Assert.That((error!.Diagnostics.Single().Code, error.Diagnostics.Single().Message), Is.EqualTo(("PLY1192", "the JavaScript driver exited with code 134: aborted")));
  }

  [Test]
  public void ATestRunThatDidNotEnd_IsAFailure() {
    var replay = new Replay(Result(RunOutcome.TimedOut, -1, diagnostics: Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 1000)));

    Assert.That(Assert.Throws<LanguageRunnerException>(() => this.Runner(replay).RunTests(this.Suite()))!.Diagnostics.Single().Code, Is.EqualTo("PLY0031"));
  }

  private static VectorFile Vectors(string expectation) => VectorFile.Parse($$$"""
    {
      "schema":1, "fixture":"runner-javascript/add",
      "provenance":{"kind":"published","source":"fixture","detail":"by hand","generatedBy":"hand"},
      "export":"F", "signature":{"parameters":[{"name":"a","type":"i32"},{"name":"b","type":"i32"}],"result":"i32"},
      "vectors":[{"name":"good","input":{"hex":"0200000003000000"},"expected":{"hex":"05000000"}}, {"name":"bad","input":{"hex":"0200000003000000"}{{{expectation}}}}]
    }
    """, "add.vectors.json");

  [TestCase(""","expected":{"hex":"06000000"}""", "05000000\n05000000\n", "call 1: expected 6, actual 5")]
  [TestCase(""","expectedTrap":"divide-by-zero" """, "05000000\n05000000\n", "call 1: expected !divide-by-zero, actual 5")]
  [TestCase(""","expected":{"hex":"05000000"}""", "05000000\n!result-type number:0.5\n", "call 1: expected 5, actual !result-type")]
  public void AFailedVector_IsExplainedWithTheValuesOfTheCall(string expectation, string answers, string message) {
    var vectors = Vectors(expectation);
    var runner = this.Runner(new Replay(Result(RunOutcome.Completed, 0), Result(RunOutcome.Completed, 0, answers)));
    var report = runner.RunVectors(Input(("add.mjs", "export const F = (a, b) => a + b;")), vectors);
    var diagnostics = NodeRunner.Explain(report, vectors);

    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0051", "PLY1194" }));
    Assert.That(diagnostics[1].Message, Is.EqualTo(message));
  }

  [Test]
  public void AReportWhereEveryVectorPassed_NeedsNoExplanation() {
    var report = new ComparisonReport(2, 2, [new VectorOutcome("good", true, null, "05000000", "05000000", null), new VectorOutcome("bad", true, null, "05000000", "05000000", null)]);

    Assert.That(NodeRunner.Explain(report, Vectors(""","expected":{"hex":"05000000"}""")), Is.Empty);
  }

  [TestCase("0500")]
  [TestCase("0500000001")]
  public void AnImageThatDoesNotFitTheSignature_IsExplainedInHexadecimal(string actual) {
    var report = new ComparisonReport(2, 1, [new VectorOutcome("good", true, null, "05000000", "05000000", null), new VectorOutcome("bad", false, 0, "06000000", actual, null)]);

    Assert.That(NodeRunner.Explain(report, Vectors(""","expected":{"hex":"06000000"}"""))[1].Message, Is.EqualTo($"call 1: expected 6, actual {actual}"));
  }

  [Test]
  public void OutputsWithoutAResult_AreExplainedByName_AndNoImageAsNothing() {
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"f", "provenance":{"kind":"published","source":"s","detail":"d","generatedBy":"hand"},
       "export":"V", "signature":{"parameters":[{"name":"a","type":"i32","mode":"inout"},{"name":"b","type":"i32","mode":"out"}],"result":"void"},
       "vectors":[{"name":"v","input":{"hex":"01000000"},"expected":{"hex":"0300000004000000"}}, {"name":"w","input":{"hex":"01000000"}}]}
      """, "v.vectors.json");
    var report = new ComparisonReport(2, 0, [new VectorOutcome("v", false, 0, "0300000004000000", "0300000005000000", null), new VectorOutcome("w", false, 0, "", "", null)]);
    var diagnostics = NodeRunner.Explain(report, vectors);

    Assert.That(diagnostics[1].Message, Is.EqualTo("call 0: expected a=3, b=4, actual a=3, b=5"));
    Assert.That(diagnostics[3].Message, Is.EqualTo("call 1: expected nothing, actual "));
  }

  [Test]
  public void AVoidSignatureWithoutOutputs_IsExplainedAsNothing() {
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"f", "provenance":{"kind":"published","source":"s","detail":"d","generatedBy":"hand"},
       "export":"V", "signature":{"parameters":[{"name":"a","type":"i32"}],"result":"void"},
       "vectors":[{"name":"v","input":{"hex":"01000000"},"expected":{"hex":""}}]}
      """, "v.vectors.json");

    Assert.That(NodeRunner.Explain(new ComparisonReport(1, 0, [new VectorOutcome("v", false, 0, "", "", null)]), vectors)[1].Message, Is.EqualTo("call 0: expected nothing, actual nothing"));
  }

  [Test]
  public void AVectorFileWithoutSignature_CannotBeExplained() {
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"f", "provenance":{"kind":"published","source":"s","detail":"d","generatedBy":"hand"},
       "vectors":[{"name":"v","input":{"hex":"01"},"expected":{"hex":"01"}}]}
      """, "v.vectors.json");

    Assert.Throws<ImageException>(() => NodeRunner.Explain(new ComparisonReport(1, 1, []), vectors));
  }

  [Test]
  public void InvalidArguments_AreRefused() {
    Assert.Throws<ArgumentNullException>(() => _ = new NodeRunner(null!, "w"));
    Assert.Throws<ArgumentException>(() => _ = new NodeRunner(new Replay(), " "));
    var runner = this.Runner(new Replay(Result(RunOutcome.Completed, 0)));
    Assert.Throws<ArgumentNullException>(() => runner.Compile(null!));
    Assert.Throws<ArgumentNullException>(() => runner.Execute(null!, []));
    Assert.Throws<ArgumentNullException>(() => runner.Execute(new CompileResult(RunOutcome.Completed, "d", [], Result(RunOutcome.Completed, 0)), null!));
    Assert.Throws<ArgumentException>(() => runner.RunHelper("", [], default));
    Assert.Throws<ArgumentNullException>(() => runner.RunHelper("a.mjs", null!, default));
    Assert.Throws<ArgumentException>(() => runner.RunTests(""));
    Assert.Throws<ArgumentNullException>(() => NodeRunner.ReadTap(null!));
    Assert.Throws<ArgumentNullException>(() => NodeRunner.SyntaxError(null!, []));
    Assert.Throws<ArgumentNullException>(() => NodeRunner.SyntaxError("", null!));
    Assert.Throws<ArgumentNullException>(() => NodeRunner.Explain(null!, Vectors("")));
    Assert.Throws<ArgumentNullException>(() => NodeRunner.Explain(new ComparisonReport(0, 0, []), null!));
  }

}
