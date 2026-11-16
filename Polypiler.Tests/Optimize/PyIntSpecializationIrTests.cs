using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Specialize;

namespace Polypiler.Tests.Optimize;

/// <summary>
/// O0062 on IR built by hand, which reaches every form the Python frontend does not write itself: the
/// truncating division and remainder, the conditional, the conversions out of the arbitrary-precision
/// integer, a shift beyond the width under a mask. The interpreter computes the same before and after
/// on boundary and random operands (seed 62).
/// </summary>
[TestFixture]
public sealed class PyIntSpecializationIrTests {

  private static readonly IrLocal A = new("a", IrType.BigInt);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrBigConst Big(BigInteger value) => new(value);

  private static IrBinary B(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  private static IrModule Module() {
    var (x, d, r, o, e, n, c, s, t, m, i) = (new IrLocal("x", IrType.BigInt), new IrLocal("d", IrType.BigInt), new IrLocal("r", IrType.BigInt),
      new IrLocal("o", IrType.BigInt), new IrLocal("e", IrType.BigInt), new IrLocal("n", IrType.BigInt), new IrLocal("c", IrType.BigInt),
      new IrLocal("s", IrType.BigInt), new IrLocal("t", IrType.BigInt), new IrLocal("m", IrType.BigInt), new IrLocal("i", IrType.I32));
    IrStmt Let(IrLocal local, IrExpr value) => new IrDeclare(local, value);
    var sum = new[] { d, r, o, e, n, c, s, t, m }.Select(local => (IrExpr)R(local)).Aggregate((left, right) => B(IrBinaryOp.Add, left, right));
    var mixed = new IrFunction("Mixed", true, IrType.BigInt, [A], [x, d, r, o, e, n, c, s, t, m, i], new IrBlock([
      Let(x, B(IrBinaryOp.And, R(A), Big(0xFFFF))),
      Let(d, B(IrBinaryOp.Divide, R(x), Big(7))),
      Let(r, B(IrBinaryOp.Remainder, B(IrBinaryOp.Subtract, R(x), Big(30000)), Big(7))),
      Let(o, B(IrBinaryOp.Or, R(x), Big(0x100))),
      Let(e, B(IrBinaryOp.Xor, R(x), Big(0xF0))),
      Let(n, new IrUnary(IrUnaryOp.Negate, R(x))),
      Let(c, new IrUnary(IrUnaryOp.BitNot, R(x))),
      Let(s, B(IrBinaryOp.ShiftLeft, R(x), new IrConst(IrType.I32, 3))),
      Let(t, new IrConditional(B(IrBinaryOp.Greater, R(x), Big(100)), R(x), R(n))),
      // Negation and complement are low-bit closed; a shift by 70 leaves no low bit.
      Let(m, B(IrBinaryOp.And, B(IrBinaryOp.Add, B(IrBinaryOp.Add, new IrUnary(IrUnaryOp.Negate, R(A)), new IrUnary(IrUnaryOp.BitNot, R(A))),
        B(IrBinaryOp.ShiftLeft, R(A), new IrConst(IrType.I32, 70))), Big(0xFFFFFFFF))),
      Let(i, new IrConvert(IrType.I32, R(x), IrConversion.Trap)),
      new IrReturn(B(IrBinaryOp.Add, sum, new IrConvert(IrType.BigInt, R(i))))
    ]));
    var real = new IrFunction("Real", true, IrType.F64, [A], [], new IrBlock([new IrReturn(new IrConvert(IrType.F64, B(IrBinaryOp.And, R(A), Big(0xFFFFFF))))]));
    var narrow = new IrFunction("Narrow", true, IrType.I32, [A], [], new IrBlock([new IrReturn(new IrConvert(IrType.I32, B(IrBinaryOp.And, R(A), Big(0xFF))))]));
    var wide = new IrFunction("Wide", true, IrType.U32, [A], [], new IrBlock([new IrReturn(new IrConvert(IrType.U32, B(IrBinaryOp.Multiply, R(A), R(A)), IrConversion.Trap))]));
    var (b, k, y) = (new IrLocal("b", IrType.BigInt), new IrLocal("k", IrType.I32), new IrLocal("y", IrType.BigInt));
    IrExpr[] parts = [
      B(IrBinaryOp.And, Big(0xFF), R(A)),
      B(IrBinaryOp.And, B(IrBinaryOp.Multiply, new IrConvert(IrType.BigInt, R(k)), Big(3)), Big(0xFF)),
      B(IrBinaryOp.And, B(IrBinaryOp.Divide, R(A), R(b)), Big(0xFF)),
      B(IrBinaryOp.And, B(IrBinaryOp.Remainder, R(A), R(b)), Big(0xFF)),
      B(IrBinaryOp.Divide, R(y), Big(-7)),
      // Bounded, but beyond what a shift of a fixed width computes: kept as it is and converted.
      B(IrBinaryOp.ShiftRight, R(y), new IrConst(IrType.I32, 70))
    ];
    var more = new IrFunction("More", true, IrType.BigInt, [A, b, k], [y], new IrBlock([
      new IrDeclare(y, B(IrBinaryOp.And, R(A), Big(0xFFF))),
      new IrReturn(parts.Aggregate((left, right) => B(IrBinaryOp.Add, left, right)))
    ]));
    return new IrModule("M", [mixed, real, narrow, wide, more]);
  }

  [Test]
  public void EveryForm_ComputesWhatItComputedBefore() {
    var before = Module();
    var after = PyIntSpecialization.Run(before).Module;
    Assert.That(IrVerifier.Verify(after), Is.Empty);
    var random = new Random(62);
    BigInteger[] values = [0, 1, -1, 7, 100, 101, 0xFFFF, 0x10000, long.MaxValue, long.MinValue, BigInteger.One << 80, -(BigInteger.One << 80) + 12345,
      .. Enumerable.Range(0, 200).Select(_ => new BigInteger(random.NextInt64()) * random.Next(-3, 4))];
    foreach (var function in new[] { "Mixed", "Real", "Narrow", "Wide" })
      foreach (var value in values)
        Assert.That(Outcome(after, function, value), Is.EqualTo(Outcome(before, function, value)), $"{function}({value})");

    foreach (var value in values)
      foreach (var (divisor, count) in new (BigInteger, int)[] { (7, 0), (-3, -1), (0, 5), (BigInteger.One << 70, int.MaxValue) }) {
        object[] arguments = [value, divisor, count];
        Assert.That(Outcome(after, "More", arguments), Is.EqualTo(Outcome(before, "More", arguments)), $"More({value}, {divisor}, {count})");
      }
  }

  [Test]
  public void OperatorsThatAreNotLowBitClosed_AreReportedByName() {
    var report = PyIntSpecialization.Run(Module()).Applications.Select(application => application.Name).ToList();
    Assert.That(report, Has.Some.EqualTo("not applied: masked expression contains a truncating division"));
    Assert.That(report, Has.Some.EqualTo("not applied: masked expression contains a truncating remainder"));
    Assert.That(report, Has.Some.EqualTo("applied: & 0xFF -> u32 [wrap] (mask) [proof: range-facts]"));
  }

  [Test]
  public void BoundedForms_LeaveNoArbitraryPrecisionOperation() {
    var after = PyIntSpecialization.Run(Module()).Module;
    var mixed = after.Functions.Single(function => function.Name == "Mixed");
    Assert.That(mixed.Locals.Where(local => local.Type.IsBigInt), Is.Empty);
    Assert.That(IrTree.Nodes(mixed.Body).OfType<IrBinary>().Where(binary => binary.Left.Type.IsBigInt), Is.Empty);
    Assert.That(mixed.ReturnType.IsBigInt, Is.False);
    var real = after.Functions.Single(function => function.Name == "Real");
    Assert.That(IrTree.Nodes(real.Body).OfType<IrConvert>().Single(conversion => conversion.Type == IrType.F64).Operand.Type, Is.EqualTo(IrType.I32));
    var wide = after.Functions.Single(function => function.Name == "Wide");
    Assert.That(IrTree.Nodes(wide.Body).OfType<IrBinary>().Single().Left.Type, Is.EqualTo(IrType.BigInt));
  }

  private static string Outcome(IrModule module, string function, BigInteger value) => Outcome(module, function, [value]);

  private static string Outcome(IrModule module, string function, object[] arguments) {
    try {
      return Convert.ToString(IrInterpreter.Call(module, function, arguments), System.Globalization.CultureInfo.InvariantCulture)!;
    } catch (IrTrapException trap) {
      return trap.Trap.ToString();
    }
  }

}
