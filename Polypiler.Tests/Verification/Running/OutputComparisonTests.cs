using System;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Running;

/// <summary>Comparing captured output with an expected value, byte for byte or with normalized line ends.</summary>
[TestFixture]
public sealed class OutputComparisonTests {

  private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

  private static ComparisonResult Compare(string expected, string actual, ComparisonMode mode = ComparisonMode.Exact)
    => OutputComparison.Compare(Bytes(expected), Bytes(actual), mode);

  [Test]
  public void EqualOutputs_AreEqual() {
    Assert.That(Compare("hello 42\n", "hello 42\n"), Is.SameAs(ComparisonResult.Equal));
    Assert.That(ComparisonResult.Equal.ToString(), Is.EqualTo("Equal"));
  }

  /// <summary>The scenario "A differing output is located".</summary>
  [Test]
  public void ADifferingOutput_IsLocated() {
    var result = Compare("hello 42\n", "hello 43\n");
    Assert.That(result, Is.EqualTo(new ComparisonResult(false, 7, 0x32, 0x33)));
    Assert.That(result.ToString(), Is.EqualTo("differs at byte offset 7: expected 0x32, actual 0x33"));
  }

  /// <summary>Test design 6: the two modes.</summary>
  [Test]
  public void LineEnds_CountInExactModeAndNotInNormalizedMode() {
    Assert.That(Compare("a\r\nb", "a\nb"), Is.EqualTo(new ComparisonResult(false, 1, (byte)'\r', (byte)'\n')));
    Assert.That(Compare("a\r\nb", "a\nb", ComparisonMode.NormalizedNewlines).IsEqual, Is.True);
  }

  /// <summary>Test design 12: empty outputs.</summary>
  [Test]
  public void EmptyOutputs_AreEqual_AndAnEmptyOneEndsFirst() {
    Assert.That(Compare("", "").IsEqual, Is.True);
    var shorter = Compare("", "a");
    Assert.That(shorter, Is.EqualTo(new ComparisonResult(false, 0, null, (byte)'a')));
    Assert.That(shorter.ToString(), Is.EqualTo("differs at byte offset 0: expected end of output, actual 0x61"));
    Assert.That(Compare("ab", "a"), Is.EqualTo(new ComparisonResult(false, 1, (byte)'b', null)));
  }

  [TestCase("a\r\nb\rc\n\r\n", "a\nb\nc\n\n")]
  [TestCase("\r", "\n")]
  [TestCase("\r\r\n", "\n\n")]
  [TestCase("x", "x")]
  [TestCase("", "")]
  public void Normalizing_TurnsEveryLineEndIntoALineFeed(string text, string normalized)
    => Assert.That(OutputComparison.NormalizeNewlines(Bytes(text)), Is.EqualTo(Bytes(normalized)));

  [Test]
  public void BytesThatAreNotText_AreComparedAsTheyAre() {
    var result = OutputComparison.Compare(new byte[] { 0xFF, 0x00, 0x80 }, new byte[] { 0xFF, 0x00, 0x81 }, ComparisonMode.NormalizedNewlines);
    Assert.That(result, Is.EqualTo(new ComparisonResult(false, 2, 0x80, 0x81)));
  }

  [Test]
  public void AnUnknownMode_IsRefused()
    => Assert.That(() => OutputComparison.Compare([], [], (ComparisonMode)7), Throws.TypeOf<ArgumentOutOfRangeException>());

}
