using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Integer;

/// <summary>
/// Booleans and their integer encodings (#112): <c>BoolToInt[true=k]</c>, <c>IntToBool[nonzero]</c> and
/// <c>[eq k]</c>, the stored-boolean encoding per field, and the verifier keeping <c>bool</c> apart from
/// the integers - the scenarios of <c>tests/features/ir/ir-bool.feature</c>.
/// </summary>
[TestFixture]
public sealed class BooleanEncodingTests {

  private static readonly IrType[] _types = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  /// <summary>The value of a function <c>F(x)</c> that returns an expression of its parameter.</summary>
  private static object? Run(IrType type, Func<IrExpr, IrExpr> body, long x) {
    var parameter = new IrLocal("x", type);
    var expression = body(new IrLocalRef(parameter));
    var module = new IrModule("M", [new IrFunction("F", true, expression.Type, [parameter], [], new IrBlock([new IrReturn(expression)]))]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return IrInterpreter.Call(module, "F", [HelperHarness.Host(type, x)]);
  }

  private static IrTruthTest Rule(string rule) => rule == "nonzero" ? IrTruthTest.NonZero : IrTruthTest.EqualTo(long.Parse(rule[3..], System.Globalization.CultureInfo.InvariantCulture));

  [TestCase("nonzero", "i16", -1, 5, -1)]
  [TestCase("nonzero", "i16", -1, 0, 0)]
  [TestCase("nonzero", "u8", 1, 255, 1)]
  [TestCase("eq -1", "i16", -1, 5, 0)]
  [TestCase("eq -1", "i16", -1, -1, -1)]
  public void TruthValueEncodings_ConvertBothWays(string rule, string type, long k, long x, long expected) {
    var irType = _types.Single(candidate => candidate.ToString() == type);
    var value = Run(irType, operand => IrBooleans.ToInt(IrBooleans.FromInt(operand, Rule(rule)), irType, k), x);
    Assert.That(value, Is.EqualTo(HelperHarness.Host(irType, expected)));
  }

  [Test]
  public void BasicNotOnAFlag_AndLogicalNot_Differ() {
    var i16 = IrType.I16;
    var bitwise = Run(i16, x => new IrConvert(i16, new IrUnary(IrUnaryOp.BitNot, new IrConvert(IrType.I32, x))), 5);
    var logical = Run(i16, x => IrBooleans.ToInt(new IrUnary(IrUnaryOp.LogicalNot, IrBooleans.FromInt(x, IrTruthTest.NonZero)), i16, -1), 5);
    Assert.That(bitwise, Is.EqualTo((short)-6));
    Assert.That(logical, Is.EqualTo((short)0));
  }

  /// <summary>Every type, every encoding it holds of 1, -1 and 255, on the boundaries of the type: exactly <c>k</c> or 0, never another value.</summary>
  [Test]
  public void BoolToInt_GivesExactlyTheEncodingOrZero_AndNonZeroReadsEveryPattern() {
    foreach (var type in _types) {
      var (minimum, maximum) = (IrOperators.Minimum(type), IrOperators.Maximum(type));
      long Carry(Int128 number) => type.Kind == IrTypeKind.U64 ? unchecked((long)(ulong)number) : (long)number;
      long[] values = [Carry(minimum), Carry(minimum + 1), 0, 1, 2, Carry(maximum - 1), Carry(maximum), .. type.IsSigned ? new[] { -1L } : []];
      foreach (var k in new long[] { 1, -1, 255 }.Where(k => IrBooleans.Problem(type, k) is null))
        foreach (var x in values) {
          var stored = Run(type, operand => IrBooleans.ToInt(IrBooleans.FromInt(operand, IrTruthTest.NonZero), type, k), x);
          Assert.That(stored, Is.EqualTo(HelperHarness.Host(type, x != 0 ? k : 0)), $"{type} k={k} x={x}");
          var equal = Run(type, operand => IrBooleans.FromInt(operand, IrTruthTest.EqualTo(k)), x);
          Assert.That(equal, Is.EqualTo(x == k), $"{type} eq {k} x={x}");
        }
    }
  }

  // ---------------------------------------------------------------- the verifier

  private static IReadOnlyList<Diagnostic> Verify(IrType parameter, IrStmt statement, IrType? result = null) {
    var x = new IrLocal("x", parameter);
    return IrVerifier.Verify(new IrModule("M", [new IrFunction("F", true, result ?? IrType.Void, [x], [], new IrBlock([statement, new IrReturn(null)]))]));
  }

  [Test]
  public void ABranchOnAnInteger_IsRejected() {
    var x = new IrLocalRef(new IrLocal("x", IrType.U32));
    var diagnostics = Verify(IrType.U32, new IrIf(x, new IrBlock([]), new IrBlock([])));
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0100" }));
  }

  [TestCase(IrBinaryOp.Add)]
  [TestCase(IrBinaryOp.ShiftLeft)]
  [TestCase(IrBinaryOp.Less)]
  public void ArithmeticAShiftOrAnOrderOnBool_IsPLY0130(IrBinaryOp op) {
    var b = new IrLocalRef(new IrLocal("x", IrType.Bool));
    var diagnostics = Verify(IrType.Bool, new IrExprStmt(new IrBinary(op, b, op == IrBinaryOp.ShiftLeft ? new IrConst(IrType.I32, 1) : b)));
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0130" }));
    Assert.That(diagnostics[0].Message, Does.StartWith($"'{op}' does not accept bool operands; convert with BoolToInt"));
  }

  [Test]
  public void AnIntegerOperationOnBool_IsPLY0130NamingIt() {
    var b = new IrLocalRef(new IrLocal("x", IrType.Bool));
    var diagnostics = Verify(IrType.Bool, new IrExprStmt(new IrIntegerOp(IrIntegerOpcode.Add, IrType.Bool, [b, b], IrOverflowContract.Wrap)));
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0130" }));
    Assert.That(diagnostics[0].Message, Does.StartWith("'Add' does not accept bool operands"));
  }

  [Test]
  public void TheOperatorsBoolTakes_StayAsTheyWere() {
    var b = new IrLocalRef(new IrLocal("x", IrType.Bool));
    foreach (var op in new[] { IrBinaryOp.LogicalAnd, IrBinaryOp.LogicalOr, IrBinaryOp.Equal, IrBinaryOp.NotEqual })
      Assert.That(Verify(IrType.Bool, new IrExprStmt(new IrBinary(op, b, b))), Is.Empty, op.ToString());

    // The bitwise operators are no arithmetic: a bool is no operand of them either, as before.
    Assert.That(Verify(IrType.Bool, new IrExprStmt(new IrBinary(IrBinaryOp.And, b, b))).Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0100" }));
    Assert.That(IrBooleans.RejectsBool(IrBinaryOp.Xor), Is.False);
    Assert.That(IrBooleans.RejectsBool(IrBinaryOp.Divide), Is.True);
  }

  // ---------------------------------------------------------------- the encodings

  [TestCase("u8", 0L)]
  [TestCase("u8", 256L)]
  [TestCase("u8", -1L)]
  [TestCase("i8", 128L)]
  public void AZeroOrUnfittingTrueEncoding_IsPLY0131(string type, long k) {
    var irType = _types.Single(candidate => candidate.ToString() == type);
    var problem = IrBooleans.Problem(irType, k);
    Assert.That(problem!.Code, Is.EqualTo("PLY0131"));
    Assert.That(problem.Message, Is.EqualTo($"true encoding {k} is zero or does not fit {type}"));
    Assert.That(() => IrBooleans.ToInt(new IrConst(IrType.Bool, 1), irType, k), Throws.ArgumentException.With.Message.Contains(problem.Message));
  }

  [Test]
  public void TheEncodingsHoldWhatTheirTypesHold() {
    Assert.That(IrBooleans.Problem(IrType.U8, 255), Is.Null);
    Assert.That(IrBooleans.Problem(IrType.I16, -1), Is.Null);
    Assert.That(IrBooleans.Problem(IrType.U64, -1), Is.Null, "a u64 is carried as its 64 bits");
    Assert.That(IrBooleans.Problem(IrType.F64, 1), Is.Not.Null);
    Assert.That(() => IrBooleans.Problem(null!, 1), Throws.ArgumentNullException);
  }

  [Test]
  public void TheConversions_RefuseWhatTheyDoNotConvert() {
    var truth = new IrConst(IrType.Bool, 1);
    var number = new IrConst(IrType.U8, 1);
    Assert.That(() => IrBooleans.ToInt(number, IrType.U8), Throws.ArgumentException.With.Message.Contains("converts a bool, not a value of u8"));
    Assert.That(() => IrBooleans.ToInt(null!, IrType.U8), Throws.ArgumentNullException);
    Assert.That(() => IrBooleans.FromInt(truth, IrTruthTest.NonZero), Throws.ArgumentException.With.Message.Contains("reads an integer, not a value of bool"));
    Assert.That(() => IrBooleans.FromInt(number, IrTruthTest.EqualTo(-1)), Throws.ArgumentException.With.Message.Contains("-1 is no value of u8"));
    Assert.That(() => IrBooleans.FromInt(null!, IrTruthTest.NonZero), Throws.ArgumentNullException);
    Assert.That(() => IrBooleans.FromInt(number, null!), Throws.ArgumentNullException);
    Assert.That(IrTruthTest.NonZero.ToString(), Is.EqualTo("nonzero"));
    Assert.That(IrTruthTest.EqualTo(-1).ToString(), Is.EqualTo("eq -1"));
    Assert.That(IrTruthTest.EqualTo(-1).Rule, Is.EqualTo(IrTruthRule.Equal));
    Assert.That(IrTruthTest.NonZero.Value, Is.Zero);
  }

  /// <summary>A stored boolean is an integer plus its conversions, recorded per field.</summary>
  [Test]
  public void AStoredBoolean_UsesTheEncodingOfItsField() {
    var storage = new IrBoolStorage(IrBoolEncoding.C, new Dictionary<string, IrBoolEncoding> { ["Visible"] = IrBoolEncoding.Basic, ["Flag"] = IrBoolEncoding.Byte });
    Assert.That(storage.Of("Visible"), Is.SameAs(IrBoolEncoding.Basic));
    Assert.That(storage.Of("Flag").Storage, Is.EqualTo(IrType.U8));
    Assert.That(storage.Of("Other"), Is.SameAs(IrBoolEncoding.C));
    Assert.That(() => storage.Of(null!), Throws.ArgumentNullException);

    foreach (var encoding in new[] { IrBoolEncoding.C, IrBoolEncoding.Basic, IrBoolEncoding.Byte }) {
      var stored = Run(encoding.Storage, x => encoding.Store(encoding.Load(x)), 7);
      Assert.That(stored, Is.EqualTo(HelperHarness.Host(encoding.Storage, encoding.TrueValue)));
      Assert.That(Run(encoding.Storage, x => encoding.Store(encoding.Load(x)), 0), Is.EqualTo(HelperHarness.Host(encoding.Storage, 0)));
    }

    Assert.That(() => IrBoolEncoding.Basic.Load(new IrConst(IrType.I32, 1)), Throws.ArgumentException.With.Message.Contains("stored as i16 is read from a value of i32"));
    Assert.That(() => IrBoolEncoding.Basic.Load(null!), Throws.ArgumentNullException);
  }

}
