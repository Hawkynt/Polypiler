using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Polypiler.Tests;

/// <summary>Checks the issue-readiness documentation and its acceptance bindings.</summary>
[TestFixture]
public sealed class IssueReadinessDocumentationTests {
  private static readonly string _root = RepositoryRoot();
  private const string ExamplePath = "docs/examples/ISSUE-READINESS.md";
  private const string FeaturePath = "tests/features/infrastructure/issue-readiness.feature";
  private static readonly string[] _scenarios = [
    "Existing templates lead to one policy",
    "The documentation example is complete before implementation",
    "Missing ABI facts cannot be marked irrelevant",
    "Changed requirements revoke readiness",
    "Broken documentation navigation is detected"
  ];

  /// <summary>Ensures each committed feature scenario has a matching NUnit binding.</summary>
  [Test, Category("Acceptance")]
  public void FeatureScenarios_HaveExactlyOneBindingEach() {
    var names = Regex.Matches(Read(FeaturePath), @"(?m)^  Scenario: (.+)$")
      .Select(match => match.Groups[1].Value.Trim()).ToArray();
    Assert.That(names, Is.EqualTo(_scenarios));
  }

  /// <summary>Runs the documentation contract for the selected feature scenario.</summary>
  /// <param name="scenario">Index of the committed scenario.</param>
  [TestCase(0), TestCase(1), TestCase(2), TestCase(3), TestCase(4)]
  [Category("Acceptance")]
  public void AcceptanceScenario(int scenario) {
    TestContext.Out.WriteLine(_scenarios[scenario]);
    switch (scenario) {
      case 0:
        this.CheckCanonicalPolicy();
        break;
      case 1:
        this.CheckReadyExample();
        break;
      case 2:
        this.CheckBlockedExample();
        break;
      case 3:
        this.CheckInvalidation();
        break;
      case 4:
        this.CheckNavigation();
        break;
    }
  }

  private void CheckCanonicalPolicy() {
    var policy = Read("ISSUES.md");
    foreach (var label in new[] { "state:draft", "state:ready", "state:blocked", "in-flight" })
      Assert.That(policy, Does.Contain(label));
    Assert.That(policy, Does.Contain("95 %"));
    Assert.That(policy, Does.Contain("conditional branches"));
    var templates = Directory.GetFiles(Path.Combine(_root, ".github/ISSUE_TEMPLATE"), "*.md");
    Assert.That(templates, Has.Length.EqualTo(9));
    foreach (var template in templates) {
      var text = File.ReadAllText(template);
      Assert.That(text, Does.Contain("ISSUES.md#applicability-and-readiness-review"), template);
      Assert.That(text, Does.Contain("state:draft"), template);
    }
    Assert.That(File.Exists(Path.Combine(_root, "docs/DEFINITION-OF-READY.md")), Is.False);
    Assert.That(File.Exists(Path.Combine(_root, ".github/ISSUE_TEMPLATE/implementation.md")), Is.False);
  }

  private void CheckReadyExample() {
    var example = Read(ExamplePath).Split("## Blocked example", StringSplitOptions.None)[0];
    var required = Regex.Matches(Read("ISSUES.md"), @"(?m)^### \d+\. (.+)$")
      .Select(match => match.Groups[1].Value).ToArray();
    Assert.That(required, Has.Length.EqualTo(12));
    foreach (var heading in required)
      Assert.That(example, Does.Contain("### " + heading), heading);
    Assert.That(example, Does.Contain("Hypothetical"));
    Assert.That(example, Does.Contain("N/A — documentation/process only"));
    Assert.That(example, Does.Contain("- [x]"));
    Assert.That(example, Does.Not.Contain("- [ ]"));
    foreach (var technique in new[] { "Happy path", "Exception", "Boundary", "Equivalence" })
      Assert.That(example, Does.Contain(technique));
    Assert.That(example, Does.Contain("No implementation or passing tests are required to be Ready"));
  }

  private void CheckBlockedExample() {
    var example = Read(ExamplePath).Split("## Blocked example", StringSplitOptions.None)[1];
    foreach (var fact in new[] { "dialect", "bitness", "calling convention", "DLL", "header", "oracle" })
      Assert.That(example, Does.Contain(fact));
    Assert.That(example, Does.Contain("Owner"));
    Assert.That(example, Does.Contain("Evidence to unblock"));
    Assert.That(example, Does.Contain("state:blocked"));
    Assert.That(example, Does.Contain("- [ ]"));
    Assert.That(example, Does.Not.Contain("- [x]"));
    Assert.That(example, Does.Contain("unknown is not N/A"));
  }

  private void CheckInvalidation() {
    var policy = Read("ISSUES.md");
    foreach (var fact in new[] { "fixture hash", "ABI", "licence", "acceptance contract", "state:draft", "state:blocked" })
      Assert.That(policy, Does.Contain(fact));
    Assert.That(policy, Does.Contain("revoke"));
    Assert.That(policy, Does.Contain("before implementation continues"));
    Assert.That(policy, Does.Contain("Owner"));
  }

  private void CheckNavigation() {
    var missing = MissingLinks("docs/fixture.md", "[example](missing-readiness-example.md)", _ => false);
    Assert.That(missing, Is.EqualTo(new[] { "docs/fixture.md -> docs/missing-readiness-example.md" }));
    foreach (var path in new[] { "README.md", "docs/README.md", "ISSUES.md", ExamplePath })
      Assert.That(MissingLinks(path, Read(path), target => File.Exists(Path.Combine(_root, target)) || Directory.Exists(Path.Combine(_root, target))), Is.Empty, path);
    Assert.That(Read("README.md"), Does.Contain("ISSUE-READINESS.md"));
    Assert.That(Read("docs/README.md"), Does.Contain("ISSUE-READINESS.md"));
  }

  /// <summary>Exercises the link check with valid, missing and non-file destinations.</summary>
  /// <param name="markdown">A small inline-link fixture.</param>
  /// <param name="expected">Number of missing destinations.</param>
  [TestCase("plain text", 0)]
  [TestCase("[one](present.md)", 0)]
  [TestCase("[one](missing.md)", 1)]
  [TestCase("[one](missing.md) [two](absent.md)", 2)]
  [TestCase("[anchor](#section) [web](https://example.com) [mail](mailto:example@example.com)", 0)]
  [TestCase("[anchor](present.md#section)", 0)]
  [TestCase("[parent](../present.md)", 0)]
  [TestCase("[encoded](present%20file.md)", 0)]
  public void LinkCheck_CoversEmptyValidMissingAndExternalTargets(string markdown, int expected) {
    var failures = MissingLinks("docs/source.md", markdown,
      target => Path.GetFileName(target).StartsWith("present", StringComparison.Ordinal));
    Assert.That(failures, Has.Count.EqualTo(expected));
  }

  private static List<string> MissingLinks(string source, string markdown, Func<string, bool> exists) {
    var missing = new List<string>();
    foreach (Match match in Regex.Matches(markdown, @"\[[^\]\r\n]+\]\(([^)\s]+)\)")) {
      var target = match.Groups[1].Value;
      if (target.StartsWith('#') || Uri.TryCreate(target, UriKind.Absolute, out _))
        continue;
      target = Uri.UnescapeDataString(target.Split('#')[0]);
      var absolute = Path.GetFullPath(Path.Combine(_root, Path.GetDirectoryName(source)!, target));
      var relative = Path.GetRelativePath(_root, absolute).Replace('\\', '/');
      if (!exists(relative))
        missing.Add(source + " -> " + relative);
    }
    return missing;
  }

  private static string Read(string path) => File.ReadAllText(Path.Combine(_root, path));

  private static string RepositoryRoot([CallerFilePath] string source = "")
    => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));
}
