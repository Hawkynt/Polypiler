using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Async;

/// <summary>
/// Async functions of JavaScript imported (#1330): the fixture <c>pipeline.mjs</c> through the frontend
/// and the interpreter computes what node computes of it (recorded here, re-measured by the acceptance
/// tests), and every construct outside the order-insensitive subset is refused by name.
/// </summary>
[TestFixture]
internal sealed class JavaScriptAsyncImportTests {

  /// <summary>What node v22.23.3 computes of the fixture's calls.</summary>
  private static readonly (string Function, double[] Arguments, string Expected)[] _node = [
    ("run", [0], "0"), ("run", [1], "1"), ("run", [4], "22"),
    ("completed", [], "7"), ("squares", [], "[4,9]"), ("observed", [], "t1 caught boom finally")
  ];

  private static string Refused(string source) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Module, Is.Null, "refused");
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  private static PrototypeCall CallOf((string Function, double[] Arguments, string Expected) call) => new(call.Function, [.. call.Arguments.Cast<object>()]);

  /// <summary>Test design 1 and 3: the interpreter computes what node computes.</summary>
  [Test]
  public void Interpreter_ComputesWhatNodeComputes() {
    var module = AsyncFixtures.ImportedScript();
    foreach (var call in _node)
      Assert.That(AsyncFixtures.InterpretedOutcome(module, CallOf(call)), Is.EqualTo(call.Expected), call.Function);
  }

  [Test]
  public void Import_MakesAsyncFunctionsOfTheInferredResults() {
    var module = AsyncFixtures.ImportedScript();
    Assert.That(module.Functions.Single(function => function.Name == "run").ReturnType, Is.EqualTo(IrType.Async(IrType.F64)));
    Assert.That(module.Functions.Single(function => function.Name == "thrower").ReturnType, Is.EqualTo(IrType.Async(IrType.Void)));
    Assert.That(module.Functions.Single(function => function.Name == "squares").ReturnType, Is.EqualTo(IrType.Async(IrType.Array(IrType.F64))));
    var completed = module.Functions.Single(function => function.Name == "completed");
    Assert.That(IrTree.Nodes(completed.Body).OfType<IrAsync>().Select(operation => operation.Op), Is.EqualTo(new[] { IrAsyncOp.FromResult, IrAsyncOp.FromResult }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>The same in the other direction: the fixture written for net4.8 compiles there and computes what node computes.</summary>
  [Test, Category("Slow")]
  public void Net48_ComputesWhatNodeComputes() {
    var module = AsyncFixtures.ImportedScript();
    Assert.That(LegalizedCSharp.Check(module, CSharpProfile.Net48), Is.Empty);
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Net48)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net4.8", "7.3"), Is.Empty, text);
    var compiled = GeneratedCSharp.Compile(text, false);
    foreach (var call in _node)
      Assert.That(AsyncFixtures.Awaited(compiled.Method(call.Function).Invoke(null, CallOf(call).FreshArguments())), Is.EqualTo(call.Expected), call.Function);
  }

  /// <summary>Test design 10 and 11: every construct of the table outside the subset, refused with its words.</summary>
  [TestCase("async function f() { return 1; } export async function m() { const p = f(); return await p; }", "not awaited where it is created")]
  [TestCase("async function f() { return 1; } export function m() { f(); return 1; }", "not awaited where it is created")]
  [TestCase("async function f() { return 1; } export async function m() { return f(); }", "not awaited where it is created")]
  [TestCase("export async function m() { return await new Promise(r => r(1)); }", "a task completed by hand: .NET continues inside SetResult, JavaScript after it")]
  [TestCase("async function f() { return 1; } export async function m() { return await f().then(x => x + 1); }", "'.then'")]
  [TestCase("export async function m() { setTimeout(() => 1, 0); return 1; }", "'setTimeout'")]
  [TestCase("export async function m() { queueMicrotask(() => 1); return 1; }", "'queueMicrotask'")]
  [TestCase("async function f() { return 1; } export async function m() { return await Promise.race([f()]); }", "'Promise.race'")]
  [TestCase("export async function m() { const p = Promise.resolve(1); return 1; }", "not awaited where it is created")]
  [TestCase("async function f() { return 1; } export async function m(a) { return await Promise.all(a); }", "'Promise.all over a collection of promises'")]
  [TestCase("export async function m() { return await Promise.all([1, 2]); }", "not awaited where it is created")]
  [TestCase("export function m() { const f = async () => 1; return 1; }", "'an async lambda'")]
  [TestCase("export async function* m() { yield 1; }", "'an async generator'")]
  [TestCase("export class C { async m() { return 1; } }", "'an async method'")]
  [TestCase("async function* g() { yield 1; } export async function m() { for await (const x of g()) { } return 1; }", "'an async generator'")]
  [TestCase("export async function m(xs) { for await (const x of xs) { } return 1; }", "'for await'")]
  [TestCase("function* g() { yield 1; } export async function m() { await 0; return g(); }", "as the result of an async function")]
  [TestCase("async function a() { await 0; return 1; } async function s() { await 0; return \"x\"; } export async function m() { return await Promise.all([a(), s()]); }", "'Promise.all: WhenAll takes async values of one result type'")]
  [TestCase("let n = 0; async function f() { await 0; n = n + 1; return n; } export async function m() { return await Promise.all([f(), f()]); }", "whose order depends on timing")]
  [TestCase("async function a(x) { await 0; if (x > 0) throw new Error(\"A\"); return x; } async function b(x) { if (x > 0) throw new Error(\"B\"); return x; } export async function m() { return await Promise.all([a(1), b(1)]); }", "'Promise.all whose rejection depends on timing'")]
  public void UnorderedConstruct_IsRefusedByName(string source, string words) {
    var diagnostics = Refused(source);
    Assert.That(diagnostics, Does.Contain("PLY1102").And.Contain(words), diagnostics);
  }

  /// <summary>Test design 6: a Promise.all of which at most one call can fault is imported.</summary>
  [Test]
  public void PromiseAll_WithOneFaultingCall_IsImported() {
    const string source = "async function a(x) { await 0; return x * x; } async function b(x) { if (x > 1) throw new Error(\"B\"); return x * x; } export async function m() { return await Promise.all([a(2), b(1)]); }";
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(result.Module!, "m", []), Is.EqualTo(new double[] { 4, 1 }).Or.EqualTo(new int[] { 4, 1 }));
  }

  /// <summary>A function inside an async function is checked by itself; a resolved promise without a value is awaited as a statement.</summary>
  [Test]
  public void NestedFunctionAndEmptyResolve_AreImported() {
    const string source = "async function g() { await Promise.resolve(); return 2; } export async function m() { const f = x => x + 1; await Promise.resolve(); return f(await g()); }";
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(result.Module!, "m", []), Is.EqualTo(3.0));
  }

  /// <summary>A class's own <c>then</c>, <c>catch</c> and <c>finally</c> are methods like any other: called on an object of the class - a variable, <c>this</c>, the result of a method or a function - they are imported in a file with async functions.</summary>
  [Test]
  public void OwnThenCatchFinally_OfAClass_AreImported() {
    const string source = "class Chain { constructor(v) { this.v = v; } then(k) { return this.v + k; } catch(k) { return this.v - k; } finally(k) { return this.then(k) * k; } self() { return this; } }"
      + " function make(v) { return new Chain(v); } async function g(x) { await 0; return x + 1; }"
      + " export async function m(x) { const c = new Chain(await g(x)); return c.then(1) + c.catch(1) + c.finally(2) + c.self().then(0) + make(5).then(0); }";
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrInterpreter.Call(result.Module!, "m", [1.0]), Is.EqualTo(19.0));
  }

  /// <summary>A class declaring <c>then</c> does not let a promise's own <c>.then</c>, <c>.catch</c> or <c>.finally</c> through: the receiver is (or may be) a promise.</summary>
  [TestCase("return await f().then(x => x + 1);", "'.then'")]
  [TestCase("return await f().catch(x => x);", "'.catch'")]
  [TestCase("return await Promise.resolve(1).finally(() => 1);", "'.finally'")]
  [TestCase("return await new Promise(r => r(1)).then(x => x);", "'.then'")]
  [TestCase("return await h().then(x => x);", "'.then'")]
  [TestCase("return await h[0]().then(x => x);", "'.then'")]
  public void PromiseContinuation_BesideAClassThen_IsRefused(string body, string words) {
    var diagnostics = Refused("class Chain { then(k) { return k; } catch(k) { return k; } finally(k) { return k; } }"
      + " async function f() { await 0; return 1; }"
      + $" export async function m(h) {{ {body} }}");
    Assert.That(diagnostics, Does.Contain("PLY1102").And.Contain(words), diagnostics);
  }

  /// <summary>Without a class that declares it, <c>.then</c> on any receiver may be a promise's and is refused.</summary>
  [Test]
  public void Then_OnAnObjectWithoutSuchAMethod_IsRefused() {
    var diagnostics = Refused("class Box { constructor(v) { this.v = v; } } async function f() { await 0; return 1; } export async function m() { const b = new Box(await f()); return b.then(1); }");
    Assert.That(diagnostics, Does.Contain("PLY1102").And.Contain("'.then'"), diagnostics);
  }

  /// <summary>An object of a class with <c>then</c> is a thenable: an async function that returns or awaits one would call it in JavaScript and not in .NET.</summary>
  [TestCase("return new Chain(1);")]
  [TestCase("const c = await new Chain(1); return 1;")]
  [TestCase("return await Promise.resolve(new Chain(1));")]
  public void Thenable_ReturnedOrAwaited_IsRefused(string body) {
    var diagnostics = Refused($"class Chain {{ constructor(v) {{ this.v = v; }} then(k) {{ return this.v + k; }} }} export async function m() {{ await 0; {body} }}");
    Assert.That(diagnostics, Does.Contain("PLY1102").And.Contain("a thenable object"), diagnostics);
  }

  [Test]
  public void ConstructTable_HasRefusalsAndImports() {
    Assert.That(JavaScriptAsyncApi.Supported.Count(), Is.EqualTo(7));
    Assert.That(JavaScriptAsyncApi.Refused.Select(member => member.Refusal), Has.All.Not.Empty);
  }

}
