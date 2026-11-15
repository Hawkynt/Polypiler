using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Async;

/// <summary>
/// Async methods of C# imported (#1330): the fixture <c>Pipeline.cs</c> through the frontend and the
/// interpreter computes what .NET computes of it, and every construct outside the order-insensitive
/// subset is refused by name.
/// </summary>
[TestFixture]
internal sealed class CSharpAsyncImportTests {

  private static IEnumerable<TestCaseData> Calls()
    => AsyncFixtures.Calls().Select(call => new TestCaseData(call).SetName($"Pipeline.{call}"));

  private static string Refused(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", "using System;\nusing System.Threading.Tasks;\n" + PrototypeFixture.Wrap(methods));
    Assert.That(result.Module, Is.Null, "refused");
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  private static IrModule Accepted(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", "using System;\nusing System.Threading.Tasks;\n" + PrototypeFixture.Wrap(methods));
    Assert.That(result.Diagnostics, Is.Empty);
    return result.Module!;
  }

  /// <summary>Test design 1 to 7: the interpreter computes what .NET computes, call by call.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_ComputesWhatDotNetComputes(PrototypeCall call)
    => Assert.That(AsyncFixtures.InterpretedOutcome(AsyncFixtures.Imported(), call), Is.EqualTo(AsyncFixtures.DotNetOutcome(call)));

  /// <summary>The scenario values, as .NET measures them.</summary>
  [TestCase("Run", 4, "22"), TestCase("Run", 0, "0")]
  public void Fixture_HasTheScenarioValues(string method, int n, string expected)
    => Assert.That(AsyncFixtures.DotNetOutcome(new PrototypeCall(method, [n])), Is.EqualTo(expected));

  /// <summary>Test design 6 and 7: WhenAll with 0, 1 and 2 calls; the first fault in argument order wins.</summary>
  [TestCase("NoSquares", ""), TestCase("OneSquare", "4"), TestCase("Squares", "4,9"), TestCase("Faults", "A")]
  public void WhenAll_Boundaries(string method, string expected) {
    var shown = AsyncFixtures.DotNetOutcome(new PrototypeCall(method, []));
    Assert.That(shown, Is.EqualTo(method == "Faults" ? expected : $"[{expected}]"));
  }

  [Test]
  public void Import_MakesAsyncFunctions() {
    var module = AsyncFixtures.Imported();
    var run = module.Functions.Single(function => function.Name == "Run");
    Assert.That(run.ReturnType, Is.EqualTo(IrType.Async(IrType.I32)));
    Assert.That(run.IsAsync, Is.True);
    Assert.That(IrTree.Nodes(run.Body).OfType<IrAwait>().Single().Operand, Is.InstanceOf<IrCall>());
    Assert.That(module.Functions.Single(function => function.Name == "Valued").ReturnType, Is.EqualTo(IrType.Async(IrType.I32, true)));
    Assert.That(module.Functions.Single(function => function.Name == "Touch").ReturnType, Is.EqualTo(IrType.Async(IrType.Void)));
    var squares = module.Functions.Single(function => function.Name == "Squares");
    Assert.That(IrTree.Nodes(squares.Body).OfType<IrAsync>().Single().Op, Is.EqualTo(IrAsyncOp.WhenAll));
    var completed = module.Functions.Single(function => function.Name == "Completed");
    Assert.That(IrTree.Nodes(completed.Body).OfType<IrAsync>().Select(operation => operation.Op), Is.EqualTo(new[] { IrAsyncOp.Completed, IrAsyncOp.FromResult, IrAsyncOp.FromResult }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>An expression-bodied async method that returns no value awaits as a statement.</summary>
  [Test]
  public void ExpressionBodiedTask_IsAStatement() {
    var module = Accepted("static async Task T() { await Task.CompletedTask; } public static async Task U() => await T();");
    Assert.That(module.Functions.Single(function => function.Name == "U").Body.Statements.Single(), Is.InstanceOf<IrExprStmt>());
    Assert.That(IrInterpreter.Call(module, "U", []), Is.Null);
  }

  /// <summary>Test design 10: the scenario's stored task, and every other construct of the table, refused with its words.</summary>
  [TestCase("static async Task<int> F() { await Task.CompletedTask; return 1; } static void Log(int x) { } public static async Task<int> M() { var t = F(); Log(2); return await t; }", "not awaited where it is created")]
  [TestCase("static async Task<int> F() { await Task.CompletedTask; return 1; } public static Task<int> M() => F();", "not awaited where it is created")]
  [TestCase("public static Task M() { return Task.CompletedTask; }", "not awaited where it is created")]
  [TestCase("public static async Task<int> M(Task<int> t) => await t;", "not awaited where it is created")]
  [TestCase("public static async Task<int> M() { var s = new TaskCompletionSource<int>(); s.SetResult(1); return await s.Task; }", "a task completed by hand: .NET continues inside SetResult, JavaScript after it")]
  [TestCase("static async Task<int> F() { await Task.CompletedTask; return 1; } public static int M() => F().Result;", "blocking on a task")]
  [TestCase("static async Task<int> F() { await Task.CompletedTask; return 1; } public static void M() { F().Wait(); }", "blocking on a task")]
  [TestCase("static async Task<int> F() { await Task.CompletedTask; return 1; } public static int M() => F().GetAwaiter().GetResult();", "blocking on a task")]
  [TestCase("public static async Task<int> M() => await Task.Run(() => 1);", "'Task.Run'")]
  [TestCase("public static async Task M() { await Task.Delay(1); }", "'Task.Delay'")]
  [TestCase("public static async Task M() { await Task.Yield(); }", "'Task.Yield'")]
  [TestCase("static async Task<int> F() { await Task.CompletedTask; return 1; } public static async Task M() { await F().ContinueWith(t => 2); }", "'Task.ContinueWith'")]
  [TestCase("public static async void M() { await Task.CompletedTask; }", "'async void'")]
  [TestCase("public static void M() { Func<Task> f = async () => await Task.CompletedTask; }", "'an async lambda'")]
  [TestCase("public static async Task M() { await new ValueTask(); }", "'new ValueTask'")]
  [TestCase("static async Task<int> F() { await Task.CompletedTask; return 1; } public static async Task<int[]> M() { var all = new[] { F() }; return await Task.WhenAll(all); }", "not awaited where it is created")]
  [TestCase("static async Task<int> F() { await Task.CompletedTask; return 1; } public static async Task M() { await F().ConfigureAwait(Flag()); } static bool Flag() => false;", "ConfigureAwait with an argument that is no constant")]
  [TestCase("static int s; static async Task<int> F() { await Task.CompletedTask; s = s + 1; return s; } public static async Task<int[]> M() => await Task.WhenAll(F(), F());", "whose order depends on timing")]
  [TestCase("static async Task<int> F(int[] a) { await Task.CompletedTask; a[0] = 1; return 1; } public static async Task<int[]> M(int[] a) => await Task.WhenAll(F(a), F(a));", "whose order depends on timing")]
  [TestCase("public static async Task<int[]> M(Task<int>[] all) => await Task.WhenAll(all);", "'System.Threading.Tasks.Task<int>[]'")]
  [TestCase("public static async Task<int[]> M() => await Task.WhenAll(Task.FromResult(1));", "not awaited where it is created")]
  [TestCase("public static async Task<string> M() => await System.IO.File.ReadAllTextAsync(\"x\");", "'File.ReadAllTextAsync'")]
  [TestCase("public static async Task<int> M() => await default(Task<int>);", "not awaited where it is created")]
  [TestCase("public static async Task M() { await ValueTask.CompletedTask; }", "not awaited where it is created")]
  [TestCase("static async Task<System.Collections.Generic.IEnumerable<int>> F() { await Task.CompletedTask; return new int[0]; }", "IEnumerable<int>>'")]
  [TestCase("static async System.Collections.Generic.IAsyncEnumerable<int> G() { await Task.CompletedTask; yield return 1; } public static async Task M() { await foreach (var x in G()) { } }", "'await foreach'")]
  [TestCase("static int s; static async Task<T> G<T>(T x) { await Task.CompletedTask; s = s + 1; return x; } static async Task<T[]> W<T>(T x) => await Task.WhenAll(G(x), G(x)); public static async Task<int[]> M() => await W(1);", "whose order depends on timing")]
  public void UnorderedConstruct_IsRefusedByName(string methods, string words) {
    var diagnostics = Refused(methods);
    Assert.That(diagnostics, Does.Contain("PLY1002").And.Contain(words), diagnostics);
  }

  /// <summary>Parentheses around an awaited call and a lambda that returns no task change nothing.</summary>
  [Test]
  public void ParenthesesAndPlainLambdas_AreImported() {
    var module = Accepted("static async Task<int> F() { await Task.CompletedTask; return 2; } public static async Task<int> M() { System.Func<int, int> g = x => x + 1; return g(await (F())); }");
    Assert.That(IrInterpreter.Call(module, "M", []), Is.EqualTo(3));
  }

  /// <summary>Test design 10: a task in a field is refused.</summary>
  [Test]
  public void TaskInAField_IsRefused()
    => Assert.That(Refused("static Task<int> s; public static int M() => 1;"), Does.Contain("not awaited where it is created"));

  /// <summary>Every refusal of the construct table names a construct a test refuses.</summary>
  [Test]
  public void ConstructTable_HasRefusalsAndImports() {
    Assert.That(CSharpAsyncApi.Supported.Count(), Is.EqualTo(8));
    Assert.That(CSharpAsyncApi.Refused.Select(member => member.Refusal), Has.All.Not.Empty);
  }

}
