using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Tests.Verification.Images;

/// <summary><see cref="ImageValue"/>: structural equality and the spelling mismatch messages use.</summary>
[TestFixture]
public sealed class ImageValueTests {

  private static ImageValue.Integer Int(int value) => new(value);

  [Test]
  public void Values_CompareStructurally() {
    Same(new ImageValue.Bytes([1, 2]), new ImageValue.Bytes([1, 2]), new ImageValue.Bytes([1, 3]));
    Same(new ImageValue.Sequence([Int(1)]), new ImageValue.Sequence([Int(1)]), new ImageValue.Sequence([Int(2)]));
    Same(new ImageValue.Record([Int(1)]), new ImageValue.Record([Int(1)]), new ImageValue.Record([Int(2)]));
    Same(new ImageValue.Tuple([Int(1), Int(2)]), new ImageValue.Tuple([Int(1), Int(2)]), new ImageValue.Tuple([Int(2), Int(1)]));
    Assert.That(new ImageValue.Bytes([1]).Equals(null), Is.False);
    Assert.That(new ImageValue.Sequence([]).Equals(null), Is.False);
    Assert.That(new ImageValue.Record([]).Equals(null), Is.False);
    Assert.That(new ImageValue.Tuple([]).Equals(null), Is.False);
    Assert.That(((ImageValue)new ImageValue.Sequence([Int(1)])).Equals(new ImageValue.Tuple([Int(1)])), Is.False);
  }

  private static void Same(ImageValue value, ImageValue equal, ImageValue other) {
    Assert.That(value, Is.EqualTo(equal).And.Not.EqualTo(other));
    Assert.That(value.GetHashCode(), Is.EqualTo(equal.GetHashCode()));
  }

  /// <summary>Floats compare by their bits: two NaN payloads differ, and so do the two zeros.</summary>
  [Test]
  public void Floats_CompareByBits() {
    Assert.That(ImageValue.FloatBits.Of(0.0), Is.Not.EqualTo(ImageValue.FloatBits.Of(-0.0)));
    Assert.That(ImageValue.FloatBits.Of(1.5f).Bits, Is.EqualTo(0x3fc00000UL));
    Assert.That(ImageValue.FloatBits.Of(1.5).Bits, Is.EqualTo(0x3ff8000000000000UL));
  }

  [Test]
  public void Values_SpellThemselves() {
    Assert.That(Int(-7).ToString(), Is.EqualTo("-7"));
    Assert.That((new ImageValue.Boolean(true).ToString(), new ImageValue.Boolean(false).ToString()), Is.EqualTo(("true", "false")));
    Assert.That(ImageValue.FloatBits.Of(1.0).ToString(), Is.EqualTo("0x3ff0000000000000"));
    Assert.That(new ImageValue.Bytes([0xab, 1]).ToString(), Is.EqualTo("bytes[ab01]"));
    Assert.That(new ImageValue.Text("a\"\\\n\ud83dé").ToString(), Is.EqualTo("\"a\\\"\\\\\\u000a\\ud83dé\""));
    Assert.That(new ImageValue.Sequence([Int(1), Int(2)]).ToString(), Is.EqualTo("[1, 2]"));
    Assert.That((ImageValue.Optional.Absent.ToString(), new ImageValue.Optional(Int(5)).ToString()), Is.EqualTo(("absent", "5")));
    Assert.That(new ImageValue.Record([Int(1), Int(2)]).ToString(), Is.EqualTo("{1, 2}"));
    Assert.That(new ImageValue.Tuple([Int(1), Int(2)]).ToString(), Is.EqualTo("(1, 2)"));
    Assert.That((new ImageValue.Union(0, null).ToString(), new ImageValue.Union(1, Int(3)).ToString()), Is.EqualTo(("#0 null", "#1 3")));
    Assert.That(new ImageValue.Trap(TrapClass.Parse("bounds")).ToString(), Is.EqualTo("!bounds"));
  }

  [Test]
  public void Null_IsRefused() {
    Assert.Throws<ArgumentNullException>(() => _ = new ImageValue.Bytes(null!));
    Assert.Throws<ArgumentNullException>(() => _ = new ImageValue.Text(null!));
    Assert.Throws<ArgumentNullException>(() => _ = new ImageValue.Sequence(null!));
    Assert.Throws<ArgumentNullException>(() => _ = new ImageValue.Record([null!]));
    Assert.Throws<ArgumentNullException>(() => _ = new ImageValue.Tuple(null!));
    Assert.Throws<ArgumentNullException>(() => _ = new ImageValue.Trap(null!));
  }

}
