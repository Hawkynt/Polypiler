using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

[Binding, Scope(Feature = "JavaScript toolchain runner")]
public sealed class NodeRunnerSteps : IDisposable {

  private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("polypiler-node-steps-");
  private string _file = "";
  private VectorFile? _vectors;
  private RunLimits? _limits;
  private ComparisonReport? _report;
  private IReadOnlyList<CallOutcome>? _outcomes;
  private IReadOnlyList<Diagnostic>? _failure;
  private VectorFile? _recorded;

  public void Dispose() => this._workspace.Delete(true);

  private NodeRunner Runner => NodeRunnerCaseTests.Runner(this._workspace.FullName, this._limits);

  private RunnerInput Input(string export, CallSignature signature)
    => NodeRunnerCaseTests.FixtureInput(this._file, export, "() -> void") with { Signature = signature };

  [Given("^the runner fixture " + StepText.Quoted + "$")]
  public void Fixture(string file) => this._file = file;

  [Given("^the runner fixture " + StepText.Quoted + " and the vector file " + StepText.Quoted + "$")]
  public void FixtureAndVectors(string file, string vectors) {
    this._file = file;
    this._vectors = VectorFile.Load(Path.Combine(NodeRunnerCaseTests.Fixtures, vectors));
  }

  [Given(@"^a vector file with the export " + StepText.Quoted + ", the signature " + StepText.Quoted + @" and the vectors (.+)$")]
  public void Vectors(string export, string signature, string vectors) {
    var entries = Regex.Matches(vectors, @"\w+\(""(?<text>[^""]*)""\) = (?<value>\d+)").Select(match => {
      var bytes = Encoding.ASCII.GetBytes(match.Groups["text"].Value);
      var name = match.Groups["text"].Value.Length == 0 ? "empty" : match.Groups["text"].Value;
      var input = Convert.ToHexStringLower([.. BitConverter.GetBytes(bytes.Length), .. bytes]);
      var output = Convert.ToHexStringLower(BitConverter.GetBytes(uint.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture)));
      return $$$"""{"name":"{{{name}}}","input":{"hex":"{{{input}}}"},"expected":{"hex":"{{{output}}}"}}""";
    });
    this._vectors = File(export, NodeRunnerCaseTests.Signature(signature), string.Join(",", entries));
  }

  [When("^RunVectors is executed$")]
  public void RunVectors() => this._report = this.Runner.RunVectors(this.Input(this._vectors!.Export!, this._vectors.Signature!), this._vectors);

  [When("^ExecuteFunction is called for the export " + StepText.Quoted + " with the signature " + StepText.Quoted + " on the input images (.+)$")]
  public void ExecuteFunction(string export, string signature, string images) {
    var runner = this.Runner;
    var input = this.Input(export, NodeRunnerCaseTests.Signature(signature));
    var built = runner.Compile(input);
    try {
      this._outcomes = runner.ExecuteFunction(built.Directory, export, input.Signature, [.. Quoted(images).Select(Convert.FromHexString)]);
    } catch (LanguageRunnerException failure) {
      this._failure = failure.Diagnostics;
    }
  }

  [When("^Record is executed for the export " + StepText.Quoted + " with the signature " + StepText.Quoted + " on the input images (.+)$")]
  public void Record(string export, string signature, string images) {
    var parsed = NodeRunnerCaseTests.Signature(signature);
    var file = File(export, parsed, string.Join(",", Quoted(images).Select((image, index) => $$$"""{"name":"v{{{index}}}","input":{"hex":"{{{image}}}"}}""")));
    this._recorded = this.Runner.Record(this.Input(export, parsed), file);
  }

  [When(@"^(Compile|ExecuteFunction) is executed with a wall-time limit of (\d+) seconds$")]
  public void Failing(string operation, int seconds) {
    this._limits = RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(seconds));
    var input = this.Input("F", NodeRunnerCaseTests.Signature("(a: i32 in) -> i32"));
    var built = this.Runner.Compile(input);
    if (operation == "Compile") {
      Assert.That(built.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
      this._failure = built.Diagnostics;
      return;
    }

    this.ExecuteFunction("F", "(a: i32 in) -> i32", "\"01000000\"");
  }

  [Then(@"^the report lists (\d+) vectors passed and (\d+) failed$")]
  public void Report(int passed, int failed) => Assert.That((this._report!.Passed, this._report.Total - this._report.Passed), Is.EqualTo((passed, failed)));

  [Then("^the vector " + StepText.Quoted + " has the input image " + StepText.Quoted + "$")]
  public void InputImage(string name, string image) => Assert.That(Convert.ToHexStringLower(this._vectors!.Vectors.Single(vector => vector.Name == name).Input), Is.EqualTo(image));

  [Then("^its output image is " + StepText.Quoted + "$")]
  public void OutputImage(string image) => Assert.That(this._report!.Outcomes[0].ActualHex, Is.EqualTo(image));

  [Then("^the driver answers exactly (.+)$")]
  public void Answers(string lines) => Assert.That(this._outcomes!.Select(NodeRunnerCaseTests.Outcome), Is.EqualTo(Quoted(lines)));

  [Then("^the third outcome has the trap class " + StepText.Quoted + " and no output image$")]
  public void ThirdTrap(string trap) {
    Assert.That(this._outcomes![2].Trap, Is.EqualTo(TrapClass.Parse(trap)));
    Assert.That(this._outcomes[2].Image, Is.Null);
  }

  [Then("^the recorded output images are (.+)$")]
  public void Recorded(string images) => Assert.That(this._recorded!.Vectors.Select(vector => Convert.ToHexStringLower(vector.Expected!)), Is.EqualTo(Quoted(images)));

  [Then("^the recorded vector file has the provenance kind " + StepText.Quoted + " and the producer " + StepText.Quoted + "$")]
  public void Provenance(string kind, string producer)
    => Assert.That((this._recorded!.Provenance.Kind, this._recorded.Provenance.GeneratedBy), Is.EqualTo((kind, producer)));

  [Then("^the report is explained by the diagnostics (PLY\\d{4}) and (PLY\\d{4}) " + StepText.Quoted + "$")]
  public void Explained(string first, string second, string message) {
    var diagnostics = NodeRunner.Explain(this._report!, this._vectors!);
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { first, second }));
    Assert.That(diagnostics[1].Message, Is.EqualTo(message));
  }

  [Then(@"^the operation fails with the diagnostics (.+)$")]
  public void Fails(string codes)
    => Assert.That(this._failure?.Select(diagnostic => diagnostic.Code), Is.SupersetOf(Regex.Matches(codes, @"PLY\d{4}").Select(match => match.Value)));

  [Then("^no call outcome is returned$")]
  public void NoOutcome() => Assert.That(this._outcomes, Is.Null);

  private static List<string> Quoted(string text) => [.. Regex.Matches(text, StepText.Quoted).Select(match => StepText.Unescape(match.Groups[1].Value))];

  private static VectorFile File(string export, CallSignature signature, string vectors) {
    var parameters = string.Join(",", signature.Parameters.Select(parameter =>
      $$"""{"name":"{{parameter.Name}}","type":"{{parameter.Type}}","mode":"{{(parameter.Mode == ParameterMode.InOut ? "inout" : parameter.Mode == ParameterMode.Out ? "out" : "in")}}"}"""));
    var result = signature.Result is { } type ? $"\"{type}\"" : "\"void\"";
    return VectorFile.Parse($$"""
      {"schema":1,"fixture":"runner-javascript/feature",
       "provenance":{"kind":"published","source":"feature","detail":"images written by hand","generatedBy":"hand"},
       "export":"{{export}}","signature":{"parameters":[{{parameters}}],"result":{{result}}},"vectors":[{{vectors}}]}
      """, "feature.vectors.json");
  }

}
