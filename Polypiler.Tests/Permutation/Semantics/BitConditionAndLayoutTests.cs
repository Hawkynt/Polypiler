using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Tests.Permutation.Semantics;

/// <summary>
/// <see cref="BitCondition"/> and <see cref="BitLayout"/> of issue #267: the input domain, the required
/// outputs, <c>PLY0806</c>, lane sizes (test design 7) and lane respect (test design 8).
/// </summary>
[TestFixture]
public sealed class BitConditionAndLayoutTests {

  /// <summary>Known zero 0b0100, known one 0b0001: the domain is every x with bit 2 clear and bit 0 set.</summary>
  [TestCase(0b0001, true)]
  [TestCase(0b1011, true)]
  [TestCase(0b0000, false)]
  [TestCase(0b0101, false)]
  public void Contains_FollowsTheKnownBits(int input, bool expected)
    => Assert.That(new BitCondition(0b0100, 0b0001, 0xF).Contains(input), Is.EqualTo(expected));

  [Test]
  public void Admits_ReportsPLY0806OutsideTheDomain() {
    var condition = new BitCondition(0b0100, 0b0001, 0xF);
    var diagnostics = new DiagnosticBag();
    Assert.Multiple(() => {
      Assert.That(condition.Admits(0b0011, diagnostics), Is.True);
      Assert.That(diagnostics.Items, Is.Empty);
      Assert.That(condition.Admits(0b0000, diagnostics), Is.False);
      Assert.That(condition.Admits(0xFE, diagnostics), Is.False);
      Assert.That(diagnostics.Items.Select(item => $"{item.Code}: {item.Message}"), Is.EqualTo(new[] {
        "PLY0806: input 0x0 is outside the declared input domain",
        "PLY0806: input 0xFE is outside the declared input domain"
      }));
      Assert.That(diagnostics.ExitCode, Is.EqualTo(ExitCode.UnsupportedConstruct));
      Assert.Throws<ArgumentNullException>(() => condition.Admits(0, null!));
      Assert.Throws<ArgumentOutOfRangeException>(() => condition.Contains(-1));
    });
  }

  [Test]
  public void Unconstrained_RequiresEveryOutput() {
    Assert.Multiple(() => {
      Assert.That(BitCondition.Unconstrained(3, 4), Is.EqualTo(new BitCondition(BigInteger.Zero, BigInteger.Zero, new BigInteger(0xF))));
      Assert.That(BitCondition.Unconstrained(1, 1).Contains(1), Is.True);
      Assert.That(Assert.Throws<PermutationModelException>(() => BitCondition.Unconstrained(0, 4))!.Message, Is.EqualTo("PLY0800: mapping width 0 is outside 1..4096"));
      Assert.That(Assert.Throws<PermutationModelException>(() => BitCondition.Unconstrained(4, 4097))!.Message, Is.EqualTo("PLY0800: mapping width 4097 is outside 1..4096"));
    });
  }

  [Test]
  public void ForMapping_RequiresAllButTheDontCares() {
    var condition = BitCondition.ForMapping(BitMapping.Parse("4:[0,?,=1,3]"));
    Assert.Multiple(() => {
      Assert.That(condition.RequiredOutputMask, Is.EqualTo(new BigInteger(0b1101)));
      Assert.That(condition.Requires(0), Is.True);
      Assert.That(condition.Requires(1), Is.False);
      Assert.That(condition.Requires(4), Is.False);
      Assert.Throws<ArgumentOutOfRangeException>(() => condition.Requires(-1));
      Assert.Throws<ArgumentNullException>(() => BitCondition.ForMapping(null!));
    });
  }

  /// <summary>Test design 7: lane size 8 divides 32, 12 does not; boundaries 1 and the width itself.</summary>
  [TestCase(8, 32, true)]
  [TestCase(12, 32, false)]
  [TestCase(1, 32, true)]
  [TestCase(32, 32, true)]
  [TestCase(33, 32, false)]
  public void Divides_IsTheLaneSizeRule(int lane, int width, bool expected)
    => Assert.That(new BitLayout(lane, ByteOrder.NotApplicable).Divides(width), Is.EqualTo(expected));

  /// <summary>Test design 8: swapping the halves of 16 bits crosses the byte lanes; reversing each byte stays inside them.</summary>
  [Test]
  public void RespectsLanes_TellsByteWiseFromCrossLane() {
    var bytes = new BitLayout(8, ByteOrder.LittleEndian);
    Assert.Multiple(() => {
      Assert.That(bytes.RespectsLanes(BitMapping.SwapHalves(16)), Is.False);
      Assert.That(bytes.RespectsLanes(BitMapping.Parse("16:[7,6,5,4,3,2,1,0,15,14,13,12,11,10,9,8]")), Is.True);
      Assert.That(bytes.RespectsLanes(BitMapping.Parse("16:[~7,=1,?,4,3,2,1,0,15,14,13,12,11,10,9,8]")), Is.True, "inverted sources, constants and don't-cares");
      Assert.That(bytes.RespectsLanes(BitMapping.Parse("16:[7,6,5,4,3,2,1,0,15,14,13,12,11,10,9,~7]")), Is.False, "an inverted source from the other lane");
      Assert.Throws<ArgumentNullException>(() => bytes.RespectsLanes(null!));
    });
  }

  [Test]
  public void Layout_ValidatesItsParts() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentOutOfRangeException>(() => _ = new BitLayout(0, ByteOrder.NotApplicable));
      Assert.Throws<ArgumentOutOfRangeException>(() => _ = new BitLayout(8, (ByteOrder)3));
      Assert.That(new BitLayout(1, ByteOrder.BigEndian).ByteOrder, Is.EqualTo(ByteOrder.BigEndian));
    });
  }

  /// <summary>The default layout is one lane of the width when the widths agree, else their greatest common divisor.</summary>
  [TestCase(32, 32, 32)]
  [TestCase(8, 4, 4)]
  [TestCase(12, 8, 4)]
  [TestCase(7, 5, 1)]
  public void Default_IsOneLaneOrTheGcd(int input, int output, int lane)
    => Assert.That(BitLayout.Default(input, output), Is.EqualTo(new BitLayout(lane, ByteOrder.NotApplicable)));

  [TestCase(0, 8)]
  [TestCase(8, 0)]
  [TestCase(4097, 8)]
  [TestCase(8, 4097)]
  public void Default_RefusesAWidthOutOfRange(int input, int output)
    => Assert.Throws<ArgumentOutOfRangeException>(() => BitLayout.Default(input, output));

}
