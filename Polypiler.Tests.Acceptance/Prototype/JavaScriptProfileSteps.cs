using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/javascript-profiles.feature</c>. The expected outcome of a
/// call is what the fixture does after the C# compiler built it into this assembly; every generated
/// file is also parsed at the level it claims.
/// </summary>
[Binding, Scope(Feature = "JavaScript output for a concrete target")]
public sealed class JavaScriptProfileSteps(PrototypeScenario scenario) {

  private ProfileCase _profile = ProfileCase.Default;
  private string _node = "";

  /// <summary>The one file the last command line wrote into <c>out</c>.</summary>
  private string Generated() {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    return Directory.GetFiles(scenario.Workspace.Resolve("out")).Single();
  }

  /// <summary>Translates the source with the options of a profile into <c>out</c>.</summary>
  private void Translate(string options) {
    this._profile = ProfileCase.Of([.. CommandLine.Split(options)]);
    var result = scenario.Polypiler.Run(["translate", scenario.Source, "--from", "csharp", "--to", "javascript", .. this._profile.Arguments, "--output", "out"]);
    Assert.That(result.ExitCode, Is.Zero, result.Output);
  }

  /// <summary>The file the command wrote parses at the level of its profile.</summary>
  [Then(@"^the generated file parses at ECMAScript level (\d+)$")]
  public void ParsesAtTheLevel(int edition) {
    this._profile = ProfileCase.Of(scenario.Polypiler.LastArguments);
    var file = this.Generated();
    Assert.That(Path.GetFileName(file), Is.EqualTo(Path.GetFileNameWithoutExtension(scenario.Source) + this._profile.Parsed.Extension));
    Assert.That((int)this._profile.Parsed.Level, Is.EqualTo(edition));
    Assert.That(GeneratedJavaScript.Problems(File.ReadAllText(file), this._profile.Parsed), Is.Empty);
  }

  /// <summary>Node computes the hash of the compiled fixture with the file.</summary>
  [Then(@"^node evaluates Hash\(bytes of ""a""\) on it to the 64-bit value (\d+)$")]
  public void NodeEvaluatesTheHash(string expected) {
    var call = new PrototypeCall("Hash", [PrototypeCases.Text("a")]) { Returns = typeof(ulong) };
    var file = this.Generated();
    Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), [call], true, this._profile.Mode, this._profile.Global).Single(), Is.EqualTo(expected + " | [97]"));
    Assert.That(PrototypeFixture.CompiledOutcome(scenario.Fixture, call), Is.EqualTo(expected + " | [97]"));
  }

  /// <summary>Copies a fixture and translates it for the profile of the options.</summary>
  [Given("^the fixture " + StepText.Quoted + " translated with " + StepText.Quoted + "$")]
  public void FixtureTranslatedWith(string path, string options) {
    scenario.Workspace.Copy(PrototypeFixture.PathOf(path[..^3]), path);
    scenario.Source = path;
    scenario.Fixture = path[..^3];
    this.Translate(options);
  }

  /// <summary>Calls the hash of the module in a script of node's own, which prints the result as JSON.</summary>
  [When(@"^node calls Hash\(bytes of ""a""\)$")]
  public void NodeCallsTheHash() {
    var file = this.Generated();
    scenario.Workspace.Write("call.mjs", $"import {{ Hash }} from './out/{Path.GetFileName(file)}';\nprocess.stdout.write(JSON.stringify(Hash(Uint8Array.from([97]))));\n");
    var (exitCode, output) = Toolchain.RunNode(scenario.Workspace.Path, "call.mjs");
    Assert.That(exitCode, Is.Zero, output);
    this._node = output;
  }

  /// <summary>The result is the pair of halves of the compiled fixture's 64-bit result.</summary>
  [Then(@"^the result is the object \{ hi: (\d+), lo: (\d+) \}$")]
  public void ResultIsAPair(ulong high, ulong low) {
    Assert.That(this._node, Is.EqualTo($"{{\"hi\":{high},\"lo\":{low}}}"));
    Assert.That(PrototypeFixture.CompiledOutcome(scenario.Fixture, new PrototypeCall("Hash", [PrototypeCases.Text("a")])), Is.EqualTo($"{(high << 32) | low} | [97]"));
  }

  /// <summary>The file does not use the name, and describes the pair in its header.</summary>
  [Then("^the file contains no " + StepText.Quoted + "$")]
  public void FileContainsNo(string name) {
    var text = File.ReadAllText(this.Generated());
    Assert.That(text, Does.Not.Contain(name));
    Assert.That(text, Does.Contain("// Hash(data: Uint8Array): { hi, lo }")
      .And.Contain("// A 64-bit integer is an object { hi, lo }: its upper and its lower 32 bits, each as an unsigned number."));
  }

  /// <summary>Translates the C# source for the profile of the options.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) => this.Translate(options);

  /// <summary>The output does not call the library function and declares a helper that replaces it.</summary>
  [Then("^the output contains no " + StepText.Quoted + "$")]
  public void OutputContainsNo(string function) {
    var helper = "$" + function[(function.LastIndexOf('.') + 1)..];
    Assert.That(File.ReadAllText(this.Generated()), Does.Not.Contain(function).And.Contain($"function {helper}(a, b) {{"));
  }

  /// <summary>Node has the result of the step, which is also the compiled fixture's.</summary>
  [Then(@"^node returns (-?\d+) for F\((-?\d+), (-?\d+)\)$")]
  public void NodeReturns(string expected, int a, int b) {
    var call = new PrototypeCall("F", [a, b]);
    var file = this.Generated();
    Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), [call], true, this._profile.Mode, this._profile.Global).Single(), Is.EqualTo(expected));
    Assert.That(PrototypeFixture.CompiledOutcome("Operators", new PrototypeCall("MultiplyInt", call.Arguments)), Is.EqualTo(expected));
  }

  /// <summary>Node computes the bits the compiled fixture computes, without Math.fround and without typed arrays.</summary>
  [Then(@"^the bit pattern node computes for F\((\d+), (\d+)\) equals the compiled fixture's$")]
  public void BitsEqualTheCompiledFixtures(float a, float b) {
    var call = new PrototypeCall("F", [a, b]);
    var compiled = PrototypeFixture.CompiledOutcome("wide/OperatorsWide", new PrototypeCall("MultiplyAdd", call.Arguments));
    Assert.That(PrototypeFixture.SourceOf("wide/OperatorsWide"), Does.Contain("public static float MultiplyAdd(float a, float b) => a * b + a;"));
    var file = this.Generated();
    Assert.That(Toolchain.NodeOutcomes(Path.GetDirectoryName(file)!, Path.GetFileName(file), [call], true, this._profile.Mode, this._profile.Global).Single(), Is.EqualTo(compiled));
    Assert.That(File.ReadAllText(file), Does.Not.Contain("Math.fround").And.Not.Contain("Float32Array").And.Contain("return $fround($fround(a * b) + a);"));
  }

  /// <summary>Runs the translate command on a fixture with the options of the step.</summary>
  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void TranslateCommandWith(string options) {
    scenario.Workspace.Copy(PrototypeFixture.PathOf("Adler32"), "Adler32.cs");
    scenario.Polypiler.Run(["translate", "Adler32.cs", "--from", "csharp", "--to", "javascript", .. CommandLine.Split(options), "--output", "out"]);
  }

  /// <summary>The command was refused as a usage error and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

  /// <summary>Takes a fixture and translates it for the profile of the options.</summary>
  [Given("^any fixture translated with " + StepText.Quoted + "$")]
  public void AnyFixtureTranslatedWith(string options) => this.FixtureTranslatedWith("Adler32.cs", options);

  /// <summary>The header of the file names the fixture, the generator and the profile.</summary>
  [Then("^the header comment of the generated file names " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void HeaderNames(string level, string module) {
    var header = File.ReadAllLines(this.Generated()).TakeWhile(line => line.StartsWith("//", StringComparison.Ordinal)).ToList();
    Assert.That(header, Is.EqualTo(new[] {
      $"// {PrototypeFixture.ClassOf(scenario.Fixture)}: generated by polypiler. Change the source, not this file.",
      $"// JavaScript profile: {level}, {module}."
    }));
  }

}
