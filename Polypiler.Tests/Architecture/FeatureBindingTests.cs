using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Support;

namespace Polypiler.Tests.Architecture;

/// <summary>
/// The static check of the feature files (PLY0074): every step of every feature file under
/// <c>tests/features</c> has exactly one binding of <c>Polypiler.Tests.Acceptance</c>, every feature
/// has a scenario and every file follows the naming rule; and the check itself on the fixtures of
/// <c>tests/fixtures/infra/bdd</c>, which are its inputs and not members of the suite.
/// </summary>
[TestFixture]
public sealed class FeatureBindingTests {

  private const string Fixtures = "tests/fixtures/infra/bdd/";

  private static readonly string _root = DocumentationChecks.Root;

  private static readonly Lazy<IReadOnlyList<StepBinding>> _bindings = new(() => StepBindingSource.Read(Path.Combine(_root, "Polypiler.Tests.Acceptance")));

  private static IReadOnlyDictionary<string, IReadOnlyList<string>> Areas => BindingCheck.Areas(_root);

  /// <summary>The repository: no problem in any feature file.</summary>
  [Test]
  public void EveryFeatureFile_PassesTheStaticCheck() {
    var problems = BindingCheck.Check(_root, FeatureFiles.Under(_root, "tests/features"), _bindings.Value, Areas);
    Assert.That(problems, Is.Empty, string.Join("\n", problems));
  }

  /// <summary>The bindings are read from the source of the step project, scoped ones once per feature.</summary>
  [Test]
  public void Bindings_AreReadFromTheStepProject() {
    var bindings = _bindings.Value;
    Assert.That(bindings, Does.Contain(new StepBinding(StepKind.When, @"^I run ""(.*)""$", "CommandSteps.Run")));
    Assert.That(bindings, Does.Contain(new StepBinding(StepKind.Then, "^stderr is empty$", "CommandSteps.StderrIsEmpty")));
    const string quoted = "\"((?:[^\"\\\\]|\\\\.)*)\"";
    Assert.That(bindings, Does.Contain(new StepBinding(StepKind.Given, "^the file " + quoted + " with content " + quoted + "$", "FileSteps.FileWithContent")));
    Assert.That(bindings.Where(binding => binding.Method == "CSharpToJavaScriptSteps.Translated").Select(binding => binding.Feature),
      Is.EquivalentTo(new[] { "Translate a small C# algorithm to JavaScript", "Translate wider C# to JavaScript" }));
    Assert.That(bindings.Select(binding => binding.Method), Has.None.StartWith("ScenarioHooks."));
  }

  /// <summary>Test design 14: a step without a binding is named with its file and line.</summary>
  [Test]
  public void UnboundStep_IsReportedWithFileAndLine()
    => Assert.That(BindingCheck.Check(_root, [Fixtures + "unbound.feature"], _bindings.Value),
      Is.EqualTo(new[] { "PLY0074: tests/fixtures/infra/bdd/unbound.feature(4): step 'When the moon is full' has no binding" }));

  /// <summary>Test design 15: a step with two bindings names both methods.</summary>
  [Test]
  public void AmbiguousStep_NamesBothBindings() {
    StepBinding[] doubled = [new(StepKind.Given, "^a doubled step$", "First.Method"), new(StepKind.Any, "a doubled (step)", "Second.Method")];
    Assert.That(BindingCheck.Check(_root, [Fixtures + "ambiguous.feature"], [.. _bindings.Value, .. doubled]),
      Is.EqualTo(new[] { "PLY0074: tests/fixtures/infra/bdd/ambiguous.feature(4): step 'Given a doubled step' has 2 bindings: First.Method, Second.Method" }));
  }

  /// <summary>Test design 13: a feature without a scenario is refused, one with a single scenario is accepted.</summary>
  [Test]
  public void FeatureWithoutAScenario_IsRefused() {
    Assert.That(BindingCheck.Check(_root, [Fixtures + "empty.feature"], _bindings.Value),
      Is.EqualTo(new[] { "PLY0074: tests/fixtures/infra/bdd/empty.feature: feature has no scenario" }));
    Assert.That(BindingCheck.Check(_root, [Fixtures + "Wrong-Name.feature"], _bindings.Value), Is.Empty);
  }

  /// <summary>Test design 16: a path that breaks the naming rule, and one that keeps it.</summary>
  [TestCase(Fixtures + "Wrong-Name.feature", false, TestName = "A fixture is not a feature of the suite")]
  [TestCase("tests/features/infra-bdd.feature", false, TestName = "A file directly in tests/features has no area")]
  [TestCase("tests/features/infra/Wrong-Name.feature", false, TestName = "An id is lower case")]
  [TestCase("tests/features/infra/infra--bdd.feature", false, TestName = "An id has no empty part")]
  [TestCase("tests/features/nosuch/infra-bdd.feature", false, TestName = "An area is a folder of the backlog")]
  [TestCase("tests/features/ir/infra-bdd.feature", false, TestName = "An id has a prefix of its area")]
  [TestCase("tests/features/infra/infra-bdd.txt", false, TestName = "A feature file ends in .feature")]
  [TestCase("tests/features/lang/cs/be-js-x.feature", false, TestName = "A language id names its own language")]
  [TestCase("tests/features/infra/infra-bdd.feature", true)]
  [TestCase("tests/features/infra/spec-ir.feature", true, TestName = "An area has more than one prefix")]
  [TestCase("tests/features/lang/cs/fe-cs-parser.feature", true, TestName = "A language area has the code of its language")]
  [TestCase("tests/features/prototype/recognition.feature", true, TestName = "A file older than the rule keeps its name")]
  public void Path_FollowsTheNamingRule(string path, bool follows)
    => Assert.That(BindingCheck.PathProblem(path, Areas),
      follows ? Is.Null : Is.EqualTo($"PLY0074: {path}: path does not follow tests/features/<area>/<id>.feature"));

  /// <summary>The areas are the folders of the backlog's table, each with its prefixes.</summary>
  [Test]
  public void Areas_AreTheTableOfTheBacklog() {
    var areas = Areas;
    Assert.That(areas.Keys, Is.SupersetOf(new[] { "infra", "ir", "intrinsics", "optimize", "recognize", "lang/<code>", "formats", "retro", "native", "permutation", "interop", "objects", "e2e", "catalog" }));
    Assert.That(areas["infra"], Is.EqualTo(new[] { "infra-", "spec-", "adr-", "cli-", "ver-" }));
    Assert.That(areas["lang/<code>"], Is.EqualTo(new[] { "lang-<c>", "fe-<c>", "be-<c>", "run-<c>" }));
  }

  /// <summary>
  /// Test design 20: a file that is not Gherkin is a problem of the check, with the parser's message
  /// and position. In <c>tests/features</c> the same file already fails the build: Reqnroll's generator
  /// parses every feature file of the step project.
  /// </summary>
  [Test]
  public void SyntaxError_IsReported() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-bdd-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      File.WriteAllText(Path.Combine(directory, "broken.feature"), "Feature: Broken\n\n  Scenario: A\n    Given a step\n  Feature: Again\n");
      var problems = BindingCheck.Check(directory, ["broken.feature"], _bindings.Value);
      Assert.That(problems, Has.Count.EqualTo(1));
      Assert.That(problems[0], Does.StartWith("PLY0074: broken.feature: (5:3): expected:").And.Contain("got 'Feature: Again'"));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>Test design 8: an outline is one test per example row, numbered from 1, with the values in its steps.</summary>
  [Test]
  public void Outline_IsOneCasePerRow() {
    var cases = FeatureFiles.Cases(FeatureFiles.Parse(Path.Combine(_root, "tests/features/infra/infra-bdd.feature")));
    var outline = cases.Where(scenario => scenario.Title == "Titles become stable slugs").ToList();
    Assert.That(outline.Select(scenario => scenario.Row), Is.EqualTo(new[] { 1, 2, 3 }));
    Assert.That(outline[2].Steps.Select(step => (step.Kind, step.Keyword, step.Text)), Is.EqualTo(new[] {
      (StepKind.Given, "Given", "the scenario title \"--opt none  vs. --opt speed!\""),
      (StepKind.When, "When", "its slug is computed"),
      (StepKind.Then, "Then", "the slug is \"opt-none-vs-opt-speed\"")
    }));
    Assert.That(cases.Where(scenario => scenario.Row == 0).Select(scenario => scenario.Values), Has.All.Empty);
    Assert.That(cases, Has.Count.EqualTo(9));
    Assert.That(BindingCheck.CountScenarios(_root, ["tests/features/infra/infra-bdd.feature", "tests/features/infra/infra-modules.feature"]), Is.EqualTo(18));
  }

  /// <summary><c>And</c> and <c>But</c> take the kind of the step before them; a background runs before every scenario, also inside a rule.</summary>
  [Test]
  public void Conjunctions_AndBackgrounds_AreResolved() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-bdd-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      File.WriteAllText(Path.Combine(directory, "rules.feature"), string.Join("\n",
        "Feature: Rules", "", "  Background:", "    Given a start", "",
        "  Scenario: Outside", "    Then it ends", "",
        "  Rule: One", "", "    Background:", "      Given a rule start", "",
        "    Scenario: Inside", "      When it runs", "      And it runs again", "      Then it ends", "      But not twice", ""));
      var cases = FeatureFiles.Cases(FeatureFiles.Parse(Path.Combine(directory, "rules.feature")));
      Assert.That(cases.Select(scenario => scenario.Title), Is.EqualTo(new[] { "Outside", "Inside" }));
      Assert.That(cases[1].Steps.Select(step => (step.Kind, step.Text)), Is.EqualTo(new[] {
        (StepKind.Given, "a start"), (StepKind.Given, "a rule start"), (StepKind.When, "it runs"), (StepKind.When, "it runs again"),
        (StepKind.Then, "it ends"), (StepKind.Then, "not twice")
      }));
      Assert.That(cases[0].Steps.Select(step => step.Text), Is.EqualTo(new[] { "a start", "it ends" }));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>A binding matches only its kind of step, its feature, and the whole text.</summary>
  [Test]
  public void Binding_MatchesItsKindItsFeatureAndTheWholeText() {
    var step = new FeatureStep(StepKind.When, "When", "I run \"x\"", 1);
    Assert.That(new StepBinding(StepKind.When, "^I run \"(.*)\"$", "A.B").Matches("F", step), Is.True);
    Assert.That(new StepBinding(StepKind.Any, "I run \"(.*)\"", "A.B").Matches("F", step), Is.True, "unanchored, still the whole text");
    Assert.That(new StepBinding(StepKind.Then, "^I run \"(.*)\"$", "A.B").Matches("F", step), Is.False, "another kind");
    Assert.That(new StepBinding(StepKind.When, "^I run \"(.*)\"$", "A.B", "G").Matches("F", step), Is.False, "another feature");
    Assert.That(new StepBinding(StepKind.When, "^I run \"(.*)\"$", "A.B", "F").Matches("F", step), Is.True, "its feature");
    Assert.That(new StepBinding(StepKind.When, "I run", "A.B").Matches("F", step), Is.False, "a part of the text");
  }

}
