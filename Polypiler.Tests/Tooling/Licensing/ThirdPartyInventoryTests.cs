using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <see cref="ThirdPartyInventory"/>: the repository's own notices and packages all listed with an
/// allowed licence, and every way a scratch repository falls out of its inventory.
/// </summary>
[TestFixture]
public sealed class ThirdPartyInventoryTests {

  private static readonly LicencePolicy _shipped = LicencePolicy.Load(LicencePolicyTests.Shipped);

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-licence-inventory-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private string[] Check(string inventory)
    => [.. ThirdPartyInventory.Parse(inventory, "third-party.yaml").Check(this._root, _shipped).Select(problem => $"{problem.Code}: {problem.Message}")];

  [Test]
  public void Repository_ListsEveryNoticeAndPackage_WithAnAllowedLicence() {
    var inventory = ThirdPartyInventory.Load(Path.Combine(PrototypeFixture.RepositoryRoot, ThirdPartyInventory.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));
    Assert.That(inventory.Check(PrototypeFixture.RepositoryRoot, _shipped).Select(problem => problem.Message), Is.Empty);
    Assert.That(inventory.Notices.Select(notice => notice.Path), Does.Contain("Polypiler.Core.IR/Float/THIRD-PARTY-NOTICE.musl.txt"));
    Assert.That(inventory.Packages, Does.Contain(new ThirdPartyPackage("FsCheck", "3.6.0", "BSD-3-Clause")));
  }

  [Test]
  public void ListedNoticesAndPackages_Pass() {
    Scratch.Write(this._root, "Lib/THIRD-PARTY-NOTICE.foo.txt", "foo, MIT");
    Scratch.Write(this._root, "Lib/Lib.csproj", "<Project><ItemGroup><PackageReference Include=\"A\" Version=\"1.0\" /><PackageReference Include=\"B\"><Version>2.0</Version></PackageReference><PackageReference Include=\"C\" /><PackageReference Update=\"D\" Version=\"1.0\" /></ItemGroup></Project>");
    Scratch.Write(this._root, "Lib/bin/THIRD-PARTY-NOTICE.copy.txt", "");
    Scratch.Write(this._root, "corpus/THIRD-PARTY-NOTICE.upstream.txt", "");
    Scratch.Write(this._root, ".claude/worktrees/x/THIRD_PARTY_NOTICES.md", "");
    Scratch.Write(this._root, "Lib/corpus/THIRD_PARTY_NOTICES.md", "");
    Assert.That(this.Check("""
      schema: 1
      notices:
        - path: Lib/THIRD-PARTY-NOTICE.foo.txt
          components: [{ name: foo, licence: MIT }]
        - path: Lib/corpus/THIRD_PARTY_NOTICES.md
          components: [{ name: bar, licence: 'BSD-2-Clause OR GPL-2.0-only' }]
      packages:
        - { id: A, version: '1.0', licence: MIT }
        - { id: B, version: '2.0', licence: Apache-2.0 }
      """), Is.Empty);
  }

  [Test]
  public void UnlistedStaleAndDeniedEntries_AreAllReported() {
    Scratch.Write(this._root, "Lib/THIRD-PARTY-NOTICE.foo.txt", "");
    Scratch.Write(this._root, "Lib/THIRD-PARTY-NOTICE.agpl.txt", "");
    Scratch.Write(this._root, "Lib/THIRD-PARTY-NOTICE.odd.txt", "");
    Scratch.Write(this._root, "Lib/Lib.csproj", "<Project><ItemGroup><PackageReference Include=\"A\" Version=\"1.0\" /><PackageReference Include=\"A\" Version=\"1.0\" /></ItemGroup></Project>");
    Scratch.Write(this._root, "Lib/Broken.props", "<Project>");
    var problems = this.Check("""
      schema: 1
      notices:
        - path: Lib/THIRD-PARTY-NOTICE.gone.txt
          components: [{ name: gone, licence: MIT }]
        - path: Lib/THIRD-PARTY-NOTICE.agpl.txt
          components: [{ name: agpl, licence: AGPL-3.0-only }]
        - path: Lib/THIRD-PARTY-NOTICE.odd.txt
          components: [{ name: odd, licence: 'MIT AND' }]
      packages:
        - { id: A, version: '2.0', licence: MIT }
      """);
    Assert.That(problems[2], Does.StartWith("PLY0026: corpus manifest invalid: licence inventory: 'Lib/Broken.props' is not XML: "));
    Assert.That(problems.Where((_, index) => index != 2), Is.EqualTo(new[] {
      "PLY0029: 'Lib/THIRD-PARTY-NOTICE.foo.txt': no licence could be determined: the notice is not listed in tooling/licensing/third-party.yaml",
      "PLY0026: corpus manifest invalid: licence inventory: notice 'Lib/THIRD-PARTY-NOTICE.gone.txt' is listed but does not exist",
      "PLY0029: 'A 1.0': no licence could be determined: the package, referenced by 'Lib/Lib.csproj', is not listed in tooling/licensing/third-party.yaml",
      "PLY0026: corpus manifest invalid: licence inventory: package 'A 2.0' is listed but no project references it",
      "PLY0024: 'Lib/THIRD-PARTY-NOTICE.agpl.txt: agpl': licence 'AGPL-3.0-only' is denied in distribution mode",
      "PLY0029: 'Lib/THIRD-PARTY-NOTICE.odd.txt: odd': no licence could be determined: 'MIT AND' is not an SPDX expression: expected a licence after 'AND' but the expression ends" }));
  }

  [TestCase("notices: [", "not YAML")]
  [TestCase("", "the file must be a mapping")]
  [TestCase("schema: 1\nnotices: []\n", "the file must be a mapping")]
  [TestCase("schema: 2\nnotices: []\npackages: []\n", "the file must be a mapping")]
  [TestCase("schema: 1\nnotices: {}\npackages: []\n", "the file must be a mapping")]
  [TestCase("schema: 1\nnotices: []\npackages: {}\n", "the file must be a mapping")]
  [TestCase("schema: 1\nnotices: [3]\npackages: []\n", "a notice must be a mapping")]
  [TestCase("schema: 1\nnotices: [{ path: a }]\npackages: []\n", "a notice must be a mapping")]
  [TestCase("schema: 1\nnotices: [{ path: a, components: [] }]\npackages: []\n", "a notice must be a mapping")]
  [TestCase("schema: 1\nnotices: [{ path: a, components: [{ name: n }] }]\npackages: []\n", "a notice must be a mapping")]
  [TestCase("schema: 1\nnotices: [{ path: a, components: [{ licence: MIT }] }]\npackages: []\n", "a notice must be a mapping")]
  [TestCase("schema: 1\nnotices: [{ components: [{ name: n, licence: MIT }], other: 1 }]\npackages: []\n", "a notice must be a mapping")]
  [TestCase("schema: 1\nnotices: [{ path: a, components: [{ name: n, licence: MIT }], other: 1 }]\npackages: []\n", "a notice must be a mapping")]
  [TestCase("schema: 1\nnotices: []\npackages: [{ id: A, version: '1' }]\n", "a package must be a mapping")]
  [TestCase("schema: 1\nnotices: []\npackages: [{ id: A, version: '1', other: MIT }]\n", "a package must be a mapping")]
  [TestCase("schema: 1\nnotices: []\npackages: [{ id: A, other: '1', licence: MIT }]\n", "a package must be a mapping")]
  [TestCase("schema: 1\nnotices: []\npackages: [{ other: A, version: '1', licence: MIT }]\n", "a package must be a mapping")]
  [TestCase("schema: 1\nnotices: []\npackages: [3]\n", "a package must be a mapping")]
  public void MalformedInventory_IsPly0026(string text, string message)
    => Assert.That(() => ThirdPartyInventory.Parse(text, "third-party.yaml"),
      Throws.TypeOf<CorpusException>().With.Message.Contains("corpus manifest invalid: licence inventory: " + message));

  [Test]
  public void Load_OfAMissingFile_IsPly0026()
    => Assert.That(() => ThirdPartyInventory.Load(Path.Combine(this._root, "missing.yaml")),
      Throws.TypeOf<CorpusException>().With.Message.Contains("licence inventory: cannot read"));

  [TestCase("THIRD-PARTY-NOTICE.musl.txt", true)]
  [TestCase("THIRD_PARTY_NOTICES.md", true)]
  [TestCase("third-party-notice.txt", false)]
  [TestCase("NOTICE", false)]
  public void IsNotice_MatchesBothSpellings(string name, bool expected) => Assert.That(ThirdPartyInventory.IsNotice(name), Is.EqualTo(expected));

}
