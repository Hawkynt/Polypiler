using System;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Specialize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Optimize;

/// <summary>
/// O0062 on Python imported from its recorded tree: what is narrowed to a fixed width and what stays
/// exact, the opt-report, and the interpreter's result before and after, which the expected values -
/// printed by CPython 3.14 for the source - hold both to.
/// </summary>
[TestFixture]
public sealed class PyIntSpecializationTests {

  private static string F(string parameters, string expression) => $"def f({parameters}) -> int:\n    return {expression}\n";

  private static (IrModule Before, IrModule After, string[] Report) Specialized(string source) {
    var before = PythonSnippet.Module(source);
    var result = PyIntSpecialization.Run(before);
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
    return (before, result.Module, [.. result.Applications.Select(application => application.Name)]);
  }

  private static IrFunction Function(IrModule module, string name = "f") => module.Functions.Single(function => function.Name == name);

  /// <summary>The operations of a function that compute with the arbitrary-precision integer.</summary>
  private static IrExpr[] Exact(IrFunction function)
    => [.. IrTree.Nodes(function.Body).Where(node => node is IrBinary { Left.Type.IsBigInt: true } or IrUnary { Type.IsBigInt: true })];

  private static IrBinary[] Fixed(IrFunction function, IrBinaryOp op, IrType type)
    => [.. IrTree.Nodes(function.Body).OfType<IrBinary>().Where(binary => binary.Op == op && binary.Type == type)];

  private static object? Call(IrModule module, string function, params object[] arguments) {
    var parameters = Function(module, function).Parameters;
    return IrInterpreter.Call(module, function, [.. arguments.Select((argument, index) => parameters[index].Type.IsBigInt && argument is long value ? (BigInteger)value : argument)]);
  }

  private static BigInteger Integer(object? value) => value switch {
    BigInteger big => big,
    ulong large => large,
    _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
  };

  /// <summary>Scenario and test design 1: a masked 32-bit addition becomes a wrapping u32 addition.</summary>
  [Test]
  public void MaskedAddition_BecomesAWrappingU32Addition() {
    var (before, after, report) = Specialized(F("a: int, b: int", "(a + b) & 0xFFFFFFFF"));
    Assert.That(Fixed(Function(after), IrBinaryOp.Add, IrType.U32), Has.Length.EqualTo(1));
    Assert.That(Exact(Function(after)), Is.Empty);
    Assert.That(report, Has.Some.EqualTo("applied: & 0xFFFFFFFF -> u32 [wrap] (mask) [proof: range-facts]"));
    Assert.That(Integer(Call(after, "f", 4294967295L, 1L)), Is.EqualTo(BigInteger.Zero));
    Assert.That(Integer(Call(before, "f", 4294967295L, 1L)), Is.EqualTo(BigInteger.Zero));
    Assert.That(Function(after).ReturnType, Is.EqualTo(IrType.U32));
  }

  /// <summary>Test design 3: every width of a mask; 8 and 16 bits are computed in 32 and masked.</summary>
  [TestCase("0xFF", 8, "u32")]
  [TestCase("0xFFFF", 16, "u32")]
  [TestCase("0xFFFFFFFF", 32, "u32")]
  [TestCase("0xFFFFFFFFFFFFFFFF", 64, "u64")]
  public void EachMaskWidth_HasItsType(string mask, int width, string type) {
    var (_, after, report) = Specialized(F("a: int, b: int", $"(a * b + 1) & {mask}"));
    Assert.That(report, Has.Some.StartsWith($"applied: & {mask} -> {type} [wrap]"));
    Assert.That(Exact(Function(after)), Is.Empty);
    var expected = (BigInteger.Parse("123456789123456789") * BigInteger.Parse("987654321987654321") + 1) & ((BigInteger.One << width) - 1);
    Assert.That(Integer(Call(after, "f", 123456789123456789L, 987654321987654321L)), Is.EqualTo(expected));
  }

  /// <summary>Test design 4 and 5: a masked multiply and shift, and a negative intermediate in a mask.</summary>
  [TestCase("(a * 16777619) & 0xFFFFFFFF", 3L, 50332857L)]
  [TestCase("(a << 5) & 0xFFFFFFFF", 4294967295L, 4294967264L)]
  [TestCase("(a - 1) & 0xFFFFFFFF", 0L, 4294967295L)]
  [TestCase("(-a) & 0xFF", 1L, 255L)]
  [TestCase("(~a) & 0xFFFF", 0L, 65535L)]
  [TestCase("(a << 40) & 0xFFFFFFFF", 1L, 0L)]
  public void LowBitClosedOperations_AreNarrowed(string expression, long a, long expected) {
    var (_, after, _) = Specialized(F("a: int", expression));
    Assert.That(Exact(Function(after)), Is.Empty);
    Assert.That(Integer(Call(after, "f", a)), Is.EqualTo(new BigInteger(expected)));
  }

  /// <summary>Test design 6: the exact-range form - the operands masked to 10 bits, the product and sum in i64.</summary>
  [Test]
  public void ExactRange_ComputesInAFixedWidth() {
    var (_, after, report) = Specialized("def f(a: int, b: int) -> int:\n    x = a & 1023\n    y = b & 1023\n    return x * y + 7\n");
    Assert.That(Function(after).Locals.Where(local => local.Name is "x" or "y").Select(local => local.Type), Is.All.EqualTo(IrType.I32));
    Assert.That(Fixed(Function(after), IrBinaryOp.Multiply, IrType.I32), Has.Length.EqualTo(1));
    Assert.That(Exact(Function(after)), Is.Empty);
    Assert.That(report, Has.Some.EqualTo("applied: 'x' held in i32, its values proven in [0, 1023] [proof: range-facts]"));
    Assert.That(Integer(Call(after, "f", 1023L, 1023L)), Is.EqualTo(new BigInteger(1046536)));
  }

  /// <summary>Test design 7 to 9: floor division and modulo keep their signs, the shift is arithmetic, a bool counts.</summary>
  [TestCase("a // b", 7L, 2L, 3L)]
  [TestCase("a // b", -7L, 2L, -4L)]
  [TestCase("a // b", 7L, -2L, -4L)]
  [TestCase("a // b", -7L, -2L, 3L)]
  [TestCase("a % b", 7L, 2L, 1L)]
  [TestCase("a % b", -7L, 2L, 1L)]
  [TestCase("a % b", 7L, -2L, -1L)]
  [TestCase("a % b", -7L, -2L, -1L)]
  [TestCase("(a >> b) + (-1 >> 1)", -1L, 1L, -2L)]
  [TestCase("(a & 0xFF) + (True + True)", -1L, 0L, 257L)]
  public void PythonSemantics_ArePreserved(string expression, long a, long b, long expected) {
    var (before, after, _) = Specialized(F("a: int, b: int", expression));
    Assert.That(Integer(Call(after, "f", a, b)), Is.EqualTo(new BigInteger(expected)));
    Assert.That(Integer(Call(before, "f", a, b)), Is.EqualTo(new BigInteger(expected)));
  }

  /// <summary>Test design 13: a zero divisor still raises ZeroDivisionError.</summary>
  [Test]
  public void ZeroDivisor_StillRaises() {
    var (_, after, _) = Specialized(F("a: int, b: int", "(a // b) & 0xFF"));
    Assert.That(() => Call(after, "f", 7L, 0L), Throws.InstanceOf<IrException>().With.Property(nameof(IrException.Class)).Property(nameof(IrClassType.Name)).EqualTo("DivideByZeroException"));
  }

  /// <summary>Scenario and test design 11, 14: 2 ** n stays exact.</summary>
  [Test]
  public void UnboundedPower_StaysExact() {
    var (_, after, report) = Specialized(F("n: int", "2 ** n"));
    Assert.That(Function(after).ReturnType, Is.EqualTo(IrType.BigInt));
    Assert.That(Integer(Call(after, "f", 64L)), Is.EqualTo(BigInteger.One << 64));
    Assert.That(report, Has.Some.EqualTo("applied: 'n' kept as arbitrary-precision integer, a parameter whose callers no contract bounds"));
  }

  /// <summary>Scenario and test design 15: a division inside a mask is not narrowed; (2^40 // 2) &amp; 0xFFFFFFFF is 0.</summary>
  [Test]
  public void DivisionInsideAMask_IsNotNarrowed() {
    var (_, after, report) = Specialized(F("a: int, b: int", "(a // b) & 0xFFFFFFFF"));
    Assert.That(report, Has.Some.EqualTo("not applied: masked expression contains a call of 'PyFloorDivideInt'"));
    Assert.That(IrTree.Nodes(Function(after).Body).OfType<IrCall>().Single().Type, Is.EqualTo(IrType.BigInt));
    Assert.That(Integer(Call(after, "f", 1L << 40, 2L)), Is.EqualTo(BigInteger.Zero));
  }

  /// <summary>Must not fire: a right shift inside a mask pulls in bits above it, so it stays exact.</summary>
  [Test]
  public void RightShiftInsideAMask_StaysExact() {
    var (_, after, report) = Specialized(F("a: int", "(a >> 3) & 0xFF"));
    Assert.That(report, Has.Some.EqualTo("not applied: masked expression contains >>"));
    Assert.That(Exact(Function(after)).Select(node => ((IrBinary)node).Op), Is.EqualTo(new[] { IrBinaryOp.ShiftRight }));
    var big = (BigInteger.One << 70) + 0x7F8;
    Assert.That(Integer(IrInterpreter.Call(after, "f", [big])), Is.EqualTo(new BigInteger(255)));
  }

  /// <summary>Must not fire: a product used unmasked as well stays exact; only its masked copy is narrowed.</summary>
  [Test]
  public void ValueUsedUnmasked_StaysExact() {
    var (_, after, report) = Specialized("def f(a: int, b: int) -> int:\n    product = a * b\n    low = product & 0xFFFFFFFF\n    return low + product\n");
    Assert.That(Exact(Function(after)).OfType<IrBinary>().Select(node => node.Op), Does.Contain(IrBinaryOp.Multiply));
    Assert.That(Function(after).Locals.Single(local => local.Name == "product").Type, Is.EqualTo(IrType.BigInt));
    Assert.That(report, Has.Some.EqualTo("applied: 'product' kept as arbitrary-precision integer, its range unbounded"));
    Assert.That(Integer(Call(after, "f", 1L << 40, 3L)), Is.EqualTo(new BigInteger(3298534883328)));
  }

  /// <summary>Test design 12, 16: a shift count that may reach the width is not turned into a 32-bit shift.</summary>
  [Test]
  public void ShiftCountThatMayReachTheWidth_IsNotNarrowed() {
    var a = new IrLocal("a", IrType.BigInt);
    var n = new IrLocal("n", IrType.I32);
    var masked = new IrBinary(IrBinaryOp.And, new IrBinary(IrBinaryOp.ShiftLeft, new IrLocalRef(a), new IrLocalRef(n)), new IrBigConst(0xFFFFFFFF));
    var module = new IrModule("M", [new IrFunction("f", true, IrType.BigInt, [a, n], [], new IrBlock([new IrReturn(masked)]))]);
    var result = PyIntSpecialization.Run(module);
    Assert.That(result.Applications.Select(application => application.Name), Has.Some.EqualTo("not applied: shift count may reach width"));
    Assert.That(Integer(IrInterpreter.Call(result.Module, "f", [BigInteger.One, 40])), Is.EqualTo(BigInteger.Zero));
    Assert.That(Integer(IrInterpreter.Call(result.Module, "f", [BigInteger.One, 31])), Is.EqualTo(BigInteger.One << 31));
  }

  /// <summary>A range with a step other than one is tested by the step's sign, which bounds no counter: it stays exact, and right.</summary>
  [Test]
  public void SteppedRange_KeepsItsCounterExact() {
    var (before, after, report) = Specialized("def f(n: int) -> int:\n    total = 0\n    for i in range(0, n, 3):\n        total = (total + i) & 0xFFFF\n    return total\n");
    Assert.That(report, Has.Some.StartsWith("applied: 'total' held in i32"));
    Assert.That(Integer(Call(after, "f", 100L)), Is.EqualTo(Integer(Call(before, "f", 100L))));
    Assert.That(Integer(Call(after, "f", 100L)), Is.EqualTo(new BigInteger(1683)));
  }

  /// <summary>A value that keeps falling is widened through the limits of the fixed widths to unbounded, and stays exact.</summary>
  [Test]
  public void FallingValue_IsWidenedToUnbounded() {
    var (_, after, report) = Specialized("def f(n: int) -> int:\n    x = 0\n    for _ in range(n):\n        x = x - 3\n    return x & 0xFF\n");
    Assert.That(report, Has.Some.EqualTo("applied: 'x' kept as arbitrary-precision integer, its range unbounded"));
    Assert.That(Integer(Call(after, "f", 5L)), Is.EqualTo(new BigInteger(241)));
  }

  /// <summary>A module without the arbitrary-precision integer is left as it is.</summary>
  [Test]
  public void ModuleWithoutBigInt_IsTheSameObject() {
    var module = new IrModule("M", [new IrFunction("f", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]))]);
    Assert.That(PyIntSpecialization.Run(module).Module, Is.SameAs(module));
  }

  /// <summary>The fixtures: FNV-1a, SplitMix64 and xorshift narrowed to native arithmetic, with the values CPython 3.14 printed for them.</summary>
  [TestCase("fnv1a64", 0L, "12161962213042174405")]
  [TestCase("fnv1a64", 81985529216486895L, "4029383781087845461")]
  [TestCase("fnv1a64", -1L, "10157053723145373757")]
  [TestCase("fnv1a32", 1L, "4218009092")]
  [TestCase("fnv1a32", -1L, "3809873841")]
  [TestCase("splitmix64", 0L, "16294208416658607535")]
  [TestCase("splitmix64", -1L, "16490336266968443936")]
  [TestCase("xorshift64", 1L, "1082269761")]
  [TestCase("xorshift64", 81985529216486895L, "4550888344034083252")]
  [TestCase("xorshift32_sum", 0L, "1420682268")]
  [TestCase("xorshift32_sum", 81985529216486895L, "552028817")]
  public void Fixture_IsNarrowedAndKeepsItsValues(string function, long argument, string expected) {
    var (before, after) = Fixture();
    Assert.That(Exact(Function(after, function)), Is.Empty, function);
    Assert.That(Function(after, function).Locals.Where(local => local.Type.IsBigInt), Is.Empty);
    Assert.That(Integer(Call(after, function, argument)), Is.EqualTo(BigInteger.Parse(expected)));
    Assert.That(Integer(Call(before, function, argument)), Is.EqualTo(BigInteger.Parse(expected)));
  }

  /// <summary>The argument beyond 64 bits is taken modulo 2^64 by the first mask, as Python does.</summary>
  [Test]
  public void Fixture_TakesAnArgumentBeyond64Bits() {
    var (_, after) = Fixture();
    Assert.That(Integer(IrInterpreter.Call(after, "fnv1a64", [(BigInteger.One << 70) + 5])), Is.EqualTo(BigInteger.Parse("1000385178204227360")));
    Assert.That(Integer(IrInterpreter.Call(after, "checksum", [])), Is.EqualTo(new BigInteger(14836)));
    Assert.That(Function(after, "checksum").ReturnType, Is.EqualTo(IrType.I32));
  }

  /// <summary>The fixture of hashes and generators, imported and specialized.</summary>
  public static (IrModule Before, IrModule After) Fixture() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "optimize", "o0062", "hashes.py");
    var source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    var before = PythonFixture.Module(PythonFixture.Import("hashes.py", source));
    var after = PyIntSpecialization.Run(before).Module;
    Assert.That(IrVerifier.Verify(after), Is.Empty);
    return (before, after);
  }

}
