using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// The owner decisions of 2026-11-14 in the shipped policy: LGPL-2.1-only is an allowed source
/// licence of weak copyleft, the sources of the emitted helpers are MIT by an override, and the GPL
/// fence around the compiler projects still holds for a backend's folder.
/// </summary>
[TestFixture]
public sealed class LicenceDecisionsTests {

  private static readonly LicencePolicy _shipped = LicencePolicy.Load(LicencePolicyTests.Shipped);

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-licence-decisions-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private static string[] Messages(LicenceReport report) => [.. report.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")];

  // Test design 5: LGPL-2.1-only and LGPL-2.1-or-later are allowed, GPL-2.0-only stays reference-only.
  [TestCase("LGPL-2.1-only", "allowed", "allowed")]
  [TestCase("LGPL-2.1-or-later", "allowed", "allowed")]
  [TestCase("GPL-2.0-only", "reference-only", "denied")]
  public void TheShippedPolicy_DecidesTheLgpl21Identifiers(string licence, string corpus, string distribution)
    => Assert.That((LicenceReport.Name(_shipped.StatusOf(licence, LicenceMode.Corpus)), LicenceReport.Name(_shipped.StatusOf(licence, LicenceMode.Distribution))), Is.EqualTo((corpus, distribution)));

  // Scenario: LGPL-2.1-only sources are allowed.
  [Test]
  public void ACorpusFileUnderLgpl21Only_IsNoViolation() {
    Scratch.Write(this._root, "lib/a.c", "/* SPDX-License-Identifier: LGPL-2.1-only */\nint a;\n");
    Scratch.Write(this._root, "lib/b.c", "// SPDX-License-Identifier: GPL-2.0-only\nint b;\n");
    var report = new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanTree(this._root, LicenceMode.Corpus);
    Assert.That((Messages(report).Length, report.ExitCode), Is.EqualTo((0, 0)));
    Assert.That(report.Entries.Select(entry => (entry.Path, entry.Licence, LicenceReport.Name(entry.Status))), Is.EqualTo(new[] {
      ("lib/a.c", "LGPL-2.1-only", "allowed"), ("lib/b.c", "GPL-2.0-only", "reference-only")
    }));
  }

  [Test]
  public void Lgpl21Only_IsWeakCopyleft_AndMayStandInACompilerProject() {
    Assert.That((LicenceClasses.Of("LGPL-2.1-only", _shipped), EffectiveLicence.ClassOf("LGPL-2.1-only AND MIT", _shipped)), Is.EqualTo(((LicenceClass?)LicenceClass.WeakCopyleft, (LicenceClass?)LicenceClass.WeakCopyleft)));
    Scratch.Write(this._root, "Polypiler.Core.Backends.CSharp/Helpers/Derived.cs", "// SPDX-License-Identifier: LGPL-2.1-only\nnamespace A;\n");
    Assert.That(Messages(new ProjectFence(_shipped, LicenceDetectorTests.ShippedTexts).Scan(this._root)), Is.Empty);
  }

  // Scenario: The GPL fence still holds, in the folder of a backend whose helpers are MIT.
  [Test]
  public void AGplHeaderInABackend_IsStoppedByTheFence() {
    Scratch.Write(this._root, "Polypiler.Core.Backends.CSharp/Helpers/Mit.cs", "// SPDX-License-Identifier: MIT\nnamespace A;\n");
    Scratch.Write(this._root, "Polypiler.Core.Backends.CSharp/Gpl.cs", "// SPDX-License-Identifier: GPL-3.0-or-later\nnamespace A;\n");
    var report = new ProjectFence(_shipped, LicenceDetectorTests.ShippedTexts).Scan(this._root);
    Assert.That(Messages(report), Is.EqualTo(new[] {
      "PLY0024: 'Polypiler.Core.Backends.CSharp/Gpl.cs': GPL-derived code may not be merged into the LGPL-licensed compiler projects; keep 'Polypiler.Core.Backends.CSharp/Gpl.cs' as a rung 2 reference"
    }));
    Assert.That((report.ExitCode, LicenceReport.Name(report.Entry("Polypiler.Core.Backends.CSharp/Helpers/Mit.cs")!.Status)), Is.EqualTo((8, "allowed")));
  }

  // The override marks a helper source without its own header as MIT; a header wins over it.
  [TestCase("Polypiler.Core.Backends.CSharp/Helpers/X.cs", "namespace A;\n", "MIT", "override")]
  [TestCase("Polypiler.Core.Backends.JavaScript/Helpers/Integer/X.cs", "namespace A;\n", "MIT", "override")]
  [TestCase("Polypiler.Core.IR/Float/Operations/X.cs", "// SPDX-License-Identifier: MIT AND BSD-2-Clause\n", "MIT AND BSD-2-Clause", "header")]
  public void TheHelperSources_AreMitByTheOverride(string path, string text, string licence, string source) {
    Scratch.Write(this._root, path, text);
    var report = new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanTree(this._root, LicenceMode.Distribution);
    var entry = report.Entry(path)!;
    Assert.That((entry.Licence, entry.Source is { } found ? LicenceReport.Name(found) : null, LicenceReport.Name(entry.Status)), Is.EqualTo((licence, source, "allowed")));
  }

  [Test]
  public void TheOverride_LeavesTheRestOfABackendAlone() {
    Scratch.Write(this._root, "Polypiler.Core.Backends.CSharp/CSharpEmitter.cs", "namespace A;\n");
    var report = new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanTree(this._root, LicenceMode.Distribution);
    Assert.That((report.Entry("Polypiler.Core.Backends.CSharp/CSharpEmitter.cs")!.Licence, Messages(report).Single()), Is.EqualTo(((string?)null,
      "PLY0029: 'Polypiler.Core.Backends.CSharp/CSharpEmitter.cs': no licence could be determined")));
  }

}
