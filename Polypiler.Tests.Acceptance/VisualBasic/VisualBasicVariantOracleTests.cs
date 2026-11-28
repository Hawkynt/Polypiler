using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Backends.VisualBasic;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.VisualBasic;

/// <summary>
/// <c>Variant</c> against the oracle, LibreOffice Basic in VBA mode (ADR 2600): the fixture as written
/// prints what the evidence file records - LibreOffice's own Variant rules, which differ from VBA's on
/// the rows the ADR lists - and the round trip through the IR, which writes VBA's rules out as helpers,
/// prints what VB6 / VBA documents on every row.
/// </summary>
[TestFixture, Layer("visualbasic")]
public sealed class VisualBasicVariantOracleTests {

  private static readonly string _classic = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "classic");

  private static IReadOnlyList<VisualBasicCase> Cases()
    => [.. File.ReadAllLines(Path.Combine(_classic, "Variants.cases.txt")).Where(line => line.Length > 0).Select(line => new VisualBasicCase(line))];

  private static async Task<IReadOnlyList<string>> RunAsync(string source) {
    var execution = await new VisualBasicRunner(new CrossLanguageRunner(Host, Toolchains)).ExecuteAsync(new Dictionary<string, string> { ["Variants"] = source }, Cases());
    Assert.That(execution.Diagnostics, Is.Empty, string.Join("\n", execution.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));
    return [.. execution.Outcomes!.Select(outcome => outcome.ToString())];
  }

  [Test, RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task Fixture_AsWritten_PrintsWhatTheEvidenceRecords() {
    var recorded = File.ReadAllLines(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "adr", "evidence", "2600", "Variants.libreoffice-26.8.3.1.txt")).Where(line => line.Length > 0);
    Assert.That(await RunAsync(File.ReadAllText(Path.Combine(_classic, "Variants.bas"))), Is.EqualTo(recorded));
  }

  [Test, RequiresToolchain(VisualBasicRunner.Toolchain)]
  public async Task Fixture_AfterTheRoundTrip_PrintsTheDocumentedValues() {
    var expected = File.ReadAllLines(Path.Combine(_classic, "Variants.expected.txt")).Where(line => line.Length > 0).ToList();
    var imported = VisualBasicFrontend.Import("Variants.bas", File.ReadAllText(Path.Combine(_classic, "Variants.bas"))).Module!;
    foreach (var profile in new[] { VisualBasicProfile.Vb6, VisualBasicProfile.Vba7 }) {
      var text = VisualBasicFixtures.Translate(imported, profile).Source!;
      Assert.That(await RunAsync(text), Is.EqualTo(expected), $"round trip, {profile}\n{text}");
    }
  }

}
