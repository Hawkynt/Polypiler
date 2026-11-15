using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// O0463 (#1360): a function value that stays in the function it is made in is lifted, and a function
/// it is passed to is copied with the value bound, so that inlining and the passes after it make one
/// loop of a LINQ chain. The rows of the test design of the issue: what escapes and what does not, the
/// captures by value and in cells, the budget on, inside and outside its bounds, the traps inside a
/// lambda, the switches, and the optimised modules of the LINQ fixtures against .NET and the
/// unoptimised ones. Every snippet is compiled by Roslyn as well, and its outcome there is the oracle.
/// </summary>
[TestFixture]
internal sealed class LambdaSpecialisationTests {

  private const string Usings = "using System; using System.Linq; ";

  private const string Apply = "static int Apply(int[] a, Func<int, int> f) { int s = 0; foreach (var x in a) s += f(x); return s; } ";

  private static readonly int[][] _arrays = [[], [1, 5, 7], [1, -2, 3], [int.MaxValue, 4], [0, 2, 1, 9]];

  private static string Source(string methods) => Usings + PrototypeFixture.Wrap(methods);

  private static IrModule Legalized(string methods) {
    var imported = CSharpFrontend.Import("Snippet.cs", Source(methods));
    Assert.That(imported.Module, Is.Not.Null, string.Join(Environment.NewLine, imported.Diagnostics));
    return CSharpEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, CSharpProfile.All.Single(profile => profile.Name == "net10.0"), FloatMode.Accurate);
  }

  private static OptimizationResult Optimized(IrModule module, params string[] disabled)
    => IrPassPipeline.Default.Run(module, new OptimizerOptions { Verify = true, Disabled = disabled.ToHashSet() });

  /// <summary>Runs the pass once over every function, with the budget given.</summary>
  private static (IrModule Module, List<string> Notes) Specialised(IrModule module, O0463LambdaSpecialisation? pass = null) {
    var notes = new List<string>();
    var result = (pass ?? new O0463LambdaSpecialisation()).Run(module, _ => true, (_, _) => { }, notes.Add);
    Assert.That(IrVerifier.Verify(result), Is.Empty);
    return (result, notes);
  }

  private static IrFunction Function(IrModule module, string name) => module.Functions.Single(function => function.Name == name);

  private static bool HasLambda(IrModule module, string name) => IrTree.Nodes(Function(module, name).Body).Any(node => node is IrLambda);

  /// <summary>The interpreter computes on both modules what .NET computes of the source, call by call, traps included.</summary>
  private static void SameOutcomes(string methods, IrModule before, IrModule after, string method, params object[][] arguments) {
    var compiled = GeneratedCSharp.CompileSource(Source(methods));
    foreach (var argument in arguments) {
      var call = new PrototypeCall(method, argument);
      var expected = compiled.Outcome(call);
      Assert.That(PrototypeFixture.InterpretedOutcome(before, call), Is.EqualTo(expected), $"before, {method}({string.Join(", ", argument.Select(Text))})");
      Assert.That(PrototypeFixture.InterpretedOutcome(after, call), Is.EqualTo(expected), $"after, {method}({string.Join(", ", argument.Select(Text))})");
    }
  }

  /// <summary>How many arrays of one element <c>G</c> declares: the cells.</summary>
  private static int Cells(IrModule module)
    => Function(module, "G").Body.Statements.SelectMany(IrStatementsProbe.All).OfType<IrDeclare>().Count(declare => declare.Initializer is IrNewArray { Length: IrConst { Value: 1 } });

  private static string Text(object value) => value is int[] array ? $"[{string.Join(", ", array)}]" : $"{value}";

  private static object[][] Arrays(params object[] more) => [.. _arrays.Select(array => (object[])[array, .. more])];

  // ---------------------------------------------------------------- escape (row 2)

  private static IEnumerable<TestCaseData> Escapes() {
    yield return new TestCaseData(Apply + "public static int G(int[] a, int k) => Apply(a, x => x * k);", true).SetName("Escape_InvokedOnly_IsSpecialised");
    yield return new TestCaseData(Apply + "static int Outer(int[] a, Func<int, int> f) => Apply(a, f) + 1; public static int G(int[] a, int k) => Outer(a, x => x * k);", true)
      .SetName("Escape_PassedOnToAParameterThatKeepsIt_IsSpecialised");
    yield return new TestCaseData("static Func<int, int> keep; static int Store(int[] a, Func<int, int> f) { keep = f; return f(a.Length); } public static int G(int[] a, int k) => Store(a, x => x * k);", false)
      .SetName("Escape_StoredToAField_IsNotSpecialised");
    yield return new TestCaseData("static Func<int, int> Same(Func<int, int> f) => f; public static int G(int[] a, int k) => Same(x => x * k)(a.Length);", false)
      .SetName("Escape_Returned_IsNotSpecialised");
    yield return new TestCaseData("static Func<int, int> Plus(Func<int, int> f) => y => f(y) + 1; public static int G(int[] a, int k) => Plus(x => x * k)(a.Length);", false)
      .SetName("Escape_CapturedByAnEscapingLambda_IsNotSpecialised");
    yield return new TestCaseData("static int Rec(int n, Func<int, int> f) => n <= 0 ? 0 : f(n) + Rec(n - 1, f); public static int G(int[] a, int k) => Rec(a.Length, x => x * k);", false)
      .SetName("Escape_Recursion_IsNotSpecialised");
  }

  /// <summary>Test design 2: specialised twice, not specialised four times; the outcomes are those of .NET either way.</summary>
  [TestCaseSource(nameof(Escapes))]
  public void Escape(string methods, bool specialised) {
    var module = Legalized(methods);
    var (result, _) = Specialised(module);
    Assert.That(HasLambda(result, "G"), Is.EqualTo(!specialised));
    Assert.That(result.Clones.Count > 0, Is.EqualTo(specialised));
    SameOutcomes(methods, module, Optimized(module).Module, "G", Arrays(3));
  }

  [Test]
  public void Escape_ComparedWithNull_IsNotSpecialised() {
    const string methods = "static int Maybe(int[] a, Func<int, int> f) => f == null ? 0 : f(a.Length); public static int G(int[] a, int k) => Maybe(a, x => x * k);";
    var module = Legalized(methods);
    Assert.That(HasLambda(Specialised(module).Module, "G"), Is.True);
    SameOutcomes(methods, module, Optimized(module).Module, "G", Arrays(3));
  }

  [Test]
  public void Escape_AnIteratorKeepsItsFunction() {
    const string methods = "static System.Collections.Generic.IEnumerable<int> Map(int[] a, Func<int, int> f) { foreach (var x in a) yield return f(x); } public static int G(int[] a, int k) { var q = Map(a, x => x * k); k = 10; return q.Sum(); }";
    var module = Legalized(methods);
    Assert.That(HasLambda(Specialised(module).Module, "G"), Is.True);
    SameOutcomes(methods, module, Optimized(module).Module, "G", Arrays(3));
  }

  // ---------------------------------------------------------------- captures (row 3)

  private static IEnumerable<TestCaseData> Captures() {
    yield return new TestCaseData(Apply + "public static int G(int[] a, int k) => Apply(a, x => x * 2);", new[] { IrCaptureClass.Static }, 0).SetName("Capture_None_IsByValue");
    yield return new TestCaseData(Apply + "public static int G(int[] a, int k) => Apply(a, x => x * k);", new[] { IrCaptureClass.Static }, 0).SetName("Capture_ReadOnly_IsByValue");
    yield return new TestCaseData("static int Count(int[] a, Func<int, bool> p) { int c = 0; foreach (var x in a) if (p(x)) c++; return c; } public static int G(int[] a, int k) { int n = k; int c = Count(a, x => { n++; return x > 0; }); return c * 100 + n; }",
      new[] { IrCaptureClass.Capturing }, 1).SetName("Capture_WrittenInside_IsInACell");
    yield return new TestCaseData("static int Two(int[] a, Func<int, int> f, Action g) { int s = 0; foreach (var x in a) { s += f(x); g(); } return s; } public static int G(int[] a, int k) { int m = k; int s = Two(a, x => x * m, () => m++); return s * 100 + m; }",
      new[] { IrCaptureClass.Capturing, IrCaptureClass.Capturing }, 1).SetName("Capture_WrittenOutsideBetweenCalls_IsInACell");
    yield return new TestCaseData(Apply + "public static int G(int[] a, int k) { int s = Apply(a, x => x * k); k = 5; return s + Apply(a, x => x * k); }", new[] { IrCaptureClass.Static }, 0)
      .SetName("Capture_WrittenByTheFunctionBetweenTwoCalls_IsByValue");
  }

  /// <summary>
  /// Test design 3: what no lambda writes is passed by value, what one writes - itself or another
  /// lambda that runs during the call - lives in a cell the lifted functions share; the results are
  /// those of .NET.
  /// </summary>
  [TestCaseSource(nameof(Captures))]
  public void Capture(string methods, IrCaptureClass[] classes, int cells) {
    var module = Legalized(methods);
    var (result, _) = Specialised(module);
    Assert.That(result.ScopeFunctions.Select(function => function.Class), Is.EqualTo(classes));
    Assert.That(result.ScopeFunctions.Select(function => function.IsLifted), Has.All.True);
    Assert.That(Cells(result), Is.EqualTo(cells));
    var optimized = Optimized(module).Module;
    Assert.That(Cells(optimized), Is.Zero, "every cell is a variable again once the copies are inlined");
    SameOutcomes(methods, module, optimized, "G", Arrays(3));
  }

  /// <summary>A variable a lambda writes that another lambda captures which stays a closure is left as it is, and so is the first lambda.</summary>
  [Test]
  public void Capture_WrittenAndCapturedByAClosureThatStays_IsNotLifted() {
    const string methods = "static int Count(int[] a, Func<int, bool> p) { int c = 0; foreach (var x in a) if (p(x)) c++; return c; } static Func<int> keep; public static int G(int[] a, int k) { int n = 0; keep = () => n; int c = Count(a, x => { n++; return x > 0; }); return c * 100 + keep(); }";
    var module = Legalized(methods);
    var (result, _) = Specialised(module);
    Assert.That(result.Clones, Is.Empty);
    SameOutcomes(methods, module, Optimized(module).Module, "G", Arrays(3));
  }

  /// <summary>A parameter a lambda writes has no declaration to make a cell of: the lambda stays.</summary>
  [Test]
  public void Capture_AWrittenParameter_IsNotLifted() {
    const string methods = "static int Count(int[] a, Func<int, bool> p) { int c = 0; foreach (var x in a) if (p(x)) c++; return c; } public static int G(int[] a, int k) { int c = Count(a, x => { k++; return x > 0; }); return c * 100 + k; }";
    var module = Legalized(methods);
    Assert.That(Specialised(module).Module.Clones, Is.Empty);
    SameOutcomes(methods, module, Optimized(module).Module, "G", Arrays(3));
  }

  // ---------------------------------------------------------------- budget (row 4)

  private static string Distinct(int count)
    => Apply + $"public static int G(int[] a) => {string.Join(" + ", Enumerable.Range(1, count).Select(index => $"Apply(a, x => x + {index})"))};";

  /// <summary>Test design 4: a callee called with 1, the limit and one more distinct lambdas gets 1, the limit and the limit copies, and the call over it a note.</summary>
  [TestCase(1, 1, false)]
  [TestCase(O0463LambdaSpecialisation.CloneLimit, O0463LambdaSpecialisation.CloneLimit, false)]
  [TestCase(O0463LambdaSpecialisation.CloneLimit + 1, O0463LambdaSpecialisation.CloneLimit, true)]
  public void Budget_BoundsTheCopiesOfAFunction(int lambdas, int clones, bool noted) {
    var module = Legalized(Distinct(lambdas));
    var (result, notes) = Specialised(module);
    Assert.That(result.Clones.Count(clone => clone.Origin == "Apply"), Is.EqualTo(clones));
    Assert.That(notes, noted ? Has.One.Contains("'G' passes its function values to 'Apply' as they are: 8 copies of 'Apply' are the most") : Is.Empty);
    SameOutcomes(Distinct(lambdas), module, Optimized(module).Module, "G", [[new[] { 1, 2 }]]);
  }

  /// <summary>Equal lambdas passed to one callee share one copy, and one lifted function.</summary>
  [Test]
  public void Budget_EqualLambdasShareACopy() {
    var methods = Apply + "public static int G(int[] a, int k) => Apply(a, x => x * k) + Apply(a, x => x * k);";
    var (result, _) = Specialised(Legalized(methods));
    Assert.That((result.Clones.Count, result.ScopeFunctions.Count), Is.EqualTo((1, 1)));
  }

  /// <summary>The growth: a copy of exactly the size left is made, one that is one larger is not.</summary>
  [TestCase(0, 1)]
  [TestCase(1, 0)]
  public void Budget_BoundsTheGrowth(int over, int clones) {
    var module = Legalized(Distinct(1));
    var size = IrPassSupportProbe.Size(Function(module, "Apply").Body);
    var (result, notes) = Specialised(module, new O0463LambdaSpecialisation(O0463LambdaSpecialisation.CloneLimit, size - over));
    Assert.That(result.Clones, Has.Count.EqualTo(clones));
    Assert.That(notes, Has.Count.EqualTo(1 - clones));
  }

  // ---------------------------------------------------------------- traps (row 5)

  private static IEnumerable<TestCaseData> Traps() {
    yield return new TestCaseData("try { return a.Where(x => { n++; return x > 0; }).Sum(); } catch (OverflowException) { return -n; }", new[] { int.MaxValue, 4, 5 }).SetName("Trap_CheckedOverflow");
    yield return new TestCaseData("try { return a.Where(x => { n++; return 10 / x > 1; }).Count(); } catch (DivideByZeroException) { return -n; }", new[] { 3, 2, 0, 1 }).SetName("Trap_DivisionByZero");
    yield return new TestCaseData("int[] t = { 1, 2, 3 }; try { return a.Where(x => { n++; return t[x] > 1; }).Count(); } catch (IndexOutOfRangeException) { return -n; }", new[] { 1, 2, 3, 0 }).SetName("Trap_IndexOutOfRange");
  }

  /// <summary>Test design 5: a trap inside a lambda stays the same exception at the same element, the calls before it counted.</summary>
  [TestCaseSource(nameof(Traps))]
  public void Trap(string body, int[] trapping) {
    var methods = $"public static int F(int[] a) {{ int n = 0; {body} }}";
    var module = Legalized(methods);
    var optimized = Optimized(module);
    Assert.That(optimized.Applications.Select(application => application.Id), Does.Contain("O0463"));
    Assert.That(HasLambda(optimized.Module, "F"), Is.False);
    SameOutcomes(methods, module, optimized.Module, "F", [[trapping], [new[] { 1, 2 }], [Array.Empty<int>()]]);
  }

  /// <summary>A trap the interpreter has no catch for propagates out of the copy as it does out of the call.</summary>
  [Test]
  public void Trap_Uncaught() {
    const string methods = "public static int F(int[] a) => a.Select(x => x).Where(x => x > 3).Sum();";
    var module = Legalized(methods);
    SameOutcomes(methods, module, Optimized(module).Module, "F", [[new[] { int.MaxValue, 4 }], [new[] { 1, 5, 7 }]]);
  }

  // ---------------------------------------------------------------- switches (row 6)

  /// <summary>Test design 6: with the pass disabled, and at <c>--opt none</c>, it never fires.</summary>
  [Test]
  public void Switch_DisabledOrNone_NeverFires() {
    var module = Legalized(Apply + "public static int G(int[] a, int k) => Apply(a, x => x * k);");
    var disabled = Optimized(module, "O0463");
    Assert.That(disabled.Applications.Select(application => application.Id), Has.None.EqualTo("O0463"));
    Assert.That(HasLambda(disabled.Module, "G"), Is.True);
    var none = IrPassPipeline.Default.Run(module, new OptimizerOptions { Level = OptimizationLevel.None });
    Assert.That((none.Module, none.Applications.Count), Is.EqualTo((module, 0)));
  }

  /// <summary>The pass runs before inlining, and neither runs in the last round.</summary>
  [Test]
  public void Pipeline_SpecialisesFirst() => Assert.That(IrPassPipeline.Default.Ids.First(), Is.EqualTo("O0463"));

  // ---------------------------------------------------------------- the forms that stay (scope functions)

  /// <summary>A local that holds a lambda capturing nothing it writes is lifted with what it reads as parameters; the local goes.</summary>
  [Test]
  public void Held_StaticLambda_IsLiftedAndItsLocalGoes() {
    const string methods = "public static int H(int a, int k) { Func<int, int> sq = x => x * x + k; return sq(a) + sq(a + 1); }";
    var module = Legalized(methods);
    var (result, _) = Specialised(module);
    Assert.That(result.ScopeFunctions, Is.EqualTo(new[] { new IrScopeFunction("H", "H_sq", IrCaptureClass.Static, true) }));
    Assert.That(Function(result, "H").Locals.Select(local => local.Name), Has.None.EqualTo("sq"));
    Assert.That(Function(result, "H_sq").Parameters.Select(parameter => parameter.Name), Is.EqualTo(new[] { "x", "k" }));
    SameOutcomes(methods, module, Optimized(module).Module, "H", [[2, 3], [-1, 0]]);
  }

  /// <summary>A local that holds a lambda writing what it captures stays a closure of its function, classified as capturing.</summary>
  [Test]
  public void Held_CapturingLambda_StaysAClosure() {
    const string methods = "public static int W(int k) { int n = k; Action inc = () => { n++; }; inc(); inc(); return n; }";
    var module = Legalized(methods);
    var (result, _) = Specialised(module);
    Assert.That(result.ScopeFunctions, Is.EqualTo(new[] { new IrScopeFunction("W", "inc", IrCaptureClass.Capturing, false) }));
    Assert.That(HasLambda(result, "W"), Is.True);
    Assert.That(Specialised(result).Module.ScopeFunctions, Has.Count.EqualTo(1), "classified once");
    SameOutcomes(methods, module, Optimized(module).Module, "W", [[0], [5]]);
  }

  /// <summary>A local that holds a lambda and leaves the function - returned here - is no function of the scope.</summary>
  [Test]
  public void Held_EscapingLambda_IsLeftAlone() {
    const string methods = "public static Func<int, int> E(int k) { Func<int, int> f = x => x + k; return f; } public static int R(int k) => E(k)(2);";
    var module = Legalized(methods);
    var (result, _) = Specialised(module);
    Assert.That(result.ScopeFunctions, Is.Empty);
    SameOutcomes(methods, module, Optimized(module).Module, "R", [[1], [7]]);
  }

  /// <summary>A lambda called where it stands is lifted, a method group passed to a parameter that keeps it binds the function itself.</summary>
  [Test]
  public void Invoked_AndMethodGroup_AreBound() {
    const string methods = Apply + "static int Twice(int x) => x * 2; public static int G(int[] a, int k) => ((Func<int, int>)(x => x + k))(a.Length) + Apply(a, Twice);";
    var module = Legalized(methods);
    var (result, _) = Specialised(module);
    Assert.That(HasLambda(result, "G"), Is.False);
    Assert.That(IrTree.Nodes(Function(result, "G").Body).OfType<IrFunctionRef>(), Is.Empty);
    Assert.That(result.Clones.Single().Key, Is.EqualTo("1=Twice"));
    SameOutcomes(methods, module, Optimized(module).Module, "G", Arrays(3));
  }

  /// <summary>A function the copies replaced at every call goes when it is private; a public one stays.</summary>
  [Test]
  public void ReplacedCallee_GoesWhenPrivate() {
    var (result, _) = Specialised(Legalized(Apply + "public static int G(int[] a, int k) => Apply(a, x => x * k);"));
    Assert.That(result.Functions.Select(function => function.Name), Has.None.EqualTo("Apply"));
    var (kept, _) = Specialised(Legalized(Apply.Replace("static int Apply", "public static int Apply") + "public static int G(int[] a, int k) => Apply(a, x => x * k);"));
    Assert.That(kept.Functions.Select(function => function.Name), Has.One.EqualTo("Apply"));
  }

  // ---------------------------------------------------------------- the LINQ fixtures (rows 1 and 7)

  private static IEnumerable<TestCaseData> FixtureCalls()
    => LinqFixtures.CSharpFixtures.SelectMany(fixture => LinqFixtures.Calls(fixture).Select(call => new TestCaseData(fixture, call).SetName($"Fixture_{fixture}.{call}")));

  private static readonly Dictionary<string, IrModule> _optimized = [];

  private static IrModule OptimizedFixture(string fixture) {
    lock (_optimized) {
      if (!_optimized.TryGetValue(fixture, out var module))
        _optimized[fixture] = module = Optimized(CSharpEmitter.Legalize(LinqFixtures.Imported(fixture), () => PrecomputedImplementations.Module, CSharpProfile.All[^1], FloatMode.Accurate)).Module;

      return module;
    }
  }

  /// <summary>Test design 1 and 7: the optimised module of every LINQ fixture computes what .NET computes, call by call, the traces of the lambdas included.</summary>
  [TestCaseSource(nameof(FixtureCalls))]
  public void Fixture_ComputesWhatDotNetComputes(string fixture, PrototypeCall call)
    => Assert.That(PrototypeFixture.InterpretedOutcome(OptimizedFixture(fixture), call), Is.EqualTo(PrototypeFixture.InvokedOutcome(LinqFixtures.Compiled(fixture, call.Method), call)));

  private static IEnumerable<TestCaseData> ScriptCalls()
    => LinqFixtures.ScriptFixtures.SelectMany(file => LinqFixtures.ScriptCalls(file).Select(call => new TestCaseData(file, call).SetName($"Script_{file}.{call}")));

  /// <summary>Test design 7: the optimised module of every JavaScript fixture computes what the module before the passes computes.</summary>
  [TestCaseSource(nameof(ScriptCalls))]
  public void Script_ComputesWhatTheUnoptimisedModuleComputes(string file, PrototypeCall call) {
    var legalized = JavaScriptEmitter.Legalize(LinqFixtures.ImportedScript(file), () => PrecomputedImplementations.Module, JavaScriptProfile.Parse("es2022"), FloatMode.Accurate);
    Assert.That(PrototypeFixture.InterpretedOutcome(Optimized(legalized).Module, call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(legalized, call)));
  }

}
