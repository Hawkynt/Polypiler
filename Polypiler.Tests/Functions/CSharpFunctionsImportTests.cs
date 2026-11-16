using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Functions;

/// <summary>
/// Delegates, lambdas, local functions and closures of C# imported into the IR (#1326): what each
/// construct becomes, the interpreter computing what .NET computes of the fixtures, and every
/// refused construct named.
/// </summary>
[TestFixture]
internal sealed class CSharpFunctionsImportTests {

  public static IEnumerable<TestCaseData> FixtureCalls()
    => FunctionsFixtures.Calls().Select(call => new TestCaseData(call.File, call.Class, call.Call).SetName($"{call.File}: {call.Call}"));

  private static IrModule Snippet(string methods, string before = "")
    => PrototypeFixture.Import("Snippet.cs", "#nullable disable\nusing System;\n" + before + PrototypeFixture.Wrap(methods));

  private static IrFunction Function(IrModule module, string name = "F") => module.Functions.Single(function => function.Name == name);

  private static IEnumerable<IrExpr> Nodes(IrFunction function) => IrTree.Nodes(function.Body);

  /// <summary>Test design 1 and 2: the interpreter computes what .NET computes of every fixture call.</summary>
  [TestCaseSource(nameof(FixtureCalls))]
  public void Fixture_InterpretsLikeDotNet(string file, string className, PrototypeCall call) {
    var module = FunctionsFixtures.Imported(file);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(PrototypeFixture.InvokedOutcome(FunctionsFixtures.Compiled(className, call.Method), call)));
  }

  /// <summary>Test design 3: each construct is the node the construct table names.</summary>
  [Test]
  public void Constructs_AreTheNodesOfTheTable() {
    var closures = FunctionsFixtures.Imported("Closures");
    Assert.That(Nodes(Function(closures, "Late")).OfType<IrLambda>().Single().Captures.Select(local => local.Name), Is.EqualTo(new[] { "x" }));
    Assert.That(Nodes(Function(closures, "MethodGroup")).OfType<IrFunctionRef>().Single().Function, Is.EqualTo("Square"));
    Assert.That(Nodes(Function(closures, "Bound")).OfType<IrBoundMethod>().Single().Method.Name, Is.EqualTo("Apply"));
    Assert.That(Nodes(Function(closures, "StaticLambda")).OfType<IrLambda>().Single().Captures, Is.Empty);
    Assert.That(Nodes(Function(closures, "StaticLambda")).OfType<IrInvoke>(), Has.Exactly(1).Items);
    Assert.That(Nodes(Function(closures, "NullChecks")).OfType<IrReferenceEquals>(), Has.Exactly(3).Items);
    var fact = Function(closures, "Fact");
    Assert.That(fact.Body.Statements[0], Is.TypeOf<IrDeclare>().With.Property(nameof(IrDeclare.Initializer)).Null);
    Assert.That(fact.Body.Statements[1], Is.TypeOf<IrAssign>());
    Assert.That(((IrLambda)((IrAssign)fact.Body.Statements[1]).Value).Captures.Select(local => local.Name), Is.EqualTo(new[] { "F" }));
    var forCapture = Function(closures, "ForCapture");
    Assert.That(IrTree.Nodes(forCapture.Body).OfType<IrLambda>().Single().Captures.Select(local => local.Name), Is.EqualTo(new[] { "i" }));
    Assert.That(forCapture.Body.Statements.OfType<IrBlock>().SelectMany(block => block.Statements).OfType<IrFor>().All(loop => loop.PerIteration.Count == 0));
    Assert.That(Function(closures, "Anonymous").Locals.Single(local => local.Name == "twice").Type, Is.EqualTo(IrType.Function(IrType.I32, IrType.I32)));
  }

  /// <summary>A delegate type of the file and a Func of the same signature are one function type; a Func, an Action and a delegate of every arity map.</summary>
  [Test]
  public void DelegateTypes_MapBySignature() {
    var module = Snippet("""
      public static int F(Op a, Func<int, int, int> b) => a(1, 2) + b(3, 4);
      public static void G(Action a, Action<int> b, Func<int> c) { a(); b(c()); }
      public static int H(Wide w) => w(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17);
      """, "public delegate int Op(int a, int b); public delegate int Wide(int a, int b, int c, int d, int e, int f, int g, int h, int i, int j, int k, int l, int m, int n, int o, int p, int q);\n");
    var f = Function(module);
    Assert.That(f.Parameters[0].Type, Is.EqualTo(f.Parameters[1].Type));
    Assert.That(Function(module, "G").Parameters.Select(parameter => parameter.Type.ToString()), Is.EqualTo(new[] { "fn() -> void", "fn(i32) -> void", "fn() -> i32" }));
    Assert.That(Function(module, "H").Parameters[0].Type.Signature!.Parameters, Has.Count.EqualTo(17));
  }

  /// <summary>A local function a statement calls before its declaration holds its closure from the start of its block, or from where what it captures is declared.</summary>
  [Test]
  public void LocalFunctions_AreAssignedWhereTheyCanFirstBeCalled() {
    var module = Snippet("""
      public static int F(int a) {
        var b = a + 1;
        var r = G(2) + H();
        return r;
        int G(int v) => v * b;
        static int H() => 7;
      }
      """);
    var statements = Function(module).Body.Statements;
    Assert.That(statements.Take(2), Is.All.TypeOf<IrDeclare>());
    Assert.That(statements[2], Is.TypeOf<IrAssign>().And.Property(nameof(IrAssign.Local)).Property(nameof(IrLocal.Name)).EqualTo("H"));
    Assert.That(statements[3], Is.TypeOf<IrDeclare>().And.Property(nameof(IrDeclare.Local)).Property(nameof(IrLocal.Name)).EqualTo("b"));
    Assert.That(statements[4], Is.TypeOf<IrAssign>().And.Property(nameof(IrAssign.Local)).Property(nameof(IrLocal.Name)).EqualTo("G"));
    Assert.That(IrInterpreter.Call(module, "F", [3]), Is.EqualTo(15));
  }

  /// <summary>Generic methods that take a function are instantiated with the function type closed.</summary>
  [Test]
  public void GenericMethodTakingAFunction_IsInstantiated() {
    var module = Snippet("""
      static T Apply<T>(Func<T, T> f, T v) => f(v);
      public static int F(int x) => Apply(v => v + 1, x);
      public static double G(double x) => Apply(v => v * 2, x);
      """);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That((IrInterpreter.Call(module, "F", [4]), IrInterpreter.Call(module, "G", [1.5])), Is.EqualTo((5, 3.0)));
  }

  /// <summary>Test design 10: every refused construct is named.</summary>
  [TestCase("public static int F() { Func<int> f = () => 1; f += () => 2; return f(); }", "a multicast delegate")]
  [TestCase("public static int F() { Func<int> f = () => 1; f -= f; return 1; }", "a multicast delegate")]
  [TestCase("public static int F() { Func<int> f = () => 1; var g = f + f; return 1; }", "a multicast delegate")]
  [TestCase("public static bool F() { Func<int> f = () => 1; return f == f; }", "delegate equality other than with null")]
  [TestCase("public static int F(Del d) { int x = 1; return d(ref x); }", "a ref parameter of a delegate", "public delegate int Del(ref int x);")]
  [TestCase("public static int F(Del d) => d(1);", "a params delegate", "public delegate int Del(params int[] x);")]
  [TestCase("public static int F() { int G<T>(T v) => 1; return G(1); }", "a generic local function")]
  [TestCase("public static int F() { Action f = async () => { }; return 1; }", "an async lambda")]
  [TestCase("public static int F() { Func<System.Threading.Tasks.Task<int>> f = async () => 1; return 1; }", "an async lambda")]
  [TestCase("public static int F() { Delegate d = null; return 1; }", "System.Delegate")]
  [TestCase("public static int F(Func<int> f) => (int)f.DynamicInvoke();", "object")]
  [TestCase("public static int F() { Func<int, int> f = Math.Abs; return f(-1); }", "the method group Math.Abs")]
  [TestCase("public static int F() => 1;", "a generic delegate type", "public delegate T Del<T>(T x);")]
  public void Refused_IsNamed(string methods, string named, string before = "") {
    var result = CSharpFrontend.Import("Snippet.cs", "#nullable disable\nusing System;\n" + before + PrototypeFixture.Wrap(methods));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains($"'{named}' is not supported yet"), string.Join("\n", result.Diagnostics));
  }

}
