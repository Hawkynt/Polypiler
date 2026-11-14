using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The table of the floating-point operations and the rules it gives the IR: the types every shape
/// takes (test design 12 of #1301), the contracts, and the verifier's rule for the mode of a rounding
/// to digits.
/// </summary>
[TestFixture]
public sealed class FloatOperationTableTests {

  private static readonly IrType[] _formats = [IrType.F32, IrType.F64];

  private static IEnumerable<TestCaseData> Operations
    => FloatOperationTable.All.Where(operation => operation.Double is not null).Select(operation => new TestCaseData(operation).SetArgDisplayNames(operation.Op.ToString()));

  [Test]
  public void Table_HasEveryOperationOnce_AndFindsItsEntries() {
    Assert.That(FloatOperationTable.All.Select(operation => operation.Op), Is.Unique);
    Assert.That(FloatOperationTable.Of(IrIntrinsicOp.CopySign)!.Family, Is.EqualTo("Sign and magnitude"));
    Assert.That(FloatOperationTable.Of(IrIntrinsicOp.RotateLeft), Is.Null);
    Assert.That(FloatOperationTable.Of(IrIntrinsicOp.Sqrt)!.Double, Is.Null, "an operation the IR had keeps its own rules");
    Assert.That(FloatOperationTable.Of(IrIntrinsicOp.Lerp)!.ToString(), Is.EqualTo("Lerp"));
  }

  [Test]
  public void Contracts_AreSpelledAsAdr0005SpellsThem() {
    Assert.That(FloatContract.Exact.ToString(), Is.EqualTo("exact"));
    Assert.That(FloatContract.Ulp(3).ToString(), Is.EqualTo("ulp(3)"));
    Assert.That(FloatContract.Ulp(3).HelperDistance, Is.Zero);
  }

  /// <summary>Every operation accepts its shape in both formats and nothing else.</summary>
  [TestCaseSource(nameof(Operations))]
  public void Operation_AcceptsItsShapeInBothFormats(object operation) {
    var target = (FloatOperation)operation;
    foreach (var format in _formats) {
      var operands = Shape(target.Shape, format);
      var result = FloatIntrinsics.Result(target.Shape, format);
      Assert.That(IrOperators.Accepts(target.Op, result, operands), Is.True, $"{format}");

      var other = format == IrType.F64 ? IrType.F32 : IrType.F64;
      Assert.That(IrOperators.Accepts(target.Op, FloatIntrinsics.Result(target.Shape, other), operands), Is.EqualTo(result == FloatIntrinsics.Result(target.Shape, other)), "a result of the other format");
      Assert.That(IrOperators.Accepts(target.Op, result, [.. operands, format]), Is.False, "one operand too many");
      Assert.That(IrOperators.Accepts(target.Op, result, [IrType.I32, .. operands.Skip(1)]), Is.False, "an integer first operand");
      if (operands.Count > 1)
        Assert.That(IrOperators.Accepts(target.Op, result, [format, .. operands.Skip(1).Select(type => type.IsFloat ? other : IrType.I64)]), Is.False, "mixed formats");
    }
  }

  [Test]
  public void AnOperationOnNoOperands_IsNotAccepted()
    => Assert.That(IrOperators.Accepts(IrIntrinsicOp.CopySign, IrType.F64, []), Is.False);

  [Test]
  public void ExistingRules_StayForIntegersAndTheOperationsTheIrHad() {
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Abs, IrType.I32, [IrType.I32]), Is.True);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Log2, IrType.I32, [IrType.U64]), Is.True);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Sqrt, IrType.F32, [IrType.F32]), Is.True);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.BitsFloat, IrType.F64, [IrType.I64]), Is.True);
  }

  [Test]
  public void ShapesOfTheOperationsTheIrHad_DescribeTheirTypes() {
    var bits = FloatOperationTable.Of(IrIntrinsicOp.FloatBits)!;
    var back = FloatOperationTable.Of(IrIntrinsicOp.BitsFloat)!;
    Assert.That(bits.Accepts(IrType.I64, [IrType.F64]) && bits.Accepts(IrType.I32, [IrType.F32]), Is.True);
    Assert.That(back.Accepts(IrType.F64, [IrType.I64]) && back.Accepts(IrType.F32, [IrType.I32]), Is.True);
    Assert.That(back.Accepts(IrType.F32, [IrType.I64]) || back.Accepts(IrType.F64, [IrType.U32]) || back.Accepts(IrType.F64, [IrType.I64, IrType.I64]), Is.False);
    Assert.That(FloatOperationTable.Of(IrIntrinsicOp.Sign)!.Accepts(IrType.I32, [IrType.F64]), Is.True);
  }

  [Test]
  public void Verifier_RefusesARoundingToDigitsWhoseModeIsNotAConstantMode() {
    var x = new IrLocal("x", IrType.F64);
    var mode = new IrLocal("mode", IrType.I32);
    IrModule Rounding(IrExpr modeArgument) => new("M", [new IrFunction("F", true, IrType.F64, [x, mode], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundDigits, IrType.F64, [new IrLocalRef(x), new IrConst(IrType.I32, 2), modeArgument]))
    ]))]);

    Assert.That(IrVerifier.Verify(Rounding(new IrConst(IrType.I32, 4))), Is.Empty);
    Assert.That(IrVerifier.Verify(Rounding(new IrLocalRef(mode))).Select(diagnostic => diagnostic.Message),
      Has.One.Contains("the mode of RoundDigits is not a constant of MidpointRounding"));
    Assert.That(IrVerifier.Verify(Rounding(new IrConst(IrType.I32, 5))), Has.Count.EqualTo(1));
  }

  [Test]
  public void Verifier_RefusesAFloatingPointOperationOnTheWrongTypes() {
    var x = new IrLocal("x", IrType.F64);
    var n = new IrLocal("n", IrType.I32);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.F64, [x, n], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.CopySign, IrType.F64, [new IrLocalRef(x), new IrLocalRef(n)]))
    ]))]);
    Assert.That(IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message), Has.One.Contains("CopySign with the type f64 is applied to (f64, i32)"));
  }

  [Test]
  public void TheInterpreter_LeavesIntegerOperationsToItsOwnRules() {
    Assert.That(FloatOperationTable.TryEvaluate(IrIntrinsicOp.CopySign, [], out var none), Is.False);
    Assert.That(none, Is.Null);
    Assert.That(FloatOperationTable.TryEvaluate(IrIntrinsicOp.CopySign, [1L, 2L], out _), Is.False);
    Assert.That(FloatOperationTable.TryEvaluate(IrIntrinsicOp.Sqrt, [2.0], out _), Is.False);
    Assert.That(FloatOperationTable.TryEvaluate(IrIntrinsicOp.CopySign, [2.0, -1.0], out var copied), Is.True);
    Assert.That(copied, Is.EqualTo(-2.0));
  }

  /// <summary>The operand types of a shape, with the integer of a rounding to digits and its mode.</summary>
  private static List<IrType> Shape(FloatShape shape, IrType format)
    => shape == FloatShape.WithDigits ? [format, IrType.I32, IrType.I32] : [.. FloatIntrinsics.Operands(shape, format)];

}
