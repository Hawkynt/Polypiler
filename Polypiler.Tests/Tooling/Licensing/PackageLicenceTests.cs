using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <see cref="LicenceScanner.ScanPackage"/> and <see cref="PackageLicence"/> over the package trees of
/// <c>tests/fixtures/infra/license-effect</c>, each with a manifest from the real writer: a GPL package,
/// an understated one, LGPL inputs pinned by the corpus, helpers, inputs inside and outside the package,
/// every member that cannot be part of an output, a folder without a manifest and an invalid one.
/// </summary>
[TestFixture]
public sealed class PackageLicenceTests {

  private static readonly LicencePolicy _shipped = LicencePolicy.Load(LicencePolicyTests.Shipped);
  private static readonly CorpusManifest _corpus = CorpusManifest.Load(Path.Combine(PrototypeFixture.RepositoryRoot, CorpusManifest.RepositoryPath));

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-package-licence-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private string Folder(string name) => Directory.CreateDirectory(Path.Combine(this._root, name)).FullName;

  private static PackageLicence? Scan(string folder, CorpusManifest? corpus = null)
    => new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanPackage(folder, corpus ?? _corpus);

  private static string[] Messages(PackageLicence package) => [.. package.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")];

  private string PackageTree(string tree) {
    var folder = this.Folder(tree);
    LicenceEffectTrees.Package(tree, folder);
    return folder;
  }

  // Test design 1.
  [Test]
  public void AGplPackage_IsGpl_AndSaysSo() {
    var package = Scan(this.PackageTree("pkg-gpl"))!;
    Assert.That((Messages(package), package.Stated, package.Effective!.Licence, package.Effective.Because), Is.EqualTo((new string[0], "GPL-3.0-or-later", "GPL-3.0-or-later", new[] { "gpl-nested/sub/x.c" })));
    Assert.That(package.ToJson(), Is.EqualTo("""
      {
        "effectiveLicence": "GPL-3.0-or-later",
        "class": "strong-copyleft",
        "because": [
          "gpl-nested/sub/x.c"
        ],
        "statedLicence": "GPL-3.0-or-later"
      }

      """.Replace("\r\n", "\n")));
  }

  [Test]
  public void TheWholePackageTree_PassesTheDistributionScan()
    => Assert.That(new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanTree(this.PackageTree("pkg-gpl"), LicenceMode.Distribution).Diagnostics, Is.Empty);

  // Test design 14.
  [Test]
  public void AGplDerivedPackageLabelledLgpl_IsRefused() {
    var package = Scan(this.PackageTree("pkg-understated"))!;
    Assert.That(Messages(package), Is.EqualTo(new[] { "PLY0024: 'pkg-understated': output licence 'LGPL-3.0-or-later' understates the effective licence 'GPL-3.0-or-later' (because of gpl-nested/sub/x.c)" }));
    Assert.That(package.Effective!.Licence, Is.EqualTo("GPL-3.0-or-later"));
  }

  // Test design 2: PB-Compiler's ARITH.BAS and the Cipher fixture, both LGPL by corpus.yaml.
  [Test]
  public void AnLgplPackage_StaysLgpl() {
    var package = Scan(this.PackageTree("pkg-lgpl"))!;
    var (repository, commit, path) = LicenceEffectTrees.PbCompilerArith;
    Assert.That((Messages(package), package.Effective!.Licence, package.Effective.Because), Is.EqualTo((new string[0], "LGPL-3.0-or-later", new[] { $"{repository}@{commit}:{path}" })));
  }

  [Test]
  public void TheCipherFixture_IsLgpl_WithoutSpecialTreatment() {
    var fixture = _corpus.Fixture("cipher.checksum.adler32")!;
    var commit = _corpus.Repository(fixture.Repository)!.Commit;
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "LGPL-3.0-or-later", [new(fixture.Path, new string('0', 64), new(fixture.Repository, commit, fixture.Path))], []);
    Assert.That(Scan(folder)!.Effective!.Licence, Is.EqualTo("LGPL-3.0-or-later"));
  }

  [Test]
  public void APinnedInput_NoFixtureLists_HasTheLicenceOfItsRepository() {
    var (repository, commit, _) = LicenceEffectTrees.PbCompilerArith;
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "LGPL-3.0-or-later", [new("README.md", new string('0', 64), new(repository, commit, "README.md"))], []);
    var package = Scan(folder)!;
    Assert.That((package.Effective!.Licence, package.Effective.Because), Is.EqualTo(("LGPL-3.0-or-later", new[] { $"{repository}@{commit}:README.md" })));
  }

  [Test]
  public void APinnedInput_OfACommitTheCorpusDoesNotPin_IsDetectedOnItsPath() {
    var folder = this.Folder("pkg");
    Scratch.Write(folder, "x.c", "// SPDX-License-Identifier: ISC\n");
    LicenceEffectTrees.Write(folder, "ISC", [new("x.c", new string('0', 64), new(LicenceEffectTrees.PbCompilerArith.Repository, new string('a', 40), "x.c"))], []);
    Assert.That(Scan(folder)!.Effective!.Licence, Is.EqualTo("ISC"));
  }

  [Test]
  public void APinnedInput_WithoutACorpus_OrOfARepositoryTheCorpusLacks_IsDetectedOnItsPath() {
    var folder = this.Folder("pkg");
    Scratch.Write(folder, "x.c", "// SPDX-License-Identifier: ISC\n");
    LicenceEffectTrees.Write(folder, "ISC", [new("x.c", new string('0', 64), new("someone/else", new string('a', 40), "x.c"))], []);
    Assert.That(new LicenceScanner(_shipped, LicenceDetectorTests.ShippedTexts).ScanPackage(folder, null)!.Effective!.Licence, Is.EqualTo("ISC"));
    Assert.That(Scan(folder)!.Effective!.Licence, Is.EqualTo("ISC"));
  }

  // Test design 8: a helper is a member of the closure.
  [Test]
  public void AnLgplHelper_MakesAnMitInputLgpl() {
    var folder = this.Folder("pkg");
    Scratch.Write(folder, "a.c", "// SPDX-License-Identifier: MIT\n");
    LicenceEffectTrees.Write(folder, "LGPL-3.0-or-later", [LicenceEffectTrees.Local(folder, "a.c")], [LicenceEffectTrees.Helper("FloatPow", "LGPL-3.0-or-later")]);
    var package = Scan(folder)!;
    Assert.That((Messages(package), package.Effective!.Licence, package.Effective.Because), Is.EqualTo((new string[0], "LGPL-3.0-or-later", new[] { "FloatPow" })));
  }

  [Test]
  public void PermissiveHelpers_JoinThePermissiveLabel() {
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "MIT", [], [LicenceEffectTrees.Helper("FloatPow", "MIT"), LicenceEffectTrees.Helper("FloatFma", "BSD-2-Clause")]);
    var package = Scan(folder)!;
    Assert.That((Messages(package), package.Effective!.Licence), Is.EqualTo((new string[0], "BSD-2-Clause AND MIT")));
  }

  // Test design 6 on a package: stating a stronger licence than the effective one is allowed.
  [Test]
  public void AStrongerStatedLicence_IsAllowed() {
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "GPL-3.0-or-later", [], [LicenceEffectTrees.Helper("FloatPow", "MIT")]);
    Assert.That(Messages(Scan(folder)!), Is.Empty);
  }

  [Test]
  public void NoAssertion_UnderstatesEveryEffectiveLicence() {
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "NOASSERTION", [], [LicenceEffectTrees.Helper("FloatPow", "MIT")]);
    Assert.That(Messages(Scan(folder)!), Is.EqualTo(new[] { "PLY0024: 'pkg': output licence 'NOASSERTION' understates the effective licence 'MIT' (because of FloatPow)" }));
  }

  // An input outside the package is detected with the declarations of its nearest ancestor that has them.
  [Test]
  public void AnInputOutsideThePackage_IsDetectedFromItsNearestDeclaredAncestor() {
    var tree = Path.Combine(this._root, "gpl-nested");
    foreach (var file in Directory.EnumerateFiles(Path.Combine(LicenceScannerTests.Fixtures, "gpl-nested"), "*", SearchOption.AllDirectories))
      Scratch.Write(tree, Path.GetRelativePath(Path.Combine(LicenceScannerTests.Fixtures, "gpl-nested"), file), File.ReadAllBytes(file));
    var folder = this.Folder("out");
    LicenceEffectTrees.Write(folder, "GPL-3.0-or-later", [LicenceEffectTrees.Local(folder, "../gpl-nested/sub/x.c")], []);
    var package = Scan(folder)!;
    Assert.That((Messages(package), package.Effective!.Licence, package.Effective.Because), Is.EqualTo((new string[0], "GPL-3.0-or-later", new[] { "../gpl-nested/sub/x.c" })));
  }

  [Test]
  public void AnInputOutsideThePackage_WithoutADeclaredAncestor_IsDetectedInItsOwnFolder() {
    var folder = this.Folder("out");
    Scratch.Write(this._root, "src/a.c", "int a(void) { return 0; }\n");
    Scratch.Write(this._root, "src/LICENSE", LicenceDetectorTests.MitText);
    LicenceEffectTrees.Write(folder, "MIT", [LicenceEffectTrees.Local(folder, "../src/a.c")], []);
    Assert.That(Scan(folder)!.Effective!.Licence, Is.EqualTo("MIT"));
  }

  [Test]
  public void AnInputOutsideThePackage_BelowAFolderWithAGitEntry_IsDetectedFromThere() {
    var folder = this.Folder("repo/out");
    Scratch.Write(this._root, "repo/.git", "gitdir: elsewhere\n");
    Scratch.Write(this._root, "repo/LICENSE", LicenceDetectorTests.MitText);
    Scratch.Write(this._root, "repo/src/a.c", "int a(void) { return 0; }\n");
    LicenceEffectTrees.Write(folder, "MIT", [LicenceEffectTrees.Local(folder, "../src/a.c")], []);
    Assert.That(Scan(folder)!.Effective!.Licence, Is.EqualTo("MIT"));
  }

  // Test design 15: a reference-only or a denied member; no effective licence.
  [Test]
  public void AReferenceOnlyInput_CannotBePartOfAnOutput() {
    var package = Scan(this.PackageTree("pkg-reference-only"))!;
    Assert.That((Messages(package), package.Effective, package.ToJson()),
      Is.EqualTo((new[] { "PLY0024: 'src/old.c': licence 'GPL-2.0-only' is reference-only and cannot be part of an output" }, (EffectiveLicenceResult?)null, (string?)null)));
  }

  [Test]
  public void ADeniedHelper_CannotBePartOfAnOutput() {
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "MIT", [], [LicenceEffectTrees.Helper("Closed", "Proprietary-Foo")]);
    Assert.That(Messages(Scan(folder)!), Is.EqualTo(new[] { "PLY0024: 'Closed': licence 'Proprietary-Foo' is denied and cannot be part of an output" }));
  }

  // Test design 16: an input whose licence cannot be determined.
  [Test]
  public void AnInputWithoutHeaderOrLicenceFile_IsPly0029_AndTheOtherMembersAreStillChecked() {
    var folder = this.Folder("pkg");
    Scratch.Write(folder, "orphan.c", "int orphan(void) { return 0; }\n");
    LicenceEffectTrees.Write(folder, "MIT", [LicenceEffectTrees.Local(folder, "orphan.c")], [LicenceEffectTrees.Helper("Closed", "Proprietary-Foo"), LicenceEffectTrees.Helper("FloatPow", "MIT")]);
    var package = Scan(folder)!;
    Assert.That((Messages(package), package.Effective), Is.EqualTo((new[] {
      "PLY0029: 'orphan.c': no licence could be determined",
      "PLY0024: 'Closed': licence 'Proprietary-Foo' is denied and cannot be part of an output"
    }, (EffectiveLicenceResult?)null)));
  }

  [Test]
  public void AMissingInput_IsPly0029_WithWhy() {
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "MIT", [LicenceEffectTrees.Local(folder, "gone.c")], [LicenceEffectTrees.Helper("FloatPow", "MIT")]);
    var package = Scan(folder)!;
    Assert.That((package.Diagnostics.Single().Code, package.Diagnostics.Single().Message, package.Effective), Is.EqualTo(("PLY0029", "'gone.c': no licence could be determined: the file cannot be read: ", (EffectiveLicenceResult?)null))
      .Using<(string, string, EffectiveLicenceResult?)>((actual, expected) => actual.Item1 == expected.Item1 && actual.Item2.StartsWith(expected.Item2, System.StringComparison.Ordinal) && actual.Item3 == expected.Item3));
  }

  [Test]
  public void AnInputWhoseDeclarationDisagreesWithItsLicenceFile_IsRefused() {
    var folder = this.Folder("pkg");
    Scratch.Write(folder, "LICENSE", LicenceDetectorTests.MitText);
    Scratch.Write(folder, "licences.yaml", "schema: 1\ndeclared:\n  - { glob: '**', licence: ISC }\n");
    Scratch.Write(folder, "a.c", "int a(void) { return 0; }\n");
    LicenceEffectTrees.Write(folder, "MIT", [LicenceEffectTrees.Local(folder, "a.c")], []);
    Assert.That(Messages(Scan(folder)!), Is.EqualTo(new[] { "PLY0024: 'a.c': declared licence 'ISC' disagrees with detected 'MIT'" }));
  }

  [Test]
  public void AManifestWithoutInputsAndHelpers_IsPly0029() {
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "MIT", [], []);
    Assert.That(Messages(Scan(folder)!), Is.EqualTo(new[] { "PLY0029: 'pkg': no licence could be determined: the manifest lists no input and no helper" }));
  }

  [Test]
  public void AManifestWhoseOnlyInputIsUndetermined_ReportsThatAlone() {
    var folder = this.Folder("pkg");
    LicenceEffectTrees.Write(folder, "MIT", [LicenceEffectTrees.Local(folder, "gone.c")], []);
    Assert.That(Scan(folder)!.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0029" }));
  }

  // Test design 12 and 18.
  [Test]
  public void AFolderWithoutAManifest_IsNoPackage() => Assert.That(Scan(this.Folder("plain")), Is.Null);

  [Test]
  public void AnInvalidManifest_IsPly0053() {
    var folder = this.Folder("pkg");
    Scratch.Write(folder, Manifest.FileName, "{\"schemaVersion\": 1, ");
    var package = Scan(folder)!;
    Assert.That((package.Stated, package.Effective, package.Diagnostics.Select(diagnostic => diagnostic.Code).Distinct()), Is.EqualTo(((string?)null, (EffectiveLicenceResult?)null, new[] { "PLY0053" })));
  }

}
