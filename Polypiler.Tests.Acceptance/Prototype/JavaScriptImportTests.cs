using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/javascript-to-ir.feature</c> and the test design of
/// the JavaScript importer, with the real <c>polypiler</c> executable and <c>node</c>. The expected
/// outcome of a call is never computed here: it is what <c>node</c> does with the original source,
/// and the imported code is held to it in the interpreter, as the JavaScript emitted from the IR and
/// run by <c>node</c> again, and as the C# emitted from the IR and built by the C# compiler.
/// <para>
/// Every test here starts processes, so the fixture is in the <c>EndToEnd</c> tier: the fast tier on
/// a branch push leaves it out, the pull request runs it on every operating system.
/// </para>
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
public sealed class JavaScriptImportTests {

  private const string Sum = "/** @param {int32} a @param {int32} b @returns {int32} */ function f(a, b) { return a + b; }";

  private static readonly string[] _scenarios = [
    "Cipher's rotates import and behave like the original",
    "A JavaScript checksum survives the round trip through the IR",
    "An uncoerced sum is a double",
    "A coerced sum wraps",
    "A coerced product is computed in doubles, Math.imul in 32 bits",
    "A function without JSDoc types is typed by what it does",
    "An unknown export name is an error"
  ];

  private string _directory = "";

  /// <summary>
  /// What node does for the calls. A module that computes in doubles has two zeros, so the outcome
  /// of every call here tells -0 from 0.
  /// </summary>
  private static IReadOnlyList<string> Exact(string directory, string module, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(directory, module, calls, true);

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/javascript-to-ir.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>
  /// Scenario outline: Cipher's rotates import and behave like the original. The same call also goes
  /// through the executable with <c>--export</c> and through the module it writes.
  /// </summary>
  [TestCase("RotL32", 0x80000001u, 1, "3")]
  [TestCase("RotL32", 0x12345678u, 8, "878082066")]
  [TestCase("RotL32", 0x12345678u, 32, "305419896")]
  [TestCase("RotR32", 0x00000003u, 1, "2147483649")]
  [TestCase("RotR32", 0x12345678u, 0, "305419896")]
  public void CiphersRotates_ImportAndBehaveLikeTheOriginal(string function, uint value, int positions, string expected) {
    var call = new PrototypeCall(function, [value, positions]);
    var module = JavaScriptFixture.Import("OpCodes.js", File.ReadAllText(JavaScriptFixture.OpCodesPath), [function]);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(expected));
    Assert.That(Exact(this._directory, JavaScriptFixture.OpCodesPath, [call]).Single(), Is.EqualTo(expected));

    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", JavaScriptFixture.OpCodesPath, "--from", "javascript", "--to", "javascript", "--output", "out", "--export", function);
    Assert.That(exitCode, Is.Zero, output);
    Assert.That(Exact(Path.Combine(this._directory, "out"), "OpCodes.mjs", [call]).Single(), Is.EqualTo(expected));
    foreach (var build in this.TranslateToCSharp(JavaScriptFixture.OpCodesPath, "OpCodes", "--export", function))
      Assert.That(build.Outcome(call), Is.EqualTo(expected));
  }

  /// <summary>Scenario outline: A JavaScript checksum survives the round trip through the IR.</summary>
  [TestCase("adler32", "compute", "Wikipedia", "300286872")]
  [TestCase("crc32", "compute", "123456789", "3421780262")]
  [TestCase("fnv1a", "hash", "a", "3826002220")]
  public void Checksum_SurvivesTheRoundTripThroughTheIr(string fixture, string function, string text, string expected) {
    var call = new PrototypeCall(function, [PrototypeCases.Text(text)]);
    File.Copy(JavaScriptFixture.PathOf(fixture), Path.Combine(this._directory, fixture + ".mjs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", fixture + ".mjs", "--from", "javascript", "--to", "javascript", "--output", "out");
    Assert.That(exitCode, Is.Zero, output);

    var unchanged = PrototypeFixture.Outcome(null, call.Arguments)["void".Length..];
    Assert.That(Exact(Path.Combine(this._directory, "out"), fixture + ".mjs", [call]).Single(), Is.EqualTo(expected + unchanged));
    Assert.That(Exact(this._directory, fixture + ".mjs", [call]).Single(), Is.EqualTo(expected + unchanged));
  }

  /// <summary>Scenario: An uncoerced sum is a double.</summary>
  [Test]
  public void UncoercedSum_IsADouble() {
    var call = new PrototypeCall("f", [2147483647, 1]);
    var module = JavaScriptFixture.Import("snippet.js", Sum);
    Assert.That(module.Functions.Single().ReturnType, Is.EqualTo(Polypiler.Core.IR.IrType.F64));
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo("2147483648"));
    Assert.That(this.OriginalAndTranslated(Sum, call), Is.EqualTo(("2147483648", "2147483648")));
  }

  /// <summary>Scenario: A coerced sum wraps.</summary>
  [Test]
  public void CoercedSum_Wraps() {
    var call = new PrototypeCall("f", [2147483647, 1]);
    var source = Sum.Replace("a + b", "(a + b) | 0", StringComparison.Ordinal);
    Assert.That(PrototypeFixture.InterpretedOutcome(JavaScriptFixture.Import("snippet.js", source), call), Is.EqualTo("-2147483648"));
    Assert.That(this.OriginalAndTranslated(source, call), Is.EqualTo(("-2147483648", "-2147483648")));
  }

  /// <summary>Scenario: A coerced product is computed in doubles, Math.imul in 32 bits.</summary>
  [TestCase("(a * b) | 0")]
  [TestCase("Math.imul(a, b)")]
  public void CoercedProduct_IsComputedInDoubles_MathImulIn32Bits(string product) {
    var call = new PrototypeCall("f", [65537, 65537]);
    var source = Sum.Replace("a + b", product, StringComparison.Ordinal);
    Assert.That(PrototypeFixture.InterpretedOutcome(JavaScriptFixture.Import("snippet.js", source), call), Is.EqualTo("131073"));
    Assert.That(this.OriginalAndTranslated(source, call), Is.EqualTo(("131073", "131073")));
  }

  /// <summary>Scenario: A function without JSDoc types is typed by what it does.</summary>
  [Test]
  public void FunctionWithoutJsDocTypes_IsTypedByWhatItDoes() {
    var (exitCode, output) = this.TranslateText("function f(a) { return a | 0; }");
    Assert.That(exitCode, Is.Zero, output);
    Assert.That(output, Does.Contain("snippet.js: note js-param-integer-inferred:").And.Contain(": f(a)"));
    var function = JavaScriptFixture.Import("snippet.js", "function f(a) { return a | 0; }").Functions.Single();
    Assert.That((function.Parameters.Single().Type, function.ReturnType), Is.EqualTo((Polypiler.Core.IR.IrType.I32, Polypiler.Core.IR.IrType.I32)));
  }

  /// <summary>Scenario: An unknown export name is an error.</summary>
  [Test]
  public void UnknownExportName_IsAnError() {
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", JavaScriptFixture.OpCodesPath, "--from", "javascript", "--to", "javascript", "--output", "out", "--export", "NoSuchFunction");
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(output, Does.Contain("error PLY1104: the file has no function 'NoSuchFunction' to export"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>Test design 9: a syntax error is exit code 4 and carries the parser's message and position.</summary>
  [Test]
  public void SyntaxError_IsExitCode4() {
    var (exitCode, output) = this.TranslateText("function f( {");
    Assert.That(exitCode, Is.EqualTo(4));
    Assert.That(output, Does.Match(@"snippet\.js\(1,\d+\): error PLY1100: \S"));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>
  /// Test design 14: the whole pinned file does not import, and says so once for every function it
  /// refuses; the list is the one the unit tests hold the importer to.
  /// </summary>
  [Test]
  public void WholeCipherFile_IsRefusedFunctionByFunction() {
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", JavaScriptFixture.OpCodesPath, "--from", "javascript", "--to", "javascript", "--output", "out");
    var expected = JavaScriptFixture.OpCodesExpectation()
      .Select(line => line.Status)
      .Where(status => status != JavaScriptFixture.Imported)
      .Distinct();
    var reported = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line[line.IndexOf("OpCodes.js(", StringComparison.Ordinal)..].TrimEnd('\r'));
    Assert.That(exitCode, Is.EqualTo(3));
    Assert.That(reported, Is.SupersetOf(expected));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>
  /// Test design 1 to 7 and 15: every call of every fixture - the published vectors, each operator on
  /// each type at its boundaries and on a thousand random operand tuples (seed 2026), each statement
  /// and each array access inside its array - has one outcome in node on the original, in the
  /// interpreter, in node on the module emitted from the IR and in the C# emitted from the IR,
  /// whichever overflow checking the C# compiler is told to use.
  /// </summary>
  [TestCaseSource(typeof(JavaScriptFixture), nameof(JavaScriptFixture.Names))]
  public void NodeOnTheOriginal_Interpreter_AndTheEmittedJavaScriptAndCSharp_Agree(string fixture) {
    var calls = JavaScriptCases.For(fixture).ToList();
    if (JavaScriptCases.RandomFixtures.Contains(fixture))
      calls.AddRange(JavaScriptCases.RandomCalls(fixture));

    var module = JavaScriptFixture.Import(fixture);
    var original = Exact(this._directory, JavaScriptFixture.PathOf(fixture), calls);
    var reEmitted = this.TranslateFixture(fixture, calls);
    var builds = this.TranslateToCSharp(JavaScriptFixture.PathOf(fixture), ClassNameOf(fixture));
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var interpreted = PrototypeFixture.InterpretedOutcome(module, calls[index]);
      var compiled = builds.Select(build => build.Outcome(calls[index])).Distinct().ToList();
      if (interpreted != original[index] || reEmitted[index] != original[index] || compiled.Exists(outcome => outcome != original[index]))
        mismatches.Add($"{calls[index]}: node on the original {original[index]}, interpreter {interpreted}, node on the re-emitted module {reEmitted[index]}, C# {string.Join(" and ", compiled)}");
    }

    TestContext.Out.WriteLine($"{fixture}: {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>
  /// The functions of Cipher's <c>OpCodes.js</c> that import and take numbers and truth values only
  /// behave like the original: at the boundaries of their parameter types and on two hundred random
  /// tuples each, node on the original, the interpreter, node on the re-emitted module and the
  /// emitted C# agree.
  /// </summary>
  [Test]
  public void CiphersImportedFunctions_BehaveLikeTheOriginal() {
    var imported = JavaScriptFixture.OpCodesExpectation().Where(line => line.Status == JavaScriptFixture.Imported).Select(line => line.Function).ToList();
    var module = JavaScriptFixture.Import("OpCodes.js", File.ReadAllText(JavaScriptFixture.OpCodesPath), imported);
    var scalar = module.Functions.Where(function => function.Parameters.Count > 0 && function.Parameters.All(parameter => !parameter.Type.IsArray)).ToList();
    var calls = JavaScriptCases.BoundaryCalls(scalar).Concat(JavaScriptCases.RandomCalls(scalar, 200)).ToList();

    var output = Path.Combine(this._directory, "out");
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, "translate", JavaScriptFixture.OpCodesPath, "--from", "javascript", "--to", "javascript", "--output", output, "--export", string.Join(',', imported));
    Assert.That(exitCode, Is.Zero, text);
    var original = Exact(this._directory, JavaScriptFixture.OpCodesPath, calls);
    var reEmitted = Exact(output, "OpCodes.mjs", calls);
    var builds = this.TranslateToCSharp(JavaScriptFixture.OpCodesPath, "OpCodes", "--export", string.Join(',', imported));
    var mismatches = new List<string>();
    for (var index = 0; index < calls.Count; ++index) {
      var interpreted = PrototypeFixture.InterpretedOutcome(module, calls[index]);
      var compiled = builds.Select(build => build.Outcome(calls[index])).Distinct().ToList();
      if (interpreted != original[index] || reEmitted[index] != original[index] || compiled.Exists(outcome => outcome != original[index]))
        mismatches.Add($"{calls[index]}: node on the original {original[index]}, interpreter {interpreted}, node on the re-emitted module {reEmitted[index]}, C# {string.Join(" and ", compiled)}");
    }

    TestContext.Out.WriteLine($"OpCodes: {scalar.Count} of {imported.Count} imported functions, {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(scalar, Has.Count.GreaterThan(20));
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>
  /// The outcomes the unit tests compare the interpreter with are what node does with the original
  /// source. Run with <c>POLYPILER_RECORD=1</c> to write the recordings after a fixture changed.
  /// </summary>
  [TestCaseSource(typeof(JavaScriptFixture), nameof(JavaScriptFixture.Names))]
  public void Recording_IsWhatNodeDoesWithTheOriginal(string fixture) {
    var calls = JavaScriptCases.For(fixture);
    var outcomes = Exact(this._directory, JavaScriptFixture.PathOf(fixture), calls);
    var lines = calls.Select((call, index) => JavaScriptFixture.Line(call, outcomes[index])).ToArray();
    if (Environment.GetEnvironmentVariable("POLYPILER_RECORD") == "1")
      JavaScriptFixture.Record(JavaScriptFixture.RecordingOf(fixture), lines);

    Assert.That(JavaScriptFixture.RecordedLines(fixture), Is.EqualTo(lines));
  }

  /// <summary>
  /// Test design 8 and the caveats: where JavaScript and the imported code differ on purpose, node
  /// does with the original what the tests say it does, and the JavaScript and the C# emitted from
  /// the IR do what the interpreter does.
  /// </summary>
  [Test]
  public void Differences_AreWhatNodeAndTheImportedCodeDo() {
    foreach (var fixture in JavaScriptCases.Differences().GroupBy(difference => difference.Fixture)) {
      var calls = fixture.Select(difference => difference.Call).ToList();
      var original = Exact(this._directory, JavaScriptFixture.PathOf(fixture.Key), calls);
      var reEmitted = this.TranslateFixture(fixture.Key, calls);
      Assert.That(original, Is.EqualTo(fixture.Select(difference => difference.Node)), fixture.Key);
      Assert.That(reEmitted, Is.EqualTo(fixture.Select(difference => difference.Imported)), fixture.Key);
      foreach (var build in this.TranslateToCSharp(JavaScriptFixture.PathOf(fixture.Key), ClassNameOf(fixture.Key)))
        Assert.That(calls.Select(build.Outcome), Is.EqualTo(fixture.Select(difference => difference.Imported)), fixture.Key);
    }
  }

  /// <summary>Test design 15: the random part is as large as the issue asks, and reproducible.</summary>
  [Test]
  public void RandomOperatorCalls_AreAThousandPerFunctionAndReproducible() {
    var calls = JavaScriptCases.RandomOperatorCalls();
    var perFunction = calls.GroupBy(call => call.Method).ToList();
    Assert.That(perFunction, Has.Count.GreaterThan(70));
    Assert.That(perFunction.Select(group => group.Count()), Has.All.EqualTo(JavaScriptCases.RandomCallsPerOperator));
    Assert.That(JavaScriptCases.RandomOperatorCalls().Select(call => call.ToString()), Is.EqualTo(calls.Select(call => call.ToString())));
  }

  /// <summary>Translates a fixture with the real executable and runs the calls through node.</summary>
  private IReadOnlyList<string> TranslateFixture(string fixture, IReadOnlyList<PrototypeCall> calls) {
    var output = Path.Combine(this._directory, "out-" + Path.GetFileName(fixture));
    var watch = Stopwatch.StartNew();
    var (exitCode, text) = Toolchain.RunPolypiler(this._directory, "translate", JavaScriptFixture.PathOf(fixture), "--from", "javascript", "--to", "javascript", "--output", output);
    TestContext.Out.WriteLine($"{fixture}: translated in {watch.ElapsedMilliseconds} ms");
    Assert.That(exitCode, Is.Zero, text);
    return Exact(output, Path.GetFileName(fixture) + ".mjs", calls);
  }

  /// <summary>
  /// Translates a JavaScript file to C# with the real executable and has the C# compiler build the
  /// result, once with overflow checking off and once with it on. The file is given the name the
  /// class is to have: a module is named after its file, and the C# compiler warns about a type
  /// name that is all lower case.
  /// </summary>
  private IReadOnlyList<GeneratedCSharp> TranslateToCSharp(string source, string className, params string[] further) {
    var directory = Path.Combine(this._directory, "csharp-" + className);
    Directory.CreateDirectory(directory);
    File.Copy(source, Path.Combine(directory, className + Path.GetExtension(source)), true);
    var (exitCode, text) = Toolchain.RunPolypiler(directory, ["translate", className + Path.GetExtension(source), "--from", "javascript", "--to", "csharp", "--output", "out", .. further]);

    // The C# backend does not write the whole IR yet. What it says it cannot write is not compared
    // through it; anything else it refuses is a failure.
    if (exitCode == 3 && text.Contains("error PLY1052:", StringComparison.Ordinal)) {
      var missing = Regex.Matches(text, @"PLY1052: '([^']+)'").Select(match => match.Groups[1].Value).Distinct();
      TestContext.Out.WriteLine($"{className}: not compared through C#, whose backend does not write {string.Join(", ", missing)} yet");
      return [];
    }

    Assert.That(exitCode, Is.Zero, text);
    var generated = File.ReadAllText(Path.Combine(directory, "out", className + ".cs"));
    return [GeneratedCSharp.Compile(generated, false), GeneratedCSharp.Compile(generated, true)];
  }

  /// <summary>The name of the class a fixture becomes: its file name without the folder, with a capital.</summary>
  private static string ClassNameOf(string fixture) {
    var name = Path.GetFileName(fixture);
    return char.ToUpperInvariant(name[0]) + name[1..];
  }

  /// <summary>What node does for a call with a source as it is, and with the module translated from it.</summary>
  private (string Original, string Translated) OriginalAndTranslated(string source, PrototypeCall call) {
    var (exitCode, text) = this.TranslateText(source);
    Assert.That(exitCode, Is.Zero, text);

    // A script exports nothing; for node to reach the function, the same text is given an export.
    File.WriteAllText(Path.Combine(this._directory, "original.cjs"), source + $"\nmodule.exports = {{ {call.Method} }};\n");
    return (
      Exact(this._directory, "original.cjs", [call]).Single(),
      Exact(Path.Combine(this._directory, "out"), "snippet.mjs", [call]).Single());
  }

  private (int ExitCode, string Output) TranslateText(string source) {
    File.WriteAllText(Path.Combine(this._directory, "snippet.js"), source);
    return Toolchain.RunPolypiler(this._directory, "translate", "snippet.js", "--from", "javascript", "--to", "javascript", "--output", "out");
  }

}
