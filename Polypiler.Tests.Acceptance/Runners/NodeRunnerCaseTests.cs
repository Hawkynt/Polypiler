using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Runners;

/// <summary>
/// The JavaScript runner against the locked <c>node</c>: the fixtures of
/// <c>tests/fixtures/runner-javascript</c> are checked and run as written and reproduce the images their
/// vector files state by hand; every row of the image-type and thrown-value tables, the parameter
/// modes, the result acceptance rules and every failure are reported with their codes.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), Layer("js")]
[RequiresToolchain(NodeRunner.Toolchain)]
public sealed class NodeRunnerCaseTests {

  internal static readonly string Fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "runner-javascript");

  private DirectoryInfo? _workspace;

  [SetUp]
  public void CreateWorkspace() => this._workspace = Directory.CreateTempSubdirectory("polypiler-node-");

  [TearDown]
  public void DeleteWorkspace() => this._workspace?.Delete(true);

  /// <summary>
  /// The sandbox of this host, vouched for as isolating. <see cref="LanguageRunnerBase.ExecuteFunction(string, string, CallSignature, IReadOnlyList{byte[]})"/>
  /// runs its program as untrusted, which a host without isolation such as Windows refuses with
  /// <c>PLY0034</c>; the fixtures here are this repository's own, so they run there all the same.
  /// </summary>
  private sealed class FixtureSandbox(ISandbox inner) : ISandbox {

    public IsolationLevel Isolation => IsolationLevel.Isolated;

    public IReadOnlyList<string> CommandLine(SandboxCommand command) => inner.CommandLine(command);

    public IProcessTreeMonitor Watch(Process process, RunLimits limits) => inner.Watch(process, limits);

  }

  internal static NodeRunner Runner(string workspace, RunLimits? driverLimits = null)
    => new(new CrossLanguageRunner(new FixtureSandbox(RunnerTesting.Host), RunnerTesting.Toolchains), workspace, null, driverLimits);

  internal static RunnerInput Input(IReadOnlyDictionary<string, string> sources, string export, string signature)
    => new(sources.ToDictionary(pair => pair.Key, pair => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(pair.Value), StringComparer.Ordinal),
      export, Signature(signature), "node", TrustLevel.Trusted);

  internal static RunnerInput Input(string name, string source, string export, string signature)
    => Input(new Dictionary<string, string> { [name] = source }, export, signature);

  internal static RunnerInput FixtureInput(string file, string export, string signature)
    => Input(file, File.ReadAllText(Path.Combine(Fixtures, file)), export, signature);

  /// <summary>A signature in the notation of the work order: <c>(a: i32 in, b: i32 in) -&gt; i32</c>.</summary>
  internal static CallSignature Signature(string text) {
    var arrow = text.LastIndexOf("->", StringComparison.Ordinal);
    var inner = text[(text.IndexOf('(') + 1)..text.LastIndexOf(')', arrow)].Trim();
    var result = text[(arrow + 2)..].Trim();
    var parameters = inner.Length == 0 ? [] : Split(inner).Select(parameter => {
      var colon = parameter.IndexOf(':');
      var rest = parameter[(colon + 1)..].Trim();
      var space = rest.LastIndexOf(' ');
      var mode = rest[(space + 1)..] switch { "inout" => ParameterMode.InOut, "out" => ParameterMode.Out, _ => ParameterMode.In };
      return new CallParameter(parameter[..colon].Trim(), ImageType.Parse(rest[..space]), mode);
    }).ToList();
    return new(parameters, result == "void" ? null : ImageType.Parse(result));
  }

  private static IEnumerable<string> Split(string text) {
    var depth = 0;
    var start = 0;
    for (var index = 0; index < text.Length; ++index) {
      depth += text[index] switch { '<' or '{' => 1, '>' or '}' => -1, _ => 0 };
      if (text[index] == ',' && depth == 0) {
        yield return text[start..index].Trim();
        start = index + 1;
      }
    }

    yield return text[start..].Trim();
  }

  internal static string Outcome(CallOutcome outcome) => outcome.Trap is { } trap ? $"!{trap} {outcome.NativeTrapName}" : Convert.ToHexStringLower(outcome.Image!);

  private IReadOnlyList<string> Calls(RunnerInput input, params string[] images) {
    var runner = Runner(this._workspace!.FullName);
    var built = runner.Compile(input);
    Assert.That(built.Outcome, Is.EqualTo(RunOutcome.Completed), string.Join("\n", built.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));
    return [.. runner.ExecuteFunction(built.Directory, input.Export, input.Signature, [.. images.Select(Convert.FromHexString)]).Select(Outcome)];
  }

  private LanguageRunnerException Failure(RunnerInput input, params string[] images) {
    var runner = Runner(this._workspace!.FullName);
    var built = runner.Compile(input);
    return Assert.Throws<LanguageRunnerException>(() => runner.ExecuteFunction(built.Directory, input.Export, input.Signature, [.. images.Select(Convert.FromHexString)]))!;
  }

  [TestCase("adler32.mjs", "adler32")]
  [TestCase("adler32.cjs", "adler32")]
  [TestCase("adler32.cjs", "Lib.adler32")]
  public void TheAdlerFixture_ReproducesItsVectorsInBothModuleSystems(string file, string export) {
    var vectors = VectorFile.Load(Path.Combine(Fixtures, "adler32.vectors.json"));
    var input = FixtureInput(file, export, "(data: seq<u8> in) -> u32");
    var report = Runner(this._workspace!.FullName).RunVectors(input with { Export = vectors.Export! }, vectors);

    Assert.That((report.Total, report.Passed), Is.EqualTo((2, 2)));
    if (export != vectors.Export)
      Assert.That(this.Calls(input, "0900000057696b697065646961"), Is.EqualTo(new[] { "9803e611" }));
  }

  [Test]
  public void TheAddFixture_ReproducesItsVectorsWithTheThrowingCase() {
    var vectors = VectorFile.Load(Path.Combine(Fixtures, "add.vectors.json"));

    Assert.That(Runner(this._workspace!.FullName).RunVectors(FixtureInput("add.mjs", "F", "(a: i32 in, b: i32 in) -> i32"), vectors).Verified, Is.True);
  }

  private const string _echo = """
    export const id = (x) => x;
    export const kind = (x) => x === null ? 'null' : x === undefined ? 'undefined' : typeof x === 'object' ? x.constructor.name : typeof x;
    """;

  [TestCase("u8", "ff", "number")]
  [TestCase("i8", "80", "number")]
  [TestCase("u16", "ffff", "number")]
  [TestCase("i16", "0080", "number")]
  [TestCase("u32", "ffffffff", "number")]
  [TestCase("i32", "00000080", "number")]
  [TestCase("u64", "ffffffffffffffff", "bigint")]
  [TestCase("i64", "0000000000000080", "bigint")]
  [TestCase("bigint", "020000008000", "bigint")]
  [TestCase("bigint", "020000007fff", "bigint")]
  [TestCase("bigint", "00000000", "bigint")]
  [TestCase("f32", "cdcccc3d", "number")]
  [TestCase("f64", "0100000000000000", "number")]
  [TestCase("bool", "01", "boolean")]
  [TestCase("char16", "41d8", "number")]
  [TestCase("bytes", "020000000102", "Uint8Array")]
  [TestCase("utf8", "03000000e282ac", "string")]
  [TestCase("utf16", "0200000000d84100", "string")]
  [TestCase("seq<u8>", "01000000ff", "Uint8Array")]
  [TestCase("seq<i8>", "0100000080", "Int8Array")]
  [TestCase("seq<u16>", "01000000ffff", "Uint16Array")]
  [TestCase("seq<i16>", "010000000080", "Int16Array")]
  [TestCase("seq<u32>", "01000000ffffffff", "Uint32Array")]
  [TestCase("seq<i32>", "0100000000000080", "Int32Array")]
  [TestCase("seq<f32>", "01000000cdcccc3d", "Float32Array")]
  [TestCase("seq<f64>", "010000000000000000000080", "Float64Array")]
  [TestCase("seq<u64>", "01000000ffffffffffffffff", "BigUint64Array")]
  [TestCase("seq<i64>", "010000000000000000000080", "BigInt64Array")]
  [TestCase("seq<bool>", "020000000100", "Array")]
  [TestCase("opt<u32>", "0107000000", "number")]
  [TestCase("opt<u32>", "00", "null")]
  [TestCase("record{hi:u32,lo:u32}", "0100000002000000", "Object")]
  [TestCase("tuple<u8,u32>", "0702000000", "Array")]
  [TestCase("union<u32|utf16|null>", "0007000000", "number")]
  [TestCase("union<u32|utf16|null>", "01010000004100", "string")]
  [TestCase("union<u32|utf16|null>", "02", "null")]
  [TestCase("enum<u8>", "03", "number")]
  public void EachRowOfTheImageTypeTable_TravelsBothWays_AsItsJavaScriptClass(string type, string image, string kind) {
    var source = new Dictionary<string, string> { ["echo.mjs"] = _echo };

    Assert.That(this.Calls(Input(source, "id", $"(x: {type} in) -> {type}"), image), Is.EqualTo(new[] { image }));
    var name = this.Calls(Input(source, "kind", $"(x: {type} in) -> utf16"), image).Single();
    Assert.That(Encoding.Unicode.GetString(Convert.FromHexString(name)[4..]), Is.EqualTo(kind));
  }

  [TestCase("f64", "010000000000f87f", "000000000000f87f")]
  [TestCase("f64", "000000000000f0ff", "000000000000f0ff")]
  [TestCase("f64", "000000000000f07f", "000000000000f07f")]
  [TestCase("f64", "0000000000000080", "0000000000000080")]
  [TestCase("f32", "0100c07f", "0000c07f")]
  [TestCase("i32", "ffffff7f", "ffffff7f")]
  public void FloatSpecialsAndIntegerEdges_AreExact_AndNaNIsCanonical(string type, string image, string expected)
    => Assert.That(this.Calls(Input("echo.mjs", _echo, "id", $"(x: {type} in) -> {type}"), image), Is.EqualTo(new[] { expected }));

  [Test]
  public void SequencesOfAnyLength_AndOptionals_Travel() {
    var large = Convert.ToHexStringLower([0, 0, 1, 0, .. Enumerable.Range(0, 65_536).Select(index => (byte)index)]);
    Assert.That(this.Calls(Input("echo.mjs", _echo, "id", "(x: seq<u8> in) -> seq<u8>"), "00000000", "01000000ff", large),
      Is.EqualTo(new[] { "00000000", "01000000ff", large }));
    Assert.That(this.Calls(Input("echo.mjs", _echo, "id", "(x: opt<seq<u8>> in) -> opt<seq<u8>>"), "00", "010100000007"),
      Is.EqualTo(new[] { "00", "010100000007" }));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(10_000)]
  public void AnyNumberOfImages_GivesOneOutcomePerImageInOrder(int count) {
    var images = Enumerable.Range(0, count).Select(index => Convert.ToHexStringLower([.. BitConverter.GetBytes(index), 0, 0, 0, 0])).ToArray();
    var expected = Enumerable.Range(0, count).Select(index => Convert.ToHexStringLower(BitConverter.GetBytes(index))).ToArray();

    Assert.That(this.Calls(FixtureInput("add.mjs", "F", "(a: i32 in, b: i32 in) -> i32"), images), Is.EqualTo(expected));
  }

  [Test]
  public void ParameterModes_PutTheValuesAfterTheCallIntoTheOutputImage() {
    const string Source = """
      export function X(buf) { for (let i = 0; i < buf.length; ++i) buf[i] ^= 0xff; }
      export function increment(holder) { holder.value += 1; }
      export function seven(holder) { holder.value = 7; }
      export function fill(holder) { holder.value = Uint8Array.of(1, 2); }
      """;
    var files = new Dictionary<string, string> { ["modes.mjs"] = Source };

    Assert.That(this.Calls(Input(files, "X", "(buf: seq<u8> inout) -> void"), "020000000001"), Is.EqualTo(new[] { "02000000fffe" }));
    Assert.That(this.Calls(Input(files, "X", "(buf: seq<u8> in) -> void"), "020000000001"), Is.EqualTo(new[] { "" }));
    Assert.That(this.Calls(Input(files, "increment", "(n: u32 inout) -> void"), "29000000"), Is.EqualTo(new[] { "2a000000" }));
    Assert.That(this.Calls(Input(files, "seven", "(n: i32 out) -> void"), ""), Is.EqualTo(new[] { "07000000" }));
    Assert.That(this.Calls(Input(files, "fill", "(b: seq<u8> out) -> void"), ""), Is.EqualTo(new[] { "020000000102" }));
  }

  [Test]
  public void EveryRowOfTheThrownValueTable_IsTheClassOfTheValue_AndTheNextCallsStillRun() {
    const string Source = """
      class Custom extends Error { constructor() { super('custom'); this.name = 'Custom'; } }
      const coded = (code) => Object.assign(new RangeError('trap'), { code });
      export function T(which) {
        switch (which) {
          case 0: throw coded('Overflow');
          case 1: throw coded('IndexOutOfRange');
          case 2: return Number(1n / BigInt(which - 2));
          case 3: return null.x;
          case 4: return new Array(-1).length;
          case 5: throw new RangeError('other text');
          case 6: throw new TypeError('custom');
          case 7: throw new Error('plain');
          case 8: throw new Custom();
          case 9: throw 1;
          case 10: throw coded('DivideByZero');
          case 11: throw coded('NullReference');
          case 12: throw coded('Unreachable');
          case 13: return new Uint8Array(-1).length;
          default: return which;
        }
      }
      """;
    var images = Enumerable.Range(0, 15).Select(index => Convert.ToHexStringLower(BitConverter.GetBytes(index))).ToArray();

    Assert.That(this.Calls(Input("throws.mjs", Source, "T", "(which: i32 in) -> i32"), images), Is.EqualTo(new[] {
      "!overflow RangeError", "!bounds RangeError", "!divide-by-zero RangeError", "!null TypeError", "!invalid-argument RangeError",
      "!other:RangeError RangeError", "!other:TypeError TypeError", "!other:Error Error", "!other:Custom Custom", "!other:NonError NonError",
      "!divide-by-zero RangeError", "!null RangeError", "!unreachable RangeError", "!invalid-argument RangeError", "0e000000"
    }));
  }

  [Test]
  public void TheDivFixture_TrapsOnADivisionByZero() {
    Assert.That(this.Calls(FixtureInput("div.mjs", "div", "(a: i64 in, b: i64 in) -> i64"), "07000000000000000200000000000000", "01000000000000000000000000000000"),
      Is.EqualTo(new[] { "0300000000000000", "!divide-by-zero RangeError" }));
  }

  [TestCase("half", "(a: i32 in) -> i32", "01000000", "!result-type number:0.5")]
  [TestCase("big", "() -> i32", "", "!result-type number:2147483648")]
  [TestCase("nothing", "() -> i32", "", "!result-type undefined:undefined")]
  [TestCase("plain", "() -> seq<u8>", "", "!result-type object:1,2")]
  [TestCase("one", "() -> u64", "", "!result-type number:1")]
  [TestCase("tenth", "() -> f32", "", "!result-type number:0.1")]
  [TestCase("extra", "() -> record{a:u8}", "", "!result-type object:[object Object]")]
  [TestCase("short", "() -> tuple<u8,u8>", "", "!result-type object:1")]
  [TestCase("holes", "() -> seq<bool>", "", "!result-type object:,")]
  [TestCase("lone", "() -> utf8", "", "!result-type string:\ufffd")]
  [TestCase("half", "(a: i32 in) -> void", "01000000", "!result-type number:0.5")]
  [TestCase("half", "(a: i32 in) -> union<u32|bool>", "01000000", "!result-type number:0.5")]
  [TestCase("half", "(a: i32 in) -> char16", "01000000", "!result-type number:0.5")]
  public void AResultOutsideItsDeclaredType_IsAResultTypeTrap_NeverACoercion(string export, string signature, string image, string expected) {
    const string Source = """
      export const half = (a) => a / 2;
      export const big = () => 2 ** 31;
      export const nothing = () => undefined;
      export const plain = () => [1, 2];
      export const one = () => 1;
      export const tenth = () => 0.1;
      export const extra = () => ({ a: 1, b: 2 });
      export const short = () => [1];
      export const holes = () => new Array(2);
      export const lone = () => '\ud800';
      """;
    Assert.That(this.Calls(Input("results.mjs", Source, export, signature), image, image), Is.EqualTo(new[] { expected, expected }));
  }

  [Test]
  public void AFunctionThatWritesToStandardOutput_IsAProtocolError() {
    var error = this.Failure(Input("noisy.mjs", "export const F = (a) => { console.log('x'); return a; };", "F", "(a: i32 in) -> i32"), "01000000");

    Assert.That(error.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Is.EqualTo(new[] { ("PLY1195", "call 1: the function under test wrote to standard output") }));
  }

  [Test]
  public void ASourceThatDoesNotParse_IsABuildFailureWithFileLineAndText() {
    var built = Runner(this._workspace!.FullName).Compile(FixtureInput("broken.js", "add", "(a: i32 in, b: i32 in) -> i32"));

    Assert.That(built.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
    Assert.That(built.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1191" }));
    Assert.That(built.Diagnostics[1].Message, Is.EqualTo("node rejected the input: broken.js:2: SyntaxError: Unexpected token ';'"));
  }

  [TestCase("throws-on-load.mjs", "F", "this module fails while it loads")]
  [TestCase("add.mjs", "missing", "the export 'missing' does not exist in ./add.mjs or is not a function")]
  public void AModuleThatThrowsOnLoad_OrAMissingExport_IsACrash(string file, string export, string stderr) {
    var error = this.Failure(FixtureInput(file, export, "(a: i32 in) -> i32"), "01000000");

    Assert.That(error.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY1192" }));
    Assert.That(error.Diagnostics[0].Message, Does.Contain(stderr));
  }

  [Test]
  public void AFunctionThatNeverReturns_EndsAtTheTimeLimit() {
    var runner = Runner(this._workspace!.FullName, RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(5)));
    var input = FixtureInput("spin.mjs", "F", "(a: i32 in) -> i32");
    var built = runner.Compile(input);
    var error = Assert.Throws<LanguageRunnerException>(() => runner.ExecuteFunction(built.Directory, "F", input.Signature, [[1, 0, 0, 0]]));

    Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("PLY0031"));
  }

  [Test]
  public void AWrongExpectation_IsExplainedWithTheValuesOfTheCall() {
    var vectors = VectorFile.Load(Path.Combine(Fixtures, "mismatch.vectors.json"));
    var report = Runner(this._workspace!.FullName).RunVectors(FixtureInput("add.mjs", "F", "(a: i32 in, b: i32 in) -> i32"), vectors);
    var diagnostics = NodeRunner.Explain(report, vectors);

    Assert.That(report.Passed, Is.Zero);
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0051", "PLY1194" }));
    Assert.That(diagnostics[1].Message, Is.EqualTo("call 0: expected 6, actual 5"));
  }

  [Test]
  public void AMismatchUnderAnUlpPolicy_IsPLY0052() {
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"runner-javascript/ulp", "provenance":{"kind":"published","source":"fixture","detail":"by hand","generatedBy":"hand"},
       "exactness":{"mode":"ulp","bound":1,"format":"f64","reason":"the test of the ulp policy"},
       "export":"id", "signature":{"parameters":[{"name":"x","type":"f64"}],"result":"f64"},
       "vectors":[{"name":"two-ulp","input":{"hex":"000000000000f03f"},"expected":{"hex":"020000000000f03f"}}]}
      """, "ulp.vectors.json");
    var report = Runner(this._workspace!.FullName).RunVectors(Input("echo.mjs", _echo, "id", "(x: f64 in) -> f64"), vectors);

    Assert.That(NodeRunner.Explain(report, vectors).Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0052", "PLY1194" }));
  }

  [Test]
  public void ModuleState_DoesNotLeakBetweenOperations() {
    var input = Input("counter.mjs", "let count = 0; export const next = () => ++count;", "next", "() -> i32");
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"runner-javascript/counter", "provenance":{"kind":"published","source":"fixture","detail":"by hand","generatedBy":"hand"},
       "export":"next", "signature":{"parameters":[],"result":"i32"},
       "vectors":[{"name":"first","input":{"hex":""},"expected":{"hex":"01000000"}}, {"name":"second","input":{"hex":""},"expected":{"hex":"02000000"}}]}
      """, "counter.vectors.json");
    var runner = Runner(this._workspace!.FullName);

    Assert.That(runner.RunVectors(input, vectors).Verified, Is.True);
    Assert.That(runner.RunVectors(input, vectors).Verified, Is.True);
  }

  [Test]
  public void ARecordedFile_NamesNodeAndCannotBeRecordedAgain() {
    var input = FixtureInput("wide.mjs", "D", "(x: f64 in) -> f64");
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"runner-javascript/wide", "provenance":{"kind":"published","source":"fixture","detail":"inputs only","generatedBy":"hand"},
       "export":"D", "signature":{"parameters":[{"name":"x","type":"f64"}],"result":"f64"},
       "vectors":[{"name":"minus-zero","input":{"hex":"0000000000000080"}}]}
      """, "wide.vectors.json");
    var runner = Runner(this._workspace!.FullName);
    var recorded = runner.Record(input, vectors);

    Assert.That((recorded.Provenance.Kind, recorded.Provenance.GeneratedBy), Is.EqualTo(("reference-run", "node")));
    Assert.Throws<InvalidOperationException>(() => runner.Record(input, recorded));
    Assert.Throws<InvalidOperationException>(() => runner.Record(FixtureInput("adler32.mjs", "adler32", "(data: seq<u8> in) -> u32"),
      VectorFile.Load(Path.Combine(Fixtures, "adler32.vectors.json"))));
  }

  [Test]
  public void TheNodeTestSuite_ReportsItsPassingAndFailingTests() {
    var report = Runner(this._workspace!.FullName).RunTests(Path.Combine(Fixtures, "tests"));

    Assert.That((report.Total, report.Passed, report.Failed, report.Skipped), Is.EqualTo((3, 2, 1, 0)));
    Assert.That(report.Failures.Single().Name, Is.EqualTo("fails on purpose"));
    Assert.That(report.Failures.Single().Message, Does.Contain("this test fails on purpose"));
  }

  [Test]
  public void AHelper_RunsWithItsStandardInput() {
    var helper = Path.Combine(this._workspace!.FullName, "echo-helper.mjs");
    File.WriteAllText(helper, "process.stdin.pipe(process.stdout);");
    var result = Runner(this._workspace.FullName).RunHelper(helper, [], "hello"u8.ToArray());

    Assert.That((result.ExitCode, Encoding.UTF8.GetString(result.Stdout.Span)), Is.EqualTo((0, "hello")));
  }

  [Test]
  public void Execute_RunsTheEntryModuleWithTheArguments() {
    var runner = Runner(this._workspace!.FullName);
    var built = runner.Compile(Input("index.mjs", "console.log(process.argv.slice(2).join('+'));", "F", "() -> void"));
    var result = runner.Execute(built, ["a", "b"]);

    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Is.EqualTo("a+b\n"));
  }

}
