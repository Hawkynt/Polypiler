using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Helpers.Support;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The bit intrinsics of #95, #96, #97 and #104: the leading and trailing one counts and the result
/// range of every count, the Boolean functions beyond <c>And</c>, <c>Or</c> and <c>Xor</c>, the signed
/// field and the two contracts of a bit position, and the reversal of the bits in each byte. Each
/// scenario of the issues is a case here; the oracles are the C# operators, <c>BitOperations</c> and
/// loops over single bits written in this class, never the evaluator itself.
/// </summary>
[TestFixture]
public sealed class BitIntrinsicsTests {

  // ---------------------------------------------------------------- building and running nodes

  /// <summary>A module whose function <c>F</c> returns one node of its parameters, or of a constant where an argument is given as one.</summary>
  private static IrModule Node(IrIntrinsicOp op, IrType result, params (IrType Type, long? Constant)[] arguments) {
    var parameters = new List<IrLocal>();
    var operands = new List<IrExpr>();
    foreach (var (type, constant) in arguments)
      if (constant is { } value) {
        operands.Add(new IrConst(type, value));
      } else {
        var parameter = new IrLocal("p" + parameters.Count, type);
        parameters.Add(parameter);
        operands.Add(new IrLocalRef(parameter));
      }

    return new IrModule("M", [new IrFunction("F", true, result, parameters, [], new IrBlock([new IrReturn(new IrIntrinsic(op, result, operands))]))]);
  }

  private static (IrType, long?) Of(IrType type) => (type, null);

  private static (IrType, long?) Constant(IrType type, long value) => (type, value);

  private static object Run(IrModule module, params object[] arguments) => IrInterpreter.Call(module, "F", arguments)!;

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}")];

  private static IrType Type(string name) => name switch {
    "i8" => IrType.I8,
    "u8" => IrType.U8,
    "i16" => IrType.I16,
    "u16" => IrType.U16,
    "i32" => IrType.I32,
    "u32" => IrType.U32,
    "i64" => IrType.I64,
    _ => IrType.U64
  };

  private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

  /// <summary>The evaluator's value of one operation of one operand as its bit pattern.</summary>
  private static ulong Bits(IrType type, long value) => (ulong)value & Mask(type.Bits);

  // ---------------------------------------------------------------- #95 the counts

  /// <summary>#95, scenario "Each count returns the specified value" and "Zero and all-ones are defined", in the interpreter.</summary>
  [TestCase(IrIntrinsicOp.PopCount, "u32", 0xF0F0F0F0UL, 16)]
  [TestCase(IrIntrinsicOp.PopCount, "u64", 0xFFFFFFFFFFFFFFFFUL, 64)]
  [TestCase(IrIntrinsicOp.PopCount, "i8", 0xFFUL, 8)]
  [TestCase(IrIntrinsicOp.LeadingZeroCount, "u32", 0x00010000UL, 15)]
  [TestCase(IrIntrinsicOp.TrailingZeroCount, "u32", 0x00010000UL, 16)]
  [TestCase(IrIntrinsicOp.LeadingOneCount, "u8", 0xF0UL, 4)]
  [TestCase(IrIntrinsicOp.TrailingOneCount, "u8", 0x0FUL, 4)]
  [TestCase(IrIntrinsicOp.LeadingZeroCount, "u32", 0UL, 32)]
  [TestCase(IrIntrinsicOp.TrailingZeroCount, "u32", 0UL, 32)]
  [TestCase(IrIntrinsicOp.LeadingOneCount, "u32", 0xFFFFFFFFUL, 32)]
  [TestCase(IrIntrinsicOp.TrailingOneCount, "u32", 0xFFFFFFFFUL, 32)]
  [TestCase(IrIntrinsicOp.PopCount, "u32", 0UL, 0)]
  [TestCase(IrIntrinsicOp.LeadingOneCount, "u32", 0UL, 0)]
  [TestCase(IrIntrinsicOp.TrailingOneCount, "u64", 0UL, 0)]
  [TestCase(IrIntrinsicOp.LeadingOneCount, "i16", 0x7FFFUL, 0)]
  [TestCase(IrIntrinsicOp.TrailingOneCount, "i32", 0x7FFFFFFEUL, 0)]
  [TestCase(IrIntrinsicOp.LeadingOneCount, "u64", 0xFFFF000000000000UL, 16)]
  [TestCase(IrIntrinsicOp.TrailingOneCount, "u64", 0xFFUL, 8)]
  public void Count_HasTheValueOfTheIssue(IrIntrinsicOp op, string type, ulong pattern, int count) {
    var irType = Type(type);
    var module = Node(op, IrType.I32, Of(irType));
    Assert.That(Run(module, IntegerCases.FromPattern(irType, pattern)), Is.EqualTo(count));
    Assert.That(HelperHarness.Outcome(HelperHarness.Legalized(module), [IntegerCases.FromPattern(irType, pattern)]), Is.EqualTo(count.ToString(System.Globalization.CultureInfo.InvariantCulture)), "the helper");
  }

  /// <summary>
  /// #95 test design 3 to 6 and 8 to 9: the one counts of every 8- and 16-bit pattern, signed and
  /// unsigned, are those of <c>BitOperations</c> of the complement, and those of a bit-by-bit loop;
  /// both equal the zero counts of the complement.
  /// </summary>
  [Test]
  public void OneCounts_AreTheZeroCountsOfTheComplement_ForEvery8And16BitValue() {
    var mismatches = 0;
    foreach (var type in new[] { IrType.U8, IrType.I8, IrType.U16, IrType.I16 })
      for (var pattern = 0UL; pattern <= Mask(type.Bits); ++pattern) {
        var value = (long)IrOperators.Wrap(type, (long)pattern);
        var shifted = (uint)(pattern << (32 - type.Bits));
        var leading = BitOperations.LeadingZeroCount(~shifted);
        var trailing = Math.Min(type.Bits, BitOperations.TrailingZeroCount(~(uint)pattern));
        var complement = (long)IrOperators.Wrap(type, (long)~pattern);
        if (IntegerOperations.LeadingOneCount(type, value) != leading || IntegerOperations.TrailingOneCount(type, value) != trailing
            || IntegerOperations.LeadingZeroCount(type, complement) != leading || IntegerOperations.TrailingZeroCount(type, complement) != trailing
            || Loop(pattern, type.Bits, true) != leading || Loop(pattern, type.Bits, false) != trailing)
          ++mismatches;
      }

    Assert.That(mismatches, Is.Zero);
  }

  /// <summary>The ones counted one bit at a time from the top or from the bottom.</summary>
  private static int Loop(ulong pattern, int width, bool fromTop) {
    var count = 0;
    while (count < width && ((pattern >> (fromTop ? width - 1 - count : count)) & 1) == 1)
      ++count;

    return count;
  }

  /// <summary>#95 test design 9 at 32 and 64 bit: 100 000 seeded values per width (seed 250) against <c>BitOperations</c>.</summary>
  [Test]
  public void OneCounts_AgreeWithBitOperations_At32And64Bit() {
    var random = new Random(250);
    var mismatches = 0;
    for (var index = 0; index < 100_000; ++index) {
      var wide = (ulong)random.NextInt64(long.MinValue, long.MaxValue);
      // One value in four has a run of ones at the top and one in four at the bottom, which random bits rarely give.
      if (index % 4 == 0)
        wide |= ulong.MaxValue << random.Next(64);
      if (index % 4 == 1)
        wide |= ulong.MaxValue >> random.Next(64);

      var word = (uint)wide;
      if (IntegerOperations.LeadingOneCount(IrType.U64, (long)wide) != BitOperations.LeadingZeroCount(~wide)
          || IntegerOperations.TrailingOneCount(IrType.I64, (long)wide) != BitOperations.TrailingZeroCount(~wide)
          || IntegerOperations.LeadingOneCount(IrType.U32, word) != BitOperations.LeadingZeroCount(~word)
          || IntegerOperations.TrailingOneCount(IrType.I32, (int)word) != Math.Min(32, BitOperations.TrailingZeroCount(~word)))
        ++mismatches;
    }

    Assert.That(mismatches, Is.Zero);
    Assert.That(IntegerOperations.TrailingOneCount(IrType.U64, -1), Is.EqualTo(64));
  }

  /// <summary>#95 test design 8: a signed and an unsigned value of one pattern have the same counts.</summary>
  [TestCase(IrIntrinsicOp.LeadingOneCount)]
  [TestCase(IrIntrinsicOp.TrailingOneCount)]
  public void SignedAndUnsigned_CountTheSamePattern(IrIntrinsicOp op) {
    Assert.That(Run(Node(op, IrType.I32, Of(IrType.I8)), (sbyte)-1), Is.EqualTo(Run(Node(op, IrType.I32, Of(IrType.U8)), (byte)0xFF)));
    Assert.That(Run(Node(op, IrType.I32, Of(IrType.I8)), (sbyte)-1), Is.EqualTo(8));
  }

  /// <summary>
  /// #95 scenario "A floating-point operand is refused" and test design 12: the verifier refuses a
  /// count of a float, one with two operands and one whose node is not an <c>i32</c>.
  /// </summary>
  [TestCase(IrIntrinsicOp.LeadingOneCount)]
  [TestCase(IrIntrinsicOp.TrailingOneCount)]
  [TestCase(IrIntrinsicOp.PopCount)]
  public void Count_OfAFloat_OrOfTwoOperands_IsRefused(IrIntrinsicOp op) {
    Assert.That(Problems(Node(op, IrType.I32, Of(IrType.F64))), Has.Some.Contains(IrVerifier.Code).And.Some.Contains($"{op} with the type i32 is applied to (f64)"));
    Assert.That(Problems(Node(op, IrType.I32, Of(IrType.U32), Of(IrType.U32))), Has.Some.Contains(IrVerifier.Code));
    Assert.That(Problems(Node(op, IrType.U32, Of(IrType.U32))), Has.Some.Contains(IrVerifier.Code), "the count is an i32, not of the operand's type");
  }

  /// <summary>
  /// #95 "range facts of the result": each count's contract states the range of its result, 0 to the
  /// widest width, and no count of any boundary of any width leaves 0 to its own width.
  /// </summary>
  [TestCase("popcount", IrIntrinsicOp.PopCount)]
  [TestCase("leadingzerocount", IrIntrinsicOp.LeadingZeroCount)]
  [TestCase("trailingzerocount", IrIntrinsicOp.TrailingZeroCount)]
  [TestCase("leadingonecount", IrIntrinsicOp.LeadingOneCount)]
  [TestCase("trailingonecount", IrIntrinsicOp.TrailingOneCount)]
  public void CountContract_StatesTheRangeOfItsResult(string file, IrIntrinsicOp op) {
    var body = Contract("bitcount", file);
    Assert.That(body["facts"]!["resultRange"]!.AsArray().Select(bound => (long)bound!), Is.EqualTo(new[] { 0L, 64L }));
    foreach (var type in IntegerCases.Integers)
      foreach (var value in IntegerCases.Edges(type)) {
        var count = (int)Run(Node(op, IrType.I32, Of(type)), value);
        Assert.That(count, Is.InRange(0, type.Bits), $"{op} of {type} {value}");
      }
  }

  // ---------------------------------------------------------------- #97 the Boolean functions

  /// <summary>#97 scenario "Two-input negated operations on u8" and test design 1.</summary>
  [TestCase(IrIntrinsicOp.Nand, 0x3F)]
  [TestCase(IrIntrinsicOp.Nor, 0x03)]
  [TestCase(IrIntrinsicOp.Xnor, 0xC3)]
  [TestCase(IrIntrinsicOp.AndNot, 0x30)]
  [TestCase(IrIntrinsicOp.OrNot, 0xF3)]
  [TestCase(IrIntrinsicOp.Implies, 0xCF)]
  public void TwoInputFunction_OfTheCanonicalOperands(IrIntrinsicOp op, int expected) {
    var module = Node(op, IrType.U8, Of(IrType.U8), Of(IrType.U8));
    Assert.That(Run(module, (byte)0xF0, (byte)0xCC), Is.EqualTo((byte)expected));
    Assert.That(HelperHarness.Outcome(HelperHarness.Legalized(module), [(byte)0xF0, (byte)0xCC]), Is.EqualTo(expected.ToString(System.Globalization.CultureInfo.InvariantCulture)));
  }

  /// <summary>#97 scenario "Three-input operations on the canonical operands".</summary>
  [TestCase(IrIntrinsicOp.BitSelect, -1, 0xCA)]
  [TestCase(IrIntrinsicOp.Majority, -1, 0xE8)]
  [TestCase(IrIntrinsicOp.TernaryLogic, 0x96, 0x96)]
  [TestCase(IrIntrinsicOp.TernaryLogic, 0x00, 0x00)]
  [TestCase(IrIntrinsicOp.TernaryLogic, 0xFF, 0xFF)]
  public void ThreeInputFunction_OfTheCanonicalOperands(IrIntrinsicOp op, int table, int expected) {
    var module = table < 0
      ? Node(op, IrType.U8, Of(IrType.U8), Of(IrType.U8), Of(IrType.U8))
      : Node(op, IrType.U8, Of(IrType.U8), Of(IrType.U8), Of(IrType.U8), Constant(IrType.I32, table));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(Run(module, (byte)0xF0, (byte)0xCC, (byte)0xAA), Is.EqualTo((byte)expected));
  }

  /// <summary>#97 scenario "AndNot complements its second operand, not its first", and test design 8.</summary>
  [Test]
  public void AndNot_ComplementsItsSecondOperand() {
    Assert.That(Run(Node(IrIntrinsicOp.AndNot, IrType.U32, Of(IrType.U32), Of(IrType.U32)), 0x0000FFFFu, 0x00FF00FFu), Is.EqualTo(0x0000FF00u));
    var module = Node(IrIntrinsicOp.AndNot, IrType.U8, Of(IrType.U8), Of(IrType.U8));
    Assert.That((Run(module, (byte)0xF0, (byte)0xCC), Run(module, (byte)0xCC, (byte)0xF0)), Is.EqualTo(((byte)0x30, (byte)0x0C)));
  }

  /// <summary>
  /// #97 scenario "Every TernaryLogic immediate expands to core operations with equal results": for
  /// each of the 256 tables, the node and its helper give the table on the canonical operands, and the
  /// helper is made of nothing but bit operations, comparisons and branches - no intrinsic.
  /// </summary>
  [Test]
  public void EveryTruthTable_IsItselfOnTheCanonicalOperands_InTheNodeAndInTheHelper() {
    for (var table = 0; table < 256; ++table) {
      var module = Node(IrIntrinsicOp.TernaryLogic, IrType.U8, Of(IrType.U8), Of(IrType.U8), Of(IrType.U8), Constant(IrType.I32, table));
      var legalized = HelperHarness.Legalized(module);
      Assert.That(Run(module, (byte)0xF0, (byte)0xCC, (byte)0xAA), Is.EqualTo((byte)table));
      Assert.That(Run(legalized, (byte)0xF0, (byte)0xCC, (byte)0xAA), Is.EqualTo((byte)table));
      Assert.That(legalized.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrIntrinsic>(), Is.Empty, "the helper has no intrinsic");
    }
  }

  /// <summary>
  /// #97 test design 9: every pair of 8-bit operands of the six two-input functions against C#'s
  /// operators, every table on 10 000 seeded triples (seed 253) against a lookup of each bit, at 8, 32
  /// and 64 bits, and the bit select and the majority as their tables 0xCA and 0xE8.
  /// </summary>
  [Test]
  public void BooleanFunctions_AgreeWithTheOperators_AndTheTableLookup() {
    var mismatches = 0;
    for (var a = 0; a < 256; ++a)
      for (var b = 0; b < 256; ++b) {
        long A(IrType type) => (long)IrOperators.Wrap(type, a);
        long B(IrType type) => (long)IrOperators.Wrap(type, b);
        if ((byte)IntegerOperations.Nand(IrType.U8, a, b) != (byte)~(a & b) || (byte)IntegerOperations.Nor(IrType.U8, a, b) != (byte)~(a | b)
            || (byte)IntegerOperations.Xnor(IrType.U8, a, b) != (byte)~(a ^ b) || (byte)IntegerOperations.AndNot(IrType.U8, a, b) != (byte)(a & ~b)
            || (byte)IntegerOperations.OrNot(IrType.U8, a, b) != (byte)(a | ~b) || (byte)IntegerOperations.Implies(IrType.U8, a, b) != (byte)(~a | b)
            || IntegerOperations.Nand(IrType.I8, A(IrType.I8), B(IrType.I8)) != (sbyte)~(a & b))
          ++mismatches;
      }

    var random = new Random(253);
    for (var index = 0; index < 10_000; ++index) {
      var table = random.Next(256);
      var (x, y, z) = ((ulong)random.NextInt64(long.MinValue, long.MaxValue), (ulong)random.NextInt64(long.MinValue, long.MaxValue), (ulong)random.NextInt64(long.MinValue, long.MaxValue));
      foreach (var type in new[] { IrType.U8, IrType.I32, IrType.U64 }) {
        long V(ulong pattern) => (long)IrOperators.Wrap(type, (long)pattern);
        var expected = Lookup(table, x, y, z, type.Bits);
        if (Bits(type, IntegerOperations.TernaryLogic(type, V(x), V(y), V(z), table)) != expected
            || Bits(type, IntegerOperations.BitSelect(type, V(x), V(y), V(z))) != Lookup(0xCA, x, y, z, type.Bits)
            || Bits(type, IntegerOperations.Majority(type, V(x), V(y), V(z))) != Lookup(0xE8, x, y, z, type.Bits))
          ++mismatches;
      }
    }

    Assert.That(mismatches, Is.Zero);
  }

  /// <summary>The function of a truth table, looked up bit by bit.</summary>
  private static ulong Lookup(int table, ulong a, ulong b, ulong c, int width) {
    var result = 0UL;
    for (var bit = 0; bit < width; ++bit) {
      var index = (int)(((a >> bit) & 1) * 4 + ((b >> bit) & 1) * 2 + ((c >> bit) & 1));
      if (((table >> index) & 1) == 1)
        result |= 1UL << bit;
    }

    return result;
  }

  /// <summary>#97 test design 2: SHA-256's <c>Ch(e, f, g)</c> is the bit select.</summary>
  [Test]
  public void ShaChoose_IsTheBitSelect()
    => Assert.That(Run(Node(IrIntrinsicOp.BitSelect, IrType.U32, Of(IrType.U32), Of(IrType.U32), Of(IrType.U32)), 0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu), Is.EqualTo(0x1F85C98Cu));

  /// <summary>#97 test design 6 and 7: the equivalence of a value with itself is all ones at every width; the complement of the or of two zeros of <c>i8</c> is -1.</summary>
  [Test]
  public void Xnor_OfAValueWithItself_IsAllOnes() {
    foreach (var type in IntegerCases.Integers) {
      var module = Node(IrIntrinsicOp.Xnor, type, Of(type), Of(type));
      foreach (var value in IntegerCases.Edges(type))
        Assert.That(Run(module, value, value), Is.EqualTo(IntegerCases.FromPattern(type, ulong.MaxValue)), $"{type} {value}");
    }

    Assert.That(Run(Node(IrIntrinsicOp.Nor, IrType.I8, Of(IrType.I8), Of(IrType.I8)), (sbyte)0, (sbyte)0), Is.EqualTo((sbyte)-1));
  }

  /// <summary>
  /// #97 scenario "An immediate above 255 is refused" and test design 4 and 12: a constant table
  /// outside 0 to 255 is refused by the verifier, a missing one is an operand too few, and a variable
  /// one outside the range traps with an invalid operation, in the node and in its helper.
  /// </summary>
  [TestCase(256)]
  [TestCase(-1)]
  public void TruthTable_OutsideItsRange_IsRefused_OrTraps(int table) {
    var constant = Node(IrIntrinsicOp.TernaryLogic, IrType.U8, Of(IrType.U8), Of(IrType.U8), Of(IrType.U8), Constant(IrType.I32, table));
    Assert.That(Problems(constant), Is.EqualTo(new[] { $"{IrVerifier.Code} ill-typed IR in 'F': intrinsic 'TernaryLogic': the truth table {table} is outside [0, 255]" }));
    Assert.That(Problems(Node(IrIntrinsicOp.TernaryLogic, IrType.U8, Of(IrType.U8), Of(IrType.U8), Of(IrType.U8))), Has.Some.Contains("TernaryLogic with the type u8"));
    var variable = Node(IrIntrinsicOp.TernaryLogic, IrType.U8, Of(IrType.U8), Of(IrType.U8), Of(IrType.U8), Of(IrType.I32));
    Assert.That(Problems(variable), Is.Empty);
    var trap = HelperHarness.Outcome(variable, [(byte)1, (byte)2, (byte)3, table]);
    Assert.That(trap, Does.StartWith("throws "));
    Assert.That(HelperHarness.Outcome(HelperHarness.Legalized(variable), [(byte)1, (byte)2, (byte)3, table]), Is.EqualTo(trap));
  }

  /// <summary>#97 scenario "Operands of different types are refused" and test design 10: mixed widths and a float are refused.</summary>
  [Test]
  public void BooleanFunction_OfMixedTypesOrOfAFloat_IsRefused() {
    Assert.That(Problems(Node(IrIntrinsicOp.Nand, IrType.U32, Of(IrType.U32), Of(IrType.U16))), Has.Some.Contains("Nand with the type u32 is applied to (u32, u16)"));
    Assert.That(Problems(Node(IrIntrinsicOp.Nand, IrType.F32, Of(IrType.F32), Of(IrType.F32))), Has.Some.Contains(IrVerifier.Code));
    Assert.That(Problems(Node(IrIntrinsicOp.Majority, IrType.Bool, Of(IrType.Bool), Of(IrType.Bool), Of(IrType.Bool))), Has.Some.Contains(IrVerifier.Code), "the one-bit type waits for its own issue");
  }

  // ---------------------------------------------------------------- #96 the signed field and the position contracts

  /// <summary>#96 scenario "Field extraction and insertion", the signed field, and test design 5 to 7.</summary>
  [TestCase("i32", 0x000000F0UL, 4, 4, 0xFFFFFFFFUL)]
  [TestCase("u32", 0x12345678UL, 8, 12, 0x00000456UL)]
  [TestCase("u32", 0x12345678UL, 4, 8, 0x00000067UL)]
  [TestCase("i16", 0x1234UL, 0, 16, 0x1234UL)]
  [TestCase("u8", 0x80UL, 7, 1, 0xFFUL)]
  [TestCase("i16", 0x0F00UL, 8, 4, 0xFFFFUL)]
  [TestCase("u64", 0x8000000000000000UL, 0, 64, 0x8000000000000000UL)]
  [TestCase("i8", 0x7FUL, 0, 0, 0x00UL)]
  [TestCase("i8", 0x7FUL, 8, 0, 0x00UL)]
  [TestCase("u32", 0x80000000UL, 28, 4, 0xFFFFFFF8UL)]
  public void SignedField_IsSignExtendedFromItsTopBit(string type, ulong value, int start, int length, ulong expected) {
    var irType = Type(type);
    var module = Node(IrIntrinsicOp.ExtractBitsSigned, irType, Of(irType), Of(IrType.I32), Of(IrType.I32));
    var argument = IntegerCases.FromPattern(irType, value);
    Assert.That(Run(module, argument, start, length), Is.EqualTo(IntegerCases.FromPattern(irType, expected)));
    Assert.That(Run(HelperHarness.Legalized(module), argument, start, length), Is.EqualTo(IntegerCases.FromPattern(irType, expected)), "the helper");
  }

  /// <summary>
  /// #96 test design 11: every 8-bit value, start and length from -1 to 9, signed and unsigned,
  /// against the field taken with <see cref="BigInteger"/> arithmetic; outside the width it traps.
  /// </summary>
  [Test]
  public void SignedField_AgreesWithUnboundedArithmetic_ForEvery8BitCase() {
    var mismatches = 0;
    foreach (var type in new[] { IrType.U8, IrType.I8 })
      for (var pattern = 0; pattern < 256; ++pattern)
        for (var start = -1; start <= 9; ++start)
          for (var length = -1; length <= 9; ++length) {
            var value = (long)IrOperators.Wrap(type, pattern);
            string actual;
            try {
              actual = Bits(type, IntegerOperations.ExtractBitsSigned(type, value, start, length)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            } catch (IrTrapException trap) {
              actual = trap.Trap.ToString();
            }

            if (actual != Unbounded(pattern, start, length))
              ++mismatches;
          }

    Assert.That(mismatches, Is.Zero);
  }

  private static string Unbounded(int pattern, int start, int length) {
    if (start < 0 || length < 0 || start + length > 8)
      return IrTrap.InvalidOperation.ToString();
    if (length == 0)
      return "0";

    var field = (new BigInteger(pattern) >> start) & ((BigInteger.One << length) - 1);
    if (field >= BigInteger.One << (length - 1))
      field -= BigInteger.One << length;

    return ((ulong)(field & 0xFF)).ToString(System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// #96 scenario "Under the mask contract the index wraps modulo the width" and test design 3 and 9:
  /// the masked position is <c>And(n, width - 1)</c> of the index converted to an <c>i32</c>, whatever
  /// the index's type, and never traps.
  /// </summary>
  [TestCase(IrIntrinsicOp.SetBit, "u32", 0x00000000UL, "u32", 32L, 0x00000001UL)]
  [TestCase(IrIntrinsicOp.SetBit, "u32", 0x00000000UL, "u32", 33L, 0x00000002UL)]
  [TestCase(IrIntrinsicOp.SetBit, "u32", 0x00000000UL, "i32", -1L, 0x80000000UL)]
  [TestCase(IrIntrinsicOp.SetBit, "u32", 0x00000000UL, "u64", -1L, 0x80000000UL)]
  [TestCase(IrIntrinsicOp.TestBit, "u32", 0x80000000UL, "u8", 63L, 1UL)]
  [TestCase(IrIntrinsicOp.TestBit, "u32", 0x80000001UL, "i32", 31L, 1UL)]
  [TestCase(IrIntrinsicOp.TestBit, "u32", 0x80000001UL, "i32", 1L, 0UL)]
  [TestCase(IrIntrinsicOp.SetBit, "u8", 0x10UL, "i32", 0L, 0x11UL)]
  [TestCase(IrIntrinsicOp.ToggleBit, "u16", 0x0000UL, "i32", 15L, 0x8000UL)]
  [TestCase(IrIntrinsicOp.ClearBit, "u64", 0xFFFFFFFFFFFFFFFFUL, "i32", 64L, 0xFFFFFFFFFFFFFFFEUL)]
  public void MaskedPosition_WrapsModuloTheWidth(IrIntrinsicOp op, string type, ulong value, string indexType, long index, ulong expected) {
    var (irType, irIndex) = (Type(type), Type(indexType));
    var parameter = new IrLocal("x", irType);
    var position = new IrLocal("n", irIndex);
    IrExpr index32 = irIndex == IrType.I32 ? new IrLocalRef(position) : new IrConvert(IrType.I32, new IrLocalRef(position));
    var masked = new IrBinary(IrBinaryOp.And, index32, new IrConst(IrType.I32, irType.Bits - 1));
    var result = op == IrIntrinsicOp.TestBit ? IrType.Bool : irType;
    var module = new IrModule("M", [new IrFunction("F", true, result, [parameter, position], [], new IrBlock([new IrReturn(new IrIntrinsic(op, result, [new IrLocalRef(parameter), masked]))]))]);
    Assert.That(Problems(module), Is.Empty);
    var outcome = Run(module, IntegerCases.FromPattern(irType, value), IntegerCases.FromPattern(irIndex, (ulong)index));
    Assert.That(outcome, Is.EqualTo(op == IrIntrinsicOp.TestBit ? expected == 1 : IntegerCases.FromPattern(irType, expected)));
  }

  /// <summary>
  /// #96 scenario "Under the strict contract", test design 4 and 13: an unmasked variable position
  /// outside the width traps, and a constant one is refused before anything runs.
  /// </summary>
  [Test]
  public void StrictPosition_OutsideTheWidth_TrapsOrIsRefused() {
    var variable = Node(IrIntrinsicOp.SetBit, IrType.U32, Of(IrType.U32), Of(IrType.I32));
    Assert.That(Problems(variable), Is.Empty);
    Assert.That(HelperHarness.Outcome(variable, [0u, 32]), Does.StartWith("throws "));
    Assert.That(HelperHarness.Outcome(variable, [0u, 31]), Is.EqualTo("2147483648"));
    Assert.That(Problems(Node(IrIntrinsicOp.TestBit, IrType.Bool, Of(IrType.U32), Constant(IrType.I32, 32))),
      Is.EqualTo(new[] { $"{IrVerifier.Code} ill-typed IR in 'F': intrinsic 'TestBit': the position 32 is outside [0, 31]" }));
    Assert.That(Problems(Node(IrIntrinsicOp.SetBit, IrType.U8, Of(IrType.U8), Constant(IrType.I32, -1))),
      Is.EqualTo(new[] { $"{IrVerifier.Code} ill-typed IR in 'F': intrinsic 'SetBit': the position -1 is outside [0, 7]" }));
    Assert.That(Problems(Node(IrIntrinsicOp.ClearBit, IrType.U64, Of(IrType.U64), Constant(IrType.I32, 63))), Is.Empty);
    Assert.That(Problems(Node(IrIntrinsicOp.ToggleBit, IrType.I16, Of(IrType.I16), Constant(IrType.I32, 0))), Is.Empty);
  }

  /// <summary>#96 scenario "A constant length larger than the width is refused", test design 14, and the other constant fields outside the width.</summary>
  [TestCase(IrIntrinsicOp.ExtractBits, 0, 33, "the length 33 is outside [0, 32]")]
  [TestCase(IrIntrinsicOp.ExtractBitsSigned, 0, -1, "the length -1 is outside [0, 32]")]
  [TestCase(IrIntrinsicOp.ExtractBits, 33, 0, "the start 33 is outside [0, 32]")]
  [TestCase(IrIntrinsicOp.ExtractBitsSigned, -1, 1, "the start -1 is outside [0, 32]")]
  [TestCase(IrIntrinsicOp.InsertBits, 28, 5, "the field of 5 bits from bit 28 reaches past bit 31")]
  [TestCase(IrIntrinsicOp.InsertBits, 0, 40, "the length 40 is outside [0, 32]")]
  [TestCase(IrIntrinsicOp.ExtractBits, 32, 0, null)]
  [TestCase(IrIntrinsicOp.ExtractBitsSigned, 28, 4, null)]
  [TestCase(IrIntrinsicOp.InsertBits, 0, 32, null)]
  public void ConstantField_OutsideTheWidth_IsRefused(IrIntrinsicOp op, int start, int length, string? problem) {
    var module = op == IrIntrinsicOp.InsertBits
      ? Node(op, IrType.U32, Of(IrType.U32), Of(IrType.U32), Constant(IrType.I32, start), Constant(IrType.I32, length))
      : Node(op, IrType.U32, Of(IrType.U32), Constant(IrType.I32, start), Constant(IrType.I32, length));
    Assert.That(Problems(module), problem is null ? Is.Empty : Is.EqualTo(new[] { $"{IrVerifier.Code} ill-typed IR in 'F': intrinsic '{op}': {problem}" }));
  }

  /// <summary>A field of variable start and constant length is checked when it runs: one that reaches past the width traps.</summary>
  [Test]
  public void VariableField_PastTheWidth_Traps() {
    var module = Node(IrIntrinsicOp.ExtractBitsSigned, IrType.U32, Of(IrType.U32), Of(IrType.I32), Constant(IrType.I32, 8));
    Assert.That(Problems(module), Is.Empty);
    Assert.That(HelperHarness.Outcome(module, [0x12345678u, 25]), Does.StartWith("throws "));
    Assert.That(HelperHarness.Outcome(module, [0x82345678u, 24]), Is.EqualTo("4294967170"));
  }

  // ---------------------------------------------------------------- #104 the reversals

  /// <summary>#104 scenario "Each reversal has its own dimension", the dimensions there are: the bits, the bytes and the bits in each byte.</summary>
  [TestCase(IrIntrinsicOp.BitReverse, "u8", 0x01UL, 0x80UL)]
  [TestCase(IrIntrinsicOp.BitReverse, "u32", 0x12345678UL, 0x1E6A2C48UL)]
  [TestCase(IrIntrinsicOp.ByteSwap, "u32", 0x12345678UL, 0x78563412UL)]
  [TestCase(IrIntrinsicOp.ByteSwap, "u16", 0x1234UL, 0x3412UL)]
  [TestCase(IrIntrinsicOp.ByteSwap, "u8", 0xABUL, 0xABUL)]
  [TestCase(IrIntrinsicOp.ReverseBitsInBytes, "u16", 0x0180UL, 0x8001UL)]
  [TestCase(IrIntrinsicOp.ReverseBitsInBytes, "u8", 0x01UL, 0x80UL)]
  [TestCase(IrIntrinsicOp.ReverseBitsInBytes, "i8", 0x01UL, 0x80UL)]
  [TestCase(IrIntrinsicOp.ReverseBitsInBytes, "u32", 0x12345678UL, 0x482C6A1EUL)]
  [TestCase(IrIntrinsicOp.ReverseBitsInBytes, "u64", 0x0123456789ABCDEFUL, 0x80C4A2E691D5B3F7UL)]
  [TestCase(IrIntrinsicOp.ReverseBitsInBytes, "i16", 0x8000UL, 0x0100UL)]
  public void Reversal_HasItsOwnDimension(IrIntrinsicOp op, string type, ulong value, ulong expected) {
    var irType = Type(type);
    var module = Node(op, irType, Of(irType));
    Assert.That(Run(module, IntegerCases.FromPattern(irType, value)), Is.EqualTo(IntegerCases.FromPattern(irType, expected)));
    Assert.That(Run(HelperHarness.Legalized(module), IntegerCases.FromPattern(irType, value)), Is.EqualTo(IntegerCases.FromPattern(irType, expected)), "the helper");
  }

  /// <summary>
  /// #104 scenario "The fallback agrees for every 16-bit value", test design 3, 4 and 8 to 9: every
  /// 8- and 16-bit value, and 100 000 seeded ones (seed 255) at 32 and 64 bits, of the reversal of the
  /// bits in each byte against a loop over its bits, against its helper, as an involution, and with
  /// the byte swap after it as the bit reversal.
  /// </summary>
  [Test]
  public void ReverseBitsInBytes_AgreesWithItsLoopAndHelper_AndComposesToTheBitReversal() {
    var mismatches = 0;
    void Check(IrType type, ulong pattern) {
      var value = (long)IrOperators.Wrap(type, (long)pattern);
      var reversed = IntegerOperations.ReverseBitsInBytes(type, value);
      var helper = type.Bits == 64 ? HelperOperations.ReverseBitsInBytes64(pattern) : HelperOperations.ReverseBitsInBytes32((uint)pattern) & (uint)Mask(type.Bits);
      if (Bits(type, reversed) != PerBit(pattern, type.Bits) || Bits(type, reversed) != helper
          || IntegerOperations.ReverseBitsInBytes(type, reversed) != value
          || IntegerOperations.ByteSwap(type, reversed) != IntegerOperations.BitReverse(type, value))
        ++mismatches;
    }

    for (var pattern = 0UL; pattern <= 0xFFFF; ++pattern) {
      Check(IrType.U16, pattern);
      Check(IrType.I16, pattern);
      if (pattern <= 0xFF) {
        Check(IrType.U8, pattern);
        Check(IrType.I8, pattern);
      }
    }

    var random = new Random(255);
    for (var index = 0; index < 100_000; ++index) {
      var pattern = (ulong)random.NextInt64(long.MinValue, long.MaxValue);
      Check(IrType.U32, pattern & 0xFFFFFFFF);
      Check(IrType.I64, pattern);
    }

    Assert.That(mismatches, Is.Zero);
  }

  private static ulong PerBit(ulong pattern, int width) {
    var result = 0UL;
    for (var bit = 0; bit < width; ++bit)
      if (((pattern >> bit) & 1) == 1)
        result |= 1UL << (8 * (bit / 8) + 7 - bit % 8);

    return result;
  }

  /// <summary>#104 scenario "Reversing twice is the identity".</summary>
  [TestCase(IrIntrinsicOp.BitReverse)]
  [TestCase(IrIntrinsicOp.ReverseBitsInBytes)]
  [TestCase(IrIntrinsicOp.ByteSwap)]
  public void ReversingTwice_IsTheIdentity(IrIntrinsicOp op) {
    var x = new IrLocal("x", IrType.U64);
    var twice = new IrIntrinsic(op, IrType.U64, [new IrIntrinsic(op, IrType.U64, [new IrLocalRef(x)])]);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.U64, [x], [], new IrBlock([new IrReturn(twice)]))]);
    Assert.That(Run(module, 0x0123456789ABCDEFUL), Is.EqualTo(0x0123456789ABCDEFUL));
  }

  /// <summary>#104 scenario "A floating-point operand is refused".</summary>
  [TestCase(IrIntrinsicOp.ByteSwap)]
  [TestCase(IrIntrinsicOp.ReverseBitsInBytes)]
  public void Reversal_OfAFloat_IsRefused(IrIntrinsicOp op)
    => Assert.That(Problems(Node(op, IrType.F64, Of(IrType.F64))), Has.Some.Contains($"{op} with the type f64 is applied to (f64)"));

  // ---------------------------------------------------------------- the contracts

  /// <summary>The tree of <c>spec/</c>, read once.</summary>
  private static readonly Lazy<SpecTree> _spec = new(() => SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot));

  /// <summary>The body of a contract under <c>spec/intrinsics/</c>.</summary>
  private static JsonObject Contract(string group, string name)
    => _spec.Value.Documents.Single(document => document.Family == "intrinsics" && Path.GetFileNameWithoutExtension(document.Path) == name
      && Path.GetFileName(Path.GetDirectoryName(document.Path)) == group).Body!.AsObject();

  /// <summary>
  /// Every vector of the new contracts is what the interpreter computes: the contracts' values come
  /// from the .NET runtime, and the interpreter has to agree with them.
  /// </summary>
  [TestCase("bitcount", "leadingonecount")]
  [TestCase("bitcount", "trailingonecount")]
  [TestCase("boolean", "nand")]
  [TestCase("boolean", "nor")]
  [TestCase("boolean", "xnor")]
  [TestCase("boolean", "andnot")]
  [TestCase("boolean", "ornot")]
  [TestCase("boolean", "implies")]
  [TestCase("boolean", "bitselect")]
  [TestCase("boolean", "majority")]
  [TestCase("boolean", "ternarylogic")]
  [TestCase("bitfield", "extractbitssigned")]
  [TestCase("shuffle", "reversebitsinbytes")]
  public void ContractVectors_AreWhatTheInterpreterComputes(string group, string name) {
    var body = Contract(group, name);
    var op = Enum.Parse<IrIntrinsicOp>((string)body["name"]!);
    var parameters = body["parameters"]!.AsArray().Select(parameter => (string)parameter!["type"]!).ToList();
    var returns = (string)body["returns"]![0]!["type"]!;
    foreach (var vector in body["vectors"]!.AsArray()) {
      var t = Type(((string)vector!["types"]!["T"]!)["TYPES.".Length..]);
      IrType Resolve(string declared) => declared == "T" ? t : Type(declared["TYPES.".Length..]);
      var types = parameters.Select(Resolve).ToList();
      var inputs = vector["inputs"]!.AsArray().Select((input, index) => Literal(types[index], input!)).ToArray();
      var module = Node(op, Resolve(returns), [.. types.Select(Of)]);
      var expected = Literal(Resolve(returns), vector["outputs"]![0]!);
      Assert.That(Run(module, inputs), Is.EqualTo(expected), $"{op} {vector.ToJsonString()}");
      Assert.That(Run(HelperHarness.Legalized(module), inputs), Is.EqualTo(expected), $"the helper of {op} {vector.ToJsonString()}");
    }
  }

  /// <summary>A literal of a vector as the host value of its type: a hexadecimal string is the bit pattern, a number the value.</summary>
  private static object Literal(IrType type, JsonNode literal) {
    var pattern = literal.GetValueKind() == System.Text.Json.JsonValueKind.String
      ? Convert.ToUInt64(((string)literal!)[2..], 16)
      : unchecked((ulong)(long)literal);
    return IntegerCases.FromPattern(type, pattern);
  }

}
