using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Prototype;

/// <summary>The registry of verifier rules, the check of the functions a pass changed, and the rule on locals read before they hold a value.</summary>
[TestFixture]
public sealed class VerifierRuleTests {

  private const string Assigned = "ir.verifier.locals.assigned-before-read";

  private static readonly IrLocal _x = new("x", IrType.I32);
  private static readonly IrLocal _y = new("y", IrType.I32);
  private static readonly IrLocal _flag = new("flag", IrType.Bool);
  private static readonly IrExpr _one = new IrConst(IrType.I32, 1);

  private static IrExpr X => new IrLocalRef(_x);

  private static IrExpr Flag => new IrLocalRef(_flag);

  private static IrBlock B(params IrStmt[] statements) => new(statements);

  private static IrFunction Function(string name, params IrStmt[] body)
    => new(name, true, IrType.I32, [_flag], [_x, _y], new IrBlock(body));

  private static IrModule Module(params IrStmt[] body) => new("M", [Function("F", body)]);

  /// <summary>The diagnostics of the rule on locals for a body of <c>F(flag)</c>, which declares <c>x</c> and <c>y</c>.</summary>
  private static List<Diagnostic> Unassigned(params IrStmt[] body)
    => [.. IrVerifier.Verify(Module(body)).Where(diagnostic => diagnostic.Message.Contains(Assigned))];

  private sealed class CountingRule(string id) : IrNodeRule<IrBinary> {

    public override string Id => id;

    public override string Family => "test";

    public override string Description => "reports every binary operation";

    protected override void Check(VerifierRuleContext context, IrBinary node) => context.Report(node, $"{node.Op} in {context.Module.Name}");

  }

  private sealed class BareRule(string id) : IVerifierRule {

    public string Id => id;

    public string Family => "test";

    public string Description => "reports nothing";

    public void Check(VerifierRuleContext context) { }

  }

  [Test]
  public void TheStandardRegistryHoldsTheRuleOnLocalsAndIsFrozen() {
    var standard = VerifierRuleRegistry.Standard;
    Assert.That(standard.IsFrozen, Is.True);
    Assert.That(standard.Rules.Select(rule => rule.Id), Is.EqualTo(new[] { Assigned }));
    Assert.That(standard.Find(Assigned)!.Family, Is.EqualTo("locals"));
    Assert.That(standard.Find(Assigned)!.Description, Does.Contain("no path"));
    Assert.That(() => standard.Register(new BareRule("more")), Throws.InvalidOperationException);
  }

  [Test]
  public void ARegistryTakesNewRulesInOrderAndRefusesBadOnes() {
    var rules = VerifierRuleRegistry.CreateStandard();
    Assert.That(rules.IsFrozen, Is.False);
    Assert.That(rules.Register(new BareRule("test.a")).Register(new BareRule("test.b")), Is.SameAs(rules));
    Assert.That(rules.Rules.Select(rule => rule.Id), Is.EqualTo(new[] { Assigned, "test.a", "test.b" }));
    Assert.That(rules.Find("test.c"), Is.Null);
    Assert.That(() => rules.Register(new BareRule("test.a")), Throws.ArgumentException.With.Message.Contains("test.a"));
    Assert.That(() => rules.Register(new BareRule(" ")), Throws.ArgumentException);
    Assert.That(() => rules.Register(null!), Throws.ArgumentNullException);
    Assert.That(rules.Freeze().IsFrozen, Is.True);
  }

  [Test]
  public void ANodeRuleRunsOnEveryNodeOfItsTypeLambdasIncludedAndNamesFunctionNodeAndRule() {
    var lambda = IrLambda.Of([], IrType.I32, [], B(new IrReturn(new IrBinary(IrBinaryOp.Multiply, _one, _one))));
    var module = Module(
      new IrExprStmt(lambda),
      new IrReturn(new IrBinary(IrBinaryOp.Add, _one, _one)));
    var rules = VerifierRuleRegistry.CreateStandard().Register(new CountingRule("test.binary"));

    var diagnostics = IrVerifier.Verify(module, rules).Select(diagnostic => diagnostic.Message).ToList();

    Assert.That(diagnostics, Is.EqualTo(new[] {
      "ill-typed IR in 'F': IrBinary: Multiply in M [test.binary]",
      "ill-typed IR in 'F': IrBinary: Add in M [test.binary]"
    }));
    Assert.That(IrVerifier.Verify(module, rules).Select(diagnostic => diagnostic.Code), Is.All.EqualTo(IrVerifier.Code));
    Assert.That(IrVerifier.Verify(module, rules).Select(diagnostic => diagnostic.Message), Is.EqualTo(diagnostics), "the same module gives the same diagnostics in the same order");
  }

  private sealed class StatementRule : IrNodeRule<object> {

    public override string Id => "test.statements";

    public override string Family => "test";

    public override string Description => "reports every node";

    protected override void Check(VerifierRuleContext context, object node) => context.Report(node, "seen");

  }

  [Test]
  public void ADiagnosticNamesWhatANodeNames() {
    var module = new IrModule("M", [
      Function("F", new IrDeclare(_x, _one), new IrAssign(_x, new IrCall("G", IrType.I32, [Flag])), new IrReturn(X)),
      Function("G", new IrReturn(_one))
    ]);
    var rules = new VerifierRuleRegistry().Register(new StatementRule()).Register(new CountingRule("test.binary"));

    var messages = IrVerifier.Verify(module, ["F"], rules).Select(diagnostic => diagnostic.Message).ToList();

    Assert.That(messages, Is.EqualTo(new[] {
      "ill-typed IR in 'F': IrBlock: seen [test.statements]",
      "ill-typed IR in 'F': IrDeclare 'x': seen [test.statements]",
      "ill-typed IR in 'F': IrConst: seen [test.statements]",
      "ill-typed IR in 'F': IrAssign 'x': seen [test.statements]",
      "ill-typed IR in 'F': IrCall 'G': seen [test.statements]",
      "ill-typed IR in 'F': IrLocalRef 'flag': seen [test.statements]",
      "ill-typed IR in 'F': IrReturn: seen [test.statements]",
      "ill-typed IR in 'F': IrLocalRef 'x': seen [test.statements]"
    }));
  }

  [Test]
  public void ARuleIsNotGivenANullContextOrANullNode() {
    var rule = new CountingRule("test.binary");
    Assert.That(() => rule.Check(null!), Throws.ArgumentNullException);
    var rules = new VerifierRuleRegistry().Register(new NullReporter());
    Assert.That(() => IrVerifier.Verify(Module(new IrReturn(_one)), rules), Throws.ArgumentNullException);
    Assert.That(() => IrVerifier.Verify(null!, rules), Throws.ArgumentNullException);
    Assert.That(() => IrVerifier.Verify(Module(), (VerifierRuleRegistry)null!), Throws.ArgumentNullException);
  }

  private sealed class NullReporter : IVerifierRule {

    public string Id => "test.null";

    public string Family => "test";

    public string Description => "reports at no node";

    public void Check(VerifierRuleContext context) => context.Report(null!, "nothing");

  }

  [Test]
  public void TheCheckOfChangedFunctionsVisitsOnlyThemAndLeavesTheDeclarationsToTheModule() {
    var broken = Function("Late", new IrReturn(new IrConst(IrType.Bool, 1)));
    var fine = Function("Early", new IrReturn(_one));
    var module = new IrModule("M", [fine, broken], [new IrModuleConst("Late", IrType.I32, [1])]);
    var rules = VerifierRuleRegistry.CreateStandard().Register(new CountingRule("test.binary"));

    var whole = IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message).ToList();
    var changed = IrVerifier.Verify(module, ["Late"]).Select(diagnostic => diagnostic.Message).ToList();

    Assert.That(whole, Has.Some.Contains("declares the name twice"));
    Assert.That(changed, Is.EqualTo(whole.Where(message => message.StartsWith("ill-typed IR in 'Late'") && !message.Contains("declares the name twice"))));
    Assert.That(changed, Is.Not.Empty);
    Assert.That(IrVerifier.Verify(module, ["Early"], rules), Is.Empty);
    Assert.That(IrVerifier.Verify(module, [], rules), Is.Empty);
    Assert.That(() => IrVerifier.Verify(module, ["Missing"]), Throws.ArgumentException.With.Message.Contains("Missing"));
    Assert.That(() => IrVerifier.Verify(module, (IEnumerable<string>)null!), Throws.ArgumentNullException);
    Assert.That(() => IrVerifier.Verify(null!, ["Late"]), Throws.ArgumentNullException);
  }

  [Test]
  public void AReadOfALocalNoPathAssignsIsReportedOnceNamingTheLocal() {
    var diagnostics = Unassigned(
      new IrDeclare(_x, null),
      new IrExprStmt(new IrBinary(IrBinaryOp.Add, X, X)),
      new IrReturn(X));

    Assert.That(diagnostics.Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      $"ill-typed IR in 'F': IrLocalRef 'x': 'x' is read where no path has given it a value [{Assigned}]"
    }));
  }

  [Test]
  public void AReadAfterAnAssignmentOrOfAnInitializedLocalOrAParameterIsAccepted() {
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrAssign(_x, _one), new IrReturn(X)), Is.Empty);
    Assert.That(Unassigned(new IrDeclare(_x, _one), new IrReturn(X)), Is.Empty);
    Assert.That(Unassigned(new IrIf(Flag, B(new IrReturn(_one)), null), new IrReturn(_one)), Is.Empty);
    Assert.That(IrVerifier.Verify(Module(new IrDeclare(_x, null), new IrAssign(_x, _one), new IrReturn(X))), Is.Empty);
  }

  [Test]
  public void ABranchAssignsForWhatFollowsTheIfButNotForTheOtherBranch() {
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrIf(Flag, B(new IrAssign(_x, _one)), null), new IrReturn(X)), Is.Empty);
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrIf(Flag, B(new IrAssign(_x, _one)), B(new IrReturn(X))), new IrReturn(_one)), Has.Count.EqualTo(1));
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrIf(Flag, B(new IrReturn(X)), B(new IrAssign(_x, _one))), new IrReturn(X)), Has.Count.EqualTo(1));
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrIf(Flag, B(), B(new IrAssign(_x, _one))), new IrReturn(X)), Is.Empty);
  }

  [Test]
  public void ALoopTopSeesWhatItsBodyAssignsLater() {
    var later = B(new IrAssign(_y, X), new IrAssign(_x, _one));
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrWhile(Flag, later), new IrReturn(_one)), Is.Empty);
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrDoWhile(later, Flag), new IrReturn(_one)), Is.Empty);
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrFor(Flag, later, B()), new IrReturn(_one)), Is.Empty);
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrWhile(Flag, B(new IrAssign(_y, X))), new IrReturn(_one)), Has.Count.EqualTo(1));
  }

  [Test]
  public void ASwitchSectionMayFollowAnotherAndAHandlerSeesWhatTheTryAssigns() {
    var choice = new IrSwitch(new IrLocalRef(_y), [
      new IrSwitchSection([1], false, B(new IrAssign(_x, _one), new IrBreak())),
      new IrSwitchSection([], true, B(new IrReturn(X)))
    ]);
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrDeclare(_y, _one), choice, new IrReturn(_one)), Is.Empty);

    var caught = new IrLocal("caught", IrType.Object(IrExceptionClass.Exception));
    var attempt = new IrTry(B(new IrAssign(_x, _one)), [
      new IrCatch(IrExceptionClass.Exception, caught, null, B(new IrReturn(X))),
      new IrCatch(IrExceptionClass.Exception, null, null, B(new IrReturn(_one)))
    ], B(new IrAssign(_y, X)));
    Assert.That(Unassigned(new IrDeclare(_x, null), attempt, new IrReturn(X)), Is.Empty);

    var unread = new IrTry(B(new IrAssign(_y, X)), [], null);
    Assert.That(Unassigned(new IrDeclare(_x, null), unread, new IrReturn(_one)), Has.Count.EqualTo(1));
  }

  [Test]
  public void ALambdaIsCheckedAgainstItsOwnDeclarationsAndMayAssignWhatItCaptures() {
    var inner = new IrLocal("inner", IrType.I32);
    var reading = IrLambda.Of([], IrType.I32, [], B(new IrReturn(X)));
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrExprStmt(reading), new IrReturn(_one)), Is.Empty, "the lambda may run after x is given a value");

    var own = IrLambda.Of([], IrType.I32, [inner], B(new IrDeclare(inner, null), new IrReturn(new IrLocalRef(inner))));
    Assert.That(Unassigned(new IrExprStmt(own), new IrReturn(_one)).Single().Message, Does.Contain("'inner'"));

    var writing = IrLambda.Of([], IrType.Void, [], B(new IrAssign(_x, _one)));
    Assert.That(Unassigned(new IrDeclare(_x, null), new IrExprStmt(writing), new IrReturn(X)), Is.Empty);
  }

  [Test]
  public void PlacesBindingsAndScopesGiveValues() {
    var field = new IrField("f", IrType.I32, false);
    var point = new IrLocal("p", IrType.I32);
    var points = new IrLocal("ps", IrType.Array(IrType.I32));
    var pointRef = new IrLocalRef(point);
    Assert.That(Unassigned(
      new IrDeclare(point, null),
      new IrFieldStore(new IrFieldLoad(pointRef, field), field, _one),
      new IrReturn(pointRef)), Is.Empty, "a field store gives the variable its value without reading it");

    Assert.That(Unassigned(
      new IrDeclare(points, null),
      new IrFieldStore(new IrArrayLoad(new IrLocalRef(points), _one), field, _one),
      new IrReturn(_one)), Has.Count.EqualTo(1), "a store into an element reads the array");

    Assert.That(Unassigned(
      new IrDeclare(_x, null),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(IrType.I32), [X])),
      new IrReturn(X)), Is.Empty, "taking the address reads nothing and lets the pointer give the value");

    Assert.That(Unassigned(
      new IrDeclare(_x, null),
      new IrExprStmt(new IrBind(_x, _one)),
      new IrReturn(X)), Is.Empty);

    Assert.That(Unassigned(
      new IrDeclare(_x, null),
      new IrExprStmt(new IrStep(X, true, false, false)),
      new IrReturn(X)), Has.Count.EqualTo(1), "a step reads the variable it steps");

    var map = new IrLocal("map", IrType.Map(IrType.I32, IrType.I32, MapOrder.Insertion));
    Assert.That(Unassigned(
      new IrDeclare(_x, null),
      new IrDeclare(_y, null),
      new IrForEachCollection(new IrLocalRef(map), EnumerationMode.Checked, _x, _y, B(new IrReturn(new IrBinary(IrBinaryOp.Add, X, new IrLocalRef(_y))))),
      new IrForEachCollection(new IrLocalRef(map), EnumerationMode.Checked, null, null, B()),
      new IrReturn(_one)), Is.Empty);

    Assert.That(Unassigned(
      new IrDeclare(_x, null),
      new IrForEachSequence(_x, new IrArraySequence(new IrLocalRef(points)), B(new IrAssign(_y, X))),
      new IrReturn(_one)), Is.Empty);
  }

}
