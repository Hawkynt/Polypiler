using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/csharp-scopes.feature</c>, with the real
/// <c>polypiler</c> executable: every C# file it writes is built by the C# compiler against the
/// reference assemblies of its profile, with every warning an error, and for this runtime, to be
/// called. What a call has to return is what <c>node</c> returns for the original source or, for IR
/// built here, what the interpreter returns and what <c>node</c> returns for the JavaScript the same
/// IR becomes at ES2022 and at ES5.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class CSharpScopesTests {

  private static readonly string[] _scenarios = [
    "A block-local let followed by an outer let of the same name compiles",
    "A local nested in a local of the same name and another type is renamed",
    "Sibling blocks keep their names"
  ];

  private static readonly bool[] _overflowChecking = [false, true];

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._directory, "js", "scopes"));
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests/features/prototype/csharp-scopes.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+?)\r?$").Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
    var examples = Regex.Matches(feature, @"(?m)^      \| (\S+) +\| (\S+) +\| (\S+) +\| (\S+) +\|\r?$").Skip(1)
      .Select(match => string.Join(" ", match.Groups.Values.Skip(1)));
    Assert.That(examples, Is.EqualTo(new[] { "later.js net2.0 f(3) 3.5", "later.js net10.0 f(0) 0.5", "loop.js net4.8 g(4) 16", "loop.js net10.0 g(0) 10" }));
  }

  /// <summary>What a call returns in a generated file, built without and with overflow checking, which must agree.</summary>
  private static string Compiled(string text, PrototypeCall call) {
    var outcomes = _overflowChecking.Select(checking => GeneratedCSharp.Compile(text, checking).Outcome(call)).Distinct().ToList();
    Assert.That(outcomes, Has.Count.EqualTo(1), $"{call} depends on CheckOverflow");
    return outcomes[0];
  }

  /// <summary>Scenario outline: A block-local let followed by an outer let of the same name compiles.</summary>
  [TestCase("later.js", "net2.0", "f", 3, 3.5)]
  [TestCase("later.js", "net10.0", "f", 0, 0.5)]
  [TestCase("loop.js", "net4.8", "g", 4, 16)]
  [TestCase("loop.js", "net10.0", "g", 0, 10)]
  public void BlockLocalLetFollowedByAnOuterLet_Compiles(string file, string profile, string function, double argument, double value) {
    var fixture = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "js", "scopes", file);
    File.Copy(fixture, Path.Combine(this._directory, "js", "scopes", file));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "js/scopes/" + file, "--from", "javascript", "--to", "csharp", "--profile", profile, "--output", "out");
    Assert.That(exitCode, Is.Zero, output);

    var text = File.ReadAllText(Path.Combine(this._directory, "out", Path.ChangeExtension(file, ".cs")));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile, CSharpProfile.Parse(profile).LanguageVersionName), Is.Empty, text);

    // node reads the source as the ES module it is.
    var call = new PrototypeCall(function, [argument]);
    File.Copy(fixture, Path.Combine(this._directory, "source.mjs"));
    var node = Toolchain.NodeOutcomes(this._directory, "source.mjs", [call], true).Single();
    Assert.That(node, Is.EqualTo(PrototypeFixture.Show(value)));
    Assert.That(Compiled(text, call), Is.EqualTo(node));
  }

  /// <summary>Scenario: A local nested in a local of the same name and another type is renamed.</summary>
  [Test]
  public void NestedLocalOfTheSameNameAndAnotherType_IsRenamed() {
    var n = new IrLocal("n", IrType.I32);
    var outer = new IrLocal("t", IrType.F64);
    var inner = new IrLocal("t", IrType.I32);
    var twice = new IrBinary(IrBinaryOp.Add, new IrLocalRef(outer), new IrLocalRef(outer));
    var inside = new IrBlock([
      new IrDeclare(inner, new IrBinary(IrBinaryOp.Multiply, new IrLocalRef(n), new IrConst(IrType.I32, 2))),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.F64, new IrLocalRef(inner)), new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(1.0))))
    ]);
    var body = new IrBlock([
      new IrDeclare(outer, new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(0.5))),
      new IrIf(new IrBinary(IrBinaryOp.Greater, new IrLocalRef(n), new IrConst(IrType.I32, 0)), inside, null),
      new IrReturn(twice)
    ]);
    var module = new IrModule("Scopes", [new IrFunction("F", true, IrType.F64, [n], [outer, inner], body)]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);

    var text = CSharpEmitter.Emit(module, CSharpProfile.Parse("net10.0")).Single().Content;
    Assert.That(text, Does.Contain("      var t_1 = n * 2;\n").And.Contain("    double t = 0.5;\n"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net10.0", "14"), Is.Empty, text);

    PrototypeCall[] calls = [new("F", [3]), new("F", [-1])];
    string[] expected = ["7", "1"];
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(expected));
    Assert.That(calls.Select(call => Compiled(text, call)), Is.EqualTo(expected));
    foreach (var profile in new[] { new ProfileCase("es2022", "esm", false, null), new ProfileCase("es5", "cjs", false, null) }) {
      var file = JavaScriptEmitter.Emit(module, profile.Parsed).Single();
      File.WriteAllText(Path.Combine(this._directory, file.Path), file.Content);
      Assert.That(Toolchain.NodeOutcomes(this._directory, file.Path, calls, true, profile.Mode), Is.EqualTo(expected), $"{profile}:\n{file.Content}");
    }
  }

  /// <summary>Scenario: Sibling blocks keep their names.</summary>
  [Test]
  public void SiblingBlocks_KeepTheirNames() {
    File.Copy(PrototypeFixture.PathOf("scopes/Sibling"), Path.Combine(this._directory, "Sibling.cs"));
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, "translate", "Sibling.cs", "--from", "csharp", "--to", "csharp", "--profile", "net10.0", "--output", "out");
    Assert.That(exitCode, Is.Zero, output);

    var text = File.ReadAllText(Path.Combine(this._directory, "out", "Sibling.cs"));
    Assert.That(Regex.Matches(text, @"(?m)^ +(?:var|int|double) t = "), Has.Count.EqualTo(2), text);
    Assert.That(text, Does.Not.Contain("t_1"));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net10.0", "14"), Is.Empty, text);
    foreach (var argument in new[] { 3, -1, -3 }) {
      var call = new PrototypeCall("F", [argument]);
      Assert.That(Compiled(text, call), Is.EqualTo(PrototypeFixture.CompiledOutcome("scopes/Sibling", call)), call.ToString());
    }
  }

}
