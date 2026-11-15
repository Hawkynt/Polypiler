using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Float.Support;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Resources;

/// <summary>
/// Resource scopes through both frontends (#1324): what the C# frontend imports and refuses (test
/// design 8), what the JavaScript frontend refuses (9), the rules of the verifier (10), and the forms
/// both backends write of hand-written sources.
/// </summary>
[TestFixture]
internal sealed class ResourceImportTests {

  private const string Resource = "public sealed class R : System.IDisposable { public int[] Log; public R(int[] log) { this.Log = log; } public void Dispose() { this.Log[0] = this.Log[0] + 1; } } ";

  private static IrModule CSharp(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\n" + source);
    return result.Module ?? throw new AssertionException(string.Join("; ", result.Diagnostics));
  }

  private static Diagnostic CSharpRefusal(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\n" + source);
    Assert.That(result.Module, Is.Null);
    return result.Diagnostics[0];
  }

  private static Diagnostic ScriptRefusal(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    return result.Diagnostics[0];
  }

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(problem => problem.Message)];

  /// <summary>Test design 8: await using, a resource of a struct type, a ref struct with a pattern-based Dispose and a finalizer are refused by name.</summary>
  [TestCase("public static class C { public static async System.Threading.Tasks.Task F(System.IAsyncDisposable r) { await using (r) { } } }", "await using")]
  [TestCase("public static class C { public static async System.Threading.Tasks.Task F(System.IAsyncDisposable r) { await using var s = r; } }", "await using")]
  [TestCase(Resource + "public sealed class Q : System.IDisposable { ~Q() { } public void Dispose() { } } public static class C { public static int F() => 1; }", "a finalizer")]
  [TestCase("public static class C { public static int F() { using (var s = new System.IO.MemoryStream()) { return 1; } } }", "MemoryStream")]
  [TestCase("public static class C { public static int F(int[] o) { lock (o) { return 1; } } }", "LockStatement")]
  public void CSharp_RefusesByName(string source, string what) {
    var refusal = CSharpRefusal(source);
    Assert.That(refusal.Code, Is.EqualTo(CSharpFrontend.Unsupported));
    Assert.That(refusal.Message, Does.Contain(what));
  }

  /// <summary>Test design 8: the refusal fixtures; nothing is written.</summary>
  [TestCase("AsyncScope.cs", "'await using' is not supported yet")]
  [TestCase("StructScope.cs", "'a resource of a struct type' is not supported yet")]
  public void CSharp_RefusesTheFixture(string file, string message) {
    var result = CSharpFrontend.Import(file, ResourceFixtures.SourceOf(file));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(problem => problem.ToString()), Has.Some.Contains(CSharpFrontend.Unsupported).And.Contains(message));
  }

  /// <summary>Test design 8: a ref struct with a Dispose by pattern is refused as a resource, besides as a struct.</summary>
  [Test]
  public void CSharp_RefusesARefStructResource() {
    var result = CSharpFrontend.Import("Snippet.cs", "public ref struct S { public void Dispose() { } } public static class C { public static int F() { using (var s = new S()) { return 1; } } }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(problem => problem.ToString()), Has.Some.Contains("'a ref struct resource with a pattern-based Dispose' is not supported yet"));
  }

  /// <summary>Test design 9: JavaScript's using declaration is a syntax error at every level a profile targets, a [Symbol.dispose] method is refused by name.</summary>
  [Test]
  public void JavaScript_RefusesExplicitResourceManagement() {
    Assert.That(ScriptRefusal("export function f(r) { using x = r; return 1; }").Code, Is.EqualTo(JavaScriptFrontend.SyntaxError));
    var refusal = ScriptRefusal("class R { [Symbol.dispose]() { } }\nexport function f() { const r = new R(); return 1; }");
    Assert.That(refusal.Code, Is.EqualTo(JavaScriptFrontend.Unsupported));
    Assert.That(refusal.Message, Does.Contain("[Symbol.dispose]"));
  }

  /// <summary>Test design 2: the resource of a using (e) is held in a variable of its own, which a later assignment of e does not change.</summary>
  [Test]
  public void ExpressionResource_IsHeldInAVariableOfItsOwn() {
    var module = CSharp(Resource + "public static class C { public static int F() { var log = new int[1]; var r = new R(log); using (r) { r = null; } return log[0]; } }");
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo(1));
  }

  /// <summary>Test design 5: no resource disposed - a null one - and one disposed through a variable of the interface's type.</summary>
  [Test]
  public void InterfaceResource_IsDisposedAndNullIsNot() {
    var module = CSharp(Resource + "public static class C { public static int F(bool none) { var log = new int[1]; using (System.IDisposable d = none ? null : new R(log)) { } return log[0]; } }");
    Assert.That(IrInterpreter.Call(module, "F", [false]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(module, "F", [true]), Is.EqualTo(0));
    foreach (var profile in new[] { CSharpProfile.Net20, CSharpProfile.Net100 }) {
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(text, Does.Contain("using (global::System.IDisposable d = "), text);
    }
  }

  /// <summary>A using declaration that is not the last statement of its block once a pass moved one behind it is written as a statement around its body.</summary>
  [Test]
  public void UsingDeclaration_NotLastInItsBlock_IsAStatement() {
    var module = CSharp(Resource + "public static class C { public static int F() { var log = new int[1]; { using var r = new R(log); log[0] = 5; } return log[0]; } }");
    var function = module.Functions.Single(candidate => candidate.Name == "F");
    var inner = (IrBlock)function.Body.Statements[1];
    var moved = function with { Body = new IrBlock([function.Body.Statements[0], new IrBlock([.. inner.Statements, new IrExprStmt(new IrConst(IrType.I32, 0))]), .. function.Body.Statements.Skip(2)]) };
    module = module with { Functions = [.. module.Functions.Select(candidate => candidate == function ? moved : candidate)] };
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Net100)[0].Content;
    Assert.That(text, Does.Contain("using (var r = new R(log)) {").And.Not.Contain("using var"), text);
  }

  /// <summary>Test design 3: a scope that leaves a loop forever and a switch section by its body is written where C# decides reachability, and computes the same.</summary>
  [Test]
  public void ScopeThatLeaves_KeepsTheReachabilityOfItsBody() {
    var module = CSharp(Resource + "public static class C { public static int F(int k) { var log = new int[1]; switch (k) { case 1: while (true) { using (var r = new R(log)) { if (log[0] > 2) break; } } using (var r = new R(log)) { return log[0] * 10; } default: using (var r = new R(log)) { log[0] = log[0] + 100; } break; } return log[0]; } }");
    Assert.That(IrInterpreter.Call(module, "F", [1]), Is.EqualTo(40));
    Assert.That(IrInterpreter.Call(module, "F", [2]), Is.EqualTo(101));
    foreach (var profile in new[] { CSharpProfile.Net20, CSharpProfile.Net100 }) {
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(GeneratedCSharp.Compile(text, false).Outcome(new PrototypeCall("F", [1])), Is.EqualTo("40"), text);
    }
  }

  /// <summary>Test design 10: what the verifier refuses of a resource scope.</summary>
  [Test]
  public void Verifier_RefusesWhatResourceScopesRule() {
    var module = CSharp(Resource + "public interface I { void M(); } public sealed class P { } public static class C { public static void F(int[] log) { using (var r = new R(log)) { } } }");
    var function = module.Functions.Single(candidate => candidate.Name == "F");
    var scope = (IrUsing)function.Body.Statements[0];
    IrModule With(IrStmt statement, params IrLocal[] locals)
      => module with { Functions = [.. module.Functions.Select(candidate => candidate == function ? function with { Locals = [.. function.Locals, .. locals], Body = new IrBlock([statement]) } : candidate)] };

    Assert.That(Problems(module), Is.Empty);
    Assert.That(Problems(With(scope with { Body = new IrBlock([new IrAssign(scope.Resource, new IrNull(scope.Resource.Type))]) })), Has.One.Contains("the resource 'r' is assigned"));
    var plain = module.Types.OfType<IrClassType>().Single(type => type.Name == "P");
    var other = new IrLocal("p", IrType.Object(plain));
    Assert.That(Problems(With(scope with { Resource = other, Initializer = new IrNull(other.Type) }, other)), Has.One.Contains("is disposed through no interface of cleanup"));
    var stranger = new IrLocal("s", scope.Resource.Type);
    Assert.That(Problems(With(scope with { Resource = stranger })), Has.One.Contains("is not among the locals"));
    Assert.That(Problems(With(new IrBlock([scope, scope with { Body = new IrBlock([scope]) }]))), Has.One.Contains("declared twice"));
    var number = new IrLocal("n", IrType.I32);
    Assert.That(Problems(With(scope with { Resource = number, Initializer = new IrConst(IrType.I32, 0) }, number)), Has.One.Contains("is disposed through no interface of cleanup"));
    var foreign = new IrMethod("Dispose", "R.Dispose", false);
    Assert.That(Problems(With(scope with { Dispose = foreign })), Has.Some.Contains("is disposed through no interface of cleanup"));
  }

  /// <summary>Test design 10: an interface of cleanup has the shape of IDisposable.</summary>
  [Test]
  public void Verifier_RefusesAnInterfaceOfCleanupOfAnotherShape() {
    var contract = new IrInterfaceType("IDisposable") { IsDisposable = true };
    var close = new IrMethod("Close", "IDisposable.Close", false) { Dispatch = IrDispatch.Abstract };
    contract.DefineMembers([close], [], []);
    var signature = new IrFunction(close.Function, false, IrType.Void, [new IrLocal("this", IrType.Object(contract))], [], new IrBlock([]));
    var module = new IrModule("M", [signature], []) { Types = [contract] };
    Assert.That(Problems(module), Has.One.Contains("an interface of cleanup has another member than the method Dispose()"));
    var (proper, dispose, declared) = IrDisposable.Declare();
    Assert.That(Problems(new IrModule("M", [declared], []) { Types = [proper] }), Is.Empty);
    Assert.That(IrDisposable.DisposeOf(proper), Is.SameAs(dispose));
    Assert.That(IrDisposable.DisposeOf(contract), Is.Null);
    Assert.That(IrDisposable.DisposeOf(new IrInterfaceType("I")), Is.Null);
  }

  /// <summary>The tree of a using statement, stacked, and of a using declaration reads back from its text, prints as it reads, and rewrites like every statement.</summary>
  [Test]
  public void UsingSyntax_ReadsPrintsAndRewrites() {
    const string source = "public static void F(R a) {\n  using (var r = a)\n  using (var s = a) {\n    r.Dispose();\n  }\n  using (R q = a) s.Dispose();\n  using var t = a;\n  t.Dispose();\n}\n";
    var member = CSharpSyntaxReader.Member(source);
    Assert.That(CSharpPrinter.Print(member, CSharpProfile.Net100), Is.EqualTo(source));
    Assert.That(CSharpSyntaxRewriter.DescendantsAndSelf(member).OfType<CsUsing>().Count(), Is.EqualTo(3));
    Assert.That(CSharpSyntaxRewriter.DescendantsAndSelf(member).OfType<CsLocalDeclaration>().Count(declaration => declaration.IsUsing), Is.EqualTo(1));
    var renamed = CSharpSyntaxRewriter.Rewrite(member, node => node is CsName { Identifier: "a" } name ? name with { Identifier = "b" } : node);
    Assert.That(CSharpPrinter.Print(renamed, CSharpProfile.Net100), Is.EqualTo(source.Replace("= a", "= b")));
    Assert.That(CSharpSyntaxRewriter.Rewrite(member, node => node), Is.SameAs(member));
  }

  /// <summary>The passes know that a scope declares and writes its resource, and that a value written in its body may be read by its Dispose after an exception.</summary>
  [Test]
  public void Passes_KnowTheResourceAndTheTry() {
    var module = CSharp(Resource + "public static class C { public static int F(int[] log) { var t = log[0]; using (var r = new R(log)) { t = 2; } return t; } }");
    var function = module.Functions.Single(candidate => candidate.Name == "F");
    var scope = function.Body.Statements.OfType<IrUsing>().Single();
    Assert.That(Polypiler.Core.Recognize.IrStatements.Mentions(scope, scope.Resource), Is.True);
    Assert.That(Polypiler.Core.Recognize.IrStatements.Writes(scope), Does.Contain(scope.Resource));
    var context = new Polypiler.Core.Recognize.RuleContext(function, new Polypiler.Core.Recognize.RuleTarget(false));
    Assert.That(context.IsStable(scope.Resource), Is.False);
    var t = function.Locals.Single(local => local.Name == "t");
    context.Enter(new Polypiler.Core.Recognize.RuleFrame(function.Body.Statements, function.Body.Statements.ToList().IndexOf(scope)));
    Assert.That(context.IsDeadAfter(t, scope.Body.Statements, 1), Is.False);
  }

  /// <summary>The scope runs through the optimizer and the float legalization as any statement with a block does, and computes the same.</summary>
  [Test]
  public void Scope_SurvivesThePasses() {
    var module = CSharp(Resource + "public static class C { public static double F(double x) { var log = new int[1]; using (var r = new R(log)) { uint u = (uint)log[0]; u = (u << 3) | (u >> 29); x = System.Math.Sin(x) + u + log[0]; } return x + log[0]; } }");
    var text = LegalizedJavaScript.Emit(module, JavaScriptProfile.Parse("es2022", "esm")).Single().Content;
    Assert.That(text, Does.Contain("const r = new R(log);").And.Contain("if (r != null) r.Dispose();"), text);
    Assert.That(IrInterpreter.Call(module, "F", [0.0]), Is.EqualTo(1.0));
    var optimized = Polypiler.Core.Optimize.Optimizer.Run(module, new Polypiler.Core.Optimize.OptimizerOptions()).Module;
    Assert.That(IrVerifier.Verify(optimized), Is.Empty);
    Assert.That(IrInterpreter.Call(optimized, "F", [0.0]), Is.EqualTo(1.0));
    var legalized = CSharpEmitter.Legalize(module, () => FloatImplementations.Module, CSharpProfile.Net100, FloatMode.Strict);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    Assert.That(legalized.Functions.Single(function => function.Name == "F").Body.Statements.OfType<IrUsing>().Single().Body.Statements, Is.Not.Empty);
    Assert.That(IrInterpreter.Call(legalized, "F", [0.0]), Is.EqualTo(1.0));
  }

}
