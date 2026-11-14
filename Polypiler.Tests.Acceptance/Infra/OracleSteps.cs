using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/ver-oracle.feature</c>. Programs are real: node runs the
/// reference and the candidate in the sandbox of this host, one process per vector.
/// </summary>
[Binding, Scope(Feature = "Oracle and differential comparison framework")]
public sealed class OracleSteps {

  private const string DefaultVectors = "tests/fixtures/verify/oracle/adler32.vectors.json";

  private VectorFile? _file;
  private IVerificationOracle? _oracle;
  private bool _registered;
  private string? _directory;
  private VerificationResult? _result;
  private byte[] _reference = [];
  private byte[] _actual = [];
  private ComparisonReport? _report;

  private VectorFile File => this._file ??= VectorFile.Load(Repository(DefaultVectors));

  private VerificationResult Result => this._result ?? throw new InvalidOperationException("nothing was verified");

  private static string Repository(string path) => Path.Combine(PrototypeFixture.RepositoryRoot, path);

  private static ToolchainProgram Node(string path)
    => new(new CrossLanguageRunner(RunnerTesting.Host, RunnerTesting.Toolchains), "node", Repository(path), RunLimits.Default, TrustLevel.Trusted);

  /// <summary>Removes the directory a scenario made.</summary>
  [AfterScenario]
  public void RemoveDirectory() {
    if (this._directory is { } directory)
      Directory.Delete(directory, true);
  }

  [Given("^the vector file " + StepText.Quoted + "$")]
  public void VectorFileIs(string path) => this._file = VectorFile.Load(Repository(path));

  [Given("^the source-toolchain oracle \"node\" running " + StepText.Quoted + "$")]
  [Given("^the source-toolchain oracle \"node\" running the missing file " + StepText.Quoted + "$")]
  public void NodeOracle(string path) => this._oracle = new ToolchainOracle("node", OracleKind.SourceToolchain, Node(path));

  [When("^the candidate " + StepText.Quoted + " is compared bit-exactly$")]
  public async Task CandidateIsCompared(string path) {
    var registry = new OracleRegistry();
    Assert.That(registry.Register(this.File.Fixture, this._oracle ?? new VectorFileOracle(this.File)), Is.True);
    var candidate = await Node(path).RunAsync(this.File.Vectors);
    Assert.That(candidate.Failure, Is.Null);
    this._result = await OracleVerification.VerifyAsync(registry, this.File.Fixture, this.File.Vectors, candidate.Outputs, new ExactnessPolicy.Exact());
  }

  [Then("^vector " + StepText.Quoted + " passes with output ([0-9a-f]+)$")]
  public void VectorPassesWith(string name, string output) {
    var outcome = this.Outcome(name);
    Assert.That((outcome.Passed, outcome.ActualHex, outcome.ReferenceHex), Is.EqualTo((true, output, output)));
  }

  [Then("^vector " + StepText.Quoted + " passes$")]
  public void VectorPasses(string name) => Assert.That(this.Outcome(name).Passed, Is.True);

  [Then(@"^the report counts (\d+) of (\d+) vectors passed$")]
  public void ReportCounts(int passed, int total) {
    Assert.That((this.Result.Report.Passed, this.Result.Report.Total), Is.EqualTo((passed, total)));
    Assert.That(this.Result.Diagnostics, Is.Empty);
  }

  [Then("^vector " + StepText.Quoted + @" fails with expected ([0-9a-f]+), actual ([0-9a-f]+) and first difference at byte (\d+)$")]
  public void VectorFails(string name, string expected, string actual, int offset) {
    var outcome = this.Outcome(name);
    Assert.That((outcome.Passed, outcome.ReferenceHex, outcome.ActualHex, outcome.FirstDifferingOffset), Is.EqualTo((false, expected, actual, (int?)offset)));
    Assert.That(this.Result.Diagnostics.Select(diagnostic => diagnostic.Message),
      Does.Contain($"'{this.File.Fixture}' vector '{name}': expected {expected}, actual {actual}, first difference at byte {offset}"));
  }

  [Then(@"^diagnostic (PLY\d{4}) is reported and the mapped exit code is (\d+)$")]
  public void DiagnosticIsReported(string code, int exitCode) {
    Assert.That(this.Result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { code }), string.Join("\n", this.Result.Diagnostics));
    Assert.That((int)this.Result.ExitCode, Is.EqualTo(exitCode));
  }

  [Then("^no vector is counted as passed$")]
  public void NoVectorPassed() {
    Assert.That((this.Result.Report.Passed, this.Result.Report.Verified, this.Result.Report.Outcomes.Count), Is.EqualTo((0, false, 0)));
    Assert.That(this.Result.Report.Total, Is.EqualTo(this.File.Vectors.Count));
  }

  [Given("^the reference f64 value with bits 0x([0-9A-Fa-f]{16}) and the actual f64 value with bits 0x([0-9A-Fa-f]{16})$")]
  public void FloatValues(string reference, string actual) {
    this._reference = Bytes(reference);
    this._actual = Bytes(actual);
  }

  [When(@"^they are compared with policy ((?:exact|ulp \d+)(?:, (?:bitwise|class))?)$")]
  public void ComparedWithPolicy(string text) {
    var (mode, rule) = text.Split(", ") is [var first, var second] ? (first, second) : (text, "");
    ExactnessPolicy policy = mode == "exact"
      ? new ExactnessPolicy.Exact(FloatFormat.F64)
      : new ExactnessPolicy.Ulp(int.Parse(mode[4..], CultureInfo.InvariantCulture), FloatFormat.F64, reason: "declared by the scenario");
    policy = rule switch {
      "bitwise" => policy with { SpecialValues = SpecialValues.Bitwise },
      "class" => policy with { SpecialValues = SpecialValues.Class },
      _ => policy
    };
    this._report = DifferentialComparer.Compare([new("value", [], null)], [this._reference], [this._actual], policy);
  }

  [Then(@"^the outcome is ((?:pass|PLY0051|PLY0052)(?:,? distance \d+)?(?: special values differ)?)$")]
  public void OutcomeIs(string text) {
    var verdict = text.StartsWith("pass", StringComparison.Ordinal) ? "pass" : text[..7];
    var distance = Regex.Match(text, @"distance (\d+)").Groups[1].Value;
    var special = text.EndsWith("special values differ", StringComparison.Ordinal) ? "yes" : "";
    var report = this._report ?? throw new InvalidOperationException("nothing was compared");
    var outcome = report.Outcomes.Single();
    var diagnostics = report.Diagnostics("scenario");
    Assert.That(outcome.Passed, Is.EqualTo(verdict == "pass"), string.Join("\n", diagnostics));
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(verdict == "pass" ? Array.Empty<string>() : [verdict]));
    if (distance.Length > 0)
      Assert.That(outcome.UlpDistance, Is.EqualTo(long.Parse(distance, CultureInfo.InvariantCulture)));
    if (special.Length > 0)
      Assert.That(diagnostics.Single().Message, Does.EndWith("special values differ under signed-zero"));
  }

  [Given("^a vector file inside a directory that contains \"polypiler.manifest.json\"$")]
  public void SelfProducedVectorFile() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-oracle-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
    System.IO.File.WriteAllText(Path.Combine(this._directory, OracleRegistry.OutputManifest), "{}");
    var copy = Path.Combine(this._directory, "adler32.vectors.json");
    System.IO.File.Copy(Repository(DefaultVectors), copy);
    this._file = VectorFile.Load(copy);
  }

  [When("^it is registered as the oracle for fixture " + StepText.Quoted + "$")]
  public async Task Registered(string fixture) {
    var registry = new OracleRegistry();
    this._registered = registry.Register(fixture, new VectorFileOracle(this.File));
    this._result = await OracleVerification.VerifyAsync(registry, fixture, this.File.Vectors, [.. this.File.Vectors.Select(vector => vector.Expected!)], this.File.Exactness);
  }

  [Then("^registration is rejected$")]
  public void RegistrationRejected() => Assert.That(this._registered, Is.False);

  [Then("^verifying " + StepText.Quoted + @" reports (PLY\d{4}) with mapped exit code (\d+)$")]
  public void VerifyingReports(string fixture, string code, int exitCode) {
    var diagnostic = this.Result.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo((code, $"no independent oracle is registered for '{fixture}'")));
    Assert.That((int)this.Result.ExitCode, Is.EqualTo(exitCode));
    Assert.That(this.Result.Report.Passed, Is.Zero);
  }

  private VectorOutcome Outcome(string name) => this.Result.Report.Outcomes.Single(outcome => outcome.Name == name);

  private static byte[] Bytes(string hex) {
    var bytes = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    return bytes;
  }

}
