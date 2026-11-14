using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Ci;

namespace Polypiler.Tests.Tooling.Ci;

/// <summary>The numbering rule of the decision records under <c>docs/adr</c>.</summary>
[TestFixture]
public sealed class AdrNumberingTests {

  private static string[] Check(CiTree tree) => [.. AdrNumbering.Check(tree.Root).Select(problem => $"{problem.Code}: {problem.Message}")];

  [Test]
  public void Repository_FollowsTheRule() => Assert.That(AdrNumbering.Check(PrototypeFixture.RepositoryRoot), Is.Empty);

  /// <summary>Test design 18: no folder, and a single record.</summary>
  [Test]
  public void NoFolder_And_OneRecord_AreFine() {
    using var tree = new CiTree();
    Assert.That(Check(tree), Is.Empty);
    tree.Write("docs/adr/0001-one.md", "# ADR 0001 - One\n\nArea: Foundation\n").Write("docs/adr/README.md", "| [0001](0001-one.md) | One |\n");
    Assert.That(Check(tree), Is.Empty);
  }

  /// <summary>The catalogue block takes its area in either spelling.</summary>
  [TestCase("catalogue")]
  [TestCase("catalog")]
  public void CatalogueRecord_InEitherSpelling_IsFine(string area) {
    using var tree = new CiTree();
    tree.Write("docs/adr/0030-one.md", $"# ADR 0030 - One\n\nArea: {area}\n").Write("docs/adr/README.md", "| [0030](0030-one.md) | One |\n");
    Assert.That(Check(tree), Is.Empty);
  }

  [TestCase("docs/adr/1-short.md", "# ADR\n\nArea: foundation\n", "PLY0075: ADR file 1-short.md does not follow <nnnn>-<slug>.md")]
  [TestCase("docs/adr/0002-two.md", "# ADR 0002\n\nThe decision.\n", "PLY0075: ADR file 0002-two.md has no line 'Area: <area>' after its title")]
  [TestCase("docs/adr/0002-two.md", "# ADR 0002\n\nArea: weather\n", "PLY0075: ADR file 0002-two.md names the unknown area 'weather'")]
  [TestCase("docs/adr/0100-ir.md", "# ADR 0100\nArea: ir\n", "PLY0075: ADR file 0100-ir.md is not listed in docs/adr/README.md")]
  public void Record_BreakingTheRule_IsReported(string file, string text, string problem) {
    using var tree = new CiTree();
    tree.Write(file, text);
    Assert.That(Check(tree), Does.Contain(problem));
  }

  [Test]
  public void NoIndex_ListsNothing() {
    using var tree = new CiTree();
    tree.Write("docs/adr/0001-one.md", "# ADR 0001\n\nArea: cli\n");
    Assert.That(Check(tree), Is.EqualTo(new[] { "PLY0075: ADR file 0001-one.md is not listed in docs/adr/README.md" }));
  }

}
