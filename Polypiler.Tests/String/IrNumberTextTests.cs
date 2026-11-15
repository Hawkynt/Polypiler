using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The conversions between numbers and text of the IR (#1315): the types and constants the verifier
/// takes, and what the interpreter computes - each rule against the values the issue states, the
/// boundaries of its layout and the reference vectors of .NET 10 and node.
/// </summary>
[TestFixture]
public sealed class IrNumberTextTests {

  private static readonly IrType _str = IrType.String;

  private static IrExpr Text(string value) => new IrStrConst(value);

  private static IrConst F64(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  private static IrConst I32(int value) => new(IrType.I32, value);

  private static IReadOnlyList<string> Problems(IrExpr body) {
    var function = new IrFunction("F", true, body.Type, [], [], new IrBlock([new IrReturn(body)]));
    return [.. IrVerifier.Verify(new IrModule("M", [function])).Select(diagnostic => diagnostic.Message)];
  }

  private static object? Evaluated(IrExpr body) {
    var function = new IrFunction("F", true, body.Type, [], [], new IrBlock([new IrReturn(body)]));
    return IrInterpreter.Call(new IrModule("M", [function]), "F", []);
  }

  [Test]
  public void Trap_InvalidFormat_IsDescribed()
    => Assert.That(IrTrapException.Describe(IrTrap.InvalidFormat), Is.EqualTo("invalid format"));

  [Test]
  public void Operations_AreTheConversions() {
    Assert.That(NumberTextOperations.Operations, Has.Count.EqualTo(11));
    Assert.That(NumberTextOperations.Operations, Has.All.Matches<IrIntrinsicOp>(NumberTextOperations.IsNumberText));
    Assert.That(NumberTextOperations.IsNumberText(IrIntrinsicOp.StrConcat), Is.False);
    Assert.That(NumberTextOperations.Problem(new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [Text("a")])), Is.Null);
  }

  /// <summary>Test design 11: the verifier refuses a conversion of the wrong type or with a constant outside its bounds.</summary>
  [Test]
  public void Verifier_RefusesMalformedConversions() {
    Assert.That(Problems(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, _str, [Text("x")])), Has.Some.Contains("FormatDotNet with the type str is applied to (str)"));
    Assert.That(Problems(new IrIntrinsic(IrIntrinsicOp.FormatEcma, _str, [new IrConst(IrType.F32, 0)])), Has.Some.Contains("FormatEcma"));
    Assert.That(Problems(new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, _str, [I32(1)])), Has.Some.Contains("FormatBoolEcma"));
    Assert.That(Problems(NumberTextOperations.Fixed(IrIntrinsicOp.FixedDotNet, F64(1), 16)), Has.Some.Contains("the digits of FixedDotNet are not a constant from 0 to 15"));
    Assert.That(Problems(NumberTextOperations.Fixed(IrIntrinsicOp.FixedEcma, F64(1), 101)), Has.Some.Contains("from 0 to 100"));
    Assert.That(Problems(NumberTextOperations.Fixed(IrIntrinsicOp.FixedEcma, F64(1), -1)), Has.Some.Contains("from 0 to 100"));
    Assert.That(Problems(new IrIntrinsic(IrIntrinsicOp.FixedDotNet, _str, [F64(1), new IrBinary(IrBinaryOp.Add, I32(1), I32(1))])), Has.Some.Contains("FixedDotNet"));
    Assert.That(Problems(new IrIntrinsic(IrIntrinsicOp.ParseDotNet, IrType.Bool, [Text("1")])), Has.Some.Contains("ParseDotNet with the type bool"));
    Assert.That(Problems(new IrIntrinsic(IrIntrinsicOp.ParseNumberEcma, IrType.F32, [Text("1")])), Has.Some.Contains("ParseNumberEcma"));
    Assert.That(Problems(new IrIntrinsic(IrIntrinsicOp.ParseIntEcma, IrType.F64, [Text("1"), F64(2)])), Has.Some.Contains("ParseIntEcma"));
    Assert.That(Problems(NumberTextOperations.Radix(I32(1), 10, 0, false, RadixMode.TwoComplement)), Has.Some.Contains("the radix of FormatRadix"));
    Assert.That(Problems(NumberTextOperations.Radix(I32(1), 37, 0, false, RadixMode.SignMagnitude)), Has.Some.Contains("the radix of FormatRadix"));
    Assert.That(Problems(NumberTextOperations.Radix(I32(1), 1, 0, false, RadixMode.SignMagnitude)), Has.Some.Contains("the radix of FormatRadix"));
    Assert.That(Problems(NumberTextOperations.Radix(I32(1), 16, 100, false, RadixMode.SignMagnitude)), Has.Some.Contains("the radix of FormatRadix"));
    Assert.That(Problems(NumberTextOperations.Radix(I32(1), 16, -1, false, RadixMode.SignMagnitude)), Has.Some.Contains("the radix of FormatRadix"));
    var radix = NumberTextOperations.Radix(I32(1), 16, 0, false, RadixMode.SignMagnitude);
    Assert.That(Problems(radix with { Arguments = [I32(1), I32(16), I32(0), new IrConst(IrType.Bool, 0), I32(2)] }), Has.Some.Contains("the radix of FormatRadix"));
    Assert.That(Problems(radix with { Arguments = [I32(1), new IrBinary(IrBinaryOp.Add, I32(8), I32(8)), I32(0), new IrConst(IrType.Bool, 0), I32(1)] }), Has.Some.Contains("the radix"));
    Assert.That(Problems(radix with { Arguments = [I32(1), I32(16), I32(0), new IrUnary(IrUnaryOp.LogicalNot, new IrConst(IrType.Bool, 0)), I32(1)] }), Has.Some.Contains("the radix"));
    Assert.That(Problems(radix with { Arguments = [Text("1"), I32(16), I32(0), new IrConst(IrType.Bool, 0), I32(1)] }), Has.Some.Contains("FormatRadix"));
    Assert.That(Problems(radix), Is.Empty);
    Assert.That(Problems(new IrIntrinsic(IrIntrinsicOp.ParseIntEcma, IrType.F64, [Text("1"), I32(36)])), Is.Empty);
  }

  /// <summary>Test design 4: the layout thresholds of both rules, the zeros, NaN, the infinities and the extremes.</summary>
  [TestCase(1e-7, "1e-7", "1E-07")]
  [TestCase(1e-6, "0.000001", "1E-06")]
  [TestCase(1e-5, "0.00001", "1E-05")]
  [TestCase(1e-4, "0.0001", "0.0001")]
  [TestCase(1e16, "10000000000000000", "10000000000000000")]
  [TestCase(1e17, "100000000000000000", "1E+17")]
  [TestCase(1e20, "100000000000000000000", "1E+20")]
  [TestCase(1e21, "1e+21", "1E+21")]
  [TestCase(1.5e-6, "0.0000015", "1.5E-06")]
  [TestCase(-0.0, "0", "-0")]
  [TestCase(0.0, "0", "0")]
  [TestCase(double.NaN, "NaN", "NaN")]
  [TestCase(double.PositiveInfinity, "Infinity", "Infinity")]
  [TestCase(double.NegativeInfinity, "-Infinity", "-Infinity")]
  [TestCase(5e-324, "5e-324", "5E-324")]
  [TestCase(double.MaxValue, "1.7976931348623157e+308", "1.7976931348623157E+308")]
  [TestCase(0.30000000000000004, "0.30000000000000004", "0.30000000000000004")]
  [TestCase(-123.456, "-123.456", "-123.456")]
  public void Format_LaysTheDigitsOutByItsRule(double value, string ecma, string dotNet) {
    Assert.That(NumberTextOperations.FormatEcma(value), Is.EqualTo(ecma));
    Assert.That(NumberTextOperations.FormatDotNet(value), Is.EqualTo(dotNet));
    Assert.That(Evaluated(new IrIntrinsic(IrIntrinsicOp.FormatEcma, _str, [F64(value)])), Is.EqualTo(ecma));
    Assert.That(Evaluated(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, _str, [F64(value)])), Is.EqualTo(dotNet));
  }

  /// <summary>Test design 3 and 4: binary32 has digits and thresholds of its own.</summary>
  [TestCase(0.1f, "0.1")]
  [TestCase(16777217f, "16777216")]
  [TestCase(1e10f, "1E+10")]
  [TestCase(1e8f, "100000000")]
  [TestCase(1e9f, "1E+09")]
  [TestCase(1e-5f, "1E-05")]
  [TestCase(1e-4f, "0.0001")]
  [TestCase(-0f, "-0")]
  [TestCase(170739.125f, "170739.12")]
  public void FormatDotNet_OfAFloat_HasItsShortestDigits(float value, string expected)
    => Assert.That(Evaluated(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, _str, [new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, value))])), Is.EqualTo(expected));

  /// <summary>Test design 5: integers at the bounds of their widths, in every rule and both modes of a radix.</summary>
  [Test]
  public void Integers_AreWrittenAtTheirBounds() {
    string Format(IrIntrinsicOp op, IrType type, long value) => (string)Evaluated(new IrIntrinsic(op, _str, [new IrConst(type, value)]))!;
    Assert.That(Format(IrIntrinsicOp.FormatEcma, IrType.I32, int.MinValue), Is.EqualTo("-2147483648"));
    Assert.That(Format(IrIntrinsicOp.FormatDotNet, IrType.U32, uint.MaxValue), Is.EqualTo("4294967295"));
    Assert.That(Format(IrIntrinsicOp.FormatDotNet, IrType.I64, long.MinValue), Is.EqualTo("-9223372036854775808"));
    Assert.That(Format(IrIntrinsicOp.FormatEcma, IrType.U64, -1), Is.EqualTo("18446744073709551615"));
    Assert.That(Format(IrIntrinsicOp.FormatDotNet, IrType.I8, -1), Is.EqualTo("-1"));
    string Radix(IrType type, long value, int radix, int padding, bool upper, RadixMode mode)
      => (string)Evaluated(NumberTextOperations.Radix(new IrConst(type, value), radix, padding, upper, mode))!;
    Assert.That(Radix(IrType.I32, -1, 16, 0, false, RadixMode.TwoComplement), Is.EqualTo("ffffffff"));
    Assert.That(Radix(IrType.I32, -1, 16, 0, false, RadixMode.SignMagnitude), Is.EqualTo("-1"));
    Assert.That(Radix(IrType.I8, -1, 2, 0, false, RadixMode.TwoComplement), Is.EqualTo("11111111"));
    Assert.That(Radix(IrType.I64, long.MinValue, 16, 0, true, RadixMode.TwoComplement), Is.EqualTo("8000000000000000"));
    Assert.That(Radix(IrType.I64, long.MinValue, 2, 0, false, RadixMode.SignMagnitude), Is.EqualTo("-1" + new string('0', 63)));
    Assert.That(Radix(IrType.U64, -1, 36, 0, false, RadixMode.SignMagnitude), Is.EqualTo("3w5e11264sgsf"));
    Assert.That(Radix(IrType.I32, 0, 16, 8, false, RadixMode.TwoComplement), Is.EqualTo("00000000"));
    Assert.That(Radix(IrType.I32, -5, 10, 3, false, RadixMode.SignMagnitude), Is.EqualTo("-005"));
    Assert.That(Radix(IrType.U16, 255, 16, 4, true, RadixMode.TwoComplement), Is.EqualTo("00FF"));
  }

  [Test]
  public void Booleans_AreSpelledByTheirLanguage() {
    Assert.That(Evaluated(new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, _str, [new IrConst(IrType.Bool, 1)])), Is.EqualTo("true"));
    Assert.That(Evaluated(new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, _str, [new IrConst(IrType.Bool, 0)])), Is.EqualTo("false"));
    Assert.That(Evaluated(new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, _str, [new IrConst(IrType.Bool, 1)])), Is.EqualTo("True"));
    Assert.That(Evaluated(new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, _str, [new IrConst(IrType.Bool, 0)])), Is.EqualTo("False"));
  }

  /// <summary>Test design 6: the ties of the fixed notations, the extreme digits and 10^21, as .NET 10 and node compute them.</summary>
  [TestCase(0.5, 0, "1", "0")]
  [TestCase(1.5, 0, "2", "2")]
  [TestCase(2.5, 0, "3", "2")]
  [TestCase(-2.5, 0, "-3", "-2")]
  [TestCase(0.125, 2, "0.13", "0.12")]
  [TestCase(1.005, 2, "1.00", "1.00")]
  [TestCase(-0.0, 2, "0.00", "-0.00")]
  [TestCase(-0.001, 2, "-0.00", "-0.00")]
  [TestCase(1e21, 2, "1e+21", "1000000000000000000000.00")]
  [TestCase(-1e21, 0, "-1e+21", "-1000000000000000000000")]
  [TestCase(double.NaN, 2, "NaN", "NaN")]
  [TestCase(double.NegativeInfinity, 1, "-Infinity", "-Infinity")]
  [TestCase(0.1, 15, "0.100000000000000", "0.100000000000000")]
  [TestCase(123.456, 15, "123.456000000000003", "123.456000000000003")]
  [TestCase(5e-324, 0, "0", "0")]
  public void Fixed_RoundsTheExactValueByItsRule(double value, int digits, string ecma, string dotNet) {
    Assert.That(Evaluated(NumberTextOperations.Fixed(IrIntrinsicOp.FixedEcma, F64(value), digits)), Is.EqualTo(ecma));
    Assert.That(Evaluated(NumberTextOperations.Fixed(IrIntrinsicOp.FixedDotNet, F64(value), digits)), Is.EqualTo(dotNet));
  }

  [Test]
  public void FixedEcma_Writes100Digits() {
    var text = NumberTextOperations.FixedEcma(0.1, 100);
    Assert.That(text, Is.EqualTo("0.1000000000000000055511151231257827021181583404541015625" + new string('0', 45)));
    Assert.That(NumberTextOperations.FixedEcma(5e-324, 100), Does.StartWith("0.000").And.EndWith("0"));
  }

  /// <summary>Test design 7: every parser of ECMA-262 on the classes of the test design, as node computes them.</summary>
  [TestCase("", 0.0, double.NaN, double.NaN)]
  [TestCase("  ", 0.0, double.NaN, double.NaN)]
  [TestCase(" 12 ", 12.0, 12.0, 12.0)]
  [TestCase("-0", -0.0, -0.0, -0.0)]
  [TestCase("+5", 5.0, 5.0, 5.0)]
  [TestCase(".5", 0.5, 0.5, double.NaN)]
  [TestCase("5.", 5.0, 5.0, 5.0)]
  [TestCase("1e3", 1000.0, 1000.0, 1.0)]
  [TestCase("1e", double.NaN, 1.0, 1.0)]
  [TestCase("12px", double.NaN, 12.0, 12.0)]
  [TestCase("0x1A", 26.0, 0.0, 26.0)]
  [TestCase("0x", double.NaN, 0.0, double.NaN)]
  [TestCase("-0x10", double.NaN, -0.0, -16.0)]
  [TestCase("0o17", 15.0, 0.0, 0.0)]
  [TestCase("0b101", 5.0, 0.0, 0.0)]
  [TestCase("0b102", double.NaN, 0.0, 0.0)]
  [TestCase("1,000", double.NaN, 1.0, 1.0)]
  [TestCase("1_000", double.NaN, 1.0, 1.0)]
  [TestCase("Infinity", double.PositiveInfinity, double.PositiveInfinity, double.NaN)]
  [TestCase("-Infinity", double.NegativeInfinity, double.NegativeInfinity, double.NaN)]
  [TestCase("Infinityx", double.NaN, double.PositiveInfinity, double.NaN)]
  [TestCase("infinity", double.NaN, double.NaN, double.NaN)]
  [TestCase("∞", double.NaN, double.NaN, double.NaN)]
  [TestCase("NaN", double.NaN, double.NaN, double.NaN)]
  [TestCase("٣", double.NaN, double.NaN, double.NaN)]
  [TestCase("﻿7 ", 7.0, 7.0, 7.0)]
  [TestCase("\u00857", double.NaN, double.NaN, double.NaN)]
  [TestCase("1e400", double.PositiveInfinity, double.PositiveInfinity, 1.0)]
  [TestCase("-1e-400", -0.0, -0.0, -1.0)]
  [TestCase("123456789012345678901234567890", 1.2345678901234568e+29, 1.2345678901234568e+29, 1.2345678901234568e+29)]
  public void ParseEcma_FollowsTheGrammarOfEachFunction(string text, double number, double parsedFloat, double parsedInt) {
    double Parsed(IrIntrinsicOp op) => (double)Evaluated(new IrIntrinsic(op, IrType.F64, [Text(text)]))!;
    Assert.That(Same(Parsed(IrIntrinsicOp.ParseNumberEcma), number), Is.True, "Number");
    Assert.That(Same(Parsed(IrIntrinsicOp.ParseFloatEcma), parsedFloat), Is.True, "parseFloat");
    Assert.That(Same(Parsed(IrIntrinsicOp.ParseIntEcma), parsedInt), Is.True, "parseInt");
  }

  /// <summary>Whether two doubles are the same: the same bits, or both a NaN.</summary>
  private static bool Same(double actual, double expected)
    => double.IsNaN(expected) ? double.IsNaN(actual) : BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected);

  /// <summary>Test design 7: parseInt with a radix: 16 drops a 0x, any other radix does not, a radix outside 2 to 36 is a NaN.</summary>
  [TestCase("0x1A", 16, 26.0)]
  [TestCase("0x1A", 10, 0.0)]
  [TestCase("1A", 16, 26.0)]
  [TestCase("zz", 36, 1295.0)]
  [TestCase("ZZ", 36, 1295.0)]
  [TestCase("12", 1, double.NaN)]
  [TestCase("12", 37, double.NaN)]
  [TestCase("12", -2, double.NaN)]
  [TestCase("777", 8, 511.0)]
  [TestCase("8", 8, double.NaN)]
  [TestCase("0000000000000000000000000000001", 7, 1.0)]
  [TestCase("6666666666666666666666666", 7, 1.3410686196639648e+21)]
  public void ParseIntEcma_TakesItsRadix(string text, int radix, double expected) {
    var value = (double)Evaluated(new IrIntrinsic(IrIntrinsicOp.ParseIntEcma, IrType.F64, [Text(text), I32(radix)]))!;
    Assert.That(Same(value, expected), Is.True, value.ToString("R", CultureInfo.InvariantCulture));
  }

  /// <summary>Test design 7, 8 and 9: .NET's Parse takes its grammar and traps a malformed text before an integer out of range.</summary>
  [TestCase("1,000", "f64", "1000")]
  [TestCase("infinity", "f64", "Infinity")]
  [TestCase(" -INFINITY ", "f64", "-Infinity")]
  [TestCase("nan", "f64", "NaN")]
  [TestCase("1e400", "f64", "Infinity")]
  [TestCase("-1e-400", "f64", "-0")]
  [TestCase("0x10", "f64", "trap: invalid format")]
  [TestCase("12px", "f64", "trap: invalid format")]
  [TestCase("", "f64", "trap: invalid format")]
  [TestCase("∞", "f64", "trap: invalid format")]
  [TestCase("3.4028235e38", "f32", "3.4028235E+38")]
  [TestCase("3.4028236e38", "f32", "Infinity")]
  [TestCase("1e39", "f32", "Infinity")]
  [TestCase("2147483647", "i32", "2147483647")]
  [TestCase("2147483648", "i32", "trap: integer overflow")]
  [TestCase("-2147483648", "i32", "-2147483648")]
  [TestCase("-0", "i32", "0")]
  [TestCase("-0", "u32", "0")]
  [TestCase("-1", "u32", "trap: integer overflow")]
  [TestCase("99999999999x", "i32", "trap: invalid format")]
  [TestCase("1,000", "i32", "trap: invalid format")]
  [TestCase("1.0", "i32", "trap: invalid format")]
  [TestCase("256", "u8", "trap: integer overflow")]
  [TestCase("-129", "i8", "trap: integer overflow")]
  [TestCase("65535", "u16", "65535")]
  [TestCase("-32768", "i16", "-32768")]
  [TestCase("18446744073709551615", "u64", "18446744073709551615")]
  [TestCase("-9223372036854775808", "i64", "-9223372036854775808")]
  [TestCase("9223372036854775808", "i64", "trap: integer overflow")]
  public void ParseDotNet_FollowsDotNet(string text, string type, string expected) {
    var target = type switch {
      "f64" => IrType.F64, "f32" => IrType.F32, "i32" => IrType.I32, "u32" => IrType.U32, "u8" => IrType.U8, "i8" => IrType.I8,
      "u16" => IrType.U16, "i16" => IrType.I16, "u64" => IrType.U64, _ => IrType.I64
    };
    string outcome;
    try {
      outcome = Evaluated(new IrIntrinsic(IrIntrinsicOp.ParseDotNet, target, [Text(text)])) switch {
        double number => number.ToString(CultureInfo.InvariantCulture),
        float single => single.ToString(CultureInfo.InvariantCulture),
        var other => Convert.ToString(other, CultureInfo.InvariantCulture)!
      };
    } catch (IrTrapException trap) {
      outcome = PrototypeFixture.Trap(trap.Trap);
    }

    Assert.That(outcome, Is.EqualTo(expected));
  }

  /// <summary>Test design 2 and 3: the interpreter writes the committed digits of .NET 10 and node for every reference vector.</summary>
  [Test]
  public void Interpreter_WritesTheReferenceVectors() {
    var mismatches = new List<string>();
    var count = 0;
    foreach (var (kind, bits, dotNet, ecma) in NumberTextModules.Vectors()) {
      ++count;
      if (kind == "f64") {
        var value = BitConverter.UInt64BitsToDouble(bits);
        if (NumberTextOperations.FormatDotNet(value) != dotNet || NumberTextOperations.FormatEcma(value) != ecma)
          mismatches.Add($"{bits:x16}: {NumberTextOperations.FormatDotNet(value)} {NumberTextOperations.FormatEcma(value)}");
      } else if (NumberTextOperations.FormatDotNet(BitConverter.UInt32BitsToSingle((uint)bits)) != dotNet) {
        mismatches.Add($"{bits:x8}: {NumberTextOperations.FormatDotNet(BitConverter.UInt32BitsToSingle((uint)bits))}");
      }
    }

    Assert.That((count, mismatches.Take(10)), Is.EqualTo((30000, Array.Empty<string>())));
  }

  /// <summary>The integer as the nearest double: a tie goes to the even one, and beyond the largest double it is infinity.</summary>
  [Test]
  public void Nearest_RoundsAnIntegerToEven() {
    Assert.That(NumberTextOperations.Nearest((System.Numerics.BigInteger.One << 53) + 1), Is.EqualTo(9007199254740992.0));
    Assert.That(NumberTextOperations.Nearest((System.Numerics.BigInteger.One << 53) + 3), Is.EqualTo(9007199254740996.0));
    Assert.That(NumberTextOperations.Nearest(((System.Numerics.BigInteger.One << 54) + 3) << 10), Is.EqualTo(Math.ScaleB(4503599627370497.0, 12)));
    Assert.That(NumberTextOperations.Nearest(System.Numerics.BigInteger.One << 1024), Is.EqualTo(double.PositiveInfinity));
  }

  [Test]
  public void ConversionsAreStaticInitializers() {
    var global = new IrGlobal("G", _str, new IrIntrinsic(IrIntrinsicOp.FormatEcma, _str, [I32(7)]));
    var function = new IrFunction("F", true, _str, [], [], new IrBlock([new IrReturn(new IrGlobalRef(global))]));
    var module = new IrModule("M", [function]) { Globals = [global] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo("7"));
  }

}
