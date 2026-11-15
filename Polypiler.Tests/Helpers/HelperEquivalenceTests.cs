using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// Test design 1 and 2 of #1353: every operation the library has a helper for computes the same as
/// its intrinsic, in the interpreter, the trap kind included - as legalization writes it and after
/// the passes over the legalized module (inlining, propagation, folding, dead code, unrolling). An
/// 8-bit first operand takes every value, a wider one its edges and random values (fixed seed).
/// </summary>
[TestFixture]
public sealed class HelperEquivalenceTests {

  private static IEnumerable<TestCaseData> IntegerCases()
    => from op in IntegerOperations.Operations.Order()
       from signature in IntegerOperations.Signatures(op)
       where !signature.Result.IsFloat && !signature.Arguments.Any(argument => argument.IsFloat)
       select new TestCaseData(op, signature.Result, signature.Arguments).SetArgDisplayNames(op + "_" + string.Join("_", signature.Arguments));

  [TestCaseSource(nameof(IntegerCases))]
  public void IntegerHelper_ComputesTheOperation(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    var module = HelperHarness.Module(op, result, arguments);
    var legalized = HelperHarness.Legalized(module);
    var optimized = HelperHarness.Optimized(legalized);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    foreach (var list in HelperHarness.Arguments(op, arguments, 600, 1353)) {
      var expected = HelperHarness.Outcome(module, list);
      Assert.That((HelperHarness.Outcome(legalized, list), HelperHarness.Outcome(optimized, list)), Is.EqualTo((expected, expected)), $"{op}({string.Join(", ", list.Select(Show))})");
    }
  }

  private static readonly double[] _numbers = [
    0.0, -0.0, 0.5, -0.5, 1.5, -1.5, 2.5, -2.5, 0.49999999999999994, 3.7, -3.7, 1e16, -1e16, 1e300, 4294967296.5, -2147483648.5,
    double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, 123.456789, -98765.4321, 2147483647.0, 9.2233720368547758e18
  ];

  /// <summary>The floating-point operations a JavaScript profile below ECMAScript 2015 writes as helpers of the library.</summary>
  [TestCase(IrIntrinsicOp.Sign, "f64", "i32")]
  [TestCase(IrIntrinsicOp.Sign, "f32", "i32")]
  [TestCase(IrIntrinsicOp.RoundEven, "f64", "f64")]
  [TestCase(IrIntrinsicOp.RoundEven, "f32", "f32")]
  [TestCase(IrIntrinsicOp.RoundAway, "f64", "f64")]
  [TestCase(IrIntrinsicOp.Truncate, "f64", "f64")]
  [TestCase(IrIntrinsicOp.Clamp, "f64", "f64")]
  [TestCase(IrIntrinsicOp.Clamp, "f32", "f32")]
  [TestCase(IrIntrinsicOp.RoundDigits, "f64", "f64")]
  [TestCase(IrIntrinsicOp.RoundDigits, "f32", "f32")]
  public void JavaScriptFloatHelper_ComputesTheOperation(IrIntrinsicOp op, string operand, string result)
    => FloatEquivalence(op, operand, result, module => JavaScriptEmitter.LegalizeHelpers(module, () => HelperHarness.Library, JavaScriptProfile.Parse("es5")));

  /// <summary>The floating-point operations .NET Framework 2.0 writes as helpers of the library.</summary>
  [TestCase(IrIntrinsicOp.Min, "f64", "f64")]
  [TestCase(IrIntrinsicOp.Max, "f64", "f64")]
  [TestCase(IrIntrinsicOp.Min, "f32", "f32")]
  [TestCase(IrIntrinsicOp.Max, "f32", "f32")]
  [TestCase(IrIntrinsicOp.RoundHalfUp, "f64", "f64")]
  [TestCase(IrIntrinsicOp.RoundHalfUp, "f32", "f32")]
  [TestCase(IrIntrinsicOp.ToInt32, "f64", "i32")]
  [TestCase(IrIntrinsicOp.ToUint32, "f64", "u32")]
  [TestCase(IrIntrinsicOp.Clamp, "f64", "f64")]
  [TestCase(IrIntrinsicOp.RoundDigits, "f32", "f32")]
  public void CSharpFloatHelper_ComputesTheOperation(IrIntrinsicOp op, string operand, string result)
    => FloatEquivalence(op, operand, result, module => CSharpEmitter.LegalizeHelpers(module, () => HelperHarness.Library, CSharpProfile.Net20));

  /// <summary>The conversions of a number to an integer that saturate, which no cast of .NET Framework 2.0 does.</summary>
  [TestCase("i8")]
  [TestCase("u16")]
  [TestCase("i32")]
  [TestCase("u32")]
  [TestCase("i64")]
  [TestCase("u64")]
  public void SaturatingConversion_IsTheHelpersInNetFramework20(string target) {
    var type = TypeOf(target);
    var value = new IrLocal("v", IrType.F64);
    var module = new IrModule("M", [new IrFunction("F", true, type, [value], [], new IrBlock([new IrReturn(new IrConvert(type, new IrLocalRef(value), IrConversion.Saturate))]))]);
    var legalized = CSharpEmitter.LegalizeHelpers(module, () => HelperHarness.Library, CSharpProfile.Net20);
    Assert.That(legalized.Implementations.Select(pair => pair.Implementation), Has.Some.StartsWith("SaturateTo"));
    var optimized = HelperHarness.Optimized(legalized);
    foreach (var number in _numbers) {
      var expected = HelperHarness.Outcome(module, [number]);
      Assert.That((HelperHarness.Outcome(legalized, [number]), HelperHarness.Outcome(optimized, [number])), Is.EqualTo((expected, expected)), $"{target}({number})");
    }
  }

  private static void FloatEquivalence(IrIntrinsicOp op, string operand, string result, Func<IrModule, IrModule> legalize) {
    var type = TypeOf(operand);
    IReadOnlyList<IrType> arguments = op switch {
      IrIntrinsicOp.Clamp => [type, type, type],
      IrIntrinsicOp.RoundDigits => [type, IrType.I32, IrType.I32],
      IrIntrinsicOp.Min or IrIntrinsicOp.Max => [type, type],
      _ => [type]
    };
    var module = HelperHarness.Module(op, TypeOf(result), arguments);
    var legalized = legalize(module);
    Assert.That(legalized.Implementations, Is.Not.Empty, "the profile writes the operation as a helper");
    var optimized = HelperHarness.Optimized(legalized);
    var random = new Random(2026);
    for (var index = 0; index < 400; ++index) {
      var list = arguments.Select((argument, position) => Value(argument, position, random)).ToArray();
      var expected = HelperHarness.Outcome(module, list);
      Assert.That((HelperHarness.Outcome(legalized, list), HelperHarness.Outcome(optimized, list)), Is.EqualTo((expected, expected)), $"{op}({string.Join(", ", list.Select(Show))})");
    }
  }

  private static object Value(IrType type, int position, Random random) {
    if (type == IrType.I32)
      return position == 1 ? random.Next(-2, 18) : random.Next(-1, 6);

    var number = random.Next(3) == 0 ? (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-3, 20)) : _numbers[random.Next(_numbers.Length)];
    return type == IrType.F32 ? (object)(float)number : number;
  }

  private static IrType TypeOf(string name) => name switch {
    "i8" => IrType.I8,
    "u16" => IrType.U16,
    "i32" => IrType.I32,
    "u32" => IrType.U32,
    "i64" => IrType.I64,
    "u64" => IrType.U64,
    "f32" => IrType.F32,
    _ => IrType.F64
  };

  private static string Show(object value) => value is byte[] bytes ? $"[{string.Join(",", bytes)}]" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;

}
