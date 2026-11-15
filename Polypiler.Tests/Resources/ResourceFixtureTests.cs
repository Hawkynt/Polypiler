using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Resources;

/// <summary>
/// The resource fixtures (#1324) through both frontends, the interpreter and both backends: the
/// interpreter computes what .NET computes of <c>Scopes.cs</c> and what node computes of
/// <c>scopes.js</c>, and every profile of both backends writes both at its level, in the form the
/// profile has. That the generated code computes the same is decided by the C# compiler and node in
/// the acceptance tests.
/// </summary>
[TestFixture]
internal sealed class ResourceFixtureTests {

  private static readonly JavaScriptProfile[] _levels = [.. JavaScriptProfile.LevelNames.Select(level => JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm"))];

  /// <summary>What .NET 10 computes of each method of the fixture, as the work order states it.</summary>
  private static readonly Dictionary<string, string> _expected = new() {
    ["Nest"] = "1,2,5,-2,-1|",
    ["BodyThrows"] = "1,5,-1|body",
    ["DisposeThrows"] = "1,5,-1|dispose 1",
    ["NullResource"] = "5|",
    ["BothThrow"] = "1,2,5,-2,-1|dispose 1",
    ["BodyAndDisposeThrow"] = "1,5,-1|dispose 1",
    ["Declarators"] = "1,2,5,-2,-1|",
    ["Expression"] = "1,5,-1|",
    ["Three"] = "1,2,3,5,-3,-2,-1|",
    ["Returns"] = "1,-1,7|",
    ["Breaks"] = "1,5,-1,2,-2|",
    ["Continues"] = "1,5,-1,2,-2,3,5,-3|",
    ["Explicit"] = "1,2,5,-2,-1|"
  };

  private static IEnumerable<TestCaseData> Calls() => ResourceFixtures.Calls().Select(call => new TestCaseData(call).SetName($"Scopes.{call.Method}"));


  /// <summary>Test design 1 to 6: .NET computes what the work order states, and the interpreter what .NET computes.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_ComputesWhatDotNetComputes(PrototypeCall call) {
    var expected = PrototypeFixture.InvokedOutcome(ResourceFixtures.Compiled(call.Method), call);
    Assert.That(expected, Is.EqualTo(_expected[call.Method]));
    Assert.That(PrototypeFixture.InterpretedOutcome(ResourceFixtures.Imported(), call), Is.EqualTo(expected));
  }

  /// <summary>A resource scope means its try ... finally: the module with every scope replaced by <see cref="IrUsing.Lowered"/> verifies and computes the same.</summary>
  [Test]
  public void EveryScope_ComputesWhatItsTryFinallyComputes() {
    var module = ResourceFixtures.Imported();
    var lowered = module with { Functions = [.. module.Functions.Select(function => function with { Body = Lower(function.Body) })] };
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    Assert.That(lowered.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrMethodCall>().Count(call => call.Method.Name == "Dispose"),
      Is.GreaterThanOrEqualTo(13));
    foreach (var call in ResourceFixtures.Calls())
      Assert.That(PrototypeFixture.InterpretedOutcome(lowered, call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)), call.Method);
  }

  private static IrBlock Lower(IrBlock block) => new([.. block.Statements.Select(Lower)]);

  private static IrStmt Lower(IrStmt statement) => statement switch {
    IrUsing scope => Lower((scope with { Body = Lower(scope.Body) }).Lowered()),
    IrBlock block => Lower(block),
    IrIf conditional => new IrIf(conditional.Condition, Lower(conditional.Then), conditional.Else is null ? null : Lower(conditional.Else)),
    IrWhile loop => new IrWhile(loop.Condition, Lower(loop.Body)),
    IrFor loop => new IrFor(loop.Condition, Lower(loop.Body), Lower(loop.Step)),
    IrTry attempt => new IrTry(Lower(attempt.Body), [.. attempt.Catches.Select(clause => clause with { Body = Lower(clause.Body) })], attempt.Finally is null ? null : Lower(attempt.Finally)),
    _ => statement
  };

  /// <summary>Test design 2: every form of the source is one scope per resource, nested in the order the resources are declared.</summary>
  [Test]
  public void EveryForm_IsOneScopePerResource() {
    var module = ResourceFixtures.Imported();
    int Scopes(string method) => Count(module.Functions.Single(function => function.Name == method).Body);
    Assert.That(Scopes("Declarations"), Is.EqualTo(2));
    Assert.That(Scopes("Declarators"), Is.EqualTo(2));
    Assert.That(Scopes("BothThrow"), Is.EqualTo(2));
    Assert.That(Scopes("Three"), Is.EqualTo(3));
    Assert.That(Scopes("Expression"), Is.EqualTo(1));
    Assert.That(Scopes("NullResource"), Is.EqualTo(1));
  }

  private static int Count(IrStmt statement)
    => (statement is IrUsing ? 1 : 0) + (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(inner => inner.Statements)).Sum(Count);

  /// <summary>Test design 1 and 11: the fixture is written in every C# profile without a refusal, and compiles there.</summary>
  [Test]
  [Category("Slow")]
  public void EveryCSharpProfile_WritesTheFixture() {
    var module = ResourceFixtures.Imported();
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 1 and 11: the fixture is written at every JavaScript level without a refusal, at that level, as nested try ... finally.</summary>
  [Test]
  public void EveryJavaScriptLevel_WritesTheFixture() {
    var module = ResourceFixtures.Imported();
    foreach (var profile in _levels) {
      Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty, profile.ToString());
      var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{profile}\n{text}");
      var keyword = profile.LevelName == "es5" ? "var" : "const";
      Assert.That(text, Does.Contain($"{keyword} first = new Res(log, 1, false);").And.Contain("if (first != null)").And.Contain("first.Dispose();").And.Contain("} finally {"), $"{profile}\n{text}");
      Assert.That(text, Does.Not.Contain("using "), profile.ToString());
    }
  }

  /// <summary>Test design 7: a using declaration keeps its form from C# 8 and is a using statement around the rest of its block below.</summary>
  [TestCase("netstandard2.0", false)]
  [TestCase("net4.8", false)]
  [TestCase("netstandard2.1", true)]
  [TestCase("net10.0", true)]
  public void UsingDeclaration_KeepsItsFormOnlyFromCSharp8(string name, bool declaration) {
    var profile = CSharpProfile.Parse(name);
    var text = CSharpEmitter.Emit(ResourceFixtures.Imported(), profile)[0].Content;
    var nest = text[text.IndexOf("Declarations(", System.StringComparison.Ordinal)..];
    nest = nest[..nest.IndexOf("Nest(", System.StringComparison.Ordinal)];
    if (declaration)
      Assert.That(nest, Does.Contain("using var first = new Res(log, 1, false);").And.Contain("using var second = new Res(log, 2, false);").And.Not.Contain("using ("), text);
    else
      Assert.That(nest, Does.Contain("using (").And.Not.Contain("using var"), text);

    Assert.That(text, Does.Contain("global::System.IDisposable").And.Not.Contain("interface IDisposable"), text);
  }

  /// <summary>Test design 1: the JavaScript fixture's try ... finally imports and computes what node computes of it, and stays a try ... finally in C#.</summary>
  [Test]
  public void JavaScriptFixture_StaysTryFinally() {
    var module = ResourceFixtures.ImportedScript();
    string[] expected = ["1,2,5,-2,-1|", "1,5,-1|body", "1,5,-1|dispose 1", "5|", "1,2,5,-2,-1|dispose 1"];
    Assert.That(ResourceFixtures.ScriptCalls().Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(expected));
    var text = CSharpEmitter.Emit(module, CSharpProfile.Net100)[0].Content;
    Assert.That(text, Does.Contain("finally").And.Not.Contain("using ("), text);
  }

}
