using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Polypiler.Tests;

/// <summary>Checks the architecture diagrams and the split requirements navigation.</summary>
[TestFixture]
public sealed class DocumentationStructureTests {
  private static readonly string _root = RepositoryRoot();
  private static readonly string[] _scenarios = [
    "Find every PRD chapter through the index",
    "Follow old and new requirement links",
    "Read rendered architecture diagrams",
    "Reject broken documentation structure"
  ];

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test, Category("Acceptance")]
  public void FeatureScenarios_HaveBindings() {
    var feature = Read("tests/features/infrastructure/documentation-navigation.feature");
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario: (.+)$")
      .Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Checks the split PRD, compatibility links and diagram coverage.</summary>
  /// <param name="scenario">The feature scenario to check.</param>
  [TestCase(0), TestCase(1), TestCase(2), TestCase(3)]
  [Category("Acceptance")]
  public void AcceptanceScenario(int scenario) {
    TestContext.Out.WriteLine(_scenarios[scenario]);
    switch (scenario) {
      case 0:
        var parts = Parts();
        Assert.That(parts, Has.Length.EqualTo(10));
        var chapters = parts.SelectMany(path => Chapters(File.ReadAllText(path))).ToArray();
        Assert.That(ChapterErrors(chapters), Is.Empty);
        foreach (var part in parts)
          Assert.That(Read("docs/PRD.md"), Does.Contain("prd/" + Path.GetFileName(part)));
        break;
      case 1:
        this.CheckNavigation();
        break;
      case 2:
        foreach (var path in ArchitecturePaths()) {
          var text = File.ReadAllText(path);
          Assert.That(Regex.Matches(text, @"(?m)^```mermaid\s*$"), Is.Not.Empty, path);
          Assert.That(Regex.IsMatch(text, "status:", RegexOptions.IgnoreCase), Is.True, path);
        }
        break;
      case 3:
        Assert.That(ChapterErrors([1, 4, 4, 34]), Does.Contain("duplicate chapter 4"));
        Assert.That(LinkErrors("docs/source.md", "[missing](missing-part.md)"),
          Is.EqualTo(new[] { "docs/source.md -> missing-part.md" }));
        break;
    }
  }

  /// <summary>Checks missing, repeated and out-of-range chapter boundaries.</summary>
  /// <param name="omitted">The chapter omitted from a complete fixture.</param>
  [TestCase(1), TestCase(34)]
  public void ChapterCheck_RejectsMissingBoundary(int omitted) {
    var chapters = Enumerable.Range(1, 34).Where(chapter => chapter != omitted);
    Assert.That(ChapterErrors(chapters), Does.Contain("missing chapter " + omitted));
  }

  /// <summary>Checks the complete range and rejects extra chapter numbers.</summary>
  [Test]
  public void ChapterCheck_RejectsEmptyDuplicateAndOutOfRangeFixtures() {
    Assert.That(ChapterErrors([]), Has.Count.EqualTo(34));
    Assert.That(ChapterErrors(Enumerable.Range(1, 34)), Is.Empty);
    Assert.That(ChapterErrors(Enumerable.Range(1, 34).Append(34)), Does.Contain("duplicate chapter 34"));
    Assert.That(ChapterErrors(Enumerable.Range(0, 36)), Does.Contain("unexpected chapter 0"));
    Assert.That(ChapterErrors(Enumerable.Range(0, 36)), Does.Contain("unexpected chapter 35"));
  }

  private void CheckNavigation() {
    var index = Read("docs/PRD.md");
    var aliases = Regex.Matches(index, @"<a id=""([^""]+)""></a> \[[^\n]+?\]\((prd/[^)]+)\)");
    var headings = Parts().Sum(path => Regex.Matches(File.ReadAllText(path), @"(?m)^#{2,3} ").Count);
    Assert.That(aliases, Has.Count.EqualTo(headings));
    Assert.That(aliases.Select(match => match.Groups[1].Value), Is.Unique);
    foreach (Match alias in aliases) {
      var target = alias.Groups[2].Value.Split('#');
      Assert.That(target, Has.Length.EqualTo(2));
      Assert.That(Read("docs/" + target[0]), Does.Contain("id=\"" + target[1] + "\""));
    }
    foreach (var part in Parts())
      Assert.That(File.ReadAllText(part), Does.Contain("../PRD.md"));
    foreach (var path in ArchitecturePaths().Concat(Parts()).Append(Path.Combine(_root, "docs/PRD.md"))
      .Append(Path.Combine(_root, "docs/README.md")).Append(Path.Combine(_root, "docs/quality/VERIFICATION.md"))) {
      var relative = Path.GetRelativePath(_root, path);
      Assert.That(LinkErrors(relative, File.ReadAllText(path)), Is.Empty, relative);
    }
  }

  private static List<string> LinkErrors(string source, string markdown) {
    var failures = new List<string>();
    foreach (Match match in Regex.Matches(markdown, @"\[[^\]\r\n]+\]\(([^)\s]+)\)")) {
      var target = match.Groups[1].Value;
      if (target.StartsWith('#') || Uri.TryCreate(target, UriKind.Absolute, out _))
        continue;
      var file = Uri.UnescapeDataString(target.Split('#')[0]);
      var path = Path.GetFullPath(Path.Combine(_root, Path.GetDirectoryName(source)!, file));
      if (!File.Exists(path) && !Directory.Exists(path))
        failures.Add(source + " -> " + target);
    }
    return failures;
  }

  private static IEnumerable<int> Chapters(string text)
    => Regex.Matches(text, @"(?m)^## (\d+)\. ")
      .Select(match => int.Parse(match.Groups[1].Value));

  private static List<string> ChapterErrors(IEnumerable<int> chapters) {
    var values = chapters.ToArray();
    var errors = Enumerable.Range(1, 34).Except(values).Select(chapter => "missing chapter " + chapter).ToList();
    errors.AddRange(values.GroupBy(chapter => chapter).Where(group => group.Count() > 1)
      .Select(group => "duplicate chapter " + group.Key));
    errors.AddRange(values.Where(chapter => chapter < 1 || chapter > 34)
      .Select(chapter => "unexpected chapter " + chapter));
    return errors;
  }

  private static string[] Parts() => Directory.GetFiles(Path.Combine(_root, "docs/prd"), "*.md").Order().ToArray();

  private static IEnumerable<string> ArchitecturePaths()
    => Directory.GetFiles(Path.Combine(_root, "docs/architecture"), "*.md").Append(Path.Combine(_root, "README.md"));

  private static string Read(string path) => File.ReadAllText(Path.Combine(_root, path));

  private static string RepositoryRoot([CallerFilePath] string source = "")
    => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));
}
