using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;

namespace Polypiler.Tests.Optimize;

/// <summary>
/// The de-dynamicization on statements Visual Basic does not lower to, built by hand around the
/// helpers of a Visual Basic module: every kind of statement the walk follows, those it does not
/// follow, which leave what they write dynamic, bindings and lambdas, and unreachable code; each
/// rewritten module verifies and computes what the module it came from computes.
/// </summary>
[TestFixture]
public sealed class DeDynamicizeTreeTests {

  private static readonly IrLocal B = new("b", IrType.Bool);
  private static readonly IrLocal V = new("v", IrType.Variant);
  private static readonly IrLocal W = new("w", IrType.Variant);
  private static readonly IrLocal U = new("u", IrType.Variant);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrExpr Double(double value) => new IrVariantOf(IrVariantTag.F64, new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(value)));

  private static IrExpr Times(IrExpr left, IrExpr right) => new IrCall("PlyVarArith", IrType.Variant, [new IrConst(IrType.I16, 2), left, right]);

  private static IrExpr Plus(IrExpr left, IrExpr right) => new IrCall("PlyVarArith", IrType.Variant, [new IrConst(IrType.I16, 0), left, right]);

  /// <summary>A module of the Visual Basic helpers with one more function, which the test builds.</summary>
  private static IrModule Module(IrFunction function) {
    var result = VisualBasicFrontend.Import("H.bas", "Public Function G(a, b)\nG = a * b + a\nEnd Function\nPublic Function H(ByVal x As Double) As Double\nH = x * x\nEnd Function\n",
      new VisualBasicImportOptions { Inference = VisualBasicInference.Dynamic });
    var module = result.Module! with { Functions = [.. result.Module!.Functions, function] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return module;
  }

  private static IrModule Typed(IrModule module) {
    var typed = DeDynamicize.Run(module, VisualBasicFrontend.Semantics, DeDynamicizeOptions.Default);
    Assert.That(IrVerifier.Verify(typed), Is.Empty);
    return typed;
  }

  private static IrType TypeOf(IrModule module, string local) => module.Functions.Single(function => function.Name == "T").Locals.Single(candidate => candidate.Name == local).Type;

  private static object? Run(IrModule module, params object[] arguments) => IrInterpreter.Call(module, "T", arguments);

  [Test]
  public void EveryLoopAndBranch_IsFollowed() {
    var n = new IrLocal("n", IrType.I32);
    var body = new IrBlock([
      new IrDeclare(V, Double(1)),
      new IrDeclare(n, new IrConst(IrType.I32, 0)),
      new IrDoWhile(new IrBlock([
        new IrAssign(V, Times(R(V), Double(2))),
        new IrIf(R(B), new IrBlock([new IrContinue()]), null),
        new IrAssign(n, new IrBinary(IrBinaryOp.Add, R(n), new IrConst(IrType.I32, 1)))
      ]), new IrBinary(IrBinaryOp.Less, new IrVariantValue(R(V), IrVariantTag.F64), new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(100.0)))),
      new IrWhile(new IrBinary(IrBinaryOp.Less, R(n), new IrConst(IrType.I32, 3)), new IrBlock([
        new IrAssign(n, new IrBinary(IrBinaryOp.Add, R(n), new IrConst(IrType.I32, 1))),
        new IrIf(new IrBinary(IrBinaryOp.LogicalOr, new IrBinary(IrBinaryOp.NotEqual, new IrVariantTagOf(R(V)), new IrConst(IrType.I32, 5)), R(B)),
          new IrBlock([new IrBreak()]), new IrBlock([new IrAssign(V, Plus(R(V), Double(1)))]))
      ])),
      new IrFor(R(B), new IrBlock([new IrBreak()]), new IrBlock([new IrAssign(V, Double(0))])),
      new IrReturn(R(V))
    ]);
    var module = Module(new IrFunction("T", true, IrType.Variant, [B], [V, n], body));
    var typed = Typed(module);
    Assert.That(TypeOf(typed, "v"), Is.EqualTo(IrType.F64));
    foreach (var flag in new[] { true, false })
      Assert.That(Run(typed, flag), Is.EqualTo(Run(module, flag)));
  }

  [Test]
  public void StatementsTheWalkDoesNotFollow_LeaveWhatTheyWriteDynamic() {
    var list = new IrLocal("l", IrType.List(IrType.I32));
    var e = new IrLocal("e", IrType.I32);
    var body = new IrBlock([
      new IrDeclare(V, Double(1)),
      new IrDeclare(W, new IrVariantOf(IrVariantTag.Empty, null)),
      new IrDeclare(list, new IrCollection(IrCollectionOp.ListOf, IrType.List(IrType.I32), [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2)])),
      new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock([
        new IrForEachCollection(R(list), EnumerationMode.Checked, e, null, new IrBlock([
          new IrAssign(W, new IrVariantOf(IrVariantTag.I32, R(e))),
          new IrIf(R(B), new IrBlock([new IrReturn(R(W))]), null),
          new IrBreak()
        ])),
        new IrBreak()
      ])),
      new IrReturn(Plus(R(V), R(W)))
    ]);
    var module = Module(new IrFunction("T", true, IrType.Variant, [B], [V, W, list, e], body));
    var typed = Typed(module);
    Assert.That(TypeOf(typed, "w"), Is.EqualTo(IrType.Variant));
    Assert.That(TypeOf(typed, "v"), Is.EqualTo(IrType.F64));
    foreach (var flag in new[] { true, false })
      Assert.That(Run(typed, flag), Is.EqualTo(Run(module, flag)));
  }

  [Test]
  public void BoundAndCapturedLocals_StayAsTheyAre() {
    var x = new IrLocal("x", IrType.Variant);
    var lambdaType = IrType.Function(IrType.Variant);
    var lambda = new IrLambda(lambdaType, [U], [], [], new IrBlock([new IrReturn(R(U))]));
    var f = new IrLocal("f", lambdaType);
    var body = new IrBlock([
      new IrDeclare(U, Double(2)),
      new IrDeclare(x, new IrVariantOf(IrVariantTag.Empty, null)),
      new IrDeclare(f, lambda),
      new IrExprStmt(new IrBind(x, Double(3))),
      new IrReturn(Plus(new IrInvoke(IrType.Variant, R(f), []), R(x)))
    ]);
    var module = Module(new IrFunction("T", true, IrType.Variant, [B], [U, x, f], body));
    var result = DeDynamicize.Analyse(module, VisualBasicFrontend.Semantics, DeDynamicizeOptions.Default);
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    Assert.That(TypeOf(result.Module, "u"), Is.EqualTo(IrType.Variant));
    Assert.That(TypeOf(result.Module, "x"), Is.EqualTo(IrType.Variant));
    Assert.That(result.Places.Single(place => place.Function == "T" && place.Name == "u").Reason, Does.Contain("captured"));
    Assert.That(Run(result.Module, true), Is.EqualTo(Run(module, true)));
  }

  [Test]
  public void DeadStores_KeepOnlyTheirEffects_AndUnreachableCodeStaysTyped() {
    var body = new IrBlock([
      new IrDeclare(V, new IrVariantOf(IrVariantTag.Empty, null)),
      new IrAssign(V, new IrVariantOf(IrVariantTag.I16, new IrConst(IrType.I16, 7))),
      new IrAssign(V, new IrVariantOf(IrVariantTag.I32, new IrConvert(IrType.I32, new IrCall("H", IrType.F64, [new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(1.0))]), IrConversion.Trap))),
      new IrAssign(V, Double(4)),
      new IrIf(R(B), new IrBlock([new IrReturn(R(V))]), new IrBlock([new IrReturn(Times(R(V), Double(2)))])),
      new IrAssign(V, Double(5)),
      new IrReturn(R(V))
    ]);
    var module = Module(new IrFunction("T", true, IrType.Variant, [B], [V], body));
    var typed = Typed(module);
    Assert.That(TypeOf(typed, "v"), Is.EqualTo(IrType.F64));
    var statements = typed.Functions.Single(function => function.Name == "T").Body.Statements;
    Assert.That(statements[1], Is.EqualTo(new IrBlock([])), "a dead store without effect goes");
    Assert.That(statements[2], Is.InstanceOf<IrExprStmt>(), "a dead store keeps its call");
    foreach (var flag in new[] { true, false })
      Assert.That(Run(typed, flag), Is.EqualTo(Run(module, flag)));
  }

  [Test]
  public void TagTestsThroughAndOrAndNot_RefineBothBranches() {
    IrExpr Tagged(IrLocal local, IrVariantTag tag) => new IrBinary(IrBinaryOp.Equal, new IrVariantTagOf(R(local)), new IrConst(IrType.I32, (long)tag));
    var body = new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.LogicalAnd, Tagged(V, IrVariantTag.F64), new IrUnary(IrUnaryOp.LogicalNot, Tagged(W, IrVariantTag.Null))),
        new IrBlock([new IrReturn(Times(R(V), Double(3)))]),
        new IrBlock([new IrIf(new IrBinary(IrBinaryOp.LogicalOr, Tagged(V, IrVariantTag.Null), Tagged(W, IrVariantTag.Null)),
          new IrBlock([new IrReturn(new IrVariantOf(IrVariantTag.Null, null))]), null)])),
      new IrReturn(R(W))
    ]);
    var module = Module(new IrFunction("T", true, IrType.Variant, [V, W], [], body));
    var typed = Typed(module);
    Assert.That(IrTree.Nodes(((IrIf)typed.Functions.Single(function => function.Name == "T").Body.Statements[0]).Then).OfType<IrCall>().Select(call => call.Function), Is.EqualTo(new[] { "PlyCheckD" }));
    var arguments = new object[][] { [new IrVariant(IrVariantTag.F64, 2.0), IrVariant.Empty], [new IrVariant(IrVariantTag.F64, 2.0), IrVariant.Null], [IrVariant.Null, IrVariant.Empty], [new IrVariant(IrVariantTag.I16, (short)1), new IrVariant(IrVariantTag.I16, (short)4)] };
    foreach (var argument in arguments)
      Assert.That(IrInterpreter.Call(typed, "T", argument), Is.EqualTo(IrInterpreter.Call(module, "T", argument)));
  }

  /// <summary>Visual Basic's table with an expansion that calls a function no module has.</summary>
  private sealed class MissingHelper : IDynamicSemantics {

    private static readonly IDynamicSemantics _table = VisualBasicFrontend.Semantics;

    public string Language => _table.Language;

    public IJoinPolicy Joins => _table.Joins;

    public DynamicType Result(DynamicOperation operation, DynamicType left, DynamicType right) => _table.Result(operation, left, right);

    public DynamicTrap Trap(DynamicOperation operation, DynamicType left, DynamicType right) => _table.Trap(operation, left, right);

    public IrExpr? Expand(DynamicOperation operation, IrExpr left, DynamicType leftType, IrExpr? right, DynamicType rightType)
      => _table.Expand(operation, left, leftType, right, rightType) is { } expanded ? new IrCall("Missing", expanded.Type, [expanded]) : null;

    public DynamicCall? Classify(IrExpr expression) => _table.Classify(expression);

    public bool IsHelper(string function) => _table.IsHelper(function);

    public string Name(DynamicExact element) => _table.Name(element);

  }

  [Test]
  public void AnExpansionThatCallsAMissingFunction_KeepsTheHelper() {
    var module = Module(new IrFunction("T", true, IrType.Variant, [], [], new IrBlock([new IrReturn(Times(Double(2), Double(3)))])));
    var typed = DeDynamicize.Run(module, new MissingHelper(), DeDynamicizeOptions.Default);
    Assert.That(IrVerifier.Verify(typed), Is.Empty);
    Assert.That(IrTree.Nodes(typed.Functions.Single(function => function.Name == "T").Body).OfType<IrCall>().Select(call => call.Function), Is.EqualTo(new[] { "PlyVarArith" }));
  }

  [Test]
  public void StatementsThatLeave_EndTheWalk_AndTestsInEitherOrderNarrow() {
    var i = new IrLocal("i", IrType.I32);
    var body = new IrBlock([
      new IrDeclare(V, Double(1)),
      new IrDeclare(i, new IrConst(IrType.I32, 0)),
      new IrFor(new IrBinary(IrBinaryOp.Less, R(i), new IrConst(IrType.I32, 2)), new IrBlock([new IrAssign(V, Times(R(V), Double(2)))]),
        new IrBlock([new IrExprStmt(new IrStep(R(i), true, false, false))])) { PerIteration = [i] },
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrConst(IrType.I32, (long)IrVariantTag.F64), new IrVariantTagOf(R(W))),
        new IrBlock([new IrReturn(Times(R(W), R(V)))]), null),
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrVariantTagOf(R(W)), new IrConst(IrType.I32, 99)), new IrBlock([new IrReturn(R(W))]), null),
      new IrReturn(R(V))
    ]);
    var module = Module(new IrFunction("T", true, IrType.Variant, [W], [V, i], body));
    var typed = Typed(module);
    Assert.That(IrTree.Nodes(((IrIf)typed.Functions.Single(function => function.Name == "T").Body.Statements[3]).Then).OfType<IrCall>().Select(call => call.Function), Is.EqualTo(new[] { "PlyCheckD" }), "a constant first is a tag test too");
    foreach (var argument in new[] { new IrVariant(IrVariantTag.F64, 1.5), new IrVariant(IrVariantTag.I16, (short)3) })
      Assert.That(IrInterpreter.Call(typed, "T", [argument]), Is.EqualTo(IrInterpreter.Call(module, "T", [argument])));
  }

  [Test]
  public void ATryFollowsEveryStatementOfItsBody_IntoTheCatch() {
    var error = new IrLocal("error", IrType.Object(IrExceptionClass.Exception));
    var body = new IrBlock([
      new IrDeclare(V, Double(1)),
      new IrTry(new IrBlock([
        new IrAssign(V, new IrVariantOf(IrVariantTag.String, new IrStrConst("in try"))),
        new IrExprStmt(new IrCall("H", IrType.F64, [new IrVariantValue(R(V), IrVariantTag.F64)])),
        new IrAssign(V, Double(2))
      ]), [new IrCatch(IrExceptionClass.Exception, error, null, new IrBlock([new IrReturn(R(V))]))], new IrBlock([new IrExprStmt(R(V))])),
      new IrReturn(R(V))
    ]);
    var module = Module(new IrFunction("T", true, IrType.Variant, [B], [V, error], body));
    var typed = Typed(module);
    Assert.That(TypeOf(typed, "v"), Is.EqualTo(IrType.Variant), "the catch may see the string");
    Assert.That(Run(typed, true), Is.EqualTo(Run(module, true)));
  }

}
