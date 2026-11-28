using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Verification.Running.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Runners;

[Binding, Scope(Feature = "C# toolchain runner")]
public sealed class CSharpRunnerSteps : IDisposable {

  private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("polypiler-csharp-steps-");
  private string _source = "";
  private VectorFile? _vectors;
  private RunLimits? _driverLimits;
  private ComparisonReport? _report;
  private CompileResult? _compiled;
  private IReadOnlyList<CallOutcome>? _outcomes;
  private LanguageRunnerException? _failure;
  private VectorFile? _recorded;
  private CallSignature? _derived;

  public void Dispose() => this._workspace.Delete(true);

  private CSharpRunner Runner => CSharpRunnerTests.Runner(this._workspace.FullName, this._driverLimits);

  private RunnerInput Input(string export, CallSignature signature, string profile = "csharp/net10")
    => new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["Fixture.cs"] = Encoding.UTF8.GetBytes(this._source) },
      export, signature, profile, TrustLevel.Trusted);

  [Given("^the C# file " + StepText.Quoted + "$")]
  public void CSharpFile(string source) => this._source = StepText.Unescape(source);

  [Given("^the fixture " + StepText.Quoted + " and the vector file " + StepText.Quoted + "$")]
  public void Fixture(string source, string vectors) {
    this._source = System.IO.File.ReadAllText(Path.Combine(CSharpRunnerTests.Fixtures, source));
    this._vectors = VectorFile.Load(Path.Combine(CSharpRunnerTests.Fixtures, vectors));
  }

  [Given("^a vector file for the export " + StepText.Quoted + " with the signature " + StepText.Quoted + " and the vectors " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void VectorFileOf(string export, string signature, string first, string second) {
    var parsed = CSharpRunnerTests.Signature(signature);
    var vectors = new[] { first, second }.Select(vector => {
      var (arguments, result) = Values(vector);
      return (CallImages.Input(parsed, [.. arguments.Select(Integer)]), (byte[]?)CallImages.Output(parsed, Integer(result), []));
    });
    this._vectors = File(export, parsed, vectors);
  }

  [Given(@"^a wall-time limit of (\d+) seconds$")]
  public void WallTime(int seconds) => this._driverLimits = RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(seconds));

  [When(@"^RunVectors is executed with the profile (\S+)$")]
  public void RunVectors(string profile) => this._report = this.Runner.RunVectors(this.Input(this._vectors!.Export!, this._vectors.Signature!, profile), this._vectors);

  [When("^Compile is executed for the export " + StepText.Quoted + " with the signature " + StepText.Quoted + "$")]
  public void Compile(string export, string signature) => this._compiled = this.Runner.Compile(this.Input(export, CSharpRunnerTests.Signature(signature)));

  [When("^ExecuteFunction is called for the export " + StepText.Quoted + " with the signature " + StepText.Quoted + " on the input images? (.+)$")]
  public void ExecuteFunction(string export, string signature, string images) {
    var input = this.Input(export, CSharpRunnerTests.Signature(signature));
    var runner = this.Runner;
    var built = runner.Compile(input);
    Assert.That(built.Outcome, Is.EqualTo(RunOutcome.Completed), string.Join("\n", built.Diagnostics));
    try {
      this._outcomes = runner.ExecuteFunction(built.Directory, export, input.Signature, [.. Hexes(images).Select(Convert.FromHexString)]);
    } catch (LanguageRunnerException failure) {
      this._failure = failure;
    }
  }

  [When("^CSharpSignatures.Derive is applied to " + StepText.Quoted + "$")]
  public void Derive(string export) => this._derived = CSharpSignatures.Derive(CSharpSignatures.Find(
    new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["Fixture.cs"] = Encoding.UTF8.GetBytes(this._source) }, export));

  [When("^Record is executed for the export " + StepText.Quoted + " with the signature " + StepText.Quoted + " on the input images? (.+)$")]
  public void Record(string export, string signature, string images) {
    var parsed = CSharpRunnerTests.Signature(signature);
    var file = File(export, parsed, Hexes(images).Select(image => (Convert.FromHexString(image), (byte[]?)null)));
    this._recorded = this.Runner.Record(this.Input(export, parsed), file);
  }

  [Then(@"^the report lists (\d+) vectors passed and (\d+) failed$")]
  public void Report(int passed, int failed) => Assert.That((this._report!.Passed, this._report.Total - this._report.Passed), Is.EqualTo((passed, failed)));

  [Then("^the first vector has the input image " + StepText.Quoted + " and the output image " + StepText.Quoted + "$")]
  public void FirstVector(string input, string output) {
    Assert.That(Convert.ToHexStringLower(this._vectors!.Vectors[0].Input), Is.EqualTo(input));
    Assert.That(this._report!.Outcomes[0].ActualHex, Is.EqualTo(output));
  }

  [Then("^the report is explained by the diagnostics (PLY\\d{4}) and (PLY\\d{4}) " + StepText.Quoted + "$")]
  public void Explained(string first, string second, string message) {
    var diagnostics = CSharpRunner.Explain(this._report!, this._vectors!);
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { first, second }));
    Assert.That(diagnostics[1].Message, Is.EqualTo(message));
  }

  [Then("^the driver answers exactly (.+)$")]
  public void Answers(string lines)
    => Assert.That(this._outcomes!.Select(outcome => outcome.Trap is { } trap ? $"!{trap} {outcome.NativeTrapName}" : Convert.ToHexStringLower(outcome.Image!)), Is.EqualTo(Hexes(lines)));

  [Then(@"^the third outcome has the trap class (\S+)$")]
  public void ThirdTrap(string trap) => Assert.That(this._outcomes![2].Trap, Is.EqualTo(TrapClass.Parse(trap)));

  [Then("^the signature is " + StepText.Quoted + "$")]
  public void Signature(string signature) => Assert.That(this._derived!.ToString(), Is.EqualTo(signature));

  [Then("^the recorded output images? (?:is|are) (.+)$")]
  public void Recorded(string images)
    => Assert.That(this._recorded!.Vectors.Select(vector => Convert.ToHexStringLower(vector.Expected!)), Is.EqualTo(Hexes(images)));

  [Then("^the outcome is BuildFailed with the diagnostics (PLY\\d{4}) and (PLY\\d{4}) carrying " + StepText.Quoted + "$")]
  public void BuildFailed(string first, string second, string error) {
    Assert.That(this._compiled!.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
    Assert.That(this._compiled.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { first, second }));
    Assert.That(this._compiled.Diagnostics[1].Message, Does.Contain(error));
  }

  [Then("^no assembly exists in the build directory$")]
  public void NoAssembly() => Assert.That(System.IO.File.Exists(Path.Combine(this._compiled!.Directory, CSharpDriverGenerator.Assembly)), Is.False);

  [Then(@"^the operation fails with the diagnostic (PLY\d{4})$")]
  public void Fails(string code) => Assert.That(this._failure?.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(code));

  [Then("^no call outcome is returned$")]
  public void NoOutcome() => Assert.That(this._outcomes, Is.Null);

  /// <summary>The quoted items of a list written <c>"a", "b" and "c"</c>.</summary>
  private static List<string> Hexes(string text) => [.. Regex.Matches(text, StepText.Quoted).Select(match => StepText.Unescape(match.Groups[1].Value))];

  /// <summary>The arguments and the result of a vector written <c>(2, 3) -&gt; 5</c>.</summary>
  private static (BigInteger[] Arguments, BigInteger Result) Values(string vector) {
    var arrow = vector.IndexOf("->", StringComparison.Ordinal);
    var arguments = vector[..arrow].Trim().Trim('(', ')').Split(',').Select(argument => BigInteger.Parse(argument.Trim(), System.Globalization.CultureInfo.InvariantCulture));
    return ([.. arguments], BigInteger.Parse(vector[(arrow + 2)..].Trim(), System.Globalization.CultureInfo.InvariantCulture));
  }

  /// <summary>A vector file of hand-written images for an export.</summary>
  private static VectorFile File(string export, CallSignature signature, IEnumerable<(byte[] Input, byte[]? Expected)> vectors) {
    var parameters = string.Join(",", signature.Parameters.Select(parameter =>
      $$"""{"name":"{{parameter.Name}}","type":"{{parameter.Type}}","mode":"{{(parameter.Mode == ParameterMode.InOut ? "inout" : parameter.Mode == ParameterMode.Out ? "out" : "in")}}"}"""));
    var entries = string.Join(",", vectors.Select((vector, index) => $$"""{"name":"v{{index}}","input":{"hex":"{{Convert.ToHexStringLower(vector.Input)}}"}"""
      + (vector.Expected is { } expected ? $$""","expected":{"hex":"{{Convert.ToHexStringLower(expected)}}"}""" : "") + "}"));
    var result = signature.Result is { } type ? $"\"{type}\"" : "null";
    return VectorFile.Parse($$"""
      {"schema":1,"fixture":"runner-csharp/feature",
       "provenance":{"kind":"published","source":"feature","detail":"images written by hand","generatedBy":"hand"},
       "export":"{{export}}","signature":{"parameters":[{{parameters}}],"result":{{result}}},"vectors":[{{entries}}]}
      """, "feature.vectors.json");
  }

  private static ImageValue Integer(BigInteger value) => new ImageValue.Integer(value);

}
