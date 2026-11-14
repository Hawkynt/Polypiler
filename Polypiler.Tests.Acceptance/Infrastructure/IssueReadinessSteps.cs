using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Reqnroll;
using static Polypiler.Tests.Support.DocumentationChecks;

namespace Polypiler.Tests.Acceptance.Infrastructure;

/// <summary>
/// The steps of <c>tests/features/infrastructure/issue-readiness.feature</c>: the one readiness policy
/// of <c>ISSUES.md</c>, its templates and its worked examples, checked as
/// <c>IssueReadinessDocumentationTests</c> checks them.
/// </summary>
[Binding, Scope(Feature = "One canonical issue-readiness standard")]
public sealed class IssueReadinessSteps {

  private const string ExamplePath = "docs/examples/ISSUE-READINESS.md";

  private string _policy = "";
  private string _example = "";
  private string[] _templates = [];
  private string[] _required = [];
  private List<string> _missing = [];

  /// <summary>The policy and the nine templates of the kinds of issue.</summary>
  [Given("^ISSUES.md and the nine kind-specific issue templates$")]
  public void PolicyAndTemplates() {
    this._policy = Read("ISSUES.md");
    this._templates = Directory.GetFiles(Path.Combine(Root, ".github/ISSUE_TEMPLATE"), "*.md");
    Assert.That(this._templates, Has.Length.EqualTo(9));
  }

  /// <summary>Every template links the readiness review of the policy and starts as a draft.</summary>
  [When("^their guidance and navigation are checked$")]
  public void GuidanceIsChecked() {
    foreach (var template in this._templates) {
      var text = File.ReadAllText(template);
      Assert.That(text, Does.Contain("ISSUES.md#applicability-and-readiness-review"), template);
      Assert.That(text, Does.Contain("state:draft"), template);
    }
  }

  /// <summary>The policy has the lifecycle labels and the coverage of lines and branches.</summary>
  [Then("^they use the lifecycle and 95 percent line and branch coverage requirements from ISSUES.md$")]
  public void PolicyHasTheLifecycle() {
    foreach (var label in new[] { "state:draft", "state:ready", "state:blocked", "in-flight" })
      Assert.That(this._policy, Does.Contain(label));
    Assert.That(this._policy, Does.Contain("95 %"));
    Assert.That(this._policy, Does.Contain("conditional branches"));
  }

  /// <summary>No second policy and no generic template exist.</summary>
  [Then("^no second normative readiness policy or generic implementation template is introduced$")]
  public void NoSecondPolicy() {
    Assert.That(File.Exists(Path.Combine(Root, "docs/DEFINITION-OF-READY.md")), Is.False);
    Assert.That(File.Exists(Path.Combine(Root, ".github/ISSUE_TEMPLATE/implementation.md")), Is.False);
  }

  /// <summary>The Ready example, which says it is hypothetical.</summary>
  [Given("^the hypothetical Ready example in docs/examples/ISSUE-READINESS.md$")]
  public void ReadyExample() {
    this._example = Read(ExamplePath).Split("## Blocked example", StringSplitOptions.None)[0];
    Assert.That(this._example, Does.Contain("Hypothetical"));
  }

  /// <summary>The twelve sections the policy requires of every issue.</summary>
  [When("^it is checked against every required issue section$")]
  public void RequiredSections() {
    this._required = [.. Regex.Matches(Read("ISSUES.md"), @"(?m)^### \d+\. (.+)$").Select(match => match.Groups[1].Value)];
    Assert.That(this._required, Has.Length.EqualTo(12));
  }

  /// <summary>The example has every section, every technique of the test design and every box ticked.</summary>
  [Then("^its scope, evidence, scenarios, tests, measurements and ownership are specified$")]
  public void ExampleIsComplete() {
    foreach (var heading in this._required)
      Assert.That(this._example, Does.Contain("### " + heading), heading);
    Assert.That(this._example, Does.Contain("- [x]"));
    Assert.That(this._example, Does.Not.Contain("- [ ]"));
    foreach (var technique in new[] { "Happy path", "Exception", "Boundary", "Equivalence" })
      Assert.That(this._example, Does.Contain(technique));
  }

  /// <summary>The example says that being Ready needs no implementation.</summary>
  [Then("^readiness does not depend on an implementation already existing or passing$")]
  public void ReadinessNeedsNoImplementation() {
    Assert.That(this._example, Does.Contain("N/A — documentation/process only"));
    Assert.That(this._example, Does.Contain("No implementation or passing tests are required to be Ready"));
  }

  /// <summary>The Blocked example and the facts it lacks.</summary>
  [Given("^the Blocked example has no pinned DLL and an unknown PowerBASIC dialect, bitness and calling convention$")]
  public void BlockedExample() {
    this._example = Read(ExamplePath).Split("## Blocked example", StringSplitOptions.None)[1];
    foreach (var fact in new[] { "dialect", "bitness", "calling convention", "DLL" })
      Assert.That(this._example, Does.Contain(fact));
  }

  /// <summary>The example names the label it carries.</summary>
  [When("^its readiness rationale is checked$")]
  public void RationaleIsChecked() => Assert.That(this._example, Does.Contain("state:blocked"));

  /// <summary>The missing facts, who resolves them and what evidence unblocks the issue.</summary>
  [Then("^it identifies those facts and their resolution owners as blockers$")]
  public void IdentifiesTheBlockers() {
    foreach (var fact in new[] { "header", "oracle" })
      Assert.That(this._example, Does.Contain(fact));
    Assert.That(this._example, Does.Contain("Owner"));
    Assert.That(this._example, Does.Contain("Evidence to unblock"));
  }

  /// <summary>No box is ticked, and an unknown is not N/A.</summary>
  [Then("^it does not replace the unknown facts with N/A or claim verified support$")]
  public void ClaimsNothing() {
    Assert.That(this._example, Does.Contain("- [ ]"));
    Assert.That(this._example, Does.Not.Contain("- [x]"));
    Assert.That(this._example, Does.Contain("unknown is not N/A"));
  }

  /// <summary>The policy names what revokes readiness.</summary>
  [Given("^a Ready work order changes its pinned source hash or target calling convention$")]
  public void ChangedWorkOrder() {
    this._policy = Read("ISSUES.md");
    foreach (var fact in new[] { "fixture hash", "ABI", "licence", "acceptance contract" })
      Assert.That(this._policy, Does.Contain(fact));
  }

  /// <summary>The policy has a revocation in its lifecycle.</summary>
  [When("^the documented lifecycle is followed$")]
  public void LifecycleIsFollowed() => Assert.That(this._policy, Does.Contain("revoke"));

  /// <summary>Readiness is reviewed again before the work goes on.</summary>
  [Then("^affected readiness evidence must be reviewed again before implementation continues$")]
  public void ReviewedAgain() => Assert.That(this._policy, Does.Contain("before implementation continues"));

  /// <summary>The issue goes back to Draft or to Blocked, with an owner.</summary>
  [Then("^ambiguity returns it to Draft or an unavailable prerequisite puts it in Blocked$")]
  public void BackToDraftOrBlocked() {
    foreach (var label in new[] { "state:draft", "state:blocked" })
      Assert.That(this._policy, Does.Contain(label));
    Assert.That(this._policy, Does.Contain("Owner"));
  }

  /// <summary>A fixture with a link to a file that does not exist.</summary>
  [Given("^a documentation-check fixture whose relative link targets missing-readiness-example.md$")]
  public void BrokenFixture() => this._example = "[example](missing-readiness-example.md)";

  /// <summary>Checks the fixture's links, no file existing.</summary>
  [When("^the documentation link check runs$")]
  public void LinkCheckRuns() => this._missing = MissingLinks("docs/fixture.md", this._example, _ => false);

  /// <summary>The check names the source and the missing target.</summary>
  [Then("^it reports that source link and missing target as a failure$")]
  public void ReportsTheFailure() => Assert.That(this._missing, Is.EqualTo(new[] { "docs/fixture.md -> docs/missing-readiness-example.md" }));

  /// <summary>The committed documents link only to files and directories that exist, and lead to the example.</summary>
  [Then("^the committed navigation links resolve to tracked files$")]
  public void CommittedLinksResolve() {
    foreach (var path in new[] { "README.md", "docs/README.md", "ISSUES.md", ExamplePath })
      Assert.That(MissingLinks(path, Read(path), target => File.Exists(Path.Combine(Root, target)) || Directory.Exists(Path.Combine(Root, target))), Is.Empty, path);
    Assert.That(Read("README.md"), Does.Contain("ISSUE-READINESS.md"));
    Assert.That(Read("docs/README.md"), Does.Contain("ISSUE-READINESS.md"));
  }

}
