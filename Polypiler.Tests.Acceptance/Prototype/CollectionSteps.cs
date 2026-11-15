using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/ir/ir-collections.feature</c>. The expected value is never computed
/// here: it is what .NET computes of the original C# fixture, built into this assembly, or what
/// <c>node</c> computes of the original JavaScript; a literal of the scenario is held to those first.
/// </summary>
[Binding, Scope(Feature = "Lists, maps and sets")]
public sealed class CollectionSteps(PrototypeScenario scenario) {

  private string _csharp = "";
  private string _script = "";

  /// <summary>The C# fixture translated to JavaScript: the file and the profile it was written for.</summary>
  private (string Path, ProfileCase Profile)? _javascript;

  /// <summary>The JavaScript fixture translated to C#: the text.</summary>
  private string _generated = "";

  /// <summary>The profile of a level in the module format the level starts with: an ES module, or CommonJS at ECMAScript 5.</summary>
  private static ProfileCase Level(string level) => ProfileCase.All.First(profile => profile.Level == level && !profile.TypedArrays && profile.Module == (level == "es5" ? "cjs" : "esm"));

  [Given("^the fixtures " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void Fixtures(string csharp, string script) {
    foreach (var path in new[] { csharp, script })
      scenario.Workspace.Copy(CollectionFixtures.PathOf(Path.GetFileName(path)), path);

    (this._csharp, this._script) = (csharp, script);
  }

  [Given("^the JavaScript function " + StepText.Quoted + "$")]
  public void JavaScriptFunction(string text) => scenario.Script("snippet.js", StepText.Unescape(text));

  private void Translate(string level, string profile) {
    var javascript = Level(level);
    var (path, text) = scenario.Translate(this._csharp, "csharp", "javascript", javascript.Arguments);
    Assert.That(GeneratedJavaScript.Problems(text, javascript.Parsed), Is.Empty, text);
    this._javascript = (path, javascript);
    var csharp = CSharpProfile.Parse(profile);
    (_, this._generated) = scenario.Translate(this._script, "javascript", "csharp", "--profile", csharp.Name);
    Assert.That(GeneratedCSharp.ComplaintsFor(this._generated, csharp.Name, csharp.LanguageVersionName), Is.Empty, this._generated);
  }

  [When("^the C# fixture is translated with \"--to javascript --profile (\\S+)\" and the JavaScript fixture with \"--to csharp --profile (\\S+)\"$")]
  public void TranslatedWith(string level, string profile) => this.Translate(level, profile);

  [When("^each is translated to the other language with the default profile$")]
  public void TranslatedByDefault() => this.Translate(JavaScriptProfileName, CSharpProfile.Default.Name);

  private static string JavaScriptProfileName => Core.Backends.JavaScript.JavaScriptProfile.Default.LevelName;

  [When("^I run the translate command to JavaScript$")]
  public void TranslateCommandToJavaScript() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");

  [When("^I run the translate command to C#$")]
  public void TranslateCommandToCSharp() => scenario.Polypiler.Run("translate", scenario.Source, "--from", "javascript", "--to", "csharp", "--output", "out");

  /// <summary>A call of the C# fixture with what .NET computes of it, and the same outcome from the generated JavaScript.</summary>
  private string FromCSharp(PrototypeCall call) {
    var typed = call with { Returns = CollectionFixtures.Compiled("CollectionsDemo", call.Method).ReturnType };
    var expected = PrototypeFixture.InvokedOutcome(CollectionFixtures.Compiled("CollectionsDemo", call.Method), typed);
    var (path, profile) = this._javascript!.Value;
    var generated = Toolchain.NodeOutcomes(Path.GetDirectoryName(path)!, Path.GetFileName(path), [typed], true, profile.Mode, profile.Global).Single();
    Assert.That(generated, Is.EqualTo(expected), $"{call} in {profile}");
    return expected;
  }

  /// <summary>A call of the JavaScript fixture with what node computes of the original, and the same outcome from the generated C#.</summary>
  private string FromScript(PrototypeCall call) {
    var original = scenario.Workspace.Resolve("original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(original, File.ReadAllText(scenario.Workspace.Resolve(this._script)));
    var expected = Toolchain.NodeOutcomes(Path.GetDirectoryName(original)!, Path.GetFileName(original), [call], true).Single();
    Assert.That(GeneratedCSharp.Compile(this._generated, false).Outcome(call), Is.EqualTo(expected), $"{call}\n{this._generated}");
    return expected;
  }

  /// <summary>The number an outcome writes: the bits of a double after <c>f:</c>, or the number itself.</summary>
  private static double Number(string outcome) => outcome.StartsWith("f:", StringComparison.Ordinal)
    ? BitConverter.Int64BitsToDouble(long.Parse(outcome[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
    : double.Parse(outcome, CultureInfo.InvariantCulture);

  private static PrototypeCall Call(string method, string? argument)
    => argument is null ? new(method, []) : new(method, [argument.Split(',').Select(item => int.Parse(item.Trim(), CultureInfo.InvariantCulture)).ToArray()]);

  [Then(@"^(\w+)\(\) returns (\d+) and (\w+)\(\) returns (\d+) in \.NET and in the generated JavaScript$")]
  public void TwoFromCSharp(string first, string one, string second, string two) {
    Assert.That(Number(this.FromCSharp(new(first, []))), Is.EqualTo(double.Parse(one, CultureInfo.InvariantCulture)));
    Assert.That(Number(this.FromCSharp(new(second, []))), Is.EqualTo(double.Parse(two, CultureInfo.InvariantCulture)));
  }

  [Then(@"^(\w+)\(\) returns (\d+) and (\w+)\(\) returns (\d+) in node and in the generated C#$")]
  public void TwoFromScript(string first, string one, string second, string two) {
    Assert.That(Number(this.FromScript(new(first, []))), Is.EqualTo(double.Parse(one, CultureInfo.InvariantCulture)));
    Assert.That(Number(this.FromScript(new(second, []))), Is.EqualTo(double.Parse(two, CultureInfo.InvariantCulture)));
  }

  [Then(@"^(\w+)\(\) returns (\d+) and (\w+)\(\[([\d, ]+)\]\) returns (\d+) in \.NET and in the generated JavaScript$")]
  public void CallAndCountFromCSharp(string first, string one, string counted, string values, string two) {
    Assert.That(Number(this.FromCSharp(new(first, []))), Is.EqualTo(double.Parse(one, CultureInfo.InvariantCulture)));
    Assert.That(this.FromCSharp(Call(counted, values)), Does.StartWith(two));
  }

  [Then(@"^(\w+)\(\) returns (\d+) and (\w+)\(\[([\d, ]+)\]\) returns (\d+) in node and in the generated C#$")]
  public void CallAndCountFromScript(string first, string one, string counted, string values, string two) {
    Assert.That(Number(this.FromScript(new(first, []))), Is.EqualTo(double.Parse(one, CultureInfo.InvariantCulture)));
    var call = new PrototypeCall(counted, [values.Split(',').Select(item => double.Parse(item.Trim(), CultureInfo.InvariantCulture)).ToArray()]);
    Assert.That(this.FromScript(call), Does.StartWith(two));
  }

  [Then(@"^(\w+)\(\) returns (\d+) in \.NET and in the generated JavaScript$")]
  public void OneFromCSharp(string method, string value) => Assert.That(Number(this.FromCSharp(new(method, []))), Is.EqualTo(double.Parse(value, CultureInfo.InvariantCulture)));

  [Then(@"^(\w+)\(\) returns (\d+) in node and in the generated C#$")]
  public void OneFromScript(string method, string value) => Assert.That(Number(this.FromScript(new(method, []))), Is.EqualTo(double.Parse(value, CultureInfo.InvariantCulture)));

  [Then(@"^(\w+)\(\) throws the (\w+) trap in the generated JavaScript, as \.NET throws ([\w.]+)$")]
  public void TrapFromCSharp(string method, string trap, string exception) {
    var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() => CollectionFixtures.Compiled("CollectionsDemo", method).Invoke(null, []))!;
    Assert.That(thrown.InnerException!.GetType().FullName, Is.EqualTo(exception));
    Assert.That(this.FromCSharp(new(method, [])), Is.EqualTo(PrototypeFixture.Trap(Enum.Parse<IrTrap>(trap))));
  }

  [Then(@"^(\w+)\(\) returns (negative|positive) infinity and (\w+)\(\) returns (\d+) in \.NET and in the generated JavaScript$")]
  public void InfinityFromCSharp(string method, string sign, string other, string value) {
    Assert.That(Number(this.FromCSharp(new(method, []))), Is.EqualTo(sign == "negative" ? double.NegativeInfinity : double.PositiveInfinity));
    this.OneFromCSharp(other, value);
  }

  [Then(@"^(\w+)\(\) returns (negative|positive) infinity and (\w+)\(\) returns (\d+) in node and in the generated C#$")]
  public void InfinityFromScript(string method, string sign, string other, string value) {
    Assert.That(Number(this.FromScript(new(method, []))), Is.EqualTo(sign == "negative" ? double.NegativeInfinity : double.PositiveInfinity));
    this.OneFromScript(other, value);
  }

  [Then(@"^(\w+)\(\) returns an empty optional in the generated C#, as node returns undefined$")]
  public void EmptyOptional(string method) => Assert.That(this.FromScript(new(method, [])), Is.EqualTo(PrototypeFixture.Show(null)));

  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + " and nothing is written$")]
  public void Refused(int exitCode, string code, string reason) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(reason));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

}
