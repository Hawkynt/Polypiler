using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The fixtures of #1565, #1566 and #1567 in the interpreter: each prints its documented values -
/// what VB6 / VBA computes, which ADR 2600 contrasts with LibreOffice's evidence - in the locale of
/// the import.
/// </summary>
[TestFixture]
public sealed class VisualBasicFixtureCaseTests {

  [TestCase("Locale.bas", "Locale.cases.txt", "Locale.expected.txt", "en-US")]
  [TestCase("Locale.bas", "Locale.de-DE.cases.txt", "Locale.de-DE.expected.txt", "de-DE")]
  [TestCase("CurrencyDate.bas", "CurrencyDate.cases.txt", "CurrencyDate.expected.txt", "en-US")]
  [TestCase("Bitwise.bas", "Bitwise.cases.txt", "Bitwise.expected.txt", "en-US")]
  public void Fixture_PrintsTheDocumentedValues(string fixture, string cases, string expected, string locale) {
    VisualBasicLocale.TryParse(locale, out var parsed);
    var module = ClassicCases.Import(fixture, cases, parsed);
    var documented = ClassicCases.Lines(expected);
    Assert.That(ClassicCases.Interpret(module, documented.Count), Is.EqualTo(documented));
  }

  [TestCase("Locale.expected.txt", "Locale.libreoffice-26.8.3.1.txt", 46)]
  [TestCase("Locale.de-DE.expected.txt", "Locale.de-DE.libreoffice-26.8.3.1.txt", 12)]
  [TestCase("CurrencyDate.expected.txt", "CurrencyDate.libreoffice-26.8.3.1.txt", 25)]
  [TestCase("Bitwise.expected.txt", "Bitwise.libreoffice-26.8.3.1.txt", 4)]
  public void Evidence_AgreesOnTheRowsTheAdrCounts(string expected, string evidence, int agreeing)
    => Assert.That(ClassicCases.Agreeing(ClassicCases.Lines(expected), ClassicCases.Evidence(evidence)), Has.Count.EqualTo(agreeing));

}
