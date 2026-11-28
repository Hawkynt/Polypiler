using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Runners;

/// <summary>
/// The steps of <c>tests/features/infra/infra-runner-kotlin.feature</c>: every unit is compiled by the pinned
/// <c>kotlinc</c> and run on the pinned JDK through <see cref="KotlinRunner"/>; a scenario whose
/// toolchains are missing is inconclusive, and fails with <c>POLYPILER_REQUIRE_TOOLCHAINS=1</c>.
/// </summary>
[Binding, Scope(Feature = "Kotlin toolchain runner")]
public sealed class KotlinRunnerSteps : IDisposable {

  private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("polypiler-kotlin-steps-");
  private readonly List<string> _sources = [];
  private KotlinRunner? _runner;
  private VectorFile? _vectors;
  private ComparisonReport? _report;
  private CompileResult? _compiled;
  private IReadOnlyList<CallOutcome>? _outcomes;
  private readonly Dictionary<string, (string Input, string Output)> _recorded = new(StringComparer.Ordinal);

  public void Dispose() => this._workspace.Delete(true);

  private KotlinRunner Runner {
    get {
      if (this._runner is null) {
        var require = Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable);
        new RequiresToolchainAttribute(KotlinRunner.Compiler).Check(require);
        new RequiresToolchainAttribute(KotlinRunner.Jdk).Check(require);
        this._runner = KotlinRunnerTests.Runner(this._workspace.FullName);
      }

      return this._runner;
    }
  }

  private static string Hex(long value, int bytes) {
    var image = new byte[bytes];
    for (var index = 0; index < bytes; ++index)
      image[index] = (byte)(value >> (8 * index));

    return Convert.ToHexStringLower(image);
  }

  private static VectorFile Vectors(string export, string parameters, string result, IEnumerable<(string Name, string Input, string? Expected)> vectors) => VectorFile.Parse($$"""
    {
      "schema":1, "fixture":"run-kt.{{export}}",
      "provenance":{"kind":"published","source":"Kotlin language specification","detail":"two's-complement Int","generatedBy":"hand"},
      "export":"{{export}}",
      "signature":{"parameters":[{{parameters}}],"result":"{{result}}"},
      "vectors":[{{string.Join(",", vectors.Select(vector => $"{{\"name\":\"{vector.Name}\",\"input\":{{\"hex\":\"{vector.Input}\"}}{(vector.Expected is null ? "" : $",\"expected\":{{\"hex\":\"{vector.Expected}\"}}")}}}"))}}]
    }
    """, export + ".vectors.json");

  private const string TwoInts = """{"name":"a","type":"i32"},{"name":"b","type":"i32"}""";

  [Given("^the Kotlin file " + StepText.Quoted + "$")]
  public void KotlinFile(string source) => this._sources.Add(StepText.Unescape(source));

  [Given("^the Kotlin file " + StepText.Quoted + " with " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void KotlinFiles(string first, string second, string third) => this._sources.AddRange([first, second, third]);

  [Given(@"^a vector file with the vectors add\((-?\d+), (-?\d+)\) = (-?\d+) and add\((-?\d+), (-?\d+)\) = (-?\d+)$")]
  public void AddVectors(long a1, long b1, long r1, long a2, long b2, long r2)
    => this._vectors = Vectors("add", TwoInts, "i32", [
      ($"add({a1}, {b1})", Hex(a1, 4) + Hex(b1, 4), Hex(r1, 4)),
      ($"add({a2}, {b2})", Hex(a2, 4) + Hex(b2, 4), Hex(r2, 4))
    ]);

  [When("^RunVectors is executed with the profile kotlin/jvm$")]
  public void RunVectors() => this._report = this.Runner.RunVectors(KotlinRunnerTests.Input("Unit.kt", this._sources.Single(), this._vectors!), this._vectors!);

  [Then(@"^the report lists (\d+) vectors passed and (\d+) failed$")]
  public void ReportLists(int passed, int failed) => Assert.That((this._report!.Passed, this._report.Total - this._report.Passed), Is.EqualTo((passed, failed)));

  [Then("^the vector " + StepText.Quoted + " has the input image " + StepText.Quoted + " and the output image " + StepText.Quoted + "$")]
  public void VectorImages(string name, string input, string output) {
    var vector = this._vectors!.Vectors.Single(candidate => candidate.Name == name);
    var outcome = this._report!.Outcomes.Single(candidate => candidate.Name == name);
    Assert.Multiple(() => {
      Assert.That(Convert.ToHexStringLower(vector.Input), Is.EqualTo(input));
      Assert.That(outcome.ActualHex, Is.EqualTo(output));
      Assert.That(outcome.Passed, Is.True);
    });
  }

  [Given("^the compiled fixture " + StepText.Quoted + "$")]
  public void CompiledFixture(string source) {
    this._vectors = Vectors("div", TwoInts, "i32", [("call", "0000000000000000", null)]);
    this._compiled = this.Runner.Compile(KotlinRunnerTests.Input("Unit.kt", StepText.Unescape(source), this._vectors));
    Assert.That(this._compiled.Outcome, Is.EqualTo(Core.Verification.Running.RunOutcome.Completed), () => string.Join("\n", this._compiled.Diagnostics));
  }

  [When(@"^ExecuteFunction is called for the export " + StepText.Quoted + @" with the tuples \((-?\d+), (-?\d+)\), \((-?\d+), (-?\d+)\) and \((-?\d+), (-?\d+)\)$")]
  public void ExecuteFunction(string export, long a1, long b1, long a2, long b2, long a3, long b3)
    => this._outcomes = this.Runner.ExecuteFunction(this._compiled!.Directory, export, this._vectors!.Signature!,
      [.. new[] { (a1, b1), (a2, b2), (a3, b3) }.Select(tuple => Convert.FromHexString(Hex(tuple.Item1, 4) + Hex(tuple.Item2, 4)))]);

  [Then("^the driver prints exactly the lines " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void DriverLines(string first, string second, string third) {
    // The protocol reads each line back into one outcome: an image, or a trap class with its native name.
    var lines = this._outcomes!.Select(outcome => outcome.Trap is { } trap ? $"!{trap} {outcome.NativeTrapName}" : Convert.ToHexStringLower(outcome.Image!));
    Assert.That(lines, Is.EqualTo(new[] { first, second, third }));
  }

  [Then("^ExecuteFunction returns three results in that order$")]
  public void ThreeResults() => Assert.That(this._outcomes!.Select(outcome => outcome.Trap is null), Is.EqualTo(new[] { true, false, true }));

  [When(@"^Record is executed for id\((\d+)\), nan\(NaN with the bit pattern 0x([0-9A-F]+)\) and sum\(\[(\d+), (\d+), (\d+)\]\)$")]
  public void Record(ulong id, string bits, int first, int second, int third) {
    var source = string.Join("\n\n", this._sources.Select(StepText.Unescape));
    var nan = ulong.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    var sum = Hex(3, 4) + Hex(first, 4) + Hex(second, 4) + Hex(third, 4);
    foreach (var (export, type, result, input) in new[] {
      ("id", "u64", "u64", Hex((long)id, 8)), ("nan", "f64", "f64", Hex((long)nan, 8)), ("sum", "seq<i32>", "i32", sum)
    }) {
      var vectors = Vectors(export, $$"""{"name":"a","type":"{{type}}"}""", result, [("call", input, null)]);
      var recorded = this.Runner.Record(KotlinRunnerTests.Input("Unit.kt", source, vectors), vectors);
      this._recorded[export] = (input, Convert.ToHexStringLower(recorded.Vectors[0].Expected!));
    }
  }

  [Then("^the recorded output image of (\\w+) is " + StepText.Quoted + "$")]
  public void RecordedOutput(string export, string image) => Assert.That(this._recorded[export].Output, Is.EqualTo(image));

  [Then("^the input image of (\\w+) is " + StepText.Quoted + " and its output image is " + StepText.Quoted + "$")]
  public void RecordedImages(string export, string input, string output) => Assert.That(this._recorded[export], Is.EqualTo((input, output)));

}
