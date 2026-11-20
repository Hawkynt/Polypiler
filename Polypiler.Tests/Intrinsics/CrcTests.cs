using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Intrinsics;

/// <summary>
/// <c>Crc.Update</c> (#352), the operation <see cref="IrIntrinsicOp.CrcUpdate"/>: the check values of
/// the CRC RevEng catalogue for the message "123456789" with the final XOR undone, single bytes and the
/// empty span, the composition law, the relation between a reflected and a normal CRC, the span checks
/// and the refused types - on the evaluator, on the helper that replaces the operation, and on the
/// interpreted module before and after legalization. The register width 24 of the issue waits for the
/// arbitrary widths of #118.
/// </summary>
[TestFixture]
public sealed class CrcTests {

  private static readonly byte[] _message = "123456789"u8.ToArray();

  private static readonly IrType[] _registers = [IrType.U8, IrType.U16, IrType.U32, IrType.U64];

  private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

  private static ulong Evaluated(IrType type, ulong crc, ulong poly, bool reflected, byte[] data, int index, int count)
    => (ulong)IntegerOperations.CrcUpdate(type, unchecked((long)crc), unchecked((long)poly), reflected, data, index, count) & Mask(type.Bits);

  private static ulong Helped(IrType type, ulong crc, ulong poly, bool reflected, byte[] data, int index, int count)
    => type.Bits == 64 ? HelperOperations.CrcUpdate64(crc, poly, reflected, data, index, count) : HelperOperations.CrcUpdate32((uint)crc, (uint)poly, reflected, data, index, count, type.Bits);

  private static IReadOnlyList<IrType> Signature(IrType type) => [type, type, IrType.Bool, IrType.Array(IrType.U8), IrType.I32, IrType.I32];

  private static object Host(IrType type, ulong pattern) => HelperHarness.Host(type, unchecked((long)pattern));

  /// <summary>
  /// Test design 1, 4 and 7, the scenario "Catalogue parameter sets over the ASCII message": CRC-32/ISO-HDLC,
  /// CRC-32/MPEG-2, CRC-16/IBM-3740, CRC-16/ARC, CRC-8/SMBUS, CRC-8/MAXIM-DOW, CRC-64/XZ and CRC-64/ECMA-182 -
  /// every width with both reflections - on the evaluator, the helper and the interpreted module before and after legalization.
  /// </summary>
  [TestCase("u32", 0x04C11DB7UL, true, 0xFFFFFFFFUL, 0x340BC6D9UL)]
  [TestCase("u32", 0x04C11DB7UL, false, 0xFFFFFFFFUL, 0x0376E6E7UL)]
  [TestCase("u16", 0x1021UL, false, 0xFFFFUL, 0x29B1UL)]
  [TestCase("u16", 0x8005UL, true, 0x0000UL, 0xBB3DUL)]
  [TestCase("u8", 0x07UL, false, 0x00UL, 0xF4UL)]
  [TestCase("u8", 0x31UL, true, 0x00UL, 0xA1UL)]
  [TestCase("u64", 0x42F0E1EBA9EA3693UL, true, 0xFFFFFFFFFFFFFFFFUL, 0x66A2364420E6C605UL)]
  [TestCase("u64", 0x42F0E1EBA9EA3693UL, false, 0x0UL, 0x6C40DF5F0B497347UL)]
  public void CatalogueParameterSets_GiveTheirCheckValues(string name, ulong poly, bool reflected, ulong initial, ulong register) {
    var type = BitPermutationTests.TypeOf(name);
    Assert.That(Evaluated(type, initial, poly, reflected, _message, 0, 9), Is.EqualTo(register), "evaluator");
    Assert.That(Helped(type, initial, poly, reflected, _message, 0, 9), Is.EqualTo(register), "helper");
    var module = HelperHarness.Module(IrIntrinsicOp.CrcUpdate, type, Signature(type));
    object[] arguments = [Host(type, initial), Host(type, poly), reflected, _message, 0, 9];
    var expected = Convert.ToString(Host(type, register), System.Globalization.CultureInfo.InvariantCulture);
    Assert.That((HelperHarness.Outcome(module, arguments), HelperHarness.Outcome(HelperHarness.Legalized(module), arguments)), Is.EqualTo((expected, expected)));
  }

  /// <summary>The scenario "The final XOR is outside the operation": the register of CRC-32 XORed with all ones is its check value 0xCBF43926.</summary>
  [Test]
  public void FinalXor_IsOutsideTheOperation()
    => Assert.That(Evaluated(IrType.U32, 0xFFFFFFFF, 0x04C11DB7, true, _message, 0, 9) ^ 0xFFFFFFFF, Is.EqualTo(0xCBF43926UL));

  /// <summary>Test design 3, the scenario "Single bytes and the empty span".</summary>
  [TestCase("u32", 0x04C11DB7UL, true, 0x00000000UL, 1, 0x77073096UL)]
  [TestCase("u16", 0x1021UL, false, 0x0000UL, 1, 0x1021UL)]
  [TestCase("u8", 0x07UL, false, 0x00UL, 1, 0x07UL)]
  [TestCase("u32", 0x04C11DB7UL, true, 0x12345678UL, 0, 0x12345678UL)]
  [TestCase("u64", 0x42F0E1EBA9EA3693UL, false, 0xDEADBEEFUL, 0, 0xDEADBEEFUL)]
  public void SingleBytesAndTheEmptySpan(string name, ulong poly, bool reflected, ulong initial, int count, ulong register) {
    var type = BitPermutationTests.TypeOf(name);
    Assert.That(Evaluated(type, initial, poly, reflected, [0x01], 0, count), Is.EqualTo(register));
    Assert.That(Helped(type, initial, poly, reflected, [0x01], 0, count), Is.EqualTo(register));
  }

  /// <summary>Test design 5: the polynomials 1 and all ones are computed, 0 traps as an invalid operation; one wider than the register is a type the node does not take.</summary>
  [Test]
  public void PolynomialBoundaries() {
    foreach (var poly in new[] { 0x01UL, 0xFFUL })
      Assert.That(Helped(IrType.U8, 0, poly, false, _message, 0, 9), Is.EqualTo(Evaluated(IrType.U8, 0, poly, false, _message, 0, 9)));

    Assert.That(() => Evaluated(IrType.U8, 0, 0, false, _message, 0, 9), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.InvalidOperation));
    Assert.That(() => Helped(IrType.U8, 0, 0, false, _message, 0, 9), Throws.TypeOf<ArithmeticException>());
    Assert.That(() => Helped(IrType.U64, 0, 0, true, _message, 0, 9), Throws.TypeOf<ArithmeticException>());
    var wider = new IrIntrinsic(IrIntrinsicOp.CrcUpdate, IrType.U8,
      [new IrConst(IrType.U8, 0), new IrConst(IrType.U16, 0x107), new IrConst(IrType.Bool, 0), new IrLocalRef(new IrLocal("d", IrType.Array(IrType.U8))), new IrConst(IrType.I32, 0), new IrConst(IrType.I32, 0)]);
    Assert.That(IrOperators.Accepts(wider.Op, wider.Type, [.. wider.Arguments.Select(argument => argument.Type)]), Is.False);
  }

  /// <summary>
  /// Test design 6, the scenario "Reading past a bounds-checked span traps before any byte is processed":
  /// a span that ends at the end of the array is read; one byte more, a negative index or count and an
  /// index past the end trap as an index out of range - also with a polynomial of 0, whose trap comes second.
  /// </summary>
  [TestCase(0, 4, null)]
  [TestCase(4, 0, null)]
  [TestCase(0, 5, IrTrap.IndexOutOfRange)]
  [TestCase(1, 4, IrTrap.IndexOutOfRange)]
  [TestCase(-1, 1, IrTrap.IndexOutOfRange)]
  [TestCase(0, -1, IrTrap.IndexOutOfRange)]
  [TestCase(int.MaxValue, 1, IrTrap.IndexOutOfRange)]
  public void Span_IsCheckedBeforeAnyByteIsRead(int index, int count, IrTrap? trap) {
    byte[] data = [1, 2, 3, 4];
    foreach (var poly in new[] { 0x07UL, 0UL }) {
      var module = HelperHarness.Module(IrIntrinsicOp.CrcUpdate, IrType.U8, Signature(IrType.U8));
      object[] arguments = [(byte)0, (byte)poly, false, data, index, count];
      var interpreted = HelperHarness.Outcome(module, arguments);
      Assert.That(HelperHarness.Outcome(HelperHarness.Legalized(module), arguments), Is.EqualTo(interpreted));
      if (trap is { } kind)
        Assert.That(() => Evaluated(IrType.U8, 0, poly, false, data, index, count), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(kind));
      else if (poly != 0)
        Assert.That(interpreted, Does.Not.StartWith("throws"));
    }
  }

  /// <summary>Test design 9, the scenario "Updating in two parts equals updating once": 1 000 seeded splits per width and reflection.</summary>
  [Test]
  public void CompositionLaw_Holds() {
    var random = new Random(285);
    foreach (var type in _registers)
      foreach (var reflected in new[] { false, true })
        for (var count = 0; count < 1000; ++count) {
          var data = new byte[random.Next(0, 40)];
          random.NextBytes(data);
          var split = random.Next(0, data.Length + 1);
          var poly = ((ulong)random.NextInt64() & Mask(type.Bits)) | 1;
          var initial = (ulong)random.NextInt64() & Mask(type.Bits);
          var first = Evaluated(type, initial, poly, reflected, data, 0, split);
          Assert.That(Evaluated(type, first, poly, reflected, data, split, data.Length - split), Is.EqualTo(Evaluated(type, initial, poly, reflected, data, 0, data.Length)));
        }
  }

  /// <summary>
  /// Test design 8: a reflected CRC with polynomial P is the normal CRC with P on the bit-reversed bytes,
  /// with the register reversed on the way in and out - 1 000 seeded buffers per width.
  /// </summary>
  [Test]
  public void ReflectedCrc_IsTheNormalOneOnReversedBits() {
    var random = new Random(285);
    foreach (var type in _registers)
      for (var count = 0; count < 1000; ++count) {
        var data = new byte[random.Next(0, 33)];
        random.NextBytes(data);
        var poly = ((ulong)random.NextInt64() & Mask(type.Bits)) | 1;
        var initial = (ulong)random.NextInt64() & Mask(type.Bits);
        var reversedBytes = data.Select(octet => (byte)IntegerOperations.BitReverse(IrType.U8, octet)).ToArray();
        ulong Reverse(ulong value) => (ulong)IntegerOperations.BitReverse(type, IrOperators.Wrap(type, unchecked((long)value))) & Mask(type.Bits);
        Assert.That(Evaluated(type, initial, poly, true, data, 0, data.Length),
          Is.EqualTo(Reverse(Evaluated(type, Reverse(initial), poly, false, reversedBytes, 0, reversedBytes.Length))));
      }
  }

  /// <summary>
  /// Test design 10, the scenario "The fallback contains no table and agrees on seeded data": 10 000
  /// seeded buffers of 0 to 64 bytes per width and reflection (seed 285), evaluator against helper; the
  /// expanded module calls no CRC operation, reads no array but the data, and agrees on 200 of them.
  /// </summary>
  [Test]
  public void SeededBuffers_EvaluatorAndHelperAgree() {
    var random = new Random(285);
    var mismatches = 0;
    foreach (var type in _registers)
      foreach (var reflected in new[] { false, true })
        for (var count = 0; count < 10_000; ++count) {
          var data = new byte[random.Next(0, 65)];
          random.NextBytes(data);
          var poly = ((ulong)random.NextInt64() & Mask(type.Bits)) | 1;
          var initial = (ulong)random.NextInt64() & Mask(type.Bits);
          if (Evaluated(type, initial, poly, reflected, data, 0, data.Length) != Helped(type, initial, poly, reflected, data, 0, data.Length))
            ++mismatches;
        }

    Assert.That(mismatches, Is.Zero);
    var module = HelperHarness.Module(IrIntrinsicOp.CrcUpdate, IrType.U16, Signature(IrType.U16));
    var legalized = HelperHarness.Legalized(module);
    Assert.That(IrVerifier.Verify(legalized), Is.Empty);
    Assert.That(BitPermutationTests.Mentions(legalized, IrIntrinsicOp.CrcUpdate), Is.False);
    Assert.That(BitPermutationTests.Statements(legalized).SelectMany(IrTree.Nodes).OfType<IrArrayLoad>().All(load => load.Type == IrType.U8), Is.True);
    for (var count = 0; count < 200; ++count) {
      var data = new byte[random.Next(0, 65)];
      random.NextBytes(data);
      object[] arguments = [(ushort)random.Next(65536), (ushort)(0x1021), random.Next(2) == 1, data, 0, data.Length];
      Assert.That(HelperHarness.Outcome(legalized, arguments), Is.EqualTo(HelperHarness.Outcome(module, arguments)));
    }
  }

  /// <summary>Test design 11: a register of another type than the polynomial, a signed register, elements other than bytes and a count of another type are not a CRC update.</summary>
  [Test]
  public void OtherTypes_AreRefused() {
    var bytes = IrType.Array(IrType.U8);
    IrType[][] refused = [
      [IrType.U16, IrType.U32, IrType.Bool, bytes, IrType.I32, IrType.I32],
      [IrType.I32, IrType.I32, IrType.Bool, bytes, IrType.I32, IrType.I32],
      [IrType.U32, IrType.U32, IrType.Bool, IrType.Array(IrType.U16), IrType.I32, IrType.I32],
      [IrType.U32, IrType.U32, IrType.Bool, bytes, IrType.I32, IrType.I64],
      [IrType.U32, IrType.U32, IrType.I32, bytes, IrType.I32, IrType.I32]
    ];
    foreach (var types in refused)
      Assert.That(IrVerifier.Verify(HelperHarness.Module(IrIntrinsicOp.CrcUpdate, types[0], types)), Is.Not.Empty, string.Join(", ", types.Select(type => type.ToString())));

    var contract = ContractConformanceTests.Read(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "intrinsics", "intmath", "crcupdate.yaml"));
    Assert.That(contract.Spelling, Is.EqualTo("Crc.Update"));
    var node = (IrIntrinsic)((IrReturn)HelperHarness.Module(IrIntrinsicOp.CrcUpdate, IrType.I32, refused[1]).Functions[0].Body.Statements[0]).Value!;
    Assert.That(contract.Check(node)?.Code, Is.EqualTo("PLY0252"));
  }

}
