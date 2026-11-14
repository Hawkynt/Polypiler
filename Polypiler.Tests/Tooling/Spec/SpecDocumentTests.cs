using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary><see cref="SpecDocument"/>, <see cref="SpecMeta"/> and <see cref="SpecTree"/>: what a read document exposes and how a tree is found and indexed.</summary>
[TestFixture]
public sealed class SpecDocumentTests {

  private static SpecDocument Parse(string text, string path = "demo/widget.yaml") => SpecDocument.Parse(path, text);

  [Test]
  public void ValidDocument_ExposesFamilyIdMetaAndBody() {
    var document = Parse(SpecText.Document());
    Assert.That((document.Path, document.Family, document.Id), Is.EqualTo(("demo/widget.yaml", "demo", "DEMO.widget")));
    Assert.That(document.Meta, Is.EqualTo(new SpecMeta("DEMO.widget", "Widget", 2, "normative", "Hawkynt", "SPEC-META", document.Meta!.Tests, "P0", null, "additive", 2, "A widget MUST name each of its parts once.")));
    Assert.That((document.Meta.Title, document.Meta.Owner, document.Meta.Issue), Is.EqualTo(("Widget", "Hawkynt", "SPEC-META")));
    Assert.That(document.Meta.Tests, Is.EqualTo(new[] { SpecRepository.TestPath }));
    Assert.That(document.Body!.ToJsonString(), Is.EqualTo("{\"parts\":[{\"name\":\"gear\"},{\"name\":\"spring\"}]}"));
  }

  [Test]
  public void DeprecatedIn_IsRead() => Assert.That(Parse(SpecText.Document(("deprecatedIn", "P3"))).Meta!.DeprecatedIn, Is.EqualTo("P3"));

  [Test]
  public void IntegralFloat_CountsAsAnInteger() => Assert.That(Parse(SpecText.Document(("version", "2.0"))).Meta!.Version, Is.EqualTo(2));

  [TestCase("id", null)]
  [TestCase("id", "3")]
  [TestCase("title", null)]
  [TestCase("version", null)]
  [TestCase("version", "two")]
  [TestCase("version", "2.5")]
  [TestCase("version", "[2]")]
  [TestCase("version", "1e20")]
  [TestCase("status", null)]
  [TestCase("owner", null)]
  [TestCase("issue", null)]
  [TestCase("tests", null)]
  [TestCase("tests", "x")]
  [TestCase("tests", "[1]")]
  [TestCase("introducedIn", null)]
  [TestCase("compatibility", null)]
  [TestCase("reviewedRevision", null)]
  [TestCase("normative", null)]
  [TestCase("deprecatedIn", "~")]
  public void MetaOfAWrongBlock_IsNull(string key, string? value) => Assert.That(Parse(SpecText.Document((key, value))).Meta, Is.Null);

  [Test]
  public void DocumentThatIsNoMapping_HasNoMetaIdOrBody() {
    var document = Parse("- a\n");
    Assert.That((document.Meta, document.Id, document.Body), Is.EqualTo(((SpecMeta?)null, (string?)null, (System.Text.Json.Nodes.JsonNode?)null)));
  }

  [Test]
  public void EmptyDocument_HasNoRootAndPointsAtTheFirstLine() {
    var document = Parse("");
    Assert.That((document.Root, document.PositionOf("/id")), Is.EqualTo(((System.Text.Json.Nodes.JsonNode?)null, (1, 1))));
  }

  [TestCase("", 1, 1)]
  [TestCase("/version", 3, 10)]
  [TestCase("/spec/parts/1/name", 15, 13)]
  [TestCase("/spec/parts/7/name", 14, 5)]
  [TestCase("/deprecatedIn", 1, 1)]
  [TestCase("version", 1, 1)]
  public void PositionOf_TheValueOrItsNearestAncestor(string pointer, int line, int column)
    => Assert.That(Parse(SpecText.Document()).PositionOf(pointer), Is.EqualTo((line, column)));

  [Test]
  public void RangeOf_NamesThePath() => Assert.That(Parse(SpecText.Document()).RangeOf("/id"), Is.EqualTo(new SourceRange("demo/widget.yaml", 1, 5, 1, 5)));

  [TestCase("widget.yaml")]
  [TestCase("/widget.yaml")]
  public void PathOutsideAFamilyFolder_IsRefused(string path) => Assert.Throws<ArgumentException>(() => Parse(SpecText.Document(), path));

  [Test]
  public void NullArguments_AreRefused() {
    Assert.Throws<ArgumentNullException>(() => SpecDocument.Parse(null!, ""));
    Assert.Throws<ArgumentNullException>(() => Parse("").PositionOf(null!));
  }

  [Test]
  public void Tree_ListsDocumentsInOrdinalPathOrderAndIgnoresFilesInTheRoot() {
    using var repository = new SpecRepository();
    repository.Document("demo/b.yaml", SpecText.Document(("id", "DEMO.b")))
      .Document("demo/a/z.yaml", SpecText.Document(("id", "DEMO.z")))
      .Document("abc/c.yaml", SpecText.Document(("id", "DEMO.c")))
      .Document("diagnostics.yaml", "schema: 1\n")
      .Document("demo/notes.txt", "not a document");
    var tree = repository.Tree();
    Assert.That(tree.Documents.Select(document => document.Path), Is.EqualTo(new[] { "abc/c.yaml", "demo/a/z.yaml", "demo/b.yaml" }));
    Assert.That((tree.Root, tree.RepositoryRoot), Is.EqualTo((repository.SpecRoot, repository.Root)));
    Assert.That(tree.ById.Keys.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "DEMO.b", "DEMO.c", "DEMO.z" }));
  }

  [Test]
  public void Tree_IndexesTheFirstOfTwoDocumentsWithOneIdAndSkipsDocumentsWithoutOne() {
    using var repository = new SpecRepository();
    repository.Document("demo/b.yaml", SpecText.Document()).Document("demo/a.yaml", SpecText.Document()).Document("demo/c.yaml", SpecText.Document(("id", "7")));
    var tree = repository.Tree();
    Assert.That(tree.ById.Single().Value.Path, Is.EqualTo("demo/a.yaml"));
  }

  [Test]
  public void Tree_ReportsAFileThatIsNotValidYamlAndKeepsTheOthers() {
    using var repository = new SpecRepository();
    repository.Document("demo/a.yaml", "a: [1\n").Document("demo/b.yaml", SpecText.Document());
    var tree = repository.Tree();
    Assert.That(tree.Documents.Select(document => document.Path), Is.EqualTo(new[] { "demo/b.yaml" }));
    Assert.That(tree.Unreadable.Select(DiagnosticRenderer.ToText).Single(), Does.StartWith("demo/a.yaml(2,1): error PLY0047: not valid YAML: "));
  }

  [Test]
  public void Tree_OfARootThatDoesNotExist_Throws() {
    using var repository = new SpecRepository();
    Assert.Throws<DirectoryNotFoundException>(() => SpecTree.Load(Path.Combine(repository.Root, "nowhere"), repository.Root));
  }

  [Test]
  public void Load_FindsTheRepositoryAboveTheRoot() {
    using var repository = new SpecRepository();
    Directory.CreateDirectory(repository.SpecRoot);
    Assert.That(SpecTree.Load(repository.SpecRoot).RepositoryRoot, Is.EqualTo(repository.Root));
  }

  [Test]
  public void Load_WithoutRepository_TakesTheDirectoryAboveTheRoot() {
    using var repository = new SpecRepository();
    Directory.Delete(Path.Combine(repository.Root, ".git"));
    Directory.CreateDirectory(repository.SpecRoot);
    Assume.That(SpecTree.FindRepositoryRoot(repository.Root), Is.Null, "the temporary folder lies inside a repository");
    Assert.That(SpecTree.Load(repository.SpecRoot).RepositoryRoot, Is.EqualTo(repository.Root));
  }

  [Test]
  public void FindRepositoryRoot_AcceptsAGitFileAsAWorktreeHasIt() {
    using var repository = new SpecRepository();
    Directory.Delete(Path.Combine(repository.Root, ".git"));
    repository.Write(".git", "gitdir: elsewhere\n");
    Assert.That(SpecTree.FindRepositoryRoot(Path.Combine(repository.Root, "spec", "schemas")), Is.EqualTo(repository.Root));
  }

  [Test]
  public void FindRepositoryRoot_OfTheCheckout_IsItsRoot()
    => Assert.That(SpecTree.FindRepositoryRoot(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "meta", "valid")), Is.EqualTo(PrototypeFixture.RepositoryRoot));

  [Test]
  public void Resolve_KnownIdIsNullAndUnknownIdIsPLY0043AtTheValue() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document());
    var tree = repository.Tree();
    var document = tree.Documents[0];
    Assert.That(tree.Resolve(document, "/id", "DEMO.widget"), Is.Null);
    Assert.That(DiagnosticRenderer.ToText(tree.Resolve(document, "/title", "DEMO.gadget")!), Is.EqualTo("demo/widget.yaml(2,8): error PLY0043: unknown spec id 'DEMO.gadget'"));
  }

}
