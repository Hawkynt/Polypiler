using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Functions;

/// <summary>
/// Arrow functions, function expressions, nested function declarations and closures of JavaScript
/// imported into the IR (#1326): the values node computes of the fixtures (measured with node 22, in
/// the fixture's header), the loop variables a closure captures, and every refused construct named.
/// </summary>
[TestFixture]
internal sealed class JavaScriptFunctionsImportTests {

  private static IrModule Snippet(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    return result.Module ?? throw new System.InvalidOperationException(string.Join("\n", result.Diagnostics));
  }

  private static IEnumerable<IrStmt> Statements(IrStmt statement)
    => IrClosures.Within(statement);

  /// <summary>Test design 1 and 4: the interpreter computes what node computes of every closure of the fixture.</summary>
  [TestCase("forVar", 9)]
  [TestCase("forLet", 3)]
  [TestCase("forLetAssigned", 9)]
  [TestCase("counter", 123.0)]
  [TestCase("interleaved", 21.0)]
  [TestCase("late", 10.0, 2.0)]
  [TestCase("fact", 120.0, 5.0)]
  [TestCase("fact", 1.0, 0.0)]
  [TestCase("fact", 1.0, 1.0)]
  [TestCase("fact", 479001600.0, 12.0)]
  [TestCase("fact", 6227020800.0, 13.0)]
  [TestCase("composed", 15.0, 4.0)]
  public void Closures_InterpretLikeNode(string function, object expected, params object[] arguments) {
    var module = FunctionsFixtures.ImportedScript("closures.js");
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, function, arguments), Is.EqualTo(expected));
  }

  /// <summary>Test design 9: a call of null traps, where node throws TypeError "f is not a function".</summary>
  [Test]
  public void CallNull_Traps()
    => Assert.That(() => IrInterpreter.Call(FunctionsFixtures.ImportedScript("closures.js"), "callNull", []),
      Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.NullReference));

  /// <summary>Test design 2: the sort and the fold of the JavaScript fixtures compute what those of the C# fixtures compute on .NET.</summary>
  [TestCaseSource(nameof(SortAndFoldCalls))]
  public void SortAndFold_InterpretLikeTheCSharpFixtures(string script, string className, PrototypeCall call) {
    var module = FunctionsFixtures.ImportedScript(script);
    // A parameter without JSDoc is a double in JavaScript, where C# has an int.
    var named = call with { Method = char.ToLowerInvariant(call.Method[0]) + call.Method[1..], Arguments = [.. call.Arguments.Select(argument => argument is int whole ? (object)(double)whole : argument)] };
    Assert.That(PrototypeFixture.InterpretedOutcome(module, named), Is.EqualTo(PrototypeFixture.InvokedOutcome(FunctionsFixtures.Compiled(className, call.Method), call)));
  }

  public static IEnumerable<TestCaseData> SortAndFoldCalls()
    => FunctionsFixtures.Calls().Where(call => call.File is "Sort" || call.Call.Method is "Sum" or "SumOfSquares")
      .Select(call => new TestCaseData(call.File == "Sort" ? "sort.js" : "accumulate.js", call.Class, call.Call).SetName($"{call.File}: {call.Call}"));

  /// <summary>Test design 3 and 4: a let of a for is a variable of every pass where a closure captures it, a var is one variable, a hoisted function a variable of its block.</summary>
  [Test]
  public void Constructs_AreTheNodesOfTheTable() {
    var module = FunctionsFixtures.ImportedScript("closures.js");
    IrFunction Function(string name) => module.Functions.Single(function => function.Name == name);
    var forLet = Statements(Function("forLet").Body).OfType<IrFor>().Single();
    Assert.That(forLet.PerIteration.Select(local => local.Name), Is.EqualTo(new[] { "i" }));
    Assert.That(Statements(Function("forVar").Body).OfType<IrFor>().Where(loop => loop.PerIteration.Count > 0), Is.Empty);
    var fact = Function("fact").Body.Statements;
    Assert.That(fact[0], Is.TypeOf<IrDeclare>().With.Property(nameof(IrDeclare.Initializer)).Null);
    Assert.That(fact[1], Is.TypeOf<IrAssign>().With.Property(nameof(IrAssign.Value)).TypeOf<IrLambda>());
    var composed = IrTree.Nodes(Function("composed").Body).OfType<IrLambda>().ToList();
    Assert.That(composed.Select(lambda => lambda.Captures.Count), Is.EquivalentTo(new[] { 0, 2, 0, 0 }));
    Assert.That(IrTree.Nodes(Function("counter").Body).OfType<IrInvoke>(), Has.Exactly(3).Items);
  }

  /// <summary>A function of the file named as a value is that function; it is called through the value with what the value is called with.</summary>
  [Test]
  public void FunctionOfTheFile_AsAValue() {
    var module = Snippet("function square(v) { return v * v; } export function f(x) { const g = square; return g(x); }");
    Assert.That(IrTree.Nodes(module.Functions.Single(function => function.Name == "f").Body).OfType<IrFunctionRef>().Single().Function, Is.EqualTo("square"));
    Assert.That(IrInterpreter.Call(module, "f", [-7.0]), Is.EqualTo(49.0));
  }

  /// <summary>A nested function may hide a variable of the function around it, and an arrow sees this of the method it stands in.</summary>
  [Test]
  public void Scopes_AreThoseOfJavaScript() {
    var module = Snippet("""
      export function f(x) { const g = (x) => x * 2; return g(x + 1) + x; }
      export class K { constructor() { this.v = 3; } get() { const h = () => this.v; return h(); } }
      export function k() { return new K().get(); }
      """);
    Assert.That((IrInterpreter.Call(module, "f", [1.0]), IrInterpreter.Call(module, "k", [])), Is.EqualTo((5.0, 3.0)));
  }

  /// <summary>Test design 7: a pass ended by continue or break still hands its variable on, a loop of no pass makes no closure (node 22: 9 and 4).</summary>
  [TestCase("continue", 9.0)]
  [TestCase("break", 4.0)]
  public void LetLoop_HandsTheVariableOnOnEveryExit(string jump, double expected) {
    var module = Snippet($$"""
      export function f() {
        const fs = [null, null, null];
        let k = 0;
        for (let i = 0; i < 6; i++) {
          fs[k] = () => i;
          k++;
          i++;
          if (k === 2) {{jump}};
        }
        return k === 3 ? fs[0]() + fs[1]() + fs[2]() : fs[0]() + fs[1]();
      }
      export function g() { let r = 0; for (let i = 0; i < 0; i++) r = r + (() => i)(); return r; }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(expected));
    Assert.That(IrInterpreter.Call(module, "g", []), Is.EqualTo(0.0));
  }

  /// <summary>Test design 10 and 11: every refused construct is named.</summary>
  [TestCase("export function f(a) { const g = function () { return arguments.length; }; return g(a, a); }", "PLY1102", "'arguments' is not supported yet")]
  [TestCase("export function f() { const g = function () { return this; }; return g(); }", "PLY1102", "'this' is not supported yet")]
  [TestCase("export function f() { const g = () => 1; return new g(); }", "PLY1102", "'new g' is not supported yet")]
  [TestCase("export function f() { const g = () => 1; return g.call(null); }", "PLY1102", "'a call of g.call' is not supported yet")]
  [TestCase("export function f() { const g = (a = 1) => a; return g(); }", "PLY1102", "'a default parameter value' is not supported yet")]
  [TestCase("export function f() { const g = (...a) => 1; return g(); }", "PLY1102", "'a rest parameter' is not supported yet")]
  [TestCase("export function f() { let f = 1; f = () => 2; return 0; }", "PLY1105", "'f' holds both number and a function")]
  [TestCase("export function f() { const g = () => 1; return g(1); }", "PLY1102", "'a call of g with 1 arguments for 0 parameters' is not supported yet")]
  public void Refused_IsNamed(string source, string code, string message) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((code, message)), string.Join("\n", result.Diagnostics));
  }

}
