using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;
using static Polypiler.Tests.Support.DocumentationChecks;

namespace Polypiler.Tests.Acceptance.Infrastructure;

/// <summary>
/// The steps of <c>tests/features/infrastructure/documentation-navigation.feature</c>: the split PRD,
/// its compatibility anchors and links, and the diagrams of the architecture guides, checked with the
/// code <c>DocumentationStructureTests</c> uses.
/// </summary>
[Binding, Scope(Feature = "Navigate the compiler design and requirements")]
public sealed class DocumentationSteps {

  private string[] _parts = [];
  private int[] _chapters = [];
  private MatchCollection? _anchors;
  private List<string> _documents = [];
  private Dictionary<string, int> _diagrams = [];
  private List<string> _chapterErrors = [];
  private List<string> _linkErrors = [];

  /// <summary>The ten parts of the PRD, each linked from its index.</summary>
  [Given(@"^docs/PRD\.md and its ten linked parts$")]
  public void PrdAndItsParts() {
    this._parts = Parts();
    Assert.That(this._parts, Has.Length.EqualTo(10));
    foreach (var part in this._parts)
      Assert.That(Read("docs/PRD.md"), Does.Contain("prd/" + Path.GetFileName(part)));
  }

  /// <summary>Reads the chapters of every part.</summary>
  [When("^the structure is checked$")]
  public void StructureIsChecked() => this._chapters = [.. this._parts.SelectMany(path => Chapters(File.ReadAllText(path)))];

  /// <summary>Every chapter from 1 to 34 is there once.</summary>
  [Then("^chapters 1 through 34 appear exactly once across the parts$")]
  public void EveryChapterOnce() => Assert.That(ChapterErrors(this._chapters), Is.Empty);

  /// <summary>Every subsection keeps the number of its chapter, in ascending order within it.</summary>
  [Then("^original subsection numbers and requirement text are preserved$")]
  public void SubsectionsKeepTheirNumbers() {
    var errors = new List<string>();
    foreach (var part in this._parts) {
      var chapter = 0;
      var last = 0;
      foreach (Match heading in Regex.Matches(File.ReadAllText(part), @"(?m)^(##|###) (\d+)(?:\.(\d+))?\.? ")) {
        var number = int.Parse(heading.Groups[2].Value, CultureInfo.InvariantCulture);
        if (heading.Groups[1].Value == "##") {
          (chapter, last) = (number, 0);
          continue;
        }

        var subsection = int.Parse(heading.Groups[3].Value, CultureInfo.InvariantCulture);
        if (number != chapter || subsection <= last)
          errors.Add($"{Path.GetFileName(part)}: {heading.Value.Trim()} after chapter {chapter}.{last}");

        last = subsection;
      }
    }

    Assert.That(errors, Is.Empty);
  }

  /// <summary>Reads the compatibility anchors of the index.</summary>
  [Given("^the original PRD heading anchors and the new part navigation$")]
  public void AnchorsAndNavigation() {
    this._anchors = Regex.Matches(Read("docs/PRD.md"), @"<a id=""([^""]+)""></a> \[[^\n]+?\]\((prd/[^)]+)\)");
    Assert.That(this._anchors, Is.Not.Empty);
  }

  /// <summary>Checks the relative links of the index, the parts, the guides and the overview documents.</summary>
  [When("^documentation links are checked$")]
  public void LinksAreChecked() {
    foreach (var path in ArchitecturePaths().Concat(Parts()).Append(Path.Combine(Root, "docs/PRD.md"))
      .Append(Path.Combine(Root, "docs/README.md")).Append(Path.Combine(Root, "docs/quality/VERIFICATION.md"))) {
      var relative = Path.GetRelativePath(Root, path);
      this._linkErrors.AddRange(LinkErrors(relative, File.ReadAllText(path)));
    }
  }

  /// <summary>Every heading of the parts has one anchor of the index, and every anchor names a heading of its part.</summary>
  [Then("^each legacy anchor points to its moved section$")]
  public void AnchorsPointToTheirSections() {
    var headings = Parts().Sum(path => Regex.Matches(File.ReadAllText(path), @"(?m)^#{2,3} ").Count);
    Assert.That(this._anchors, Has.Count.EqualTo(headings));
    Assert.That(this._anchors!.Select(match => match.Groups[1].Value), Is.Unique);
    foreach (Match alias in this._anchors) {
      var target = alias.Groups[2].Value.Split('#');
      Assert.That(target, Has.Length.EqualTo(2));
      Assert.That(Read("docs/" + target[0]), Does.Contain("id=\"" + target[1] + "\""));
    }
  }

  /// <summary>Every part links back to the index, and every relative link resolves.</summary>
  [Then("^index, previous, next and architecture links resolve$")]
  public void LinksResolve() {
    foreach (var part in Parts())
      Assert.That(File.ReadAllText(part), Does.Contain("../PRD.md"));

    Assert.That(this._linkErrors, Is.Empty);
  }

  /// <summary>The README and the seven architecture guides.</summary>
  [Given("^README.md and all seven architecture guides$")]
  public void ReadmeAndGuides() {
    this._documents = [.. ArchitecturePaths()];
    Assert.That(this._documents.Count(path => Path.GetDirectoryName(path)!.EndsWith("architecture", StringComparison.Ordinal)), Is.EqualTo(7));
    Assert.That(this._documents, Does.Contain(Path.Combine(Root, "README.md")));
  }

  /// <summary>Finds the Mermaid blocks of every document.</summary>
  [When("^their Mermaid blocks are rendered$")]
  public void MermaidBlocks() => this._diagrams = this._documents.ToDictionary(path => path, path => Regex.Matches(File.ReadAllText(path), @"(?m)^```mermaid\s*$").Count);

  /// <summary>Every document has a diagram.</summary>
  [Then("^each document has a valid diagram matching its described design$")]
  public void EveryDocumentHasADiagram() => Assert.That(this._diagrams.Where(document => document.Value == 0).Select(document => document.Key), Is.Empty);

  /// <summary>Every document states the status of what it draws.</summary>
  [Then("^the proof, capability and oracle boundaries remain explicit$")]
  public void BoundariesRemainExplicit() {
    foreach (var path in this._documents)
      Assert.That(Regex.IsMatch(File.ReadAllText(path), "status:", RegexOptions.IgnoreCase), Is.True, path);
  }

  /// <summary>Two invalid fixtures: chapters with a duplicate, and a link to a missing file.</summary>
  [Given("^a test fixture with a missing target or duplicate PRD chapter$")]
  public void InvalidFixture() => this._chapters = [1, 4, 4, 34];

  /// <summary>Checks both fixtures.</summary>
  [When("^the documentation checks run$")]
  public void ChecksRun() {
    this._chapterErrors = ChapterErrors(this._chapters);
    this._linkErrors = LinkErrors("docs/source.md", "[missing](missing-part.md)");
  }

  /// <summary>The checks name the duplicate chapter and the missing target.</summary>
  [Then("^the invalid fixture is rejected with the offending target or chapter$")]
  public void FixtureIsRejected() {
    Assert.That(this._chapterErrors, Does.Contain("duplicate chapter 4"));
    Assert.That(this._linkErrors, Is.EqualTo(new[] { "docs/source.md -> missing-part.md" }));
  }

}
