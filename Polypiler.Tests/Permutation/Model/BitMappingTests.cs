using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Tests.Permutation.Model;

/// <summary>
/// The bit-mapping value of issue #268: validation, derived facts, factories, equality and the route
/// map. Every expected mask is worked out by hand from the definition and written next to the case.
/// </summary>
[TestFixture]
public sealed class BitMappingTests {

  private static BitMapping Of(int inputWidth, params BitSource[] outputs) => BitMapping.Create(inputWidth, outputs);

  private static PermutationModelException Refused(TestDelegate action) => Assert.Throws<PermutationModelException>(action)!;

  /// <summary>Test design 1: a byte reversal reads every input once, plain, into a square output.</summary>
  [Test]
  public void ByteReversal_IsABijection() {
    var mapping = BitMapping.Parse("8:[7,6,5,4,3,2,1,0]");
    Assert.Multiple(() => {
      Assert.That(mapping.IsSquare, Is.True);
      Assert.That(mapping.IsBijective, Is.True);
      Assert.That(mapping.IsInjective, Is.True);
      Assert.That(mapping.HasDuplication, Is.False);
      Assert.That(mapping.HasInversion, Is.False);
      Assert.That(mapping.HasConstants, Is.False);
      Assert.That(mapping.HasDontCares, Is.False);
      // Every output has a fixed value: bits 0..7 = 0xFF; every input is read: dropped 0.
      Assert.That(mapping.RequiredOutputMask, Is.EqualTo(new BigInteger(0xFF)));
      Assert.That(mapping.UsedInputMask, Is.EqualTo(new BigInteger(0xFF)));
      Assert.That(mapping.DroppedInputMask, Is.EqualTo(BigInteger.Zero));
      Assert.That(mapping.InvertMask, Is.EqualTo(BigInteger.Zero));
      Assert.That(mapping.OnesMask, Is.EqualTo(BigInteger.Zero));
      Assert.That(mapping.ZerosMask, Is.EqualTo(BigInteger.Zero));
      Assert.That(mapping, Is.EqualTo(BitMapping.Reverse(8)));
      Assert.That(mapping.Outputs, Is.EqualTo(Enumerable.Range(0, 8).Reverse().Select(BitSource.From)));
    });
  }

  /// <summary>Test design 3: one entry of every kind, each a fact and a mask of its own.</summary>
  [Test]
  public void EveryEntryKind_IsAFactOfTheMapping() {
    // Outputs 0..7: src 0, src 0, not 1, one, don't-care, src 7, src 6, src 5.
    var mapping = BitMapping.Parse("8:[0,0,~1,=1,?,7,6,5]");
    Assert.Multiple(() => {
      Assert.That(mapping.IsBijective, Is.False);
      Assert.That(mapping.IsInjective, Is.False);
      Assert.That(mapping.HasDuplication, Is.True);
      Assert.That(mapping.HasInversion, Is.True);
      Assert.That(mapping.HasConstants, Is.True);
      Assert.That(mapping.HasDontCares, Is.True);
      Assert.That(mapping.UseCount(0), Is.EqualTo(2));
      Assert.That(mapping.UseCount(1), Is.EqualTo(1));
      Assert.That(mapping.UseCount(2), Is.Zero);
      // Inverted output 2 = 0x04; constant one at output 3 = 0x08; no constant zero.
      Assert.That(mapping.InvertMask, Is.EqualTo(new BigInteger(0x04)));
      Assert.That(mapping.OnesMask, Is.EqualTo(new BigInteger(0x08)));
      Assert.That(mapping.ZerosMask, Is.EqualTo(BigInteger.Zero));
      // Everything but the don't-care at output 4: 0xFF & ~0x10 = 0xEF.
      Assert.That(mapping.RequiredOutputMask, Is.EqualTo(new BigInteger(0xEF)));
      // Inputs read: 0, 1, 5, 6, 7 = 0x01 | 0x02 | 0x20 | 0x40 | 0x80 = 0xE3; dropped 2, 3, 4 = 0x1C.
      Assert.That(mapping.UsedInputMask, Is.EqualTo(new BigInteger(0xE3)));
      Assert.That(mapping.DroppedInputMask, Is.EqualTo(new BigInteger(0x1C)));
    });
  }

  [Test]
  public void ConstantZeros_AreTheirOwnMask() {
    var mapping = BitMapping.Parse("4:[=0,=1,=0,3]");
    Assert.Multiple(() => {
      Assert.That(mapping.ZerosMask, Is.EqualTo(new BigInteger(0b0101)));
      Assert.That(mapping.OnesMask, Is.EqualTo(new BigInteger(0b0010)));
      Assert.That(mapping.UsedInputMask, Is.EqualTo(new BigInteger(0b1000)));
      Assert.That(mapping.IsInjective, Is.True);
      Assert.That(mapping.IsBijective, Is.False);
    });
  }

  /// <summary>Test design 5: a narrower output, then a wider output that reads its inputs twice.</summary>
  [Test]
  public void NonSquareMappings_AreValidButNoBijection() {
    var narrow = BitMapping.Parse("8:[0,2,4,6]");
    var wide = BitMapping.Parse("2:[0,1,0,1]");
    Assert.Multiple(() => {
      Assert.That((narrow.InputWidth, narrow.OutputWidth, narrow.IsSquare, narrow.IsInjective, narrow.IsBijective), Is.EqualTo((8, 4, false, true, false)));
      // Inputs 0, 2, 4, 6 = 0x55 read; 1, 3, 5, 7 = 0xAA dropped.
      Assert.That((narrow.UsedInputMask, narrow.DroppedInputMask), Is.EqualTo((new BigInteger(0x55), new BigInteger(0xAA))));
      Assert.That((wide.InputWidth, wide.OutputWidth, wide.HasDuplication, wide.IsBijective), Is.EqualTo((2, 4, true, false)));
      Assert.That(wide.RequiredOutputMask, Is.EqualTo(new BigInteger(0x0F)));
    });
  }

  [Test]
  public void InversionOrConstant_IsNeverABijection() {
    Assert.Multiple(() => {
      Assert.That(BitMapping.Parse("2:[~0,1]").IsBijective, Is.False);
      Assert.That(BitMapping.Parse("2:[~0,1]").IsInjective, Is.True);
      Assert.That(BitMapping.Parse("2:[=0,1]").IsBijective, Is.False);
      Assert.That(BitMapping.Parse("2:[?,1]").IsBijective, Is.False);
      Assert.That(BitMapping.Parse("2:[1,1]").IsBijective, Is.False);
    });
  }

  /// <summary>Test design 7: widths 0, 1, 4096 and 4097.</summary>
  [TestCase(0, false)]
  [TestCase(1, true)]
  [TestCase(4096, true)]
  [TestCase(4097, false)]
  [TestCase(-1, false)]
  public void IdentityWidth_Boundaries(int width, bool valid) {
    if (valid) {
      var mapping = BitMapping.Identity(width);
      Assert.That((mapping.InputWidth, mapping.OutputWidth, mapping.IsBijective), Is.EqualTo((width, width, true)));
      Assert.That(mapping.UsedInputMask, Is.EqualTo((BigInteger.One << width) - 1));
      return;
    }
    var refusal = Refused(() => BitMapping.Identity(width));
    Assert.That(refusal.Diagnostic.Code, Is.EqualTo("PLY0800"));
    Assert.That(refusal.Diagnostic.Message, Is.EqualTo($"mapping width {width} is outside 1..4096"));
    Assert.That(refusal.Message, Is.EqualTo($"PLY0800: mapping width {width} is outside 1..4096"));
  }

  [TestCase(0)]
  [TestCase(4097)]
  public void OutputCount_OutsideTheRange_IsRefused(int count) {
    var refusal = Refused(() => BitMapping.Create(8, [.. Enumerable.Repeat(BitSource.Zero, count)]));
    Assert.That(refusal.Diagnostic.Message, Is.EqualTo($"mapping width {count} is outside 1..4096"));
  }

  [Test]
  public void OutputCount_AtTheLimit_IsAccepted()
    => Assert.That(BitMapping.Create(1, [.. Enumerable.Repeat(BitSource.From(0), 4096)]).UseCount(0), Is.EqualTo(4096));

  /// <summary>Test design 8: source index w-1 and w.</summary>
  [Test]
  public void SourceIndex_Boundaries() {
    Assert.That(BitMapping.Parse("8:[7]").UsedInputMask, Is.EqualTo(new BigInteger(0x80)));
    var refusal = Refused(() => BitMapping.Parse("8:[8]"));
    Assert.That(refusal.Diagnostic.ToString(), Is.EqualTo("error PLY0801: output bit 0 reads input bit 8, input width is 8"));
  }

  [Test]
  public void NegativeSource_IsRefused() {
    var refusal = Refused(() => Of(8, BitSource.From(0), BitSource.Not(-1)));
    Assert.That(refusal.Diagnostic.Message, Is.EqualTo("output bit 1 reads input bit -1, input width is 8"));
    Assert.That(refusal.Diagnostic.ExitCode, Is.EqualTo(ExitCode.UnsupportedConstruct));
  }

  [Test]
  public void TryCreate_ReportsInsteadOfThrowing() {
    var diagnostics = new DiagnosticBag();
    Assert.That(BitMapping.TryCreate(4, [BitSource.From(4)], diagnostics, out var mapping), Is.False);
    Assert.That(mapping, Is.Null);
    Assert.That(diagnostics.Items.Select(item => item.Code), Is.EqualTo(new[] { "PLY0801" }));
    Assert.That(diagnostics.ExitCode, Is.EqualTo(ExitCode.UnsupportedConstruct));

    Assert.That(BitMapping.TryCreate(4, [BitSource.From(3)], diagnostics, out mapping), Is.True);
    Assert.That(mapping!.OutputWidth, Is.EqualTo(1));
    Assert.That(diagnostics.Items, Has.Count.EqualTo(1));
  }

  [Test]
  public void Create_ArgumentErrors() {
    Assert.Throws<ArgumentNullException>(() => BitMapping.Create(1, null!));
    Assert.Throws<ArgumentNullException>(() => BitMapping.TryCreate(1, [BitSource.Zero], null!, out _));
    var malformed = Assert.Throws<ArgumentException>(() => Of(1, new BitSource(BitSourceKind.One, 0)))!;
    Assert.That(malformed.Message, Does.StartWith("output bit 0: bit source One with index 0 is not well formed"));
    Assert.Throws<ArgumentException>(() => Of(1, new BitSource((BitSourceKind)9, 0)));
  }

  [Test]
  public void Create_CopiesTheEntries() {
    var outputs = new[] { BitSource.From(0) };
    var mapping = BitMapping.Create(1, outputs);
    outputs[0] = BitSource.One;
    Assert.That(mapping.Outputs[0], Is.EqualTo(BitSource.From(0)));
    Assert.That(mapping.Outputs, Is.Not.AssignableTo<BitSource[]>());
  }

  [Test]
  public void UseCount_OutsideTheInput_Throws() {
    var mapping = BitMapping.Identity(4);
    Assert.Throws<ArgumentOutOfRangeException>(() => mapping.UseCount(-1));
    Assert.Throws<ArgumentOutOfRangeException>(() => mapping.UseCount(4));
    Assert.That(mapping.UseCount(3), Is.EqualTo(1));
  }

  [Test]
  public void Factories_AsDefined() {
    Assert.Multiple(() => {
      Assert.That(BitMapping.Identity(4).ToString(), Is.EqualTo("4:[0..3]"));
      Assert.That(BitMapping.Reverse(3).ToString(), Is.EqualTo("3:[2,1,0]"));
      // Output i reads (i + w/2) mod w.
      Assert.That(BitMapping.SwapHalves(4).ToString(), Is.EqualTo("4:[2,3,0,1]"));
      Assert.That(BitMapping.SwapHalves(4).IsBijective, Is.True);
      // 0b0110 = outputs 1 and 2 are one.
      Assert.That(BitMapping.Constant(4, 6).ToString(), Is.EqualTo("4:[=0,=1,=1,=0]"));
      Assert.That(BitMapping.Constant(4, 6).UsedInputMask, Is.EqualTo(BigInteger.Zero));
      Assert.That(BitMapping.Constant(4, 6).DroppedInputMask, Is.EqualTo(new BigInteger(0x0F)));
      Assert.That(BitMapping.Constant(2, 3).ToString(), Is.EqualTo("2:[=1,=1]"));
      Assert.That(BitMapping.Constant(4096, (BigInteger.One << 4096) - 1).OnesMask, Is.EqualTo((BigInteger.One << 4096) - 1));
    });
  }

  /// <summary>Test design 9: rotation amounts 0, w-1, w and -1.</summary>
  [TestCase(0, "8:[0..7]")]
  [TestCase(7, "8:[1..7,0]")]
  [TestCase(8, "8:[0..7]")]
  [TestCase(-1, "8:[1..7,0]")]
  [TestCase(1, "8:[7,0..6]")]
  [TestCase(int.MinValue, "8:[0..7]")]
  public void Rotation_Amounts(int amount, string expected) {
    var rotation = BitMapping.Rotation(8, amount);
    Assert.That(rotation.ToString(), Is.EqualTo(expected));
    Assert.That(rotation.IsBijective, Is.True);
  }

  [Test]
  public void Rotation_ByOneAndByMinusSeven_ReadInputBitsAsDefined() {
    // Output (i + 1) mod 8 reads input i: output 0 reads 7, output 1 reads 0.
    Assert.That(BitMapping.Rotation(8, 1).Outputs.Take(2), Is.EqualTo(new[] { BitSource.From(7), BitSource.From(0) }));
    Assert.That(BitMapping.Rotation(8, -7), Is.EqualTo(BitMapping.Rotation(8, 1)));
  }

  /// <summary>Test design 12: swap-halves of an odd width.</summary>
  [Test]
  public void SwapHalves_OddWidth_IsAnArgumentError() {
    var error = Assert.Throws<ArgumentException>(() => BitMapping.SwapHalves(7))!;
    Assert.That(error.Message, Does.StartWith("swap-halves needs an even width, got 7"));
  }

  [TestCase(0)]
  [TestCase(4098)]
  public void Factories_RefuseAWidthOutOfRange(int width) {
    Assert.Multiple(() => {
      Assert.That(Refused(() => BitMapping.SwapHalves(width)).Diagnostic.Code, Is.EqualTo("PLY0800"));
      Assert.That(Refused(() => BitMapping.Reverse(width)).Diagnostic.Code, Is.EqualTo("PLY0800"));
      Assert.That(Refused(() => BitMapping.Rotation(width, 3)).Diagnostic.Code, Is.EqualTo("PLY0800"));
      Assert.That(Refused(() => BitMapping.Constant(width, 0)).Diagnostic.Code, Is.EqualTo("PLY0800"));
    });
  }

  [Test]
  public void Constant_OutsideTheWidth_IsAnArgumentError() {
    Assert.Throws<ArgumentOutOfRangeException>(() => BitMapping.Constant(4, 16));
    Assert.Throws<ArgumentOutOfRangeException>(() => BitMapping.Constant(4, -1));
    Assert.That(BitMapping.Constant(4, 15).OnesMask, Is.EqualTo(new BigInteger(15)));
  }

  [Test]
  public void Equality_IsWidthsAndEntries() {
    var a = BitMapping.Parse("8:[0,?]");
    Assert.Multiple(() => {
      Assert.That(a, Is.EqualTo(BitMapping.Parse("8:[0, ?]")));
      Assert.That(a == BitMapping.Parse("8:[0,?]"), Is.True);
      Assert.That(a.GetHashCode(), Is.EqualTo(BitMapping.Parse("8:[0,?]").GetHashCode()));
      // A don't-care is not the constant 0; nothing is normalised.
      Assert.That(a, Is.Not.EqualTo(BitMapping.Parse("8:[0,=0]")));
      Assert.That(a != BitMapping.Parse("8:[0,=0]"), Is.True);
      Assert.That(a, Is.Not.EqualTo(BitMapping.Parse("9:[0,?]")));
      Assert.That(a, Is.Not.EqualTo(BitMapping.Parse("8:[0,?,?]")));
      Assert.That(a.Equals((object)BitMapping.Parse("8:[0,?]")), Is.True);
      Assert.That(a.Equals("8:[0,?]"), Is.False);
      Assert.That(a.Equals(null as BitMapping), Is.False);
      Assert.That(a.Equals(null as object), Is.False);
      Assert.That(BitMapping.Identity(1).Equals(BitMapping.Identity(1)), Is.True);
      var same = a;
      Assert.That(a.Equals(same), Is.True);
      Assert.That(BitMapping.Identity(1) == null, Is.False);
      Assert.That(null == BitMapping.Identity(1), Is.False);
      Assert.That((BitMapping?)null == null, Is.True);
    });
  }

  /// <summary>The hash is FNV-1a, so it is the same in every process; these values are fixed by the definition.</summary>
  [Test]
  public void HashCode_IsStableAcrossProcesses() {
    static int Fnv(params int[] words) {
      var hash = 2166136261u;
      foreach (var word in words)
        for (var shift = 0; shift < 32; shift += 8) {
          hash ^= (byte)(word >> shift);
          hash *= 16777619u;
        }
      return unchecked((int)hash);
    }

    Assert.That(BitMapping.Parse("2:[~1,?]").GetHashCode(), Is.EqualTo(Fnv(2, 2, (int)BitSourceKind.InvertedSource, 1, (int)BitSourceKind.DontCare, -1)));
    // FNV-1a of the eight bytes 01 00 00 00 01 00 00 00 followed by kind 0 and index 0.
    Assert.That(BitMapping.Identity(1).GetHashCode(), Is.EqualTo(Fnv(1, 1, 0, 0)));
  }

  /// <summary>Test design 13 and the BitPerm view of every entry kind.</summary>
  [Test]
  public void RouteMap_KeepsSourcesAndLosesTheRest() {
    var mapping = BitMapping.Parse("5:[4,~0,=1,=0,?]");
    Assert.That(mapping.ToRouteMap(), Is.EqualTo(new[] { 4, 0, -1, -1, -1 }));
    var back = BitMapping.FromRouteMap(mapping.ToRouteMap());
    Assert.That(back.ToString(), Is.EqualTo("5:[4,0,?,?,?]"));
    Assert.That(back.HasConstants || back.HasInversion, Is.False);
    Assert.That(Assert.Throws<InvalidOperationException>(() => BitMapping.Parse("8:[0,2,4,6]").ToRouteMap())!.Message,
      Is.EqualTo("a route map needs a square mapping; this one maps 8 input bits to 4 output bits"));
  }

  [Test]
  public void FromRouteMap_Errors() {
    Assert.Throws<ArgumentNullException>(() => BitMapping.FromRouteMap(null!));
    Assert.That(Refused(() => BitMapping.FromRouteMap([])).Diagnostic.Code, Is.EqualTo("PLY0800"));
    Assert.That(Refused(() => BitMapping.FromRouteMap([0, 2])).Diagnostic.Message, Is.EqualTo("output bit 1 reads input bit 2, input width is 2"));
    Assert.That(Refused(() => BitMapping.FromRouteMap([0, -2])).Diagnostic.Code, Is.EqualTo("PLY0801"));
  }

  [Test]
  public void BitSource_FactoriesAndText() {
    Assert.Multiple(() => {
      Assert.That(BitSource.From(3), Is.EqualTo(new BitSource(BitSourceKind.Source, 3)));
      Assert.That(BitSource.Not(3), Is.EqualTo(new BitSource(BitSourceKind.InvertedSource, 3)));
      Assert.That(new[] { BitSource.Zero, BitSource.One, BitSource.DontCare }.Select(entry => entry.Index), Is.All.EqualTo(-1));
      Assert.That(new[] { BitSource.From(3), BitSource.Not(3), BitSource.Zero, BitSource.One, BitSource.DontCare }.Select(entry => entry.ToString()),
        Is.EqualTo(new[] { "3", "~3", "=0", "=1", "?" }));
      Assert.That(BitSource.Not(3).ReadsInput && !BitSource.One.ReadsInput, Is.True);
      Assert.That(new BitSource(BitSourceKind.DontCare, 0).IsWellFormed, Is.False);
      Assert.That(BitSource.From(-5).IsWellFormed, Is.True);
    });
    Assert.Throws<InvalidOperationException>(() => _ = new BitSource(BitSourceKind.Zero, 2).ToString());
  }

  [Test]
  public void Exception_NeedsADiagnostic()
    => Assert.Throws<ArgumentNullException>(() => _ = new PermutationModelException(null!));

  /// <summary>The model holds widths beyond 64 bits; nothing is capped at a register.</summary>
  [Test]
  public void WideMappings_KeepEveryBit() {
    var outputs = new List<BitSource>();
    for (var bit = 0; bit < 200; ++bit)
      outputs.Add(bit == 130 ? BitSource.Not(199) : BitSource.From(bit));
    var mapping = BitMapping.Create(200, outputs);
    Assert.That(mapping.InvertMask, Is.EqualTo(BigInteger.One << 130));
    Assert.That(mapping.DroppedInputMask, Is.EqualTo(BigInteger.One << 130));
    Assert.That(mapping.UseCount(199), Is.EqualTo(2));
  }

}
