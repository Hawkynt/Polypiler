using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Ieee;

/// <summary>
/// #117: the conversions between integers and floating-point numbers and between the two formats with
/// a rounding mode, and the conversion to an integer with its range policy - trap with its own trap
/// for a NaN, saturation, ECMA-262's wrap-around and x87's sentinel - as the interpreter computes them
/// and as the helpers compute them where legalization puts them in.
/// </summary>
[TestFixture]
public sealed class IeeeConversionTests {

  private static IrModule Module(IrIntrinsic node, IrLocal parameter)
    => new("M", [new IrFunction("F", true, node.Type, [parameter], [], new IrBlock([new IrReturn(node)]))]);

  /// <summary>The outcome of a conversion of a double to an integer type under a contract: the value as text, or the trap.</summary>
  private static string ToInteger(string type, string contract, double value) {
    var target = TypeOf(type);
    var d = new IrLocal("d", IrType.F64);
    var module = Module(FloatToIntContract.Parse(contract, target).Apply(target, new IrLocalRef(d)), d);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    try {
      return Convert.ToString(IrInterpreter.Call(module, "F", [value]), System.Globalization.CultureInfo.InvariantCulture)!;
    } catch (IrTrapException exception) {
      return "trap " + exception.Trap;
    }
  }

  private static IrType TypeOf(string name) => name switch {
    "i8" => IrType.I8,
    "u8" => IrType.U8,
    "i16" => IrType.I16,
    "u16" => IrType.U16,
    "i32" => IrType.I32,
    "u32" => IrType.U32,
    "i64" => IrType.I64,
    _ => IrType.U64
  };

  // ---------------------------------------------------------------- the scenarios of the issue

  [TestCase("i32", "rtz, sat, nan=0", 3000000000.0, "2147483647")]
  [TestCase("i32", "rtz, sat, nan=0", double.NaN, "0")]
  [TestCase("i32", "rtz, wrap", 3000000000.0, "-1294967296")]
  [TestCase("i32", "rtz, wrap", 4294967301.0, "5")]
  [TestCase("u32", "rtz, wrap", -1.0, "4294967295")]
  [TestCase("i32", "rtz, wrap", double.NaN, "0")]
  [TestCase("i32", "rtz, wrap", double.NegativeInfinity, "0")]
  [TestCase("i32", "rtz, trap Overflow", 3000000000.0, "trap IntegerOverflow")]
  [TestCase("i32", "rtz, trap Overflow", -1.5, "-1")]
  [TestCase("i32", "rtz, trap Overflow", double.NaN, "trap IntegerOverflow")]
  [TestCase("i32", "rtz, trap Overflow, nan=trap InvalidOperation", 3000000000.0, "trap IntegerOverflow")]
  [TestCase("i32", "rtz, trap Overflow, nan=trap InvalidOperation", double.NaN, "trap InvalidOperation")]
  [TestCase("i32", "rtz, trap Overflow, nan=trap InvalidOperation", 42.9, "42")]
  [TestCase("i32", "rtz, sentinel -2147483648", 3000000000.0, "-2147483648")]
  [TestCase("u8", "rtz, sat, nan=0", -3.7, "0")]
  [TestCase("u8", "rtz, sat, nan=trap InvalidOperation", double.NaN, "trap InvalidOperation")]
  [TestCase("i64", "rtz, trap Overflow, nan=trap InvalidOperation", 1e30, "trap IntegerOverflow")]
  [TestCase("i64", "rtz, trap Overflow, nan=trap InvalidOperation", double.PositiveInfinity, "trap IntegerOverflow")]
  [TestCase("i64", "rtz, trap Overflow, nan=trap InvalidOperation", double.NegativeInfinity, "trap IntegerOverflow")]
  [TestCase("i64", "round-even", 2.5, "2")]
  [TestCase("i64", "round-even", 3.5, "4")]
  [TestCase("i64", "round-even", -2.5, "-2")]
  [TestCase("i64", "round-even", 1e30, "-9223372036854775808")]
  [TestCase("i64", "round-even", double.NaN, "-9223372036854775808")]
  [TestCase("u8", "round-even, modular", 257.5, "2")]
  [TestCase("u64", "rtz, wrap", -1.0, "18446744073709551615")]
  [TestCase("u64", "rtz, sat", 1e30, "18446744073709551615")]
  [TestCase("i64", "rtz, wrap", 18446744073709551616.0 + 4096.0, "4096")]
  [TestCase("i16", "rtz, sentinel 7", double.PositiveInfinity, "7")]
  [TestCase("i16", "rtz, sentinel 7", double.NaN, "7")]
  [TestCase("i16", "rtz, sentinel 7, nan=trap DivideByZero", double.NaN, "trap DivideByZero")]
  public void FloatToInt_FollowsItsRangePolicy(string type, string contract, double value, string outcome)
    => Assert.That(ToInteger(type, contract, value), Is.EqualTo(outcome));

  /// <summary>Test design 3: each rounding mode into an integer.</summary>
  [TestCase("rne", "2,-2,4")]
  [TestCase("rtz", "2,-2,3")]
  [TestCase("rup", "3,-2,4")]
  [TestCase("rdn", "2,-3,3")]
  [TestCase("rna", "3,-3,4")]
  public void EachRoundingMode_IntoAnInteger(string mode, string expected)
    => Assert.That(string.Join(",", new[] { 2.5, -2.5, 3.5 }.Select(value => ToInteger("i32", mode + ", trap", value))), Is.EqualTo(expected));

  /// <summary>Test design 6 and 7: the bounds of the destination and the truncation window.</summary>
  [TestCase(2147483647.0, "2147483647", "2147483647")]
  [TestCase(2147483648.0, "trap IntegerOverflow", "2147483647")]
  [TestCase(-2147483648.0, "-2147483648", "-2147483648")]
  [TestCase(-2147483649.0, "trap IntegerOverflow", "-2147483648")]
  [TestCase(2147483647.9, "2147483647", "2147483647")]
  [TestCase(-2147483648.9, "-2147483648", "-2147483648")]
  public void TheBoundsOfTheDestination(double value, string trapping, string saturating) {
    Assert.That(ToInteger("i32", "rtz, trap", value), Is.EqualTo(trapping));
    Assert.That(ToInteger("i32", "rtz, sat", value), Is.EqualTo(saturating));
  }

  [Test]
  public void IntToFloat_RoundsToTheFormat() {
    string Single(RoundingMode mode, long value) {
      var i = new IrLocal("i", IrType.I32);
      return ((float)IrInterpreter.Call(Module(IeeeConversions.IntToFloat(IrType.F32, new IrLocalRef(i), mode), i), "F", [(int)value])!).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    Assert.That(Single(RoundingMode.NearestEven, 16777217), Is.EqualTo("16777216"));
    Assert.That(Single(RoundingMode.NearestEven, 16777219), Is.EqualTo("16777220"));
    Assert.That(Single(RoundingMode.Up, 16777217), Is.EqualTo("16777218"));
    Assert.That(Single(RoundingMode.Down, -16777217), Is.EqualTo("-16777218"));
    var u = new IrLocal("u", IrType.U64);
    Assert.That(IrInterpreter.Call(Module(IeeeConversions.IntToFloat(IrType.F64, new IrLocalRef(u), RoundingMode.NearestEven), u), "F", [ulong.MaxValue]), Is.EqualTo(1.8446744073709552e19));
    var s = new IrLocal("s", IrType.I64);
    Assert.That(IrInterpreter.Call(Module(IeeeConversions.IntToFloat(IrType.F64, new IrLocalRef(s), RoundingMode.TowardZero), s), "F", [-1L]), Is.EqualTo(-1.0));
    Assert.That(IrInterpreter.Call(Module(IeeeConversions.IntToFloat(IrType.F64, new IrLocalRef(s), RoundingMode.Up), s), "F", [(1L << 53) + 1]), Is.EqualTo(9007199254740994.0));
  }

  [Test]
  public void FloatConvert_OverflowsByMode() {
    var d = new IrLocal("d", IrType.F64);
    float Narrow(RoundingMode mode, double value) => (float)IrInterpreter.Call(Module(IeeeConversions.FloatConvert(IrType.F32, new IrLocalRef(d), mode), d), "F", [value])!;
    Assert.That(BitConverter.SingleToUInt32Bits(Narrow(RoundingMode.NearestEven, 1e39)), Is.EqualTo(0x7F800000u));
    Assert.That(BitConverter.SingleToUInt32Bits(Narrow(RoundingMode.TowardZero, 1e39)), Is.EqualTo(0x7F7FFFFFu));
    Assert.That(BitConverter.SingleToUInt32Bits(Narrow(RoundingMode.NearestEven, 1e-45)), Is.EqualTo(1u), "a subnormal result");
    Assert.That(BitConverter.SingleToUInt32Bits(Narrow(RoundingMode.Down, 1e-46)), Is.EqualTo(0u));
    Assert.That(BitConverter.SingleToUInt32Bits(Narrow(RoundingMode.Up, 1e-46)), Is.EqualTo(1u));
    Assert.That(float.IsNaN(Narrow(RoundingMode.Up, BitConverter.Int64BitsToDouble(0x7FF0000000000005L))), Is.True);
    Assert.That(Narrow(RoundingMode.Up, 0.1), Is.EqualTo(BitConverter.Int32BitsToSingle(0x3DCCCCCD)));
    Assert.That(Narrow(RoundingMode.Down, 0.1), Is.EqualTo(BitConverter.Int32BitsToSingle(0x3DCCCCCC)));
    var f = new IrLocal("f", IrType.F32);
    Assert.That(IrInterpreter.Call(Module(IeeeConversions.FloatConvert(IrType.F64, new IrLocalRef(f), RoundingMode.Up), f), "F", [0.1f]), Is.EqualTo((double)0.1f));
    Assert.That(IrInterpreter.Call(Module(IeeeConversions.FloatConvert(IrType.F32, new IrLocalRef(f), RoundingMode.Up), f), "F", [0.1f]), Is.EqualTo(0.1f), "to its own format");
  }

  // ---------------------------------------------------------------- the text form

  [TestCase("saturate, nan→0", "rtz, sat, nan=0")]
  [TestCase("trap", "rtz, trap Overflow")]
  [TestCase("modular", "rtz, wrap")]
  [TestCase("round-even", "rne, sentinel -2147483648")]
  [TestCase("round-even, modular", "rne, wrap")]
  [TestCase("rtz, trap OverflowError", null)]
  [TestCase("rdn, trap DivideByZero, nan=trap InvalidOperation", "rdn, trap DivideByZero, nan=trap InvalidOperation")]
  [TestCase("sat", "rtz, sat, nan=0")]
  [TestCase("rup, sentinel 5", "rup, sentinel 5")]
  public void TheShortContracts_ExpandToOneCanonicalForm(string text, string? canonical) {
    var parsed = FloatToIntContract.TryParse(text, IrType.I32, out var contract, out var error);
    Assert.That(parsed, Is.EqualTo(canonical is not null), error);
    Assert.That(contract?.ToString(), Is.EqualTo(canonical));
  }

  [TestCase("", "requires a contract")]
  [TestCase("rne, rtz", "two rounding modes")]
  [TestCase("sat, wrap", "two range policies")]
  [TestCase("wrap, nan=0", "nan=0 belongs to sat alone")]
  [TestCase("rne, nan=0", "nan=0 belongs to sat alone")]
  [TestCase("sat, nan=0, nan=trap InvalidOperation", "nan=0 belongs to sat alone")]
  [TestCase("trap Spurious", "unknown trap 'Spurious'")]
  [TestCase("trap, nan=trap NullReference", "unknown trap 'NullReference'")]
  [TestCase("trap ArgumentOutOfRange", "unknown trap 'ArgumentOutOfRange'")]
  [TestCase("sentinel 3000000000", "the sentinel '3000000000' is no value of i32")]
  [TestCase("sentinel x", "the sentinel 'x' is no value of i32")]
  [TestCase("rtz, fast", "unknown part 'fast'")]
  public void AMalformedContract_IsRejected(string text, string message) {
    Assert.That(FloatToIntContract.TryParse(text, IrType.I32, out var contract, out var error), Is.False);
    Assert.That((contract, error), Is.EqualTo(((FloatToIntContract?)null, error)));
    Assert.That(error, Does.Contain(message));
    Assert.That(() => FloatToIntContract.Parse(text, IrType.I32), Throws.TypeOf<FormatException>());
  }

  [Test]
  public void AContract_ConvertsToAnIntegerTypeOnly() {
    Assert.That(FloatToIntContract.TryParse("rtz, sat", IrType.F64, out _, out var error), Is.False);
    Assert.That(error, Does.Contain("not f64"));
    Assert.That(() => FloatToIntContract.TryParse(null!, IrType.I32, out _, out _), Throws.ArgumentNullException);
    Assert.That(FloatToIntContract.Parse("rne, sentinel 255", IrType.U8).Sentinel, Is.EqualTo(255));
    Assert.That(FloatToIntContract.Label(IrTrap.InvalidOperation), Is.EqualTo("InvalidOperation"));
  }

  // ---------------------------------------------------------------- the rules of the verifier

  private static IReadOnlyList<Diagnostic> Verify(IrIntrinsic node, IrLocal parameter) => IrVerifier.Verify(Module(node, parameter));

  [Test]
  public void TheContract_IsConstantsTheVerifierChecks() {
    var d = new IrLocal("d", IrType.F64);
    var mode = new IrLocal("m", IrType.I32);
    IrIntrinsic Node(IrExpr rounding, long policy = 1, IrExpr? sentinel = null, long trap = 2, long nan = -1)
      => new(IrIntrinsicOp.FloatToInt, IrType.I32, [new IrLocalRef(d), rounding, new IrConst(IrType.I32, policy), sentinel ?? new IrConst(IrType.I32, 0), new IrConst(IrType.I32, trap), new IrConst(IrType.I32, nan)]);
    var verify = (IrIntrinsic node) => IrVerifier.Verify(new IrModule("M", [new IrFunction("F", true, node.Type, [d, mode], [], new IrBlock([new IrReturn(node)]))])).Select(diagnostic => diagnostic.Message).ToList();
    var zero = new IrConst(IrType.I32, 0);
    Assert.That(verify(Node(zero)), Is.Empty);
    Assert.That(verify(Node(new IrLocalRef(mode))).Single(), Does.Contain("'FloatToInt' requires a rounding mode that is a constant"));
    Assert.That(verify(Node(new IrConst(IrType.I32, 5))).Single(), Does.Contain("unknown rounding mode 5 of FloatToInt"));
    Assert.That(verify(Node(zero, policy: 4)).Single(), Does.Contain("the range policy of FloatToInt"));
    Assert.That(verify(Node(zero, sentinel: new IrLocalRef(mode))).Single(), Does.Contain("the sentinel of FloatToInt is no constant"));
    Assert.That(verify(Node(zero, trap: (long)IrTrap.NullReference)).Single(), Does.Contain("the trap of FloatToInt"));
    Assert.That(verify(Node(zero, nan: -2)).Single(), Does.Contain("the trap of a NaN"));
    Assert.That(verify(Node(zero, nan: (long)IrTrap.InvalidOperation)), Is.Empty);
    Assert.That(verify(new IrIntrinsic(IrIntrinsicOp.IntToFloat, IrType.F64, [new IrLocalRef(mode), new IrConst(IrType.I32, 9)])).Single(), Does.Contain("unknown rounding mode 9 of IntToFloat"));
    Assert.That(IeeeConversions.Problem(new IrIntrinsic(IrIntrinsicOp.FloatConvert, IrType.F32, [new IrLocalRef(d)])), Is.Null);
    Assert.That(IeeeConversions.Problem(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [new IrLocalRef(d), zero])), Is.Null);
    Assert.That(() => IeeeConversions.Problem(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void TheTypes_AreChecked() {
    var d = new IrLocal("d", IrType.F64);
    var i = new IrLocal("i", IrType.I32);
    var zero = new IrConst(IrType.I32, 0);
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.IntToFloat, IrType.F64, [new IrLocalRef(d), zero]), d), Has.Count.EqualTo(1), "a float operand");
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.IntToFloat, IrType.I64, [new IrLocalRef(i), zero]), i), Has.Count.EqualTo(1), "an integer result");
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.FloatConvert, IrType.F32, [new IrLocalRef(i), zero]), i), Has.Count.EqualTo(1), "an integer operand");
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.FloatConvert, IrType.F32, [new IrLocalRef(d), new IrConst(IrType.I64, 0)]), d), Has.Count.EqualTo(1), "a mode of i64");
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.FloatToInt, IrType.I32, [new IrLocalRef(d), zero]), d), Has.Count.EqualTo(1), "a contract missing");
    var wrongSentinel = new IrIntrinsic(IrIntrinsicOp.FloatToInt, IrType.I32, [new IrLocalRef(d), zero, zero, new IrConst(IrType.I64, 0), zero, new IrConst(IrType.I32, -1)]);
    Assert.That(Verify(wrongSentinel, d), Has.Count.EqualTo(1), "a sentinel of another type");
    Assert.That(Verify(new IrIntrinsic(IrIntrinsicOp.FloatToInt, IrType.F32, [new IrLocalRef(d), zero, zero, new IrConst(IrType.F32, 0), zero, new IrConst(IrType.I32, -1)]), d), Has.Count.EqualTo(1), "a float result");
  }

  [Test]
  public void TheBuilders_CheckTheirArguments() {
    Assert.That(() => IeeeConversions.IntToFloat(IrType.F64, null!, RoundingMode.Up), Throws.ArgumentNullException);
    Assert.That(() => IeeeConversions.FloatConvert(IrType.F64, null!, RoundingMode.Up), Throws.ArgumentNullException);
    Assert.That(() => new FloatToIntContract(RoundingMode.Up, RangePolicy.Wrap).Apply(IrType.I32, null!), Throws.ArgumentNullException);
    Assert.That(() => FloatToIntContract.Of(null!), Throws.ArgumentNullException);
    Assert.That(() => IeeeConversions.Evaluate(null!, []), Throws.ArgumentNullException);
    Assert.That(() => IeeeConversions.Evaluate(IeeeConversions.FloatConvert(IrType.F64, new IrConst(IrType.F32, 0), RoundingMode.Up), null!), Throws.ArgumentNullException);
    Assert.That(() => IeeeConversions.ToInteger(IrType.I32, null!, 0, new FloatToIntContract(RoundingMode.Up, RangePolicy.Wrap)), Throws.ArgumentNullException);
    Assert.That(() => IeeeConversions.ToInteger(IrType.I32, IeeeFormats.Binary64, 0, null!), Throws.ArgumentNullException);
    Assert.That(IeeeConversions.Operations, Has.Count.EqualTo(3));
    var contract = new FloatToIntContract(RoundingMode.NearestAway, RangePolicy.Trap, 0, IrTrap.InvalidOperation, IrTrap.DivideByZero);
    Assert.That(FloatToIntContract.Of(contract.Apply(IrType.U16, new IrConst(IrType.F64, 0))), Is.EqualTo(contract));
  }

  // ---------------------------------------------------------------- legalization and the helpers

  private static readonly double[] _numbers = [
    0.0, -0.0, 0.5, -0.5, 1.5, -1.5, 2.5, -2.5, 3.5, 0.49999999999999994, -3.7, 42.9, 127.5, 128.0, -128.5, 255.5, 256.0, 32767.5, 65535.5,
    2147483647.0, 2147483647.5, 2147483648.0, -2147483648.0, -2147483648.5, -2147483649.0, 4294967295.5, 4294967296.0, 4294967301.0,
    9007199254740993.0, 9.2233720368547758e18, -9.2233720368547758e18, 1.8446744073709552e19, -1.8446744073709552e19, 1e30, -1e30,
    double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, 1e-300
  ];

  private static IrModule Legalized(IrModule module) => LibraryLegalization.Lower(module, () => HelperHarness.Library, FloatLegalization.Rewrite(_ => true));

  private static IEnumerable<TestCaseData> ToIntegerCases()
    => from type in new[] { "i8", "u8", "i16", "u16", "i32", "u32", "i64", "u64" }
       from contract in new[] { "rtz, trap", "rtz, sat", "rtz, wrap", "rne, wrap", "rup, trap, nan=trap InvalidOperation", "rdn, sat, nan=trap DivideByZero", "rna, sentinel 7", "rne", "rtz, sentinel 7, nan=trap InvalidOperation" }
       from source in new[] { IrType.F64, IrType.F32 }
       select new TestCaseData(type, contract, source).SetArgDisplayNames(type, contract, source.ToString());

  [TestCaseSource(nameof(ToIntegerCases))]
  public void LegalizedFloatToInt_ComputesTheNode(string type, string contract, IrType source) {
    var target = TypeOf(type);
    var d = new IrLocal("d", source);
    var module = Module(FloatToIntContract.Parse(contract, target).Apply(target, new IrLocalRef(d)), d);
    var legalized = Legalized(module);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    Assert.That(IrTree.Nodes(legalized.Functions[0].Body).OfType<IrIntrinsic>().Where(node => IeeeConversions.Is(node.Op)), Is.Empty);
    foreach (var number in _numbers) {
      object[] arguments = source == IrType.F32 ? [(float)number] : [number];
      Assert.That(HelperHarness.Outcome(legalized, arguments), Is.EqualTo(HelperHarness.Outcome(module, arguments)), $"{type}[{contract}]({number:R})");
    }
  }

  [Test]
  public void LegalizedConversionsToFloatingPoint_ComputeTheNode() {
    var random = new Random(117);
    foreach (var type in new[] { IrType.I8, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64 })
      foreach (var target in new[] { IrType.F32, IrType.F64 })
        foreach (var mode in Enum.GetValues<RoundingMode>()) {
          var i = new IrLocal("i", type);
          var module = Module(IeeeConversions.IntToFloat(target, new IrLocalRef(i), mode), i);
          var legalized = Legalized(module);
          Assert.That(IrVerifier.Verify(legalized), Is.Empty);
          var values = new long[] { 0, 1, -1, 16777217, -16777217, (1L << 53) + 1, long.MaxValue, long.MinValue, (long)IrOperators.Minimum(type), (long)(ulong)IrOperators.Maximum(type) }
            .Concat(Enumerable.Range(0, 30).Select(_ => random.NextInt64()));
          foreach (var value in values) {
            var argument = HelperHarness.Host(type, IrOperators.Wrap(type, value));
            Assert.That(HelperHarness.Outcome(legalized, [argument]), Is.EqualTo(HelperHarness.Outcome(module, [argument])), $"{type}->{target} {value} in {mode}");
          }
        }

    foreach (var mode in Enum.GetValues<RoundingMode>())
      foreach (var (source, target) in new[] { (IrType.F64, IrType.F32), (IrType.F32, IrType.F64), (IrType.F64, IrType.F64) }) {
        var d = new IrLocal("d", source);
        var module = Module(IeeeConversions.FloatConvert(target, new IrLocalRef(d), mode), d);
        var legalized = Legalized(module);
        foreach (var number in _numbers.Concat([1e39, -1e39, 1e-45, 1e-46, 0.1, -0.1, 3.4028235677973366e38]).Concat(Enumerable.Range(0, 200).Select(_ => BitConverter.Int64BitsToDouble(random.NextInt64())))) {
          object[] arguments = source == IrType.F32 ? [(float)number] : [number];
          var expected = IrInterpreter.Call(module, "F", arguments)!;
          var actual = IrInterpreter.Call(legalized, "F", arguments)!;
          Assert.That(IsNaN(actual) ? "NaN" : actual.ToString(), Is.EqualTo(IsNaN(expected) ? "NaN" : expected.ToString()), $"{source}->{target} {number:R} in {mode}");
        }
      }
  }

  private static bool IsNaN(object value) => value is float single ? float.IsNaN(single) : double.IsNaN((double)value);

  /// <summary>The helpers, compiled, against the exact conversions on random bit patterns.</summary>
  [Test]
  public void Helpers_AgreeWithTheExactConversions() {
    var random = new Random(1170);
    for (var index = 0; index < 20000; ++index) {
      var value = random.NextInt64() >> random.Next(64);
      var bits = BitConverter.DoubleToInt64Bits(random.Next(2) == 0 ? value : random.NextDouble() * Math.Pow(2, random.Next(-1100, 1100)));
      var number = BitConverter.Int64BitsToDouble(random.Next(3) == 0 ? random.NextInt64() : bits);
      for (var mode = 0; mode < 5; ++mode) {
        var rounding = (RoundingMode)mode;
        Assert.That(BitConverter.DoubleToUInt64Bits(HelperOperations.IntToDoubleRounded(value, false, mode)), Is.EqualTo(IeeeFormats.Binary64.FromInteger(value, rounding)), $"{value} in {rounding}");
        Assert.That(BitConverter.DoubleToUInt64Bits(HelperOperations.IntToDoubleRounded(value, true, mode)), Is.EqualTo(IeeeFormats.Binary64.FromInteger((ulong)value, rounding)), $"{(ulong)value} in {rounding}");
        Assert.That((ulong)BitConverter.SingleToUInt32Bits(HelperOperations.IntToSingleRounded(value, false, mode)), Is.EqualTo(IeeeFormats.Binary32.FromInteger(value, rounding)), $"{value} in {rounding}");
        var narrowed = HelperOperations.DoubleToSingleRounded(number, mode);
        var exact = IeeeFormats.Binary32.Convert(BitConverter.DoubleToUInt64Bits(number), IeeeFormats.Binary64, rounding);
        Assert.That(float.IsNaN(narrowed) ? IeeeFormats.Binary32.CanonicalNaN : BitConverter.SingleToUInt32Bits(narrowed), Is.EqualTo(exact), $"{number:R} in {rounding}");
        var integral = HelperOperations.RoundToIntegral(number, mode);
        var rounded = IeeeFormats.Binary64.ToInteger(BitConverter.DoubleToUInt64Bits(number), rounding);
        if (rounded is { } whole)
          Assert.That((System.Numerics.BigInteger)integral, Is.EqualTo(whole), $"{number:R} in {rounding}");
      }
    }
  }

}
