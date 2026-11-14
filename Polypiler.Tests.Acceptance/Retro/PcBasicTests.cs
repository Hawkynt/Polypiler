using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Emulators;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tooling.Toolchains;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tests.Acceptance.Retro;

/// <summary>
/// The rows of the test design of FMT-GWBASIC-ORACLE that the feature file does not cover, against
/// PC-BASIC itself. Without PC-BASIC at its pin they are inconclusive, in CI a failure; the
/// availability table is checked everywhere.
/// </summary>
[TestFixture]
[Layer("core")]
public sealed class PcBasicTests {

  private const string ExternalInterop = "ExternalInterop";

  private static readonly TimeSpan _limit = TimeSpan.FromSeconds(30);

  private static PcBasicRun RunListing(string listing, string? input = null) {
    var pcBasic = PcBasicTesting.Adapter();
    return pcBasic.Run(pcBasic.ToTokenized(listing), input, _limit);
  }

  /// <summary>
  /// Test design 4: the single-precision results GW-BASIC documents. PC-BASIC 2.0.8 prints
  /// <c> .3333334 </c> and <c> 1.414213 </c> instead, so the issue went back to draft with that
  /// recording until the reference values are settled; the test stays as the issue states it.
  /// </summary>
  [TestCase("10 PRINT 1/3", " .3333333 ")]
  [TestCase("10 PRINT 2^0.5", " 1.414214 ")]
  [Category(ExternalInterop), RequiresToolchain(PcBasic.Toolchain), Explicit("PC-BASIC 2.0.8 disagrees with the known answers of the issue; see #1012")]
  public void KnownAnswers_AreReproduced(string listing, string output)
    => Assert.That(RunListing(listing).Output, Is.EqualTo(output));

  /// <summary>Test design 5: what the program reads comes from standard input, its line end sent as a keyboard sends it.</summary>
  [Test, Category(ExternalInterop), RequiresToolchain(PcBasic.Toolchain)]
  public void Input_IsReadFromStandardInput()
    => Assert.That(RunListing("10 INPUT A:PRINT A*2", "21\n").Output, Does.Contain(" 42 "));

  /// <summary>Test design 6: the empty program lists as nothing and prints nothing.</summary>
  [Test, Category(ExternalInterop), RequiresToolchain(PcBasic.Toolchain)]
  public void TheEmptyProgram_ListsAndPrintsNothing() {
    var pcBasic = PcBasicTesting.Adapter();
    byte[] empty = [0xFF, 0x00, 0x00, 0x1A];
    Assert.That(pcBasic.ToAscii(empty), Is.Empty);
    Assert.That(pcBasic.Run(empty, null, _limit).Output, Is.Empty);
    Assert.That(pcBasic.ToTokenized(""), Is.EqualTo(empty));
  }

  /// <summary>Test design 7: an output of nothing and one of only a line end are both empty after normalisation.</summary>
  [TestCase("10 END")]
  [TestCase("10 PRINT")]
  [Category(ExternalInterop), RequiresToolchain(PcBasic.Toolchain)]
  public void NoOutputAndALoneLineEnd_AreEmpty(string listing)
    => Assert.That(RunListing(listing).Output, Is.Empty);

  /// <summary>Test design 8: the endless program is cut off at its limit and yields no output.</summary>
  [Test, Category(ExternalInterop), RequiresToolchain(PcBasic.Toolchain)]
  public void AnEndlessProgram_TimesOut() {
    var run = PcBasicTesting.Adapter().Run(File.ReadAllBytes(PcBasicTesting.Fixture("forever.bas")), null, TimeSpan.FromSeconds(5));
    Assert.That((run.Outcome, run.Output), Is.EqualTo((RunOutcome.TimedOut, (string?)null)));
    Assert.That(run.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY3960", "PLY0031" }));
  }

  /// <summary>Test design 9: a corrupt token file, which PC-BASIC lists as nothing, is an oracle failure and never an empty program.</summary>
  [Test, Category(ExternalInterop), RequiresToolchain(PcBasic.Toolchain)]
  public void ACorruptTokenFile_IsPLY3960() {
    var exception = Assert.Throws<PcBasicException>(() => PcBasicTesting.Adapter().ToAscii(new byte[] { 0xFF, 0x09 }))!;
    Assert.That(exception.Diagnostic.Code, Is.EqualTo("PLY3960"));
    Assert.That(exception.Message, Is.EqualTo("PC-BASIC conversion to ASCII failed: the token file lists as nothing, but it is not the empty program"));
  }

  /// <summary>The oracle runs the fixture program and hands back what it printed.</summary>
  [Test, Category(ExternalInterop), RequiresToolchain(PcBasic.Toolchain)]
  public async Task TheOracle_ComputesTheReference() {
    var oracle = new PcBasicOracle(PcBasicTesting.Adapter(), PcBasicTesting.Fixture("print1.bas"), _limit);
    var registry = new OracleRegistry();
    Assert.That(registry.Register("gwbasic.print1", oracle), Is.True);
    var result = await oracle.EvaluateAsync(new OracleRequest("gwbasic.print1", [new TestVector("none", [], null)]));
    Assert.That(result.Outputs.Select(output => PcBasic.CodePage437.GetString(output)), Is.EqualTo(new[] { " 1 " }));
  }

  /// <summary>The listing fixture is what PC-BASIC lists the token fixture as.</summary>
  [Test, Category(ExternalInterop), RequiresToolchain(PcBasic.Toolchain)]
  public void TheListingFixture_MatchesTheTokenFixture()
    => Assert.That(PcBasicTesting.Adapter().ToAscii(File.ReadAllBytes(PcBasicTesting.Fixture("print1.bas"))), Is.EqualTo(PcBasic.Normalise(File.ReadAllText(PcBasicTesting.Fixture("print1.txt")))));

  /// <summary>Test design 10: without PC-BASIC the tests are inconclusive locally and fail with PLY0021 in CI.</summary>
  [Test]
  public void AMissingToolchain_IsInconclusiveLocallyAndPLY0021InCi() {
    var attribute = new RequiresToolchainAttribute(PcBasic.Toolchain) { Lock = "tests/fixtures/retro/gwbasic/oracle/missing.lock" };
    Assert.That(Assert.Throws<InconclusiveException>(() => attribute.Check(null))!.Message, Does.Contain("toolchain 'pcbasic'").And.Contain("PLY0021"));
    Assert.That(Assert.Throws<AssertionException>(() => attribute.Check("1"))!.Message, Does.Contain("PLY0021"));
  }

  /// <summary>The availability table of the BASIC layer names PC-BASIC as running in CI and the genuine interpreter as external.</summary>
  [Test]
  public void TheAvailabilityTable_NamesBothGwBasicOracles() {
    var yaml = new YamlStream();
    yaml.Load(new StringReader(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "ci", "layers", "basic.oracles.yaml"))));
    var rows = ((YamlSequenceNode)((YamlMappingNode)yaml.Documents[0].RootNode)["oracles"]).Cast<YamlMappingNode>()
      .ToDictionary(row => row["oracle"].ToString(), row => (row.Children.TryGetValue("toolchain", out var key) ? key.ToString() : null, row["availability"].ToString()));
    Assert.That(rows["pcbasic"], Is.EqualTo(((string?)"pcbasic", "ci")));
    Assert.That(rows["gwbasic-genuine"], Is.EqualTo(((string?)null, "external")));
    Assert.That(ToolchainLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath))[PcBasic.Toolchain].OracleOnly, Is.True);
  }

  [TestCase("vice")]
  [TestCase("vice-x128")]
  [TestCase("vice-xvic")]
  [TestCase("petcat")]
  public void TheAvailabilityTable_NamesTheCommodoreOracles(string oracle) {
    var yaml = new YamlStream();
    yaml.Load(new StringReader(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "ci", "layers", "basic.oracles.yaml"))));
    var row = ((YamlSequenceNode)((YamlMappingNode)yaml.Documents[0].RootNode)["oracles"]).Cast<YamlMappingNode>()
      .Single(candidate => candidate["oracle"].ToString() == oracle);
    Assert.That(row["toolchain"].ToString(), Is.EqualTo(oracle));
    Assert.That(row["availability"].ToString(), Is.EqualTo("ci"));
    Assert.That(ToolchainLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath))[oracle].OracleOnly, Is.True);
  }

}
