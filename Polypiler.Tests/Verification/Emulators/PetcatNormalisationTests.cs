using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Emulators;

namespace Polypiler.Tests.Verification.Emulators;

/// <summary>
/// The text form petcat's listings are normalised to and the screen text is decoded to, and the way
/// back to petcat's own notation. What petcat prints is recorded from petcat 3.10 (see
/// <c>Resources/petcat-escapes.tsv</c>); petcat itself runs in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class PetcatNormalisationTests {

  [Test]
  public void AListing_IsUpperCased_AndLosesItsIndentation() {
    Assert.That(Petcat.Normalise("\n   10 print \"hi\"\n"), Is.EqualTo("10 PRINT \"HI\""));
  }

  /// <summary>Test design 6: named escapes become their bytes.</summary>
  [Test]
  public void NamedEscapes_BecomeHexadecimalEscapes() {
    Assert.That(Petcat.Normalise("   10 print \"{clr}{down}hi\""), Is.EqualTo("10 PRINT \"{$93}{$11}HI\""));
  }

  [TestCase("{CTRL-A}", "{$01}")]
  [TestCase("{CBM-K}", "{$A1}")]
  [TestCase("{SHIFT-*}", "{$C0}")]
  [TestCase("{CBM-POUND}", "{$A8}")]
  [TestCase("{$0a}", "{$0A}")]
  [TestCase("{$de}", "{$DE}")]
  [TestCase("A", "{$C1}")]
  [TestCase("Z", "{$DA}")]
  [TestCase("a", "A")]
  [TestCase("z", "Z")]
  [TestCase("~", "{$FF}")]
  [TestCase("\\^_[]", "\\^_[]")]
  [TestCase("{unknown}", "{unknown}")]
  [TestCase("{$zz}", "{$zz}")]
  [TestCase("{open", "{OPEN")]
  public void EachElement_IsNormalised(string petcat, string normal) {
    Assert.That(Petcat.Normalise("10 ?\"" + petcat + "\""), Is.EqualTo("10 ?\"" + normal + "\""));
  }

  [Test]
  public void Lines_KeepTheirOrder_WithoutEmptyLinesOrCarriageReturns() {
    Assert.That(Petcat.Normalise("\r\n   10 a=1\r\n\r\n   20 b=2  \r\n"), Is.EqualTo("10 A=1\n20 B=2"));
  }

  [Test]
  public void TheEscapeTable_HoldsPetcatsNames() {
    Assert.That(Petcat.Escapes, Has.Count.EqualTo(92));
    Assert.That((Petcat.Escapes["clr"], Petcat.Escapes["down"], Petcat.Escapes["stop"], Petcat.Escapes["f8"]), Is.EqualTo(((byte)0x93, (byte)0x11, (byte)0x03, (byte)0x8C)));
    Assert.That(Petcat.Escapes.Values.Distinct().Count(), Is.EqualTo(92), "one name per byte");
  }

  [Test]
  public void TheDialects_AreTheThreeCommodoreBasics() {
    Assert.That(Petcat.Dialects.Select(pair => $"{pair.Key}={pair.Value}").Order(StringComparer.Ordinal), Is.EqualTo(new[] { "c128-basic-7=70", "c64-basic-v2=2", "vic20-basic-v2=2" }));
  }

  [TestCase("10 PRINT \"HI\"", "10 print \"hi\"")]
  [TestCase("10 PRINT \"{$93}{$C1}\"", "10 print \"{$93}{$c1}\"")]
  [TestCase("10 a", "10 A")]
  [TestCase("10 {clr", "10 {CLR")]
  [TestCase("10 {clr}", "10 {clr}")]
  public void AListing_IsWrittenAsPetcatReadsIt(string listing, string petcat) {
    Assert.That(Petcat.ToPetcatText(listing), Is.EqualTo(petcat));
  }

  [Test]
  public void NoText_IsRefused() {
    Assert.That(() => Petcat.Normalise(null!), Throws.ArgumentNullException);
    Assert.That(() => Petcat.ToPetcatText(null!), Throws.ArgumentNullException);
    Assert.That(() => PetsciiText.Trim(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void PetsciiBytes_AreDecoded() {
    Assert.That(PetsciiText.Decode([0x48, 0x49, 0x0D, 0x20, 0x5F, 0x1F, 0x60, 0xC1, 0x0A, 0x00]), Is.EqualTo("HI\n _{$1F}{$60}{$C1}{$0A}{$00}"));
  }

  [TestCase("HI\n", "HI")]
  [TestCase("HI  \nX \n\n  \n", "HI\nX")]
  [TestCase("\nHI", "\nHI")]
  [TestCase("", "")]
  [TestCase("\n\n", "")]
  public void ScreenText_LosesTrailingBlanksAndEmptyLines(string text, string trimmed) {
    Assert.That(PetsciiText.Trim(text), Is.EqualTo(trimmed));
  }

}
