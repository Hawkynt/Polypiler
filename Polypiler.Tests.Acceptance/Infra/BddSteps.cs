using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>The steps of <c>tests/features/infra/infra-bdd.feature</c>: slugs, the static binding check and the scenario count.</summary>
[Binding, Scope(Feature = "Feature files are bound to NUnit")]
public sealed class BddSteps {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private readonly List<StepBinding> _extraBindings = [];
  private string _title = "";
  private string _slug = "";
  private string _feature = "";
  private IReadOnlyList<string> _problems = [];
  private int _scenarios;

  /// <summary>Takes a scenario title.</summary>
  [Given("^the scenario title " + StepText.Quoted + "$")]
  public void ScenarioTitle(string title) => this._title = title;

  /// <summary>Computes the slug of the title.</summary>
  [When("^its slug is computed$")]
  public void SlugIsComputed() => this._slug = Slug.Of(this._title);

  /// <summary>Checks the slug.</summary>
  [Then("^the slug is " + StepText.Quoted + "$")]
  public void SlugIs(string slug) => Assert.That(this._slug, Is.EqualTo(slug));

  /// <summary>Takes a feature file of the repository and checks one of its lines.</summary>
  [Given("^the feature file " + StepText.Quoted + @" whose line (\d+) is " + StepText.Quoted + "$")]
  public void FeatureFileWithLine(string path, int line, string text) {
    this._feature = path;
    Assert.That(File.ReadAllLines(Path.Combine(_root, path))[line - 1].Trim(), Is.EqualTo(text));
  }

  /// <summary>Takes a feature file of the repository and adds two bindings of the test's own that match one text.</summary>
  [Given("^the feature file " + StepText.Quoted + " and two bindings matching " + StepText.Quoted + "$")]
  public void FeatureFileWithTwoBindings(string path, string text) {
    this._feature = path;
    this._extraBindings.Add(new StepBinding(StepKind.Given, "^" + text + "$", "Doubled.First"));
    this._extraBindings.Add(new StepBinding(StepKind.Any, "^a doubled (step)$", "Doubled.Second"));
    Assert.That(File.ReadAllText(Path.Combine(_root, path)), Does.Contain(text));
  }

  /// <summary>Runs the static check over the feature file with every binding of this assembly.</summary>
  [When("^the binding check runs over it$")]
  public void BindingCheckRuns()
    => this._problems = BindingCheck.Check(_root, [this._feature], [.. AssemblyBindings.Of(typeof(BddSteps).Assembly), .. this._extraBindings]);

  /// <summary>Checks that the check reported exactly one problem, with this text.</summary>
  [Then("^it reports " + StepText.Quoted + "$")]
  public void Reports(string problem) => Assert.That(this._problems, Is.EqualTo(new[] { problem }));

  /// <summary>Checks that the check reported exactly one problem, containing a text.</summary>
  [Then("^it reports exactly one problem containing " + StepText.Quoted + "$")]
  public void ReportsOneProblemContaining(string text) {
    Assert.That(this._problems, Has.Count.EqualTo(1));
    Assert.That(this._problems[0], Does.Contain(text).And.Contain("Doubled.First").And.Contain("Doubled.Second"));
  }

  /// <summary>Counts the scenarios and outline rows of every feature file under a folder with the Gherkin parser.</summary>
  [When("^the scenarios in " + StepText.Quoted + " are counted with the Gherkin parser$")]
  public void ScenariosAreCounted(string folder) => this._scenarios = BindingCheck.CountScenarios(_root, FeatureFiles.Under(_root, folder));

  /// <summary>Checks the count against the test cases Reqnroll generated into this assembly.</summary>
  [Then("^the count equals the number of Reqnroll-generated test cases in Polypiler.Tests.Acceptance$")]
  public void CountEqualsGeneratedTestCases()
    => Assert.That(AssemblyBindings.GeneratedTestCases(typeof(BddSteps).Assembly), Is.EqualTo(this._scenarios).And.GreaterThan(0));

  /// <summary>Checks that one feature file stands for at least a number of the counted tests, and that each of them was generated.</summary>
  [Then("^" + StepText.Quoted + @" contributes at least (\d+) of them$")]
  public void FeatureContributes(string file, int minimum) {
    var path = FeatureFiles.Under(_root, "tests/features").Single(candidate => Path.GetFileName(candidate) == file);
    var document = FeatureFiles.Parse(Path.Combine(_root, path));
    var count = FeatureFiles.Cases(document).Count;
    Assert.That(count, Is.GreaterThanOrEqualTo(minimum));
    var generated = AssemblyBindings.GeneratedFeatures(typeof(BddSteps).Assembly).Single(feature => AssemblyBindings.TitleOf(feature) == FeatureFiles.TitleOf(document));
    Assert.That(AssemblyBindings.TestCasesOf(generated), Is.EqualTo(count));
  }

}
