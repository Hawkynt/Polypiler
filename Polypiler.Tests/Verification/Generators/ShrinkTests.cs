using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Generators;

namespace Polypiler.Tests.Verification.Generators;

/// <summary>The shrink candidates of integers and buffers.</summary>
[TestFixture]
public sealed class ShrinkTests {

  /// <summary>Test design 7: 0 has no candidate, 1 only 0, larger values halve the distance to themselves.</summary>
  [TestCase(0UL, new ulong[0])]
  [TestCase(1UL, new ulong[] { 0 })]
  [TestCase(2UL, new ulong[] { 0, 1 })]
  [TestCase(3UL, new ulong[] { 0, 2 })]
  [TestCase(127UL, new ulong[] { 0, 64, 96, 112, 120, 124, 126 })]
  [TestCase(ulong.MaxValue, new ulong[] { 0, 0x8000000000000000 })]
  public void Integer_YieldsZeroThenHalvesTheDistance(ulong value, ulong[] head)
    => Assert.That(Shrink.Integer(value).Take(head.Length), Is.EqualTo(head));

  /// <summary>Every candidate is smaller than the value, so a pattern keeps its width; the last one is the value minus 1.</summary>
  [TestCase(5UL)]
  [TestCase(255UL)]
  [TestCase(ulong.MaxValue)]
  public void Integer_CandidatesAreSmallerAndEndOneBelow(ulong value) {
    var candidates = Shrink.Integer(value).ToList();
    Assert.That(candidates, Has.All.LessThan(value));
    Assert.That(candidates[^1], Is.EqualTo(value - 1));
    Assert.That(candidates, Is.Unique);
  }

  /// <summary>Test design 8: the empty buffer has no candidate.</summary>
  [Test]
  public void Buffer_OfNothing_HasNoCandidate()
    => Assert.That(Shrink.Buffer([]), Is.Empty);

  /// <summary>Test design 8: a one-byte buffer shrinks to the empty buffer, then its byte shrinks.</summary>
  [Test]
  public void Buffer_OfOneByte_IsEmptiedThenTheByteShrinks()
    => Assert.That(Shrink.Buffer([0x05]), Is.EqualTo(new byte[][] { [], [0x00], [0x03], [0x04] }));

  /// <summary>The candidate order: empty, the halves, each byte removed, each byte shrunk — never a repeat, never the buffer itself.</summary>
  [Test]
  public void Buffer_CandidatesComeInTheirOrderWithoutRepeats() {
    byte[] value = [0x01, 0x02, 0x01];
    Assert.That(Shrink.Buffer(value), Is.EqualTo(new byte[][] {
      [],
      [0x01],
      [0x02, 0x01],
      [0x01, 0x01],
      [0x01, 0x02],
      [0x00, 0x02, 0x01],
      [0x01, 0x00, 0x01],
      [0x01, 0x01, 0x01],
      [0x01, 0x02, 0x00]
    }));
    Assert.That(value, Is.EqualTo(new byte[] { 0x01, 0x02, 0x01 }), "the buffer itself is left alone");
  }

  /// <summary>No buffer, no candidates.</summary>
  [Test]
  public void Buffer_RefusesNull()
    => Assert.That(() => Shrink.Buffer(null!), Throws.TypeOf<ArgumentNullException>());

}
