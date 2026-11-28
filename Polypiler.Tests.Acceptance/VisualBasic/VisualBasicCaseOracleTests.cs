using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Backends.VisualBasic;
using Polypiler.Tests.Frontends.VisualBasic;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.VisualBasic;

/// <summary>
/// The fixtures of #1564 to #1567 against the oracle, LibreOffice Basic in VBA mode (ADR 2600), in the
/// locale each states: the fixture as written prints what the evidence file records, and its round
/// trip through the IR (profiles vb6 and vba7) prints the documented value on every row - for the text
/// of a number, which the round trip writes with the office's own <c>CStr</c>, on the rows on which
/// LibreOffice agrees with VB6 / VBA. <c>CurrencyDate.bas</c> has no round trip: a currency is an
/// <c>i64</c>, which only the profile vba7-64 writes and LibreOffice does not run.
/// </summary>
[TestFixture, Layer("visualbasic")]
public sealed class VisualBasicCaseOracleTests {

  private static async Task<IReadOnlyList<string>> RunAsync(string module, string source, IReadOnlyList<string> cases, string locale) {
    var execution = await new VisualBasicRunner(new CrossLanguageRunner(Host, Toolchains))
      .ExecuteAsync(new Dictionary<string, string> { [module] = source }, [.. cases.Select(line => new VisualBasicCase(line))], default, locale);
    Assert.That(execution.Diagnostics, Is.Empty, string.Join("\n", execution.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));
    return [.. execution.Outcomes!.Select(outcome => outcome.ToString())];
  }

  [TestCase("Locale.bas", "Locale.cases.txt", "Locale.libreoffice-26.8.3.1.txt", "en-US")]
  [TestCase("Locale.bas", "Locale.de-DE.cases.txt", "Locale.de-DE.libreoffice-26.8.3.1.txt", "de-DE")]
  [TestCase("CurrencyDate.bas", "CurrencyDate.cases.txt", "CurrencyDate.libreoffice-26.8.3.1.txt", "en-US")]
  [TestCase("Bitwise.bas", "Bitwise.cases.txt", "Bitwise.libreoffice-26.8.3.1.txt", "en-US")]
  [TestCase("Inference.bas", "Inference.cases.txt", "Inference.libreoffice-26.8.3.1.txt", "en-US")]
  [RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task Fixture_AsWritten_PrintsWhatTheEvidenceRecords(string fixture, string cases, string evidence, string locale) {
    var printed = await RunAsync(Path.GetFileNameWithoutExtension(fixture), File.ReadAllText(Path.Combine(ClassicCases.Folder, fixture)), ClassicCases.Lines(cases), locale);
    Assert.That(printed, Is.EqualTo(ClassicCases.Evidence(evidence)));
  }

  [TestCase("Locale.bas", "Locale.cases.txt", "Locale.expected.txt", "Locale.libreoffice-26.8.3.1.txt", "en-US")]
  [TestCase("Locale.bas", "Locale.de-DE.cases.txt", "Locale.de-DE.expected.txt", "Locale.de-DE.libreoffice-26.8.3.1.txt", "de-DE")]
  [TestCase("Bitwise.bas", "Bitwise.cases.txt", "Bitwise.expected.txt", null, "en-US")]
  [TestCase("Inference.bas", "Inference.cases.txt", "Inference.expected.txt", null, "en-US")]
  [RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task Fixture_AfterTheRoundTrip_PrintsTheDocumentedValues(string fixture, string cases, string expected, string? evidence, string locale) {
    VisualBasicLocale.TryParse(locale, out var parsed);
    var lines = ClassicCases.Lines(cases);
    var imported = ClassicCases.Import(fixture, cases, parsed);
    var documented = ClassicCases.Lines(expected);
    // The text of a number is the office's CStr, which agrees with VBA's on the rows the evidence shows;
    // every other rule is the helpers' and agrees on every row.
    var agreeing = evidence is null ? [.. Enumerable.Range(0, documented.Count)] : ClassicCases.Agreeing(documented, ClassicCases.Evidence(evidence));
    var calls = Enumerable.Range(0, lines.Count).Select(index => ClassicCases.Name(index) + "()").ToList();
    foreach (var profile in new[] { VisualBasicProfile.Vb6, VisualBasicProfile.Vba7 }) {
      var text = VisualBasicFixtures.Translate(imported, profile).Source!;
      var printed = await RunAsync(Path.GetFileNameWithoutExtension(fixture), text, calls, locale);
      Assert.That(agreeing.Select(index => printed[index]), Is.EqualTo(agreeing.Select(index => documented[index])), $"round trip, {profile}\n{text}");
    }
  }

}
