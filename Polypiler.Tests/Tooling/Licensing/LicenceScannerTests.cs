using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <see cref="LicenceScanner"/> and <see cref="LicenceReport"/> over the synthetic trees of
/// <c>tests/fixtures/infra/licenses</c> and scratch trees: both modes, every allowed and
/// reference-only identifier, restricted assets, vendored libraries, pending decisions, several
/// violations at once, the corpus of a lock, and the report's bytes.
/// </summary>
[TestFixture]
public sealed class LicenceScannerTests {

  /// <summary>The folder of the synthetic trees.</summary>
  public static string Fixtures { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "licenses");

  private static readonly LicencePolicy _shipped = LicencePolicy.Load(LicencePolicyTests.Shipped);

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-licence-scanner-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private static LicenceReport Tree(string tree, LicenceMode mode, LicencePolicy? policy = null)
    => new LicenceScanner(policy ?? _shipped, LicenceDetectorTests.ShippedTexts).ScanTree(Path.Combine(Fixtures, tree), mode);

  private LicenceReport ScanRoot(LicenceMode mode, LicencePolicy? policy = null)
    => new LicenceScanner(policy ?? _shipped, LicenceDetectorTests.ShippedTexts).ScanTree(this._root, mode);

  private static string[] Messages(LicenceReport report) => [.. report.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")];

  private static (string? Licence, string? Source, string Status) Row(LicenceReport report, string path) {
    var entry = report.Entry(path) ?? throw new AssertionException($"no entry for '{path}'");
    return (entry.Licence, entry.Source is { } source ? LicenceReport.Name(source) : null, LicenceReport.Name(entry.Status));
  }

  [TestCase(LicenceMode.Corpus)]
  [TestCase(LicenceMode.Distribution)]
  public void NestedLicenceFile_OverridesTheRoot_AndGplIsAllowed(LicenceMode mode) {
    var report = Tree("gpl-nested", mode);
    Assert.That(report.ExitCode, Is.Zero, string.Join("\n", Messages(report)));
    Assert.That(Row(report, "sub/x.c"), Is.EqualTo(("GPL-3.0-only", "licence-file", "allowed")));
    Assert.That(Row(report, "sub/deep/y.c"), Is.EqualTo(("GPL-3.0-only", "licence-file", "allowed")));
    Assert.That(Row(report, "other/z.c"), Is.EqualTo(("LGPL-3.0-or-later", "licence-file", "allowed")));
    Assert.That(report.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "LICENSE", "licences.yaml", "other/z.c", "sub/LICENSE", "sub/deep/y.c", "sub/x.c" }));
  }

  [Test]
  public void Header_TakesPrecedenceOverTheLicenceFile() {
    var report = Tree("mit-header", LicenceMode.Distribution);
    Assert.That(report.ExitCode, Is.Zero);
    Assert.That(Row(report, "a.js"), Is.EqualTo(("MIT", "header", "allowed")));
    Assert.That(Row(report, "LICENSE"), Is.EqualTo(("LGPL-3.0-or-later", "licence-file", "allowed")));
  }

  [TestCase(LicenceMode.Corpus, 0, "reference-only")]
  [TestCase(LicenceMode.Distribution, 8, "denied")]
  public void ReferenceOnly_IsAnOracleInTheCorpusAndABlockerInADistribution(LicenceMode mode, int exit, string status) {
    var report = Tree("agpl-header", mode);
    Assert.That(report.ExitCode, Is.EqualTo(exit));
    Assert.That(Row(report, "ref.c").Status, Is.EqualTo(status));
    if (exit != 0)
      Assert.That(Messages(report), Is.EqualTo(new[] { "PLY0024: 'ref.c': licence 'AGPL-3.0-only' is denied in distribution mode" }));
  }

  [TestCase("gpl-header", "g.c", "GPL-3.0-or-later")]
  [TestCase("dual", "d.js", "MIT OR AGPL-3.0-only")]
  public void OneFileTrees_AreAllowedInBothModes(string tree, string file, string licence) {
    foreach (var mode in (LicenceMode[])[LicenceMode.Corpus, LicenceMode.Distribution]) {
      var report = Tree(tree, mode);
      Assert.That((report.ExitCode, Row(report, file)), Is.EqualTo((0, (licence, "header", "allowed"))), mode.ToString());
    }
  }

  [Test]
  public void NoLicenceAnywhere_IsPly0029() {
    var report = Tree("no-licence", LicenceMode.Corpus);
    Assert.That(report.ExitCode, Is.EqualTo(8));
    Assert.That(Messages(report), Is.EqualTo(new[] { "PLY0029: 'orphan.py': no licence could be determined" }));
    Assert.That(Row(report, "orphan.py"), Is.EqualTo(((string?)null, (string?)null, "denied")));
  }

  [Test]
  public void Weights_AreRestrictedInADistribution_ButNotInTheCorpus_AndAnOverrideReleasesThem() {
    var distribution = Tree("weights", LicenceMode.Distribution);
    Assert.That(distribution.ExitCode, Is.EqualTo(8));
    Assert.That(Messages(distribution), Is.EqualTo(new[] { "PLY0024: 'model.bin' is a restricted asset and may not be redistributed" }));
    Assert.That(Row(distribution, "model.bin"), Is.EqualTo(("MIT", "licence-file", "denied")));
    Assert.That(Tree("weights", LicenceMode.Corpus).ExitCode, Is.Zero);

    var released = LicencePolicy.Parse(
      LicencePolicyTests.Policy(overrides: "[{ glob: 'model.bin', licence: MIT, reason: 'weights trained here', reviewer: owner }]"), "p.yaml", LicencePolicyTests.Decisions);
    var report = Tree("weights", LicenceMode.Distribution, released);
    Assert.That(report.ExitCode, Is.Zero, string.Join("\n", Messages(report)));
    Assert.That(Row(report, "model.bin"), Is.EqualTo(("MIT", "override", "allowed")));
  }

  [Test]
  public void DeclaredLicenceOutsideTheDetectedFamily_IsPly0024NamingBoth() {
    var report = Tree("declared-mismatch", LicenceMode.Corpus);
    Assert.That(report.ExitCode, Is.EqualTo(8));
    Assert.That(Messages(report), Does.Contain("PLY0024: 'a.c': declared licence 'MIT' disagrees with detected 'LGPL-3.0'"));
    Assert.That(Row(report, "a.c"), Is.EqualTo(("LGPL-3.0", "licence-file", "denied")));
  }

  [Test]
  public void EachAllowedIdentifier_PassesBothModes_AndEachReferenceOnlyOnlyTheCorpus() {
    foreach (var licence in _shipped.Allowed.Concat(_shipped.ReferenceOnly))
      Scratch.Write(this._root, $"{licence}/f.c", $"// SPDX-License-Identifier: {licence}\n");

    var corpus = this.ScanRoot(LicenceMode.Corpus);
    var distribution = this.ScanRoot(LicenceMode.Distribution);
    Assert.That(corpus.ExitCode, Is.Zero);
    Assert.That(distribution.ExitCode, Is.EqualTo(8));
    foreach (var licence in _shipped.Allowed) {
      Assert.That(Row(corpus, $"{licence}/f.c").Status, Is.EqualTo("allowed"), licence);
      Assert.That(Row(distribution, $"{licence}/f.c").Status, Is.EqualTo("allowed"), licence);
    }

    foreach (var licence in _shipped.ReferenceOnly)
      Assert.That(Row(corpus, $"{licence}/f.c").Status, Is.EqualTo("reference-only"), licence);
    Assert.That(Messages(distribution), Is.EquivalentTo(_shipped.ReferenceOnly.Select(licence => $"PLY0024: '{licence}/f.c': licence '{licence}' is denied in distribution mode")));
  }

  [TestCase("MIT AND GPL-3.0-only", "allowed")]
  [TestCase("MIT OR AGPL-3.0-only", "allowed")]
  [TestCase("MIT AND AGPL-3.0-only", "denied")]
  [TestCase("(MIT OR Apache-2.0) AND ISC", "allowed")]
  [TestCase("MIT OR AGPL-3.0-only AND ISC", "allowed")]
  [TestCase("GPL-2.0-only", "denied")]
  [TestCase("Proprietary-Foo", "denied")]
  [TestCase("GPL-2.0-only WITH Classpath-exception-2.0", "denied")]
  public void Expressions_AreEvaluatedInADistribution(string expression, string status) {
    Scratch.Write(this._root, "f.c", $"/* SPDX-License-Identifier: {expression} */\n");
    Assert.That(Row(this.ScanRoot(LicenceMode.Distribution), "f.c").Status, Is.EqualTo(status));
  }

  [TestCase("MIT OR")]
  [TestCase("(MIT")]
  [TestCase("AND MIT")]
  public void UnparsableHeader_IsPly0029(string expression) {
    Scratch.Write(this._root, "f.c", $"// SPDX-License-Identifier: {expression}\n");
    var report = this.ScanRoot(LicenceMode.Corpus);
    Assert.That(report.ExitCode, Is.EqualTo(8));
    Assert.That(Messages(report).Single(), Does.StartWith($"PLY0029: 'f.c': no licence could be determined: '{expression}' is not an SPDX expression"));
  }

  [Test]
  public void UnknownLicenceText_IsPly0029() {
    Scratch.Write(this._root, "LICENSE", "You may do some things.\n");
    Scratch.Write(this._root, "f.c", "");
    Assert.That(Messages(this.ScanRoot(LicenceMode.Corpus)), Does.Contain("PLY0029: 'f.c': no licence could be determined: the text of 'LICENSE' is not a known licence text"));
  }

  [Test]
  public void EmptyRoot_GivesAnEmptyReport() {
    var report = this.ScanRoot(LicenceMode.Distribution);
    Assert.That((report.Entries.Count, report.ExitCode, report.ToJson()), Is.EqualTo((0, 0, "[]\n")));
  }

  [Test]
  public void EmptyFile_InheritsTheLicenceFile() {
    Scratch.Write(this._root, "LICENSE", LicenceDetectorTests.MitText);
    Scratch.Write(this._root, "empty.txt", "");
    Assert.That(Row(this.ScanRoot(LicenceMode.Distribution), "empty.txt"), Is.EqualTo(("MIT", "licence-file", "allowed")));
  }

  [Test]
  public void ReviewedOverride_IsTheSource() {
    var policy = LicencePolicy.Parse(
      LicencePolicyTests.Policy(allowed: "[MIT, BSD-3-Clause]", overrides: "[{ glob: 'legacy/*.asm', licence: BSD-3-Clause, reason: 'header lost', reviewer: owner }]"),
      "p.yaml", LicencePolicyTests.Decisions + "\n| `BSD-3-Clause` | allowed | 2026-11-13 | test | T |\n");
    Scratch.Write(this._root, "legacy/boot.asm", "; boot\n");
    var report = this.ScanRoot(LicenceMode.Distribution, policy);
    Assert.That((report.ExitCode, Row(report, "legacy/boot.asm")), Is.EqualTo((0, ("BSD-3-Clause", "override", "allowed"))));
  }

  [Test]
  public void VendoredLibraryWithoutLicenceFile_IsPly0029_EvenUnderTheRootLicence() {
    Scratch.Write(this._root, "LICENSE", LicenceDetectorTests.MitText);
    Scratch.Write(this._root, "Vendored/Foo/foo.c", "");
    Scratch.Write(this._root, "Vendored/Bar/LICENSE.txt", LicenceDetectorTests.MitText);
    Scratch.Write(this._root, "Vendored/Bar/bar.c", "");
    Scratch.Write(this._root, "Other/vendored/Baz/baz.c", "");
    var report = this.ScanRoot(LicenceMode.Distribution);
    Assert.That(Messages(report), Is.EqualTo(new[] { "PLY0029: 'Vendored/Foo/': no licence could be determined: a vendored library needs a licence file of its own" }));
    Assert.That(Row(report, "Vendored/Foo/foo.c"), Is.EqualTo(("MIT", "licence-file", "allowed")));

    var vendored = new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanTree(Path.Combine(this._root, "Vendored"), LicenceMode.Distribution);
    Assert.That(Messages(vendored), Is.EqualTo(new[] {
      "PLY0029: 'Foo/foo.c': no licence could be determined",
      "PLY0029: 'Foo/': no licence could be determined: a vendored library needs a licence file of its own" }));
  }

  [Test]
  public void PendingIdentifier_IsDenied_NamingTheRequest() {
    var policy = LicencePolicy.Parse(
      LicencePolicyTests.Policy(allowed: "[MIT, LGPL-3.0-or-later]", pending: "[{ licence: EUPL-1.2, requestedBy: CAT-IMP-EXAMPLE, question: 'may EUPL-1.2 code be a source?' }]"),
      "p.yaml", LicencePolicyTests.Decisions + "\n| `LGPL-3.0-or-later` | allowed | 2026-11-13 | test | T |\n");
    Scratch.Write(this._root, "a.c", "// SPDX-License-Identifier: EUPL-1.2\n");
    Scratch.Write(this._root, "b.c", "// SPDX-License-Identifier: LGPL-3.0-or-later AND EUPL-1.2\n");
    var report = this.ScanRoot(LicenceMode.Corpus, policy);
    Assert.That(report.ExitCode, Is.EqualTo(8));
    Assert.That(Messages(report), Is.EqualTo(new[] {
      "PLY0024: 'a.c': licence 'EUPL-1.2' is denied in corpus mode (EUPL-1.2 awaits an owner decision, requested by CAT-IMP-EXAMPLE)",
      "PLY0024: 'b.c': licence 'LGPL-3.0-or-later AND EUPL-1.2' is denied in corpus mode (EUPL-1.2 awaits an owner decision, requested by CAT-IMP-EXAMPLE)" }));
  }

  [Test]
  public void SeveralViolations_AreAllReported_AndGitFoldersAreNotScanned() {
    Scratch.Write(this._root, "a.c", "// SPDX-License-Identifier: Proprietary-A\n");
    Scratch.Write(this._root, "b.c", "// SPDX-License-Identifier: Proprietary-B\n");
    Scratch.Write(this._root, ".git/config", "");
    Scratch.Write(this._root, ".hidden/c.c", "// SPDX-License-Identifier: MIT\n");
    var report = this.ScanRoot(LicenceMode.Distribution);
    Assert.That(Messages(report), Is.EqualTo(new[] {
      "PLY0024: 'a.c': licence 'Proprietary-A' is denied in distribution mode",
      "PLY0024: 'b.c': licence 'Proprietary-B' is denied in distribution mode" }));
    Assert.That(report.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { ".hidden/c.c", "a.c", "b.c" }));
  }

  [Test]
  public void InvalidDeclarations_AreRefused()
    => Assert.That(() => {
      Scratch.Write(this._root, LicenceDeclarations.FileName, "schema: 1\n");
      return this.ScanRoot(LicenceMode.Corpus);
    }, Throws.TypeOf<CorpusException>().With.Message.Contains("licence declarations: the file must be a mapping of exactly 'schema: 1' and the list 'declared'"));

  [Test]
  public void Report_IsCanonicalJson_SortedByPath_AndTheSameTwice() {
    var first = Tree("gpl-nested", LicenceMode.Distribution).ToJson();
    Assert.That(Tree("gpl-nested", LicenceMode.Distribution).ToJson(), Is.EqualTo(first));
    Assert.That(first, Does.StartWith("[\n  {\n    \"path\": \"LICENSE\",\n    \"licence\": \"LGPL-3.0-or-later\",\n    \"source\": \"licence-file\",\n    \"status\": \"allowed\"\n  },\n"));
    Assert.That(Tree("no-licence", LicenceMode.Corpus).ToJson(), Is.EqualTo("[\n  {\n    \"path\": \"orphan.py\",\n    \"licence\": null,\n    \"source\": null,\n    \"status\": \"denied\"\n  }\n]\n"));
    Assert.That(LicenceReport.Name(LicenceSource.Override), Is.EqualTo("override"));
    Assert.That(LicenceReport.Name(LicenceMode.Corpus), Is.EqualTo("corpus"));
  }

  [Test]
  public void ExitCode_IsTheEnvironmentErrorWhenAnyDiagnosticIsOne() {
    var blocker = LicenceDiagnostics.Unknown("x");
    var environment = CorpusDiagnostics.Mismatch("m");
    Assert.That(LicenceDiagnostics.ExitCodeOf([]), Is.Zero);
    Assert.That(LicenceDiagnostics.ExitCodeOf([blocker]), Is.EqualTo((int)ExitCode.LicenseBlocker));
    Assert.That(LicenceDiagnostics.ExitCodeOf([blocker, environment]), Is.EqualTo((int)ExitCode.EnvironmentError));
  }

  [Test]
  public void Corpus_UsesTheFixturesDeclaredLicence_ThenTheRepositorys_AndReportsWhatIsNotImported() {
    var lgpl = System.Text.Encoding.UTF8.GetBytes(LicenceDetectorTests.Lgpl3Text);
    const string commit = "b3a8a8e92cbca83475986391c3cac56f0b185d29";
    var files = new Dictionary<string, byte[]> {
      ["LICENSE"] = lgpl,
      ["Cipher/LICENSE"] = lgpl,
      ["Cipher/algorithms/checksum/adler.js"] = "class AdlerAlgorithm {}\n"u8.ToArray(),
      ["Cipher/OpCodes.js"] = "const OpCodes = {};\n"u8.ToArray(),
      ["Other/gpl.c"] = "int g;\n"u8.ToArray()
    };
    var entries = new List<CorpusLockEntry>();
    foreach (var (path, content) in files) {
      Scratch.Write(this._root, $"corpus/upstream/Hawkynt/Hawkynt.github.io@{commit}/{path}", content);
      entries.Add(new("Hawkynt/Hawkynt.github.io", commit, path, CorpusLockEntry.Hash(content), content.Length));
    }

    entries.Add(new("Hawkynt/Hawkynt.github.io", commit, "Cipher/missing.js", new string('0', 64), 1));
    var mit = System.Text.Encoding.UTF8.GetBytes(LicenceDetectorTests.MitText);
    Scratch.Write(this._root, $"corpus/upstream/o/undeclared@{commit}/LICENSE", mit);
    entries.Add(new("o/undeclared", commit, "LICENSE", CorpusLockEntry.Hash(mit), mit.Length));
    var manifest = CorpusManifest.Parse($$"""
      schema: 1
      repositories:
        - { name: Hawkynt/Hawkynt.github.io, url: 'https://example.invalid/x.git', commit: {{commit}}, licence: LGPL-3.0-only, licenceFiles: [LICENSE, Cipher/LICENSE] }
      fixtures:
        - id: cipher.checksum.adler32
          repository: Hawkynt/Hawkynt.github.io
          path: Cipher/algorithms/checksum/adler.js
          export: AdlerAlgorithm
          dependencies: [Cipher/OpCodes.js]
          run: [node, '{file}']
          vectors: { kind: embedded, source: tests }
          licence: LGPL-3.0-or-later
          determinism: deterministic
          tolerance: exact
          profiles: []
      """, "corpus.yaml");
    var report = new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanCorpus(this._root, manifest, new CorpusLock(entries), LicenceMode.Corpus);

    var location = $"Hawkynt/Hawkynt.github.io@{commit}/";
    Assert.That(Row(report, location + "Cipher/algorithms/checksum/adler.js"), Is.EqualTo(("LGPL-3.0-or-later", "licence-file", "allowed")));
    Assert.That(Row(report, location + "Cipher/OpCodes.js"), Is.EqualTo(("LGPL-3.0-or-later", "licence-file", "allowed")));
    Assert.That(Row(report, location + "Other/gpl.c"), Is.EqualTo(("LGPL-3.0-only", "licence-file", "allowed")));
    Assert.That(Row(report, location + "Cipher/missing.js"), Is.EqualTo(((string?)null, (string?)null, "denied")));
    Assert.That(Row(report, $"o/undeclared@{commit}/LICENSE"), Is.EqualTo(("MIT", "licence-file", "allowed")));
    Assert.That(Messages(report), Is.EqualTo(new[] {
      $"PLY0023: corpus file 'Hawkynt/Hawkynt.github.io:Cipher/missing.js' cannot be read, the lock records {new string('0', 64)}: it is not imported; run 'corpus import'" }));
    Assert.That(report.ExitCode, Is.EqualTo(1));
  }

}
