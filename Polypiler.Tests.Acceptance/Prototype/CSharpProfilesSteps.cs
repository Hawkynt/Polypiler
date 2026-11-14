using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/prototype/csharp-profiles.feature</c>. A file written for a profile
/// is built by Roslyn in the language version of the profile against the reference assemblies of its
/// framework, with every warning an error, and for the running runtime with overflow checking off and
/// on, to be called.
/// </summary>
[Binding, Scope(Feature = "C# output for a concrete target framework")]
public sealed class CSharpProfilesSteps(PrototypeScenario scenario) {

  private static readonly bool[] _overflowChecking = [false, true];

  private readonly List<(string Profile, string Text)> _outputs = [];
  private string[] _options = [];

  private static string LanguageVersionOf(string profile) => CSharpProfile.All.Single(candidate => candidate.Name == profile).LanguageVersionName;

  private string Translated(params string[] options) => scenario.Translate(scenario.Source, "csharp", "csharp", options).Text;

  /// <summary>The one file the last command line wrote into <c>out</c>.</summary>
  private string Generated() => File.ReadAllText(Directory.GetFiles(scenario.Workspace.Resolve("out")).Single());

  /// <summary>Scenario outline: the file compiles for its profile and names it in its header.</summary>
  [Then(@"^the generated file compiles against the reference assemblies of (\S+) at C# (\S+) without diagnostics$")]
  public void CompilesForTheProfile(string profile, string languageVersion) {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    var generated = this.Generated();
    Assert.That(generated, Does.Contain($"\n// Profile: {profile}, C# {languageVersion}.\n"));
    Assert.That(GeneratedCSharp.ComplaintsFor(generated, profile, languageVersion), Is.Empty, generated);
  }

  /// <summary>Scenario outline: the file, compiled for the running runtime with overflow checking off and on, computes the published value.</summary>
  [Then(@"^compiled for the running runtime, Compute\(bytes of " + StepText.Quoted + @"\) returns (\d+)$")]
  public void ComputeReturns(string text, uint expected) {
    var generated = this.Generated();
    foreach (var checking in _overflowChecking)
      Assert.That(GeneratedCSharp.Compile(generated, checking).Method("Compute").Invoke(null, [PrototypeCases.Text(text)]), Is.EqualTo(expected), $"CheckOverflow {checking}");
  }

  /// <summary>Translates the source with the options of the step for the profile they name.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToArray();
    this._outputs.Add((arguments[Array.IndexOf(arguments, "--profile") + 1], this.Translated(arguments)));
  }

  /// <summary>The last output calls a library member and declares no helper.</summary>
  [Then("^the output calls " + StepText.Quoted + "$")]
  public void OutputCalls(string member) {
    var (profile, text) = this._outputs[^1];
    Assert.That(text, Does.Contain(member).And.Not.Contain("private static"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile, LanguageVersionOf(profile)), Is.Empty, text);
  }

  /// <summary>The last output names no library member and declares the rotate helper.</summary>
  [Then("^the output contains no " + StepText.Quoted + " and declares a private rotate helper$")]
  public void OutputDeclaresTheHelper(string member) {
    var (profile, text) = this._outputs[^1];
    Assert.That(text, Does.Not.Contain(member).And.Contain("  private static uint RotateLeft(uint value, int count) {\n"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile, LanguageVersionOf(profile)), Is.Empty, text);
  }

  /// <summary>Every output, compiled with overflow checking off and on, computes the same value.</summary>
  [Then(@"^both return (\d+) for F\(0x([0-9A-Fa-f]+), (\d+)\)$")]
  public void BothReturn(uint expected, string value, int count) {
    Assert.That(this._outputs, Has.Count.EqualTo(2));
    foreach (var (_, text) in this._outputs)
      foreach (var checking in _overflowChecking)
        Assert.That(GeneratedCSharp.Compile(text, checking).Method("F").Invoke(null, [Convert.ToUInt32(value, 16), count]), Is.EqualTo(expected));
  }

  /// <summary>Takes a fixture and the options every translation of it gets.</summary>
  [Given("^any fixture translated with " + StepText.Quoted + "$")]
  public void AnyFixtureTranslatedWith(string options) {
    scenario.Workspace.Copy(PrototypeFixture.PathOf("Adler32"), "Adler32.cs");
    scenario.Source = "Adler32.cs";
    scenario.Fixture = "Adler32";
    this._options = [.. CommandLine.Split(options)];
  }

  /// <summary>The output for a profile has one form of the namespace and not the other, compiles for the profile and computes the checksum in its namespace.</summary>
  [Then("^the " + StepText.Quoted + " output contains " + StepText.Quoted + "$")]
  public void ProfileOutputContains(string profile, string text) {
    var generated = this.Translated([.. this._options, "--profile", profile]);
    var other = text.EndsWith(';') ? text[..^1] + " {" : text[..^2] + ";";
    Assert.That(generated, Does.Contain("\n" + text + "\n").And.Not.Contain(other));
    Assert.That(GeneratedCSharp.ComplaintsFor(generated, profile, LanguageVersionOf(profile)), Is.Empty, generated);
    var build = GeneratedCSharp.Compile(generated, true);
    Assert.That(build.ClassName, Is.EqualTo("Demo.Adler32"));
    Assert.That(build.Method("Compute").Invoke(null, [PrototypeCases.Text("Wikipedia")]), Is.EqualTo(300286872u));
  }

  /// <summary>Takes a fixture and translates it.</summary>
  [Given("^the fixture " + StepText.Quoted + " translated with " + StepText.Quoted + "$")]
  public void FixtureTranslatedWith(string fixture, string options) {
    scenario.Workspace.Copy(PrototypeFixture.PathOf(fixture[..^3]), fixture);
    scenario.Source = fixture;
    scenario.Fixture = fixture[..^3];
    var arguments = CommandLine.Split(options).ToArray();
    this._outputs.Add((arguments[Array.IndexOf(arguments, "--profile") + 1], this.Translated(arguments)));
  }

  /// <summary>The generated class declares its one public method and nothing private.</summary>
  [Then("^the generated class declares no helper method$")]
  public void DeclaresNoHelper() {
    var (profile, text) = this._outputs[^1];
    Assert.That(text, Does.Contain("  public static uint Compute(byte[] data) {\n").And.Not.Contain("private"));
    Assert.That(Regex.Matches(text, @"(?m)^  \w+ static "), Has.Count.EqualTo(1));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile, LanguageVersionOf(profile)), Is.Empty, text);
  }

  /// <summary>Runs the translate command on a fixture with the options of the step; the target is C# unless they say otherwise.</summary>
  [When("^I run the translate command with " + StepText.Quoted + "$")]
  public void TranslateCommandWith(string options) {
    scenario.Workspace.Copy(PrototypeFixture.PathOf("Adler32"), "Adler32.cs");
    var arguments = CommandLine.Split(options).ToList();
    if (!arguments.Contains("--to"))
      arguments.InsertRange(0, ["--to", "csharp"]);

    scenario.Polypiler.Run(["translate", "Adler32.cs", "--from", "csharp", .. arguments, "--output", "out"]);
  }

  /// <summary>The command was refused as a usage error and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + "$")]
  public void ExitCodeAndOutput(int exitCode, string text) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(text).And.Contain("usage: polypiler translate"));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
