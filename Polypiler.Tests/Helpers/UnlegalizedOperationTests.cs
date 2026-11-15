using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// The other side of the limb and trap helpers of #1358: an operation the library computes reaches the
/// JavaScript writer only where the module was not legalized, and is reported there (PLY1152) instead
/// of being written without its trap. A step of a variable as a statement is the assignment of the
/// helper's result, also of a global; the limb helpers trap where the operation does.
/// </summary>
[TestFixture]
public sealed class UnlegalizedOperationTests {

  private static readonly IrLocal _a = new("a", IrType.I32);
  private static readonly IrLocal _l = new("l", IrType.I64);
  private static readonly IrLocal _d = new("d", IrType.F64);

  private static IEnumerable<TestCaseData> Operations() => [
    Case("es5", IrType.I64, new IrStep(new IrLocalRef(_l), true, false, false), "of a 64-bit integer"),
    Case("es2020", IrType.I32, new IrStep(new IrLocalRef(_a), true, true, true), "'a checked step'"),
    Case("es5", IrType.I64, new IrUnary(IrUnaryOp.Negate, new IrLocalRef(_l)), "'Negate' of a 64-bit integer"),
    Case("es2020", IrType.I32, new IrUnary(IrUnaryOp.NegateChecked, new IrLocalRef(_a)), "'NegateChecked'"),
    Case("es2020", IrType.I32, new IrBinary(IrBinaryOp.Divide, new IrLocalRef(_a), new IrLocalRef(_a)), "'Divide'"),
    Case("es2020", IrType.I32, new IrBinary(IrBinaryOp.AddChecked, new IrLocalRef(_a), new IrLocalRef(_a)), "'AddChecked'"),
    Case("es2020", IrType.I64, new IrBinary(IrBinaryOp.MultiplyChecked, new IrLocalRef(_l), new IrLocalRef(_l)), "'MultiplyChecked'"),
    Case("es2020", IrType.U8, new IrConvert(IrType.U8, new IrLocalRef(_a), IrConversion.Trap), "'checked conversion'"),
    Case("es5", IrType.I32, new IrConvert(IrType.I32, new IrLocalRef(_l), IrConversion.Trap), "'conversion' of a 64-bit integer"),
    Case("es5", IrType.F64, new IrConvert(IrType.F64, new IrLocalRef(_l)), "'conversion' of a 64-bit integer"),
    Case("es5", IrType.I64, new IrConvert(IrType.I64, new IrLocalRef(_d), IrConversion.Saturate), "'conversion' of a 64-bit integer"),
    Case("es2020", IrType.I32, new IrConvert(IrType.I32, new IrLocalRef(_d), IrConversion.Saturate), "'conversion from floating point'")
  ];

  private static TestCaseData Case(string level, IrType type, IrExpr value, string message)
    => new TestCaseData(level, type, value, message).SetArgDisplayNames(level, value.GetType().Name, message);

  [TestCaseSource(nameof(Operations))]
  public void UnlegalizedOperation_IsReported(string level, IrType type, IrExpr value, string message) {
    var module = new IrModule("M", [new IrFunction("F", true, type, [_a, _l, _d], [], new IrBlock([new IrReturn(value)]))]);
    var problems = JavaScriptEmitter.Check(module, JavaScriptProfile.Parse(level));
    Assert.That(problems.Select(problem => problem.Message), Has.Some.Contains(message).And.Some.Contains("the module was not legalized"));
  }

  /// <summary>A step of a 64-bit global or local as a statement, up or down, wrapping or checked, is the assignment of the helper's result.</summary>
  [Test]
  public void StepAsAStatement_IsTheAssignmentOfTheHelpersResult([Values] bool increment, [Values] bool trap, [Values] bool global) {
    var counter = new IrGlobal("counter", IrType.I64, null);
    var local = new IrLocal("x", IrType.I64);
    IrExpr target = global ? new IrGlobalRef(counter) : new IrLocalRef(local);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I64, [local], [], new IrBlock([
      new IrGlobalAssign(counter, new IrLocalRef(local)),
      new IrExprStmt(new IrStep(target, increment, false, trap)),
      new IrReturn(target)
    ]))]) { Globals = [counter] };
    var legalized = LibraryLegalization.Lower(module, () => HelperHarness.Library, WideIntegerLibrary.Call);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    Assert.That(legalized.Functions[0].Body.Statements[1], Is.InstanceOf(global ? typeof(IrGlobalAssign) : typeof(IrAssign)));
    foreach (var value in new object[] { 0L, -1L, long.MaxValue, long.MinValue, 41L })
      Assert.That(HelperHarness.Outcome(legalized, [value]), Is.EqualTo(HelperHarness.Outcome(module, [value])), value.ToString());
  }

  /// <summary>The limb helpers, compiled, trap where the operation does: the smallest value divided by -1, by 0; equality tells the limbs apart.</summary>
  [Test]
  public void CompiledLimbHelpers_TrapAsTheOperation() {
    Assert.Multiple(() => {
      Assert.That(() => HelperOperations.DivideSigned64(long.MinValue, -1L), Throws.TypeOf<OverflowException>());
      Assert.That(() => HelperOperations.RemainderSigned64(long.MinValue, -1L), Throws.TypeOf<OverflowException>());
      Assert.That(() => HelperOperations.DivideUnsigned64(1UL, 0UL), Throws.TypeOf<DivideByZeroException>());
      Assert.That(HelperOperations.DivideSigned64(long.MinValue, 1L), Is.EqualTo(long.MinValue));
      Assert.That(HelperOperations.DivideSigned64(long.MinValue + 1, -1L), Is.EqualTo(long.MaxValue));
      Assert.That(HelperOperations.Equal64(0x1_0000_0000L, 0L), Is.False);
      Assert.That(HelperOperations.Equal64(1L, 2L), Is.False);
      Assert.That(HelperOperations.Equal64(-5L, -5L), Is.True);
    });
  }

}
