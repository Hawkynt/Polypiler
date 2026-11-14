using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <see cref="ProjectFence"/> and <see cref="ProvenanceNotice"/> over the repository trees of
/// <c>tests/fixtures/infra/license-effect</c>, scratch trees and the repository itself: the fence
/// triggered by a notice entry, an SPDX header and a licence file; permissive and LGPL origins that
/// pass; entries without their lines; what is outside the fence; and the two forms of a notice.
/// </summary>
[TestFixture]
public sealed class ProjectFenceTests {

  private static readonly LicencePolicy _shipped = LicencePolicy.Load(LicencePolicyTests.Shipped);

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-project-fence-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private static LicenceReport Scan(string root) => new ProjectFence(_shipped, LicenceDetectorTests.ShippedTexts).Scan(root);

  private LicenceReport Tree(string tree) {
    LicenceEffectTrees.Copy(tree, this._root);
    return Scan(this._root);
  }

  private static string[] Messages(LicenceReport report) => [.. report.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")];

  private const string Fence = "GPL-derived code may not be merged into the LGPL-licensed compiler projects";

  // Scenario: Permissive and LGPL origins pass the fence; a GPL file outside a Polypiler.* folder is not looked at.
  [Test]
  public void PermissiveAndLgplOrigins_PassTheFence() {
    var report = this.Tree("proj-clean");
    Assert.That(Messages(report), Is.Empty);
    Assert.That(report.Entries.Select(entry => (entry.Path, entry.Licence, LicenceReport.Name(entry.Status))), Is.EqualTo(new[] {
      ("Polypiler.Demo/Demo.cs", (string?)null, "allowed"),
      ("Polypiler.Demo/THIRD_PARTY_NOTICES.md", "MIT AND LGPL-2.1-or-later", "allowed")
    }));
  }

  // Test design 7: the fence is triggered by a notice entry, an SPDX header and a licence file in the project.
  [Test]
  public void AGplNoticeEntry_CannotEnterACompilerProject() {
    var report = this.Tree("proj-gpl-notice");
    Assert.That(Messages(report), Is.EqualTo(new[] { $"PLY0024: 'Polypiler.Demo/THIRD_PARTY_NOTICES.md': {Fence}; keep 'https://example.org/gpl-lib:src/x.c' as a rung 2 reference" }));
    Assert.That((report.ExitCode, LicenceReport.Name(report.Entry("Polypiler.Demo/THIRD_PARTY_NOTICES.md")!.Status)), Is.EqualTo((8, "denied")));
  }

  [Test]
  public void AGplHeader_CannotEnterACompilerProject() {
    var report = this.Tree("proj-gpl-header");
    Assert.That(Messages(report), Is.EqualTo(new[] { $"PLY0024: 'Polypiler.Demo/Derived.cs': {Fence}; keep 'Polypiler.Demo/Derived.cs' as a rung 2 reference" }));
    Assert.That(report.Entry("Polypiler.Demo/Derived.cs")!.Source, Is.EqualTo(LicenceSource.Header));
  }

  [Test]
  public void AGplLicenceFile_CannotEnterACompilerProject_AndIsReportedOnce() {
    Scratch.Write(this._root, "Polypiler.Demo/Vendored/GplLib/LICENSE", File.ReadAllText(Path.Combine(LicenceScannerTests.Fixtures, "gpl-nested", "sub", "LICENSE")));
    Scratch.Write(this._root, "Polypiler.Demo/Vendored/GplLib/a.c", "int a(void) { return 0; }\n");
    Scratch.Write(this._root, "Polypiler.Demo/Vendored/GplLib/b.c", "int b(void) { return 0; }\n");
    var report = Scan(this._root);
    Assert.That(Messages(report), Is.EqualTo(new[] { $"PLY0024: 'Polypiler.Demo/Vendored/GplLib/LICENSE': {Fence}; keep 'Polypiler.Demo/Vendored/GplLib/' as a rung 2 reference" }));
    Assert.That(report.Entry("Polypiler.Demo/Vendored/GplLib/LICENSE")!.Licence, Is.EqualTo("GPL-3.0"));
  }

  [Test]
  public void AnLgplOrMitLicenceFile_PassesTheFence_AndAnUnknownTextIsLeftToTheInventory() {
    Scratch.Write(this._root, "Polypiler.A/LICENSE", LicenceDetectorTests.Lgpl3Text);
    Scratch.Write(this._root, "Polypiler.B/Vendored/X/LICENSE.txt", LicenceDetectorTests.MitText);
    Scratch.Write(this._root, "Polypiler.C/COPYING", "All rights reserved by nobody in particular.\n");
    var report = Scan(this._root);
    Assert.That(Messages(report), Is.Empty);
    Assert.That(report.Entries.Select(entry => entry.Licence), Is.EqualTo(new[] { "LGPL-3.0", "MIT", null }));
  }

  [Test]
  public void ADeniedLicenceOutsideTheGplFamily_IsDeniedInProjectMode() {
    Scratch.Write(this._root, "Polypiler.Demo/Closed.cs", "// SPDX-License-Identifier: Proprietary-Foo\n");
    Assert.That(Messages(Scan(this._root)), Is.EqualTo(new[] { "PLY0024: 'Polypiler.Demo/Closed.cs': licence 'Proprietary-Foo' is denied in project mode" }));
  }

  [TestCase("GPL-2.0-only")]
  [TestCase("AGPL-3.0-only")]
  [TestCase("MIT AND GPL-3.0-only")]
  public void AReferenceOnlyOrStrongCopyleftHeader_IsFenced(string expression) {
    Scratch.Write(this._root, "Polypiler.Demo/X.cs", $"// SPDX-License-Identifier: {expression}\n");
    Assert.That(Messages(Scan(this._root)), Is.EqualTo(new[] { $"PLY0024: 'Polypiler.Demo/X.cs': {Fence}; keep 'Polypiler.Demo/X.cs' as a rung 2 reference" }));
  }

  [TestCase("MIT")]
  [TestCase("LGPL-2.1-or-later")]
  [TestCase("MIT OR GPL-3.0-only")]
  public void APermissiveOrWeakHeader_Passes(string expression) {
    Scratch.Write(this._root, "Polypiler.Demo/X.cs", $"// SPDX-License-Identifier: {expression}\n");
    Assert.That(Messages(Scan(this._root)), Is.Empty);
  }

  [Test]
  public void AHeaderThatDoesNotParse_IsPly0029() {
    Scratch.Write(this._root, "Polypiler.Demo/X.cs", "// SPDX-License-Identifier: MIT AND\n");
    Assert.That(Messages(Scan(this._root)), Is.EqualTo(new[] { "PLY0029: 'Polypiler.Demo/X.cs': no licence could be determined: 'MIT AND' is not an SPDX expression: expected a licence after 'AND' but the expression ends" }));
  }

  // Test design 17: a notice entry without its Origin: or Licence: line.
  [Test]
  public void ANoticeEntryWithoutOrigin_IsPly0029() {
    var report = this.Tree("proj-no-origin");
    Assert.That(Messages(report), Is.EqualTo(new[] { "PLY0029: 'Polypiler.Demo/THIRD-PARTY-NOTICE.Lib.txt': provenance entry lacks 'Origin:' or 'Licence:' (line 1)" }));
    Assert.That(report.ExitCode, Is.EqualTo(8));
  }

  [Test]
  public void ANoticeEntryWithoutLicence_AndOneWithALicenceThatDoesNotParse_AreBothReported() {
    Scratch.Write(this._root, "Polypiler.Demo/THIRD_PARTY_NOTICES.md", "# Notices\n\n## A\n\n- Origin: a/b@0:c\n\n## B\n\n- Origin: https://example.org/b\n- Licence: MIT OR\n");
    Assert.That(Messages(Scan(this._root)), Is.EqualTo(new[] {
      "PLY0029: 'Polypiler.Demo/THIRD_PARTY_NOTICES.md': provenance entry lacks 'Origin:' or 'Licence:' (line 3)",
      "PLY0029: 'Polypiler.Demo/THIRD_PARTY_NOTICES.md': no licence could be determined: 'MIT OR' is not an SPDX expression: expected a licence after 'OR' but the expression ends"
    }));
  }

  // Test design 13: a repository with no compiler project; build output and repository data are not scanned.
  [Test]
  public void ARepositoryWithoutCompilerProjects_Passes() {
    Scratch.Write(this._root, "tests/X.cs", "// SPDX-License-Identifier: GPL-3.0-only\n");
    Scratch.Write(this._root, "Polypiler.slnx", "// SPDX-License-Identifier: GPL-3.0-only\n");
    var report = Scan(this._root);
    Assert.That((report.Entries.Count, report.Diagnostics.Count), Is.EqualTo((0, 0)));
  }

  [Test]
  public void BinObjAndGitFolders_AreNotScanned() {
    foreach (var folder in new[] { "bin", "obj", ".git", "sub/bin" })
      Scratch.Write(this._root, $"Polypiler.Demo/{folder}/X.cs", "// SPDX-License-Identifier: GPL-3.0-only\n");
    Scratch.Write(this._root, "Polypiler.Demo/binary/bin.cs", "// SPDX-License-Identifier: MIT\n");
    var report = Scan(this._root);
    Assert.That((report.Entries.Select(entry => entry.Path), report.Diagnostics.Count), Is.EqualTo((new[] { "Polypiler.Demo/binary/bin.cs" }, 0)));
  }

  [Test]
  public void AFileThatCannotBeRead_IsPly0029() {
    if (OperatingSystem.IsWindows()) {
      Assert.Ignore("a file mode that denies reading is a Unix permission");
      return;
    }

    var file = Scratch.Write(this._root, "Polypiler.Demo/X.cs", "x\n");
    File.SetUnixFileMode(file, UnixFileMode.None);
    try {
      if (File.Exists(file) && CanRead(file))
        Assert.Ignore("the user may read any file");
      Assert.That(Messages(Scan(this._root)).Single(), Does.StartWith("PLY0029: 'Polypiler.Demo/X.cs': no licence could be determined: the file cannot be read: "));
    } finally {
      File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
  }

  private static bool CanRead(string file) {
    try {
      using var stream = File.OpenRead(file);
      return true;
    } catch (UnauthorizedAccessException) {
      return false;
    }
  }

  // Measurement: no GPL-derived file inside the repository's own compiler projects.
  [Test]
  public void TheRepository_HasNoGplDerivedCodeInItsCompilerProjects() {
    var watch = Stopwatch.StartNew();
    var report = Scan(PrototypeFixture.RepositoryRoot);
    TestContext.Out.WriteLine($"{report.Entries.Count} files in {watch.ElapsedMilliseconds} ms");
    Assert.That(Messages(report), Is.Empty);
    Assert.That(report.Entries.Where(entry => ProvenanceNotice.IsNotice(entry.Path[(entry.Path.LastIndexOf('/') + 1)..])).Select(entry => entry.Licence), Is.All.Not.Null);
  }

  [TestCase("THIRD_PARTY_NOTICES.md", true)]
  [TestCase("THIRD-PARTY-NOTICE.musl.txt", true)]
  [TestCase("THIRD-PARTY-NOTICE..txt", false)]
  [TestCase("THIRD-PARTY-NOTICE.musl.md", false)]
  [TestCase("THIRD_PARTY_NOTICES.txt", false)]
  [TestCase("NOTICE", false)]
  public void IsNotice_KnowsTheTwoForms(string name, bool notice) => Assert.That(ProvenanceNotice.IsNotice(name), Is.EqualTo(notice));

  [Test]
  public void AMarkdownNotice_HasOneEntryPerSection_AndIgnoresItsPreamble() {
    var entries = ProvenanceNotice.Parse("THIRD_PARTY_NOTICES.md", "# N\r\nLicence: GPL-3.0-only\r\n\r\n## A\r\n- Origin: a\r\n* Licence: MIT\r\n  - **Licence:** ISC\r\n## B\r\nOrigin:\r\nLicence:   \r\n");
    Assert.That(entries.Select(entry => (entry.Line, string.Join(",", entry.Origins), string.Join(",", entry.Licences), entry.IsComplete)),
      Is.EqualTo(new[] { (4, "a", "MIT", true), (8, "", "", false) }));
  }

  [Test]
  public void AMarkdownNoticeWithoutSections_HasNoEntry() => Assert.That(ProvenanceNotice.Parse("THIRD_PARTY_NOTICES.md", "Origin: a\nLicence: MIT\n"), Is.Empty);

  [Test]
  public void ATextNotice_IsOneEntry_WithEveryPairOfLines() {
    var entry = ProvenanceNotice.Parse("THIRD-PARTY-NOTICE.musl.txt", "Origin: a\nLicence: MIT\nOrigin: b\nLicence: BSD-2-Clause\n\n## not a section\n").Single();
    Assert.That((entry.Line, entry.Origins, entry.Licences, entry.IsComplete), Is.EqualTo((1, new[] { "a", "b" }, new[] { "MIT", "BSD-2-Clause" }, true)));
  }

  [Test]
  public void AnEntryWithOnlyALicence_IsIncomplete() => Assert.That(ProvenanceNotice.Parse("THIRD-PARTY-NOTICE.x.txt", "Licence: MIT\n").Single().IsComplete, Is.False);

}
