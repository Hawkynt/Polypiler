using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.IR.Intrinsics;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Intrinsics;

/// <summary>
/// <c>ParallelBitExtract</c> and <c>ParallelBitDeposit</c> (#103) and <c>ByteShuffle</c> (#105): the
/// scenarios of both issues on the interpreter and on the helper that replaces the operation, the
/// helpers against loops transcribed from the pseudo-code of the Intel SDM (<c>PEXT</c>, <c>PDEP</c>,
/// <c>PSHUFB</c>) - exhaustively at 8 bits, on seeded values at the other widths - and against the
/// processor through <c>Bmi2</c> and <c>Ssse3</c> where it has them, which is reported and never
/// required. The widths 128 to 512 of both issues wait for the wider integers of #118.
/// </summary>
[TestFixture]
public sealed class BitPermutationTests {

  private static readonly IrType[] _integers = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  // ---------------------------------------------------------------- the oracles of the SDM

  /// <summary><c>PEXT</c> as the SDM states it: walk the mask, copy each selected bit to the next free position.</summary>
  private static ulong Pext(ulong value, ulong mask, int width) {
    ulong result = 0;
    for (int m = 0, k = 0; m < width; ++m)
      if (((mask >> m) & 1) == 1) {
        result |= ((value >> m) & 1) << k;
        ++k;
      }

    return result;
  }

  /// <summary><c>PDEP</c> as the SDM states it: walk the mask, take the next source bit for each selected position.</summary>
  private static ulong Pdep(ulong value, ulong mask, int width) {
    ulong result = 0;
    for (int m = 0, k = 0; m < width; ++m)
      if (((mask >> m) & 1) == 1) {
        result |= ((value >> k) & 1) << m;
        ++k;
      }

    return result;
  }

  /// <summary><c>PSHUFB</c> as the SDM states it, with groups of <paramref name="group"/> bytes.</summary>
  private static ulong Pshufb(ulong table, ulong indices, int group) {
    ulong result = 0;
    for (var i = 0; i < 8; ++i) {
      var index = (byte)(indices >> (8 * i));
      if ((index & 0x80) != 0)
        continue;

      var source = i / group * group + (index & (group - 1));
      result |= ((table >> (8 * source)) & 0xFF) << (8 * i);
    }

    return result;
  }

  /// <summary>An integer or floating-point type by its name in the IR.</summary>
  internal static IrType TypeOf(string name) => new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.F32, IrType.F64 }
    .Single(type => type.ToString() == name);

  private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

  // ---------------------------------------------------------------- the evaluator and the helper

  private static ulong Evaluated(IrIntrinsicOp op, IrType type, ulong value, ulong mask) {
    var result = op == IrIntrinsicOp.ParallelBitExtract
      ? IntegerOperations.ParallelBitExtract(type, IrOperators.Wrap(type, (long)value), IrOperators.Wrap(type, (long)mask))
      : IntegerOperations.ParallelBitDeposit(type, IrOperators.Wrap(type, (long)value), IrOperators.Wrap(type, (long)mask));
    return (ulong)result & Mask(type.Bits);
  }

  /// <summary>The helper of the library as compiled C#, the narrow widths handed over as the bits of their pattern, sign-extended where signed.</summary>
  private static ulong Helped(IrIntrinsicOp op, IrType type, ulong value, ulong mask) {
    if (type.Bits == 64)
      return op == IrIntrinsicOp.ParallelBitExtract ? HelperOperations.ParallelBitExtract64(value, mask) : HelperOperations.ParallelBitDeposit64(value, mask);

    var x = (uint)IrOperators.Wrap(type, (long)value);
    var m = (uint)IrOperators.Wrap(type, (long)mask);
    var result = op == IrIntrinsicOp.ParallelBitExtract ? HelperOperations.ParallelBitExtract32(x, m, type.Bits) : HelperOperations.ParallelBitDeposit32(x, m, type.Bits);
    return result & Mask(type.Bits);
  }

  private static object Host(IrType type, ulong pattern) => HelperHarness.Host(type, IrOperators.Wrap(type, (long)pattern));

  /// <summary>Interprets the node on the values, and the module with the node replaced by its helper.</summary>
  private static (string Interpreted, string Legalized) BothWays(IrIntrinsicOp op, IrType type, IReadOnlyList<IrType> arguments, object[] values) {
    var module = HelperHarness.Module(op, type, arguments);
    return (HelperHarness.Outcome(module, values), HelperHarness.Outcome(HelperHarness.Legalized(module), values));
  }

  // ---------------------------------------------------------------- #103: scenarios

  /// <summary>Test design 1, 2: the scenario "Extract and deposit follow the mask", and the single-bit masks and signed values of rows 4 and 7.</summary>
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "u32", 0x12345678UL, 0x0000FF00UL, 0x00000056UL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "u8", 0xB2UL, 0xF0UL, 0x0BUL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "u32", 0x12345678UL, 0xF0F0F0F0UL, 0x00001357UL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "u64", 0xFEDCBA9876543210UL, 0x00FF00FF00FF00FFUL, 0x00000000DC985410UL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, "u8", 0x0BUL, 0xF0UL, 0xB0UL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, "u32", 0x000000ABUL, 0x0F0F0000UL, 0x0A0B0000UL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, "u32", 0xFFFFFFFFUL, 0x80000001UL, 0x80000001UL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "u32", 0xFFFFFFFFUL, 0x00000001UL, 0x00000001UL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "u32", 0xFFFFFFFFUL, 0x80000000UL, 0x00000001UL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, "u64", 0xFFFFFFFFFFFFFFFFUL, 0x8000000000000000UL, 0x8000000000000000UL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, "u16", 0xFFFFUL, 0x0001UL, 0x0001UL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "i8", 0xFFUL, 0xF0UL, 0x0FUL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, "i16", 0xFFFFUL, 0x8000UL, 0x8000UL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "u32", 0x00000000UL, 0x0F0F0000UL, 0x00000000UL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, "u32", 0xFFFFFFFFUL, 0x0F0F0000UL, 0x000000FFUL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, "u32", 0xFFFFFFFFUL, 0x0F0F0000UL, 0x0F0F0000UL)]
  public void ExtractAndDeposit_FollowTheMask(IrIntrinsicOp op, string name, ulong value, ulong mask, ulong expected) {
    var type = TypeOf(name);
    Assert.That(Evaluated(op, type, value, mask), Is.EqualTo(expected), "evaluator");
    Assert.That(Helped(op, type, value, mask), Is.EqualTo(expected), "helper");
    var (interpreted, legalized) = BothWays(op, type, [type, type], [Host(type, value), Host(type, mask)]);
    Assert.That((interpreted, legalized), Is.EqualTo((Convert.ToString(Host(type, expected), System.Globalization.CultureInfo.InvariantCulture), interpreted)));
  }

  /// <summary>Test design 3: the scenario "Empty and full masks" for both operations.</summary>
  [TestCase(IrIntrinsicOp.ParallelBitExtract, 0x00000000UL, 0x00000000UL)]
  [TestCase(IrIntrinsicOp.ParallelBitExtract, 0xFFFFFFFFUL, 0xDEADBEEFUL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, 0x00000000UL, 0x00000000UL)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit, 0xFFFFFFFFUL, 0xDEADBEEFUL)]
  public void EmptyAndFullMasks(IrIntrinsicOp op, ulong mask, ulong expected) {
    Assert.That(Evaluated(op, IrType.U32, 0xDEADBEEF, mask), Is.EqualTo(expected));
    Assert.That(Helped(op, IrType.U32, 0xDEADBEEF, mask), Is.EqualTo(expected));
  }

  /// <summary>Test design 6: at every width the alternating mask 0x55… extracts the even bits into the lower half.</summary>
  [TestCaseSource(nameof(Integers))]
  public void AlternatingMask_ExtractsTheEvenBits(string name) {
    var type = TypeOf(name);
    var value = 0xA5C3_0F96_3C5A_E718UL & Mask(type.Bits);
    var expected = 0UL;
    for (var bit = 0; bit < type.Bits / 2; ++bit)
      expected |= ((value >> (2 * bit)) & 1) << bit;

    Assert.That(Evaluated(IrIntrinsicOp.ParallelBitExtract, type, value, 0x5555555555555555UL), Is.EqualTo(expected));
    Assert.That(Helped(IrIntrinsicOp.ParallelBitExtract, type, value, 0x5555555555555555UL), Is.EqualTo(expected));
  }

  private static IEnumerable<string> Integers() => _integers.Select(type => type.ToString());

  /// <summary>
  /// The scenario "The fallback is branch-free and agrees for every 8-bit value and mask", test design
  /// 9: all 65 536 pairs of each operation at 8 bits, evaluator against helper against the SDM loop, and
  /// the helpers contain no loop and no call of the operation they replace.
  /// </summary>
  [TestCase(IrIntrinsicOp.ParallelBitExtract)]
  [TestCase(IrIntrinsicOp.ParallelBitDeposit)]
  public void EveryEightBitPair_AgreesWithTheSdm(IrIntrinsicOp op) {
    var mismatches = 0;
    foreach (var type in new[] { IrType.U8, IrType.I8 })
      for (var value = 0UL; value < 256; ++value)
        for (var mask = 0UL; mask < 256; ++mask) {
          var expected = op == IrIntrinsicOp.ParallelBitExtract ? Pext(value, mask, 8) : Pdep(value, mask, 8);
          if (Evaluated(op, type, value, mask) != expected || Helped(op, type, value, mask) != expected)
            ++mismatches;
        }

    Assert.That(mismatches, Is.Zero);
    var legalized = HelperHarness.Legalized(HelperHarness.Module(op, IrType.U8, [IrType.U8, IrType.U8]));
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    Assert.That(Mentions(legalized, op), Is.False);
    Assert.That(Statements(legalized).Any(statement => statement is IrWhile or IrFor or IrDoWhile), Is.False);
  }

  /// <summary>Every statement of every function of a module, nested ones included.</summary>
  internal static IEnumerable<IrStmt> Statements(IrModule module) {
    var pending = new Stack<IrStmt>(module.Functions.SelectMany(function => function.Body.Statements));
    while (pending.Count > 0) {
      var statement = pending.Pop();
      yield return statement;
      foreach (var child in IrTree.Blocks(statement).SelectMany(block => block.Statements))
        pending.Push(child);
    }
  }

  /// <summary>Whether a node of an operation stands anywhere in a module.</summary>
  internal static bool Mentions(IrModule module, IrIntrinsicOp op)
    => Statements(module).SelectMany(IrTree.Nodes).Any(node => node is IrIntrinsic intrinsic && intrinsic.Op == op);

  /// <summary>Test design 9 at the other widths: 100 000 seeded pairs per operation and width (seed 256), evaluator, helper and SDM loop agree; and the processor where it has BMI2.</summary>
  [TestCaseSource(nameof(Integers))]
  public void SeededPairs_AgreeWithTheSdm(string name) {
    var type = TypeOf(name);
    var random = new Random(256);
    var mismatches = 0;
    var hardware = 0;
    for (var count = 0; count < 100_000; ++count) {
      var value = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 40);
      var mask = (ulong)random.NextInt64() & (ulong)random.NextInt64() | ((ulong)random.Next() << 33);
      var (x, m) = (value & Mask(type.Bits), mask & Mask(type.Bits));
      foreach (var op in new[] { IrIntrinsicOp.ParallelBitExtract, IrIntrinsicOp.ParallelBitDeposit }) {
        var expected = op == IrIntrinsicOp.ParallelBitExtract ? Pext(x, m, type.Bits) : Pdep(x, m, type.Bits);
        if (Evaluated(op, type, x, m) != expected || Helped(op, type, x, m) != expected)
          ++mismatches;
        if (Bmi2.X64.IsSupported) {
          ++hardware;
          var native = op == IrIntrinsicOp.ParallelBitExtract ? Bmi2.X64.ParallelBitExtract(x, m) : Bmi2.X64.ParallelBitDeposit(x, m);
          if (native != expected)
            ++mismatches;
        }
      }
    }

    TestContext.Out.WriteLine($"{name}: 200000 comparisons, hardware oracle {(hardware > 0 ? "used" : "not available")}, {mismatches} mismatches");
    Assert.That(mismatches, Is.Zero);
  }

  /// <summary>Test design 8: Deposit(Extract(x, m), m) = x &amp; m and Extract(Deposit(x, m), m) = x &amp; (2^popcount(m) - 1), 10 000 seeded pairs.</summary>
  [Test]
  public void RoundTrips_HoldTheirIdentities() {
    var random = new Random(256);
    for (var count = 0; count < 10_000; ++count) {
      var type = _integers[random.Next(_integers.Length)];
      var x = (ulong)random.NextInt64() & Mask(type.Bits);
      var m = (ulong)random.NextInt64() & Mask(type.Bits);
      var ones = System.Numerics.BitOperations.PopCount(m);
      Assert.That(Evaluated(IrIntrinsicOp.ParallelBitDeposit, type, Evaluated(IrIntrinsicOp.ParallelBitExtract, type, x, m), m), Is.EqualTo(x & m));
      Assert.That(Evaluated(IrIntrinsicOp.ParallelBitExtract, type, Evaluated(IrIntrinsicOp.ParallelBitDeposit, type, x, m), m), Is.EqualTo(x & Mask(ones)));
    }
  }

  /// <summary>Test design 11: a float operand and operands of two types are not an extract; the contract refuses them with PLY0252, the verifier as a malformed node.</summary>
  [TestCase("f32", "f32")]
  [TestCase("u32", "u64")]
  [TestCase("u64", "u32")]
  public void OtherTypes_AreRefused(string value, string mask) {
    var types = new[] { TypeOf(value), TypeOf(mask) };
    foreach (var op in new[] { IrIntrinsicOp.ParallelBitExtract, IrIntrinsicOp.ParallelBitDeposit }) {
      var module = HelperHarness.Module(op, types[0], types);
      Assert.That(IrVerifier.Verify(module), Is.Not.Empty);
      var contract = Contract("shuffle", op.ToString().ToLowerInvariant());
      var node = (IrIntrinsic)((IrReturn)module.Functions[0].Body.Statements[0]).Value!;
      Assert.That(contract.Check(node)?.Code, Is.EqualTo("PLY0252"));
    }
  }

  private static IntrinsicContract Contract(string group, string name)
    => ContractConformanceTests.Read(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "intrinsics", group, name + ".yaml"));

  // ---------------------------------------------------------------- #105: ByteShuffle

  private static ulong Shuffled(ulong table, ulong indices, int group = 8)
    => (ulong)IntegerOperations.ByteShuffle(IrType.U64, unchecked((long)table), unchecked((long)indices), group);

  /// <summary>Test design 1 and the scenarios "Indices select table bytes, bit 7 zeroes" and "Index bits between the selector and bit 7 are ignored".</summary>
  [TestCase(0x8877665544332211UL, 0x0001020304058007UL, 0x1122334455660088UL)]
  [TestCase(0x8877665544332211UL, 0x1313131313131313UL, 0x4444444444444444UL)]
  [TestCase(0x0706050403020100UL, 0x0001020304050607UL, 0x0001020304050607UL)]
  [TestCase(0x0706050403020100UL, 0xFF7F100F08070100UL, 0x0007000700070100UL)]
  [TestCase(0x0706050403020100UL, 0x8080808080808080UL, 0x0000000000000000UL)]
  [TestCase(0x8877665544332211UL, 0x0706050403020100UL, 0x8877665544332211UL)]
  public void ByteShuffle_SelectsAndZeroes(ulong table, ulong indices, ulong expected) {
    Assert.That(Shuffled(table, indices), Is.EqualTo(expected), "evaluator");
    Assert.That(HelperOperations.ByteShuffle64(table, indices, 8), Is.EqualTo(expected), "helper");
    Assert.That(Pshufb(table, indices, 8), Is.EqualTo(expected), "SDM loop");
    var (interpreted, legalized) = BothWays(IrIntrinsicOp.ByteShuffle, IrType.U64, [IrType.U64, IrType.U64, IrType.I32], [table, indices, 8]);
    Assert.That((interpreted, legalized), Is.EqualTo((expected.ToString(System.Globalization.CultureInfo.InvariantCulture), interpreted)));
  }

  /// <summary>Test design 3: the indices 0x00, 0x07, 0x08, 0x0F, 0x10, 0x7F, 0x80 and 0xFF in every byte, on the table of the bytes 0 to 7.</summary>
  [TestCase(0x00, 0x00)]
  [TestCase(0x07, 0x07)]
  [TestCase(0x08, 0x00)]
  [TestCase(0x0F, 0x07)]
  [TestCase(0x10, 0x00)]
  [TestCase(0x7F, 0x07)]
  [TestCase(0x80, 0x00)]
  [TestCase(0xFF, 0x00)]
  public void ByteShuffle_IndexBoundaries(int index, int chosen) {
    var indices = 0x0101010101010101UL * (ulong)index;
    Assert.That(Shuffled(0x0706050403020100UL, indices), Is.EqualTo(0x0101010101010101UL * (ulong)chosen));
    Assert.That(HelperOperations.ByteShuffle64(0x0706050403020100UL, indices, 8), Is.EqualTo(0x0101010101010101UL * (ulong)chosen));
  }

  /// <summary>Test design 10 and 11, the scenario "A group that does not divide the byte count is refused": every other group traps as an invalid operation, in the interpreter and in the helper alike.</summary>
  [TestCase(16)]
  [TestCase(32)]
  [TestCase(64)]
  [TestCase(4)]
  [TestCase(0)]
  [TestCase(-8)]
  public void ByteShuffle_OtherGroups_Trap(int group) {
    Assert.That(() => Shuffled(1, 2, group), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.InvalidOperation));
    Assert.That(() => HelperOperations.ByteShuffle64(1, 2, group), Throws.TypeOf<ArithmeticException>());
    var (interpreted, legalized) = BothWays(IrIntrinsicOp.ByteShuffle, IrType.U64, [IrType.U64, IrType.U64, IrType.I32], [1UL, 2UL, group]);
    Assert.That(interpreted, Does.StartWith("throws"));
    Assert.That(legalized, Is.EqualTo(interpreted));
  }

  /// <summary>
  /// Test design 12, the scenario "A 32-bit pattern is refused": u32, i64 and f64 patterns are not a
  /// shuffle, which the verifier says; the contract says PLY0252 for a type outside its unsigned
  /// parameter, while the width of at least 64 bits is its legality, which the signatures of the IR enforce.
  /// </summary>
  [TestCase("u32", null)]
  [TestCase("i64", "PLY0252")]
  [TestCase("f64", "PLY0252")]
  public void ByteShuffle_OtherTypes_AreRefused(string name, string? code) {
    var type = TypeOf(name);
    var module = HelperHarness.Module(IrIntrinsicOp.ByteShuffle, type, [type, type, IrType.I32]);
    Assert.That(IrVerifier.Verify(module), Is.Not.Empty);
    var node = (IrIntrinsic)((IrReturn)module.Functions[0].Body.Statements[0]).Value!;
    Assert.That(Contract("shuffle", "byteshuffle").Check(node)?.Code, Is.EqualTo(code));
  }

  /// <summary>
  /// Test design 8 and 9, the scenario "The fallback agrees with the evaluator on seeded inputs": 100 000
  /// pairs from seed 258, evaluator, helper and SDM loop agree, and so does <c>Ssse3.Shuffle</c> with the
  /// table in both halves where the processor has it.
  /// </summary>
  [Test]
  public void ByteShuffle_SeededPairs_AgreeWithTheSdm() {
    var random = new Random(258);
    var mismatches = 0;
    for (var count = 0; count < 100_000; ++count) {
      var table = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 40);
      var indices = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 41);
      var expected = Pshufb(table, indices, 8);
      if (Shuffled(table, indices) != expected || HelperOperations.ByteShuffle64(table, indices, 8) != expected)
        ++mismatches;
      if (Ssse3.IsSupported && Ssse3.Shuffle(Vector128.Create(table, table).AsByte(), Vector128.Create(indices, 0UL).AsByte()).AsUInt64().GetElement(0) != expected)
        ++mismatches;
    }

    TestContext.Out.WriteLine($"100000 pairs, hardware oracle {(Ssse3.IsSupported ? "used" : "not available")}, {mismatches} mismatches");
    Assert.That(mismatches, Is.Zero);
    var legalized = HelperHarness.Legalized(HelperHarness.Module(IrIntrinsicOp.ByteShuffle, IrType.U64, [IrType.U64, IrType.U64, IrType.I32]));
    Assert.That(Mentions(legalized, IrIntrinsicOp.ByteShuffle), Is.False);
  }

}
