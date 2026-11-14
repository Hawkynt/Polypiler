using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Architecture;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-modules.feature</c>, through the rule engine
/// <c>DependencyDirectionTests</c> holds to the module issue. The expected projects are the table of
/// the module issue, written from PRD §5.1 by hand — never derived from the rule file or from the
/// references that happen to exist.
/// </summary>
[Binding, Scope(Feature = "Module projects and dependency direction")]
public sealed class ModulesSteps {

  private const string Rules = "tooling/ci/module-rules.yaml";

  /// <summary>The projects the module issue names.</summary>
  private static readonly string[] _named = [
    "Polypiler.Base", "Polypiler.Catalog", "Polypiler.Cli", "Polypiler.Core", "Polypiler.Core.Analysis",
    "Polypiler.Core.Backends.CSharp", "Polypiler.Core.Backends.JavaScript", "Polypiler.Core.BinaryFormats",
    "Polypiler.Core.Frontends.CSharp", "Polypiler.Core.Frontends.JavaScript", "Polypiler.Core.IR", "Polypiler.Core.Legalize",
    "Polypiler.Core.MachineSemantics", "Polypiler.Core.Optimize", "Polypiler.Core.Recognize", "Polypiler.Core.Recovery",
    "Polypiler.Core.Types", "Polypiler.Core.Verification", "Polypiler.Interop", "Polypiler.Permutation", "Polypiler.Tooling"
  ];

  /// <summary>The test projects, which the issue does not count among its modules.</summary>
  private static readonly string[] _tests = ["Polypiler.Tests", "Polypiler.Tests.Acceptance", "Polypiler.Tests.Probe"];

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private readonly List<ProjectFile> _projects = [];
  private ModuleRules _rules = ModuleRules.Load(Path.Combine(_root, Rules));
  private IReadOnlyList<RuleViolation> _violations = [];

  /// <summary>Loads a rule file of the repository.</summary>
  [Given("^the rule file " + StepText.Quoted + "$")]
  public void RuleFile(string path) => this._rules = ModuleRules.Load(Path.Combine(_root, path));

  /// <summary>Reads every project file of the repository.</summary>
  [Given("^every " + StepText.Quoted + " under the repository root$")]
  public void EveryProject(string pattern) {
    Assert.That(pattern, Is.EqualTo("*.csproj"));
    this._projects.AddRange(ProjectFile.Discover(_root));
  }

  /// <summary>Takes a project with one reference.</summary>
  [Given("^a project " + StepText.Quoted + " that references " + StepText.Quoted + "$")]
  public void ProjectReferencing(string from, string to) => this._projects.Add(new ProjectFile(from, [to]));

  /// <summary>Checks the projects against the rules.</summary>
  [When("^the dependency rules are checked$")]
  public void RulesAreChecked() => this._violations = this._rules.Check(this._projects);

  /// <summary>No reference breaks a rule, and every project is covered by the rules.</summary>
  [Then("^there are 0 violations$")]
  public void NoViolations() {
    Assert.That(this._violations.Select(violation => violation.Message), Is.Empty);
    Assert.That(this._projects.Where(project => !this._rules.Covers(project.Name)), Is.Empty);
  }

  /// <summary>The repository has exactly the projects of the issue, and the solution lists each of them.</summary>
  [Then(@"^the solution " + StepText.Quoted + @" lists each of the (\d+) projects named in this issue$")]
  public void SolutionListsTheProjects(string solution, int count) {
    Assert.That(_named, Has.Length.EqualTo(count));
    string[] all = [.. _named.Concat(_tests).Order(System.StringComparer.Ordinal)];
    Assert.That(this._projects.Select(project => project.Name), Is.EqualTo(all));
    var listed = XDocument.Load(Path.Combine(_root, solution)).Descendants("Project").Select(project => Path.GetFileNameWithoutExtension((string)project.Attribute("Path")!));
    Assert.That(listed, Is.EquivalentTo(all));
  }

  /// <summary>Exactly one reference breaks a rule, the rule of the step.</summary>
  [Then("^there is 1 violation with rule " + StepText.Quoted + "$")]
  public void OneViolationWithRule(string rule) {
    Assert.That(this._violations, Has.Count.EqualTo(1));
    Assert.That(this._violations[0].Rule, Is.EqualTo(rule));
    var project = this._projects.Single();
    Assert.That((this._violations[0].From, this._violations[0].To), Is.EqualTo((project.Name, project.References.Single())));
  }

  /// <summary>Exactly one violation.</summary>
  [Then("^there is 1 violation$")]
  public void OneViolation() => Assert.That(this._violations, Has.Count.EqualTo(1));

  /// <summary>The violation's message starts with the text of the step.</summary>
  [Then("^its message starts with " + StepText.Quoted + "$")]
  public void MessageStartsWith(string text) => Assert.That(this._violations.Single().Message, Does.StartWith(text));

  /// <summary>The violation's message is the text of the step: a project the rules do not cover.</summary>
  [Then("^its message is " + StepText.Quoted + "$")]
  public void MessageIs(string text) {
    Assert.That(this._violations.Single().Message, Is.EqualTo(text));
    Assert.That(this._violations.Single().Rule, Is.EqualTo("uncovered"));
  }

  /// <summary>One violation has the rule; the cycle closes a same-layer reference, which is reported first.</summary>
  [Then("^exactly 1 violation has rule " + StepText.Quoted + "$")]
  public void ExactlyOneHasRule(string rule) {
    Assert.That(this._violations.Count(violation => violation.Rule == rule), Is.EqualTo(1));
    Assert.That(this._violations.Select(violation => violation.Rule), Is.EqualTo(new[] { "same layer", rule }));
  }

}
