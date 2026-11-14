using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Core.Recognize.Rules;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Recognize;

/// <summary>The pass pipeline: levels, disabled rules, the pass limit, verification after each application, the rule table and positions.</summary>
[TestFixture]
public sealed class OptimizerTests {

  private static IrModule Snippet(string methods) => PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods));

  /// <summary>A test double: wraps every <c>i32</c> expression in a conversion to <c>i32</c>, which always applies and always changes something.</summary>
  private sealed class Wrapping() : IrRule("O9001", "wrap every int") {

    public override bool TryApply(IrExpr expression, RuleContext context, [NotNullWhen(true)] out IrExpr? replacement) {
      replacement = expression.Type == IrType.I32 ? new IrConvert(IrType.I32, expression) : null;
      return replacement is not null;
    }

  }

  /// <summary>A rule that breaks the module: it reads a local that is not in scope.</summary>
  private sealed class Breaking() : IrRule("O9002", "read a stranger") {

    public override bool TryApply(IrExpr expression, RuleContext context, [NotNullWhen(true)] out IrExpr? replacement) {
      replacement = expression is IrLocalRef { Type.Kind: IrTypeKind.I32 } ? new IrLocalRef(new IrLocal("stranger", IrType.I32)) : null;
      return replacement is not null;
    }

  }

  /// <summary>A rule that changes the type of what it replaces.</summary>
  private sealed class Retyping() : IrRule("O9003", "change a type") {

    public override bool TryApply(IrExpr expression, RuleContext context, [NotNullWhen(true)] out IrExpr? replacement) {
      replacement = expression is IrLocalRef { Type.Kind: IrTypeKind.I32 } local ? new IrConvert(IrType.I64, local) : null;
      return replacement is not null;
    }

  }

  /// <summary>A statement rule that drops every second statement of the form <c>x = x</c>.</summary>
  private sealed class Dropping() : IrRule("O9004", "drop a self-assignment") {

    public override bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
      rewrite = statements[index] is IrAssign { Value: IrLocalRef reference } assign && reference.Local == assign.Local ? new(1, []) : null;
      return rewrite is not null;
    }

  }

  [Test]
  public void LevelNone_LeavesTheModuleAlone() {
    var module = Snippet("public static uint F(uint x) => (x << 8) | (x >> 24);");
    var result = Optimizer.Run(module, new OptimizerOptions { Level = OptimizationLevel.None });
    Assert.That(result.Module, Is.SameAs(module));
    Assert.That(result.Applications, Is.Empty);
  }

  [Test]
  public void DefaultLevel_RunsTheRulesAndNamesWhereTheyApplied() {
    var module = Snippet("public static uint F(uint x) { uint y = x; if (y > 0) { y = (y << 8) | (y >> 24); } return y; }");
    var result = Optimizer.Run(module, new OptimizerOptions());
    Assert.That(result.Applications, Is.EqualTo(new[] { new RuleApplication("O0100", "rotate from a shift pair", "F", "2.1") }));
    Assert.That(result.Applications[0].ToString(), Is.EqualTo("O0100 rotate from a shift pair: 'F' statement 2.1"));
    Assert.That(IrText.Of(result.Module), Does.Contain("RotateLeft<u32>(y, 8:i32)"));
  }

  [Test]
  public void DisabledRule_DoesNotRun() {
    var module = Snippet("public static uint F(uint x) => (x << 8) | (x >> 24);");
    var result = Optimizer.Run(module, new OptimizerOptions { Disabled = new HashSet<string> { "O0100" } });
    Assert.That(result.Applications, Is.Empty);
    Assert.That(IrText.Of(result.Module), Is.EqualTo(IrText.Of(module)));
  }

  /// <summary>Test design 10: a rule that always applies stops at the pass limit, and what it leaves is well-typed.</summary>
  [TestCase(1)]
  [TestCase(3)]
  [TestCase(8)]
  public void RuleThatAlwaysApplies_StopsAtThePassLimit(int limit) {
    var module = Snippet("public static int F(int x) => x;");
    var options = new OptimizerOptions { Rules = new RuleTable([new Wrapping()]), PassLimit = limit, Verify = true };
    var result = Optimizer.Run(module, options);
    Assert.That(result.Applications, Has.Count.EqualTo(limit));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    Assert.That(IrInterpreter.Call(result.Module, "F", [7]), Is.EqualTo(7));
  }

  [Test]
  public void PassLimitOfZero_RunsNothing()
    => Assert.That(Optimizer.Run(Snippet("public static int F(int x) => x;"), new OptimizerOptions { Rules = new RuleTable([new Wrapping()]), PassLimit = 0 }).Applications, Is.Empty);

  [Test]
  public void Verification_CatchesARuleThatBreaksTheModule() {
    var module = Snippet("public static int F(int x) => x;");
    var rules = new RuleTable([new Breaking()]);
    Assert.That(() => Optimizer.Run(module, new OptimizerOptions { Rules = rules, Verify = true }),
      Throws.InvalidOperationException.With.Message.Contains("O9002").And.Message.Contains("stranger"));
    Assert.That(Optimizer.Run(module, new OptimizerOptions { Rules = rules, Verify = false, PassLimit = 1 }).Applications, Has.Count.EqualTo(1));
  }

  [Test]
  public void ReplacementOfAnotherType_IsRefused() {
    var module = Snippet("public static int F(int x) => x;");
    Assert.That(() => Optimizer.Run(module, new OptimizerOptions { Rules = new RuleTable([new Retyping()]), Verify = false }),
      Throws.InvalidOperationException.With.Message.Contains("O9003").And.Message.Contains("i64"));
  }

  [Test]
  public void StatementRule_ReplacesStatementsAndCountsPositions() {
    var module = Snippet("public static int F(int x) { x = x; { x = x; x = x + 1; } return x; }");
    var result = Optimizer.Run(module, new OptimizerOptions { Rules = new RuleTable([new Dropping()]), Verify = true });
    Assert.That(result.Applications.Select(application => application.Position), Is.EqualTo(new[] { "1", "1.1" }));
    Assert.That(IrInterpreter.Call(result.Module, "F", [1]), Is.EqualTo(2));
  }

  /// <summary>Rules apply inside every kind of statement - do, for with a continue, switch, stores, steps, if with else - and the rebuilt function computes the same.</summary>
  [Test]
  public void RulesApplyInsideEveryStatement() {
    var module = Snippet(string.Join(" ",
      "public static uint F(uint x, int k, uint[] a) {",
      "  uint s = 0;",
      "  do { s += (x << 3) | (x >> 29); k--; } while (k > 0);",
      "  for (int i = 0; i < 4; i++) { if (i == 2) continue; s ^= (x << 5) | (x >> 27); }",
      "  switch (k) { case 0: s += (x << 7) | (x >> 25); break; default: s -= 1; break; }",
      "  a[0] = (x << 9) | (x >> 23);",
      "  if (s > 3) { s = s + 1; } else { s = (s << 1) | (s >> 31); }",
      "  a[0]++;",
      "  return s;",
      "}"));
    var result = Optimizer.Run(module, new OptimizerOptions { Verify = true });
    Assert.That(result.Applications.Count(application => application.Id == "O0100"), Is.EqualTo(5));
    foreach (var (x, k) in new[] { (0x12345678u, 3), (0u, 0), (0xFFFFFFFFu, -1), (0x80000001u, 1) }) {
      var call = new PrototypeCall("F", [x, k, new uint[] { 0 }]);
      Assert.That(PrototypeFixture.InterpretedOutcome(result.Module, call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)));
    }
  }

  [Test]
  public void Optimization_IsDeterministic() {
    var module = PrototypeFixture.Import("XxHash32Spelled.cs", RecognitionFixtures.SourceOf("XxHash32Spelled.cs"));
    var first = Optimizer.Run(module, new OptimizerOptions());
    var second = Optimizer.Run(module, new OptimizerOptions());
    Assert.That(IrText.Of(second.Module), Is.EqualTo(IrText.Of(first.Module)));
    Assert.That(second.Applications, Is.EqualTo(first.Applications));
  }

  [Test]
  public void RuleTable_ListsTheRulesInTheirOrder() {
    Assert.That(RuleTable.Default.Ids, Is.EqualTo(new[] {
      "O0100", "O0101", "O0102", "O0103", "O0105", "O0106", "O0108", "O0109", "O0110", "O0120", "O0121", "O0122", "O0123", "O0125", "O0127", "O0140", "O0141"
    }));
    Assert.That(RuleTable.Default.Find("O0106"), Is.InstanceOf<O0106ByteSwap>());
    Assert.That(RuleTable.Default.Find("O0104"), Is.Null);
    Assert.That(RuleTable.Default.Rules.Select(rule => rule.ToString()), Has.Member("O0100 rotate from a shift pair"));
  }

  [Test]
  public void RuleTable_RefusesANumberTwice()
    => Assert.That(() => new RuleTable([new O0100Rotate(), new O0100Rotate()]), Throws.ArgumentException.With.Message.Contains("O0100"));

  [TestCase("O0100", true)]
  [TestCase("O9999", true)]
  [TestCase("O100", false)]
  [TestCase("o0100", false)]
  [TestCase("O01000", false)]
  [TestCase("O9999x", false)]
  [TestCase("", false)]
  public void Id_IsTheLetterOAndFourDigits(string id, bool valid) => Assert.That(IrRule.IsId(id), Is.EqualTo(valid));

  [Test]
  public void Rule_RefusesAMalformedNumber()
    => Assert.That(() => new MalformedRule(), Throws.ArgumentException.With.Message.Contains("'O1'"));

  private sealed class MalformedRule() : IrRule("O1", "malformed");

  [Test]
  public void BaseRule_AppliesToNothing() {
    var rule = new Wrapping();
    var context = new RuleContext(Snippet("public static int F(int x) => x;").Functions[0], RuleTarget.Neutral);
    Assert.That(rule.TryApply([new IrReturn(null)], 0, context, out var rewrite), Is.False);
    Assert.That(rewrite, Is.Null);
    Assert.That(new Dropping().TryApply(new IrConst(IrType.I32, 1), context, out var replacement), Is.False);
    Assert.That(replacement, Is.Null);
  }

  [Test]
  public void Options_HaveTheirDefaults() {
    var options = new OptimizerOptions();
    Assert.That((options.Level, options.PassLimit, options.Disabled.Count, options.Target, options.Rules),
      Is.EqualTo((OptimizationLevel.Default, OptimizerOptions.DefaultPassLimit, 0, RuleTarget.Neutral, RuleTable.Default)));
    Assert.That(RuleTarget.Neutral.RemainderFromQuotient, Is.False);
  }

}
