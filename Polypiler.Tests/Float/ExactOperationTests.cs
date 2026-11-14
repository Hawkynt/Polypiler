using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// Test design 5 of #1301: every exact operation on the boundary set and 1 000 random operands of
/// seed 2026, in both formats, against .NET 10 as the oracle (or the definition, where the IR defines
/// the operation itself), and the same operands through the intrinsic in the interpreter and through
/// the implementation the frontend imports from the embedded source, all bit for bit.
/// </summary>
[TestFixture]
public sealed class ExactOperationTests {

  private static readonly IrType[] _formats = [IrType.F32, IrType.F64];

  private static readonly int[] _exponents = [int.MinValue, -2000, -1130, -1075, -1074, -1023, -1022, -1000, -150, -149, -127, -126, -60, -1, 0, 1, 60, 127, 128, 1000, 1023, 1024, 2000, 2100, int.MaxValue];

  /// <summary>The oracle of every exact operation with an implementation: .NET's own method, or the definition.</summary>
  private static readonly Dictionary<IrIntrinsicOp, (Func<double[], object> Double, Func<float[], object> Single)> _oracles = new() {
    [IrIntrinsicOp.FusedMultiplyAdd] = (a => Math.FusedMultiplyAdd(a[0], a[1], a[2]), a => MathF.FusedMultiplyAdd(a[0], a[1], a[2])),
    [IrIntrinsicOp.IeeeRemainder] = (a => Math.IEEERemainder(a[0], a[1]), a => MathF.IEEERemainder(a[0], a[1])),
    [IrIntrinsicOp.Reciprocal] = (a => 1 / a[0], a => 1 / a[0]),
    [IrIntrinsicOp.CopySign] = (a => Math.CopySign(a[0], a[1]), a => MathF.CopySign(a[0], a[1])),
    [IrIntrinsicOp.MinMagnitude] = (a => Math.MinMagnitude(a[0], a[1]), a => MathF.MinMagnitude(a[0], a[1])),
    [IrIntrinsicOp.MaxMagnitude] = (a => Math.MaxMagnitude(a[0], a[1]), a => MathF.MaxMagnitude(a[0], a[1])),
    [IrIntrinsicOp.MinNumber] = (a => double.MinNumber(a[0], a[1]), a => float.MinNumber(a[0], a[1])),
    [IrIntrinsicOp.MaxNumber] = (a => double.MaxNumber(a[0], a[1]), a => float.MaxNumber(a[0], a[1])),
    [IrIntrinsicOp.Clamp] = (a => Math.Clamp(a[0], a[1], a[2]), a => Math.Clamp(a[0], a[1], a[2])),
    [IrIntrinsicOp.ScaleB] = (a => Math.ScaleB(a[0], (int)a[1]), a => MathF.ScaleB(a[0], (int)a[1])),
    [IrIntrinsicOp.ILogB] = (a => Math.ILogB(a[0]), a => MathF.ILogB(a[0])),
    [IrIntrinsicOp.BitIncrement] = (a => Math.BitIncrement(a[0]), a => MathF.BitIncrement(a[0])),
    [IrIntrinsicOp.BitDecrement] = (a => Math.BitDecrement(a[0]), a => MathF.BitDecrement(a[0])),
    [IrIntrinsicOp.Fraction] = (a => Frexp(a[0]).Fraction, a => (float)Frexp(a[0]).Fraction),
    [IrIntrinsicOp.Exponent] = (a => Frexp(a[0]).Exponent, a => Frexp(a[0]).Exponent),
    [IrIntrinsicOp.IsNaN] = (a => double.IsNaN(a[0]), a => float.IsNaN(a[0])),
    [IrIntrinsicOp.IsInfinity] = (a => double.IsInfinity(a[0]), a => float.IsInfinity(a[0])),
    [IrIntrinsicOp.IsFinite] = (a => double.IsFinite(a[0]), a => float.IsFinite(a[0])),
    [IrIntrinsicOp.IsNegative] = (a => double.IsNegative(a[0]), a => float.IsNegative(a[0])),
    [IrIntrinsicOp.IsNormal] = (a => double.IsNormal(a[0]), a => float.IsNormal(a[0])),
    [IrIntrinsicOp.IsSubnormal] = (a => double.IsSubnormal(a[0]), a => float.IsSubnormal(a[0])),
    [IrIntrinsicOp.IsInteger] = (a => double.IsInteger(a[0]), a => float.IsInteger(a[0])),
    [IrIntrinsicOp.DegreesToRadians] = (a => double.DegreesToRadians(a[0]), a => float.DegreesToRadians(a[0])),
    [IrIntrinsicOp.RadiansToDegrees] = (a => double.RadiansToDegrees(a[0]), a => float.RadiansToDegrees(a[0])),
    [IrIntrinsicOp.Lerp] = (a => a[0] + (a[1] - a[0]) * a[2], a => a[0] + (a[1] - a[0]) * a[2])
  };

  private static IEnumerable<TestCaseData> Cases
    => FloatOperationTable.All.Where(operation => operation.Double is not null && operation.Contract.Kind == FloatContractKind.Exact && operation.Shape != FloatShape.WithDigits)
      .SelectMany(operation => _formats.Select(format => new TestCaseData(operation, format).SetArgDisplayNames(operation.Op.ToString(), format.ToString())));

  [Test]
  public void EveryExactOperationWithAnImplementation_HasAnOracle() {
    var exact = FloatOperationTable.All.Where(operation => operation.Double is not null && operation.Contract.Kind == FloatContractKind.Exact && operation.Shape != FloatShape.WithDigits);
    Assert.That(exact.Select(operation => operation.Op).Where(op => !_oracles.ContainsKey(op)), Is.Empty);
  }

  [TestCaseSource(nameof(Cases))]
  public void Operation_AgreesWithItsOracle_TheInterpreterAndTheImportedSource(object operation, object format) {
    var (target, type) = ((FloatOperation)operation, (IrType)format);
    var oracle = _oracles[target.Op];

    var failures = new List<string>();
    foreach (var operands in Operands(target, type)) {
      if (target.Op == IrIntrinsicOp.Clamp && operands[1] > operands[2])
        continue;

      var expected = type == IrType.F32 ? oracle.Single([.. operands.Select(operand => (float)operand)]) : oracle.Double(operands);
      var compiled = Compiled(target, type, operands);
      var interpreted = FloatIntrinsics.Evaluate(target, type, operands);
      var imported = Imported(target, type, operands);
      if (!Same(expected, compiled) || !Same(compiled, interpreted) || !Same(compiled, imported))
        failures.Add($"{target.Op}({string.Join(", ", operands)}): oracle {Show(expected)}, compiled {Show(compiled)}, interpreted {Show(interpreted)}, imported {Show(imported)}");
    }

    Assert.That(failures, Is.Empty, $"{failures.Count} failures; the first: {string.Join("; ", failures.Take(5))}");
  }

  /// <summary>Test design 9: rounding to digits in every mode, on the ties and the boundary set, for the digits at the ends of the range.</summary>
  [Test]
  public void RoundDigits_AgreesWithDotNet_InEveryMode([Values(0, 1, 2, 3, 4)] int mode) {
    var operation = FloatOperationTable.Of(IrIntrinsicOp.RoundDigits)!;
    var failures = new List<string>();
    foreach (var format in _formats) {
      int[] digits = format == IrType.F32 ? [0, 1, 3, 6] : [0, 1, 2, 7, 15];
      foreach (var value in FloatSamples.Boundaries(format).Concat(FloatSamples.Random(format, 300, 7)))
        foreach (var count in digits) {
          object expected = format == IrType.F32
            ? MathF.Round((float)value, count, (MidpointRounding)mode)
            : (object)Math.Round(value, count, (MidpointRounding)mode);
          object compiled = format == IrType.F32
            ? FloatOperations.RoundDigitsF((float)value, count, mode)
            : (object)FloatOperations.RoundDigits(value, count, mode);
          var interpreted = FloatIntrinsics.Evaluate(operation, format, [value, count], mode);
          if (!Same(expected, compiled) || !Same(compiled, interpreted))
            failures.Add($"{format} Round({value:R}, {count}, {(MidpointRounding)mode}): {Show(expected)}, {Show(compiled)}, {Show(interpreted)}");
        }
    }

    Assert.That(failures, Is.Empty, string.Join("; ", failures.Take(5)));
  }

  /// <summary>Test design 9: the ties at ±0.5, ±1.5, ±2.5, 2^52 ± 0.5 and 2^23 ± 0.5 go where each mode sends them.</summary>
  [TestCase(0.5, 0, 0.0, 1.0, 0.0, 0.0, 1.0)]
  [TestCase(-0.5, 0, -0.0, -1.0, -0.0, -1.0, -0.0)]
  [TestCase(1.5, 0, 2.0, 2.0, 1.0, 1.0, 2.0)]
  [TestCase(-2.5, 0, -2.0, -3.0, -2.0, -3.0, -2.0)]
  [TestCase(4503599627370495.5, 0, 4503599627370496.0, 4503599627370496.0, 4503599627370495.0, 4503599627370495.0, 4503599627370496.0)]
  [TestCase(0.125, 2, 0.12, 0.13, 0.12, 0.12, 0.13)]
  [TestCase(2.5, 15, 2.5, 2.5, 2.5, 2.5, 2.5)]
  public void RoundDigits_SendsATieWhereTheModeSays(double value, int digits, double even, double away, double zero, double down, double up) {
    double[] expected = [even, away, zero, down, up];
    for (var mode = 0; mode < 5; ++mode)
      Assert.That(BitConverter.DoubleToInt64Bits(FloatOperations.RoundDigits(value, digits, mode)), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected[mode])), $"mode {mode}");
  }

  [TestCase(8388607.5f, 8388608.0f)]
  [TestCase(-2.5f, -2.0f)]
  public void RoundDigitsOfFloat_RoundsATieToEven(float value, float expected)
    => Assert.That(FloatOperations.RoundDigitsF(value, 0, FloatOperations.RoundToEven), Is.EqualTo(expected));

  /// <summary>Test design 11: digits outside the range trap, the ends of the range do not.</summary>
  [TestCase(16, 7)]
  [TestCase(-1, -1)]
  public void RoundDigits_OutsideItsRange_Traps(int digits64, int digits32) {
    var operation = FloatOperationTable.Of(IrIntrinsicOp.RoundDigits)!;
    Assert.That(() => FloatIntrinsics.Evaluate(operation, IrType.F64, [1.5, digits64]), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.InvalidOperation));
    Assert.That(() => FloatIntrinsics.Evaluate(operation, IrType.F32, [1.5, digits32]), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.InvalidOperation));
    Assert.That(FloatIntrinsics.Evaluate(operation, IrType.F64, [1.25, 15]), Is.EqualTo(1.25));
    Assert.That(FloatIntrinsics.Evaluate(operation, IrType.F32, [1.25, 6]), Is.EqualTo(1.25f));
  }

  [Test]
  public void Clamp_WithTheLowerBoundAboveTheUpper_Traps() {
    var operation = FloatOperationTable.Of(IrIntrinsicOp.Clamp)!;
    foreach (var format in _formats) {
      Assert.That(() => FloatIntrinsics.Evaluate(operation, format, [1.0, 2.0, 1.0]), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.InvalidOperation));
      Assert.That(Same(FloatIntrinsics.Evaluate(operation, format, [1.0, double.NaN, 1.0]), format == IrType.F32 ? 1.0f : (object)1.0), Is.True);
    }

    Assert.That(() => Math.Clamp(1.0, 2.0, 1.0), Throws.ArgumentException, "the oracle traps as well");
  }

  /// <summary>Test design 6: the special values of the table, in the interpreter and in the compiled implementation.</summary>
  [Test]
  public void SpecialValues_OfTheTable_AreMetExactly() {
    var values = FloatOperationTable.All.SelectMany(operation => operation.SpecialValues.Select(value => (operation, value))).ToList();
    Assert.That(values, Is.Not.Empty);
    foreach (var (operation, value) in values) {
      var types = FloatIntrinsics.Operands(operation.Shape, value.Format);
      var operands = value.Operands.Select((bits, index) => types[index] == IrType.I32 ? bits : FloatVectors.Value(value.Format, bits)).ToArray();
      var interpreted = FloatImplementations.Bits(value.Format, FloatIntrinsics.Evaluate(operation, value.Format, operands));
      Assert.That(FloatVectors.Same(value.Format, value.Result, interpreted), Is.True, $"{operation.Op} {value.Format}: {value.Note}");
    }
  }

  private static IEnumerable<double[]> Operands(FloatOperation operation, IrType format) {
    var boundaries = FloatSamples.Boundaries(format);
    var random = FloatSamples.Random(format, 1000, (ulong)operation.Op);
    switch (operation.Shape) {
      case FloatShape.Binary:
        foreach (var a in boundaries)
          foreach (var b in boundaries)
            yield return [a, b];

        for (var index = 0; index + 1 < random.Count; index += 2)
          yield return [random[index], random[index + 1]];

        for (var index = 0; index < random.Count; ++index)
          yield return [random[index], FloatSamples.Round(format, random[index] * 0.75)];

        break;
      case FloatShape.Ternary:
        for (var index = 0; index + 2 < boundaries.Count; ++index)
          foreach (var c in boundaries)
            yield return [boundaries[index], boundaries[index + 1], c];

        for (var index = 0; index + 2 < random.Count; index += 3)
          yield return [random[index], random[index + 1], random[index + 2]];

        break;
      case FloatShape.WithInteger:
        foreach (var value in boundaries.Concat(random.Take(200)))
          foreach (var exponent in _exponents)
            yield return [value, exponent];

        break;
      default:
        foreach (var value in boundaries.Concat(random))
          yield return [value];

        break;
    }
  }

  private static object Compiled(FloatOperation operation, IrType format, double[] operands) {
    var method = FloatImplementations.Method(format == IrType.F32 ? operation.SingleName! : operation.DoubleName!)!;
    return method.Invoke(null, Arguments(method, format, operands))!;
  }

  private static object Imported(FloatOperation operation, IrType format, double[] operands) {
    var method = FloatImplementations.Method(format == IrType.F32 ? operation.SingleName! : operation.DoubleName!)!;
    return IrInterpreter.Call(FloatImplementations.Module, method.Name, Arguments(method, format, operands))!;
  }

  private static object[] Arguments(System.Reflection.MethodInfo method, IrType format, double[] operands)
    => method.GetParameters().Select((parameter, index) => parameter.ParameterType == typeof(int) ? (object)(int)operands[index]
      : format == IrType.F32 ? (float)operands[index] : (object)operands[index]).ToArray();

  /// <summary>Two results are the same: equal bits, any NaN equal to any NaN, an integer or truth value equal.</summary>
  private static bool Same(object? a, object? b) => (a, b) switch {
    (float x, float y) => float.IsNaN(x) ? float.IsNaN(y) : BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
    (double x, double y) => double.IsNaN(x) ? double.IsNaN(y) : BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
    _ => Equals(a, b)
  };

  private static string Show(object? value) => value switch {
    float x => $"{x:R} ({BitConverter.SingleToInt32Bits(x):x8})",
    double x => $"{x:R} ({BitConverter.DoubleToInt64Bits(x):x16})",
    _ => $"{value}"
  };

  /// <summary>C's <c>frexp</c> computed from .NET's exact operations: the exponent one above <c>ILogB</c>.</summary>
  private static (double Fraction, int Exponent) Frexp(double x) {
    if (x == 0 || !double.IsFinite(x))
      return (x, 0);

    var exponent = Math.ILogB(x) + 1;
    return (Math.ScaleB(x, -exponent), exponent);
  }

}
