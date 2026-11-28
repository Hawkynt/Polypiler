using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The fixtures of #1564, #1565, #1566 and #1567 in the interpreter: each prints its documented values -
/// what VB6 / VBA computes, which ADR 2600 contrasts with LibreOffice's evidence - in the locale of
/// the import.
/// </summary>
[TestFixture]
public sealed class VisualBasicFixtureCaseTests {

  [TestCase("Locale.bas", "Locale.cases.txt", "Locale.expected.txt", "en-US")]
  [TestCase("Locale.bas", "Locale.de-DE.cases.txt", "Locale.de-DE.expected.txt", "de-DE")]
  [TestCase("CurrencyDate.bas", "CurrencyDate.cases.txt", "CurrencyDate.expected.txt", "en-US")]
  [TestCase("Bitwise.bas", "Bitwise.cases.txt", "Bitwise.expected.txt", "en-US")]
  [TestCase("Inference.bas", "Inference.cases.txt", "Inference.expected.txt", "en-US")]
  public void Fixture_PrintsTheDocumentedValues(string fixture, string cases, string expected, string locale) {
    VisualBasicLocale.TryParse(locale, out var parsed);
    var module = ClassicCases.Import(fixture, cases, parsed);
    var documented = ClassicCases.Lines(expected);
    Assert.That(ClassicCases.Interpret(module, documented.Count), Is.EqualTo(documented));
  }

  [TestCase("Locale.bas", "Locale.cases.txt", "PlyFmtR8(", "PlyFmtR4(", "PlyParseR8(")]
  [TestCase("Bitwise.bas", "Bitwise.cases.txt", "PlyVarBits_(", "PlyVarBitNot_(", "As Variant")]
  [TestCase("Inference.bas", "Inference.cases.txt", "Add_Long_Long(", "Twice_String(", "Public Function Scaled(")]
  public void Fixture_TranslatesToVisualBasic(string fixture, string cases, string first, string second, string third) {
    var module = ClassicCases.Import(fixture, cases);
    foreach (var profile in Polypiler.Core.Backends.VisualBasic.VisualBasicProfile.All) {
      var translated = Polypiler.Tests.Backends.VisualBasic.VisualBasicFixtures.Translate(module, profile);
      Assert.That(translated.Problems, Is.Empty, profile.Name);
      Assert.That(translated.Source, Does.Contain(first).And.Contain(second).And.Contain(third), profile.Name);
    }
  }

  [Test]
  public void CurrencyFixture_TranslatesToVisualBasicWithLongLongAlone() {
    var module = ClassicCases.Import("CurrencyDate.bas", "CurrencyDate.cases.txt");
    Assert.That(Polypiler.Tests.Backends.VisualBasic.VisualBasicFixtures.Translate(module, Polypiler.Core.Backends.VisualBasic.VisualBasicProfile.Vba7x64).Problems, Is.Empty);
    Assert.That(Polypiler.Tests.Backends.VisualBasic.VisualBasicFixtures.Translate(module, Polypiler.Core.Backends.VisualBasic.VisualBasicProfile.Vba7).Problems, Has.Some.Contains("LongLong"));
  }

  [TestCase("Locale.expected.txt", "Locale.libreoffice-26.8.3.1.txt", 46)]
  [TestCase("Locale.de-DE.expected.txt", "Locale.de-DE.libreoffice-26.8.3.1.txt", 12)]
  [TestCase("CurrencyDate.expected.txt", "CurrencyDate.libreoffice-26.8.3.1.txt", 25)]
  [TestCase("Bitwise.expected.txt", "Bitwise.libreoffice-26.8.3.1.txt", 4)]
  [TestCase("Inference.expected.txt", "Inference.libreoffice-26.8.3.1.txt", 7)]
  public void Evidence_AgreesOnTheRowsTheAdrCounts(string expected, string evidence, int agreeing)
    => Assert.That(ClassicCases.Agreeing(ClassicCases.Lines(expected), ClassicCases.Evidence(evidence)), Has.Count.EqualTo(agreeing));

}
