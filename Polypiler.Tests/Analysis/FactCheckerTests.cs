using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Checking;
using Polypiler.Core.Analysis.Constant;
using Polypiler.Core.IR;
using static Polypiler.Tests.Analysis.AnalysisIr;

namespace Polypiler.Tests.Analysis;

/// <summary>
/// The soundness checker of the facade (#88) and the constant analysis it checks: the checker refutes
/// a wrong fact by running the function, and the constant analysis survives it on real C# and on
/// seeded random functions.
/// </summary>
[TestFixture]
public sealed class FactCheckerTests {

  /// <summary>Asks the constant fact of every variable at every point where it is defined.</summary>
  private static ValueFacts AskEverything(IrFunction function, IFactProvider<ConstantFact>? provider = null) {
    var facts = new ValueFacts(function).Add(provider ?? new ConstantAnalysis(function));
    var locals = function.Parameters.Concat(function.Locals).ToList();
    foreach (var block in Blocks(function))
      for (var index = 0; index <= block.Statements.Count; ++index) {
        var point = new ProgramPoint(block, index);
        foreach (var local in locals.Where(local => facts.Layout.Defined(local, point)))
          facts.Get<ConstantFact>(local, point);
      }

    return facts;
  }

  private static object[][] Inputs(IrFunction function, int seed, int count) {
    var random = new Random(seed);
    return [.. Enumerable.Range(0, count).Select(_ => function.Parameters.Select(parameter => (object)(random.Next(3) == 0 ? (uint)random.Next() : (uint)random.Next(0, 8))).ToArray())];
  }

  /// <summary>Test design 11: a deliberately wrong provider is caught with the value, the claimed fact and the observation.</summary>
  [Test]
  public void TheChecker_CatchesAnUnsoundProvider() {
    var function = Function([X], new IrDeclare(A, Op(IrBinaryOp.Add, R(X), K(1))), new IrReturn(R(A)));
    var facts = new ValueFacts(function).Add(new ValueFactsTests.FixedProvider(_ => ConstantFact.Of(4)));
    facts.Get<ConstantFact>(A, new ProgramPoint(function.Body, 1));
    Assert.That(FactChecker.Run(facts, [[5u]]).Select(diagnostic => diagnostic.ToString()),
      Is.EqualTo(new[] { "error PLY0302: unsound fact: constant claims constant 4 for a at block 0, statement 1, observed 6" }));
    Assert.That(FactChecker.Run(facts, [[3u]]), Is.Empty);
  }

  /// <summary>A fact refuted twice is reported once; a point the analysis calls unreachable that a run reaches is refuted.</summary>
  [Test]
  public void ARefutedFact_IsReportedOnce() {
    var function = Function([X], new IrReturn(R(X)));
    var facts = new ValueFacts(function).Add(new ValueFactsTests.FixedProvider(_ => ConstantFact.Unreachable));
    facts.Get<ConstantFact>(X, ProgramPoint.Entry(function.Body));
    Assert.That(FactChecker.Run(facts, [[1u], [2u]]).Select(diagnostic => diagnostic.Message),
      Is.EqualTo(new[] { "unsound fact: constant claims unreachable for x at block 0, statement 0, observed 1" }));
  }

  /// <summary>A run that traps or runs out of steps is checked up to where it stopped; a value without a value is skipped.</summary>
  [Test]
  public void StoppedRuns_AreCheckedUpToWhereTheyStopped() {
    var forever = Block(new IrAssign(Y, Op(IrBinaryOp.Add, R(Y), K(1))));
    var function = Function([X],
      new IrDeclare(Y, Op(IrBinaryOp.Divide, K(10), R(X))),
      new IrWhile(new IrConst(IrType.Bool, 1), forever),
      new IrReturn(R(Y)));
    var facts = new ValueFacts(function).Add(new ValueFactsTests.FixedProvider(_ => ConstantFact.Of(99)));
    facts.Get<ConstantFact>(X, ProgramPoint.Entry(function.Body));
    facts.Get<ConstantFact>(Y, ProgramPoint.Entry(forever));
    var reported = FactChecker.Run(Module(function), facts, [[0u], [1u]], 2_000).Select(diagnostic => diagnostic.Message).ToList();
    Assert.That(reported, Is.EqualTo(new[] {
      "unsound fact: constant claims constant 99 for x at block 0, statement 0, observed 0",
      "unsound fact: constant claims constant 99 for y at block 1, statement 0, observed 10"
    }));
  }

  /// <summary>Arguments are checked: no facade, no module, no inputs.</summary>
  [Test]
  public void Run_ChecksItsArguments() {
    var facts = new ValueFacts(Function([X], new IrReturn(R(X))));
    Assert.That(() => FactChecker.Run(null!, []), Throws.ArgumentNullException);
    Assert.That(() => FactChecker.Run(null!, facts, []), Throws.ArgumentNullException);
    Assert.That(() => FactChecker.Run(Module(facts.Function), null!, []), Throws.ArgumentNullException);
    Assert.That(() => FactChecker.Run(Module(facts.Function), facts, null!), Throws.ArgumentNullException);
  }

  /// <summary>
  /// Measurement "checker sensitivity": 100 injected wrong facts - each claims a constant for a parameter at the
  /// entry of a random function that a run refutes - are all detected.
  /// </summary>
  [Test]
  public void InjectedWrongFacts_AreAllDetected() {
    var detected = 0;
    for (var seed = 0; seed < 100; ++seed) {
      var function = RandomIr.Generate(seed);
      var wrong = (uint)seed + 1000;
      var facts = new ValueFacts(function).Add(new ValueFactsTests.FixedProvider(_ => ConstantFact.Of(wrong)));
      facts.Get<ConstantFact>(function.Parameters[0], ProgramPoint.Entry(function.Body));
      if (FactChecker.Run(facts, [[(uint)seed, 1u]]).Count == 1)
        ++detected;
    }

    Assert.That(detected, Is.EqualTo(100));
  }

  /// <summary>Test design 13: the same seed gives the same function, which verifies; different seeds differ.</summary>
  [Test]
  public void RandomIr_IsDeterministic() {
    static byte[] Bytes(int seed) => IrModuleFormat.Write(Module(RandomIr.Generate(seed)));
    var (first, second) = (Bytes(13), Bytes(13));
    Assert.That(first, Is.EqualTo(second));
    Assert.That(Bytes(13), Is.Not.EqualTo(Bytes(14)));
    for (var seed = 0; seed < 50; ++seed)
      Assert.That(IrVerifier.Verify(Module(RandomIr.Generate(seed))), Is.Empty, $"seed {seed}");
  }

  /// <summary>Measurement "soundness of the constant domain", the fast share: 25 random functions, 16 inputs each, no violation.</summary>
  [Test]
  public void TheConstantDomain_IsSoundOnRandomFunctions() => AssertSound(seed: 300, functions: 25);

  /// <summary>Measurement "soundness of the constant domain" in full: 10 000 random functions, seed 300, 16 inputs each.</summary>
  [Test, Category("Slow")]
  public void TheConstantDomain_IsSoundOnTenThousandRandomFunctions() => AssertSound(seed: 300, functions: 10_000);

  private static void AssertSound(int seed, int functions) {
    var violations = new List<string>();
    var decided = 0;
    for (var index = 0; index < functions; ++index) {
      var function = RandomIr.Generate(seed + index);
      var facts = AskEverything(function);
      decided += facts.Queries.Count(query => query.Fact.StartsWith("constant", StringComparison.Ordinal));
      violations.AddRange(FactChecker.Run(facts, Inputs(function, seed + index, 16)).Select(diagnostic => $"seed {seed + index}: {diagnostic.Message}"));
    }

    Assert.That(violations, Is.Empty);
    Assert.That(decided, Is.GreaterThan(functions));
  }

  /// <summary>Loops, switches and exception handlers of real C#: every fact the constant analysis gives holds on every run.</summary>
  [TestCase("public static uint Loops(uint n) { uint s = 0; uint k = 3; for (uint i = 0; i < n; i++) { if (i == 2) continue; if (i == 5) break; s += k; } uint j = 0; while (j < 3) { j++; } do { k = 4; } while (false); return s + k + j; }", "Loops")]
  [TestCase("public static int Switch(int v) { int r = 1; switch (v) { case 1: r = 2; break; case 2: return 5; default: r = 3; break; } return r; }", "Switch")]
  [TestCase("public static int NoDefault(int v) { int r = 1; switch (v) { case 1: r = 2; break; } while (v > 100) { switch (v) { case 101: v = 0; continue; } v = 1; } return r; }", "NoDefault")]
  [TestCase("public static int Finally(int v) { int r = 0; while (true) { try { r = 1; if (v > 0) break; r = 2; } finally { r = 3; } break; } return r; }", "Finally")]
  [TestCase("public static int Caught(int v) { int r = 0; try { r = 10 / v; } catch (System.DivideByZeroException) { r = -1; } return r; }", "Caught")]
  [TestCase("public static int Each(int[] a) { int s = 0; int t = 1; foreach (var e in a) { if (e == 3) break; if (e == 4) continue; s += e; } return s + t; }", "Each")]
  [TestCase("public static int Steps(int v) { int a = 0; int b = a++ + v; bool c = v > 2; if (c) { a = 7; } else { a = 7; } if (b++ == 3) { b = 1; } return a + b; }", "Steps")]
  [TestCase("public static int Repeat(int v) { int r = 0; do { r += 1; if (r == 2) continue; if (r > 5) break; } while (r < v); return r; }", "Repeat")]
  public void TheConstantAnalysis_IsSoundOnCSharp(string method, string name) {
    var module = ImportedModule(method);
    var function = module.Functions.Single(candidate => candidate.Name == name);
    var facts = AskEverything(function);
    object[][] inputs = function.Parameters[0].Type.IsArray
      ? [[new[] { 1, 3, 5 }], [new[] { 4, 2 }], [Array.Empty<int>()]]
      : function.Parameters[0].Type == IrType.U32 ? [[0u], [3u], [7u]] : [[0], [1], [2], [3], [7], [101]];
    Assert.That(FactChecker.Run(module, facts, inputs), Is.Empty);
    Assert.That(facts.Queries, Is.Not.Empty);
  }

  /// <summary>What the analysis knows after the constructs it models: the join after a branch and after a loop.</summary>
  [Test]
  public void TheConstantAnalysis_KnowsWhatHoldsAfterEachConstruct() {
    var function = Imported("public static int Steps(int v) { int a = 0; bool c = v > 2; if (c) { a = 7; } else { a = 7; } int k = 5; for (int i = 0; i < v; i++) { k = 5; } return a + k; }", "Steps");
    var facts = new ValueFacts(function).Add(new ConstantAnalysis(function));
    var end = new ProgramPoint(function.Body, function.Body.Statements.Count - 1);
    var a = function.Locals.Single(local => local.Name == "a");
    var k = function.Locals.Single(local => local.Name == "k");
    Assert.That(facts.Get<ConstantFact>(a, end), Is.EqualTo(ConstantFact.Of(7)));
    Assert.That(facts.Get<ConstantFact>(k, end), Is.EqualTo(ConstantFact.Of(5)));
  }

  /// <summary>A variable a lambda captures or whose address is taken is Unknown, whatever was assigned to it.</summary>
  [Test]
  public void EscapingVariables_AreUnknown() {
    var function = Imported("public static int Captured(int v) { int c = 1; System.Func<int> f = () => c; c = 2; return f(); }", "Captured");
    var c = function.Locals.Single(local => local.Name == "c");
    var facts = new ValueFacts(function).Add(new ConstantAnalysis(function));
    Assert.That(facts.Get<ConstantFact>(c, new ProgramPoint(function.Body, function.Body.Statements.Count - 1)), Is.EqualTo(ConstantFact.Unknown));

    var address = new IrLocal("p", IrType.Pointer(IrType.U32));
    var taken = Function([X], new IrDeclare(Y, K(1)), new IrDeclare(address, new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.U32), [R(Y)])), new IrReturn(R(Y)));
    Assert.That(new ConstantAnalysis(taken).At(R(Y), new ProgramPoint(taken.Body, 2)), Is.EqualTo(ConstantFact.Unknown));
  }

  /// <summary>Folding goes through the interpreter: a trap folds to nothing, a non-integer is not the domain's, a short circuit skips its right side.</summary>
  [Test]
  public void Folding_FollowsTheInterpreter() {
    var f = new IrLocal("d", IrType.F64);
    var t = new IrLocal("t", IrType.Bool);
    var u = new IrLocal("u", IrType.U64);
    var function = Function([X, f],
      new IrDeclare(A, Op(IrBinaryOp.Divide, K(1), K(0))),
      new IrDeclare(B, new IrConditional(Op(IrBinaryOp.LogicalOr, new IrConst(IrType.Bool, 1), Op(IrBinaryOp.Equal, R(X), K(0))), K(4), K(5))),
      new IrDeclare(t, Op(IrBinaryOp.LogicalAnd, new IrConst(IrType.Bool, 1), Op(IrBinaryOp.Equal, K(2), K(2)))),
      new IrDeclare(u, Op(IrBinaryOp.Subtract, new IrConst(IrType.U64, 0), new IrConst(IrType.U64, 1))),
      new IrDeclare(Y, new IrConditional(Op(IrBinaryOp.Equal, R(X), K(0)), K(4), K(5))),
      new IrDeclare(C, Op(IrBinaryOp.Equal, R(f), new IrConst(IrType.F64, 0))),
      new IrReturn(R(B)));
    var analysis = new ConstantAnalysis(function);
    var end = new ProgramPoint(function.Body, 6);
    Assert.That(analysis.At(R(A), end), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(analysis.At(R(B), end), Is.EqualTo(ConstantFact.Of(4)));
    Assert.That(analysis.At(R(t), end), Is.EqualTo(ConstantFact.Of(1)));
    Assert.That(analysis.At(R(u), end), Is.EqualTo(ConstantFact.Of(-1)));
    Assert.That(analysis.At(R(Y), end), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(analysis.At(R(C), end), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(analysis.At(new IrConst(IrType.F64, 0), end), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(analysis.At(new IrStrConst("s"), end), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(analysis.At(Op(IrBinaryOp.LogicalAnd, Op(IrBinaryOp.Equal, R(X), K(0)), new IrConst(IrType.Bool, 1)), end), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(() => analysis.StateAt(ProgramPoint.Entry(Block())), Throws.ArgumentException);
  }

  /// <summary>The variables an expression and a statement may write.</summary>
  [Test]
  public void Writes_AreFoundInEveryPart() {
    var step = new IrStep(R(X), true, false, false);
    Assert.That(ConstantAnalysis.Writes(Op(IrBinaryOp.Add, step, new IrBind(Y, K(1)))), Is.EqualTo(new[] { X, Y }));
    Assert.That(ConstantAnalysis.Writes(new IrStep(new IrFieldLoad(R(X), new IrField("f", IrType.U32, false)), true, false, false)), Is.Empty);
    var resource = new IrLocal("r", IrType.String);
    var element = new IrLocal("e", IrType.I32);
    var caught = new IrLocal("ex", IrType.String);
    var list = new IrLocal("list", IrType.List(IrType.I32));
    var statement = Block(
      new IrForEachCollection(R(list), EnumerationMode.Checked, element, null, Block(new IrAssign(A, K(1)))),
      new IrForEachSequence(element, R(list), Block()),
      new IrUsing(resource, new IrNull(IrType.String), new IrMethod("Dispose", "Dispose", false), Block()),
      new IrTry(Block(new IrExprStmt(step)), [new IrCatch(IrExceptionClass.Exception, caught, null, Block())], null));
    Assert.That(ConstantAnalysis.Assigned(statement), Is.EqualTo(new[] { element, A, element, resource, caught, X }));
  }

}
