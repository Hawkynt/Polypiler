using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Tooling.Ci;

namespace Polypiler.Tests.Tooling.Ci;

/// <summary>The workflow file and the category file as <c>ci audit</c> reads them.</summary>
[TestFixture]
public sealed class WorkflowAndCategoriesTests {

  private const string Workflow = """
    name: CI
    on:
      pull_request: {}
    jobs:
      ci:
        uses: owner/repo/.github/workflows/shared.yml@v1
      layer-core:
        runs-on: ubuntu-latest
        steps:
          - uses: actions/checkout@v4
          - name: test
            id: tests
            run: >
              dotnet test Demo.slnx
              --filter "TestCategory=Slow"
          - id: audit
            run: polypiler-tooling ci audit
    """;

  [Test]
  public void Parse_JobsAndSteps_InOrder() {
    var workflow = WorkflowFile.Parse(Workflow, "ci.yml");
    Assert.That(workflow.Jobs.Select(job => job.Id), Is.EqualTo(new[] { "ci", "layer-core" }));
    Assert.That(workflow.Job("ci")!.Steps, Is.Empty);
    var steps = workflow.Job("layer-core")!.Steps;
    Assert.That(steps.Select(step => (step.Id, step.Name)), Is.EqualTo(new (string?, string?)[] { (null, null), ("tests", "test"), ("audit", null) }));
    Assert.That(steps[0].Run, Is.Empty);
    Assert.That(steps[1].Run, Is.EqualTo("dotnet test Demo.slnx --filter \"TestCategory=Slow\"\n"));
    Assert.That((steps[1].IsNamed("test"), steps[1].IsNamed("tests"), steps[1].IsNamed("audit")), Is.EqualTo((true, true, false)));
    Assert.That(workflow.Job("layer-rust"), Is.Null);
  }

  [Test]
  public void Parse_StepsThatAreNoList_AreNone()
    => Assert.That(WorkflowFile.Parse("jobs:\n  a:\n    steps: 1\n  b: 2\n", "ci.yml").Jobs.Select(job => job.Steps.Count), Is.EqualTo(new[] { 0, 0 }));

  /// <summary>Test design 14: a workflow that is missing, not YAML or without jobs is an environment error naming the path.</summary>
  [TestCase(null, "'.github/workflows/ci.yml' cannot be read: the file does not exist")]
  [TestCase("jobs: [", "'.github/workflows/ci.yml' cannot be read: not valid YAML")]
  [TestCase("name: CI\n", "'.github/workflows/ci.yml' cannot be read: the workflow has no mapping 'jobs'")]
  [TestCase("- a\n", "'.github/workflows/ci.yml' cannot be read: the workflow has no mapping 'jobs'")]
  public void Load_Unusable_IsAnEnvironmentError(string? text, string message) {
    using var tree = new CiTree();
    tree.Delete(WorkflowFile.RepositoryPath);
    if (text is not null)
      tree.Write(WorkflowFile.RepositoryPath, text);
    var exception = Assert.Throws<CiEnvironmentException>(() => WorkflowFile.Load(Path.Combine(tree.Root, WorkflowFile.RepositoryPath), WorkflowFile.RepositoryPath))!;
    Assert.That((exception.Diagnostic.Code, exception.Diagnostic.Message), Is.EqualTo(("PLY0004", exception.Diagnostic.Message)));
    Assert.That(exception.Diagnostic.Message, Does.StartWith(message));
  }

  private static (IReadOnlyList<CiCategory> Categories, string[] Problems) Categories(string text) {
    var problems = new List<Diagnostic>();
    var categories = CiCategories.Parse(text, "categories.yaml", problems);
    return (categories, [.. problems.Select(problem => $"{problem.Code}: {problem.Message}")]);
  }

  [Test]
  public void ParseCategories_Valid_KeepsTheOrderAndThePending() {
    var (categories, problems) = Categories("schema: 1\ncategories:\n  - { name: Slow, job: layer-core, step: test }\n  - { name: IrVerifier, job: layer-core, step: ir-verifier, pending: IR-VERIFIER }\n");
    Assert.That(problems, Is.Empty);
    Assert.That(categories, Is.EqualTo(new[] { new CiCategory("Slow", "layer-core", "test", null), new CiCategory("IrVerifier", "layer-core", "ir-verifier", "IR-VERIFIER") }));
  }

  [TestCase("categories: [", "PLY0075: categories.yaml: not valid YAML")]
  [TestCase("schema: 2\ncategories: []\n", "PLY0075: categories.yaml: expected 'schema: 1' and a list 'categories'")]
  [TestCase("schema: 1\n", "PLY0075: categories.yaml: expected 'schema: 1' and a list 'categories'")]
  [TestCase("- 1\n", "PLY0075: categories.yaml: expected 'schema: 1' and a list 'categories'")]
  public void ParseCategories_NoList_IsReported(string text, string message) {
    var (categories, problems) = Categories(text);
    Assert.That((categories.Count, problems.Length), Is.EqualTo((0, 1)));
    Assert.That(problems[0], Does.StartWith(message));
  }

  [TestCase("{ name: Slow, job: layer-core }")]
  [TestCase("{ name: Slow, job: layer-core, step: test, extra: 1 }")]
  [TestCase("{ name: Slow, job: layer-core, step: test, pending: 7 }")]
  [TestCase("{ name: '', job: layer-core, step: test }")]
  [TestCase("Slow")]
  public void ParseCategories_InvalidEntry_IsReportedAndSkipped(string entry) {
    var (categories, problems) = Categories($"schema: 1\ncategories:\n  - {entry}\n  - {{ name: Slow, job: layer-core, step: test }}\n");
    Assert.That(categories.Select(category => category.Name), Is.EqualTo(new[] { "Slow" }));
    Assert.That(problems, Is.EqualTo(new[] { "PLY0075: categories.yaml: /categories/0: expected 'name', 'job' and 'step' as text and an optional 'pending', nothing else" }));
  }

  [Test]
  public void ParseCategories_NameTwice_IsReported() {
    var (categories, problems) = Categories("schema: 1\ncategories:\n  - { name: Slow, job: a, step: b }\n  - { name: Slow, job: c, step: d }\n");
    Assert.That(categories.Single().Job, Is.EqualTo("a"));
    Assert.That(problems, Is.EqualTo(new[] { "PLY0075: categories.yaml: category 'Slow' is listed twice" }));
  }

  [Test]
  public void LoadCategories_Missing_IsAnEnvironmentError() {
    using var tree = new CiTree();
    tree.Delete(CiCategories.RepositoryPath);
    var exception = Assert.Throws<CiEnvironmentException>(() => CiCategories.Load(tree.Root, []))!;
    Assert.That(exception.Diagnostic.Message, Is.EqualTo("'tooling/ci/categories.yaml' cannot be read: the file does not exist"));
  }

}
