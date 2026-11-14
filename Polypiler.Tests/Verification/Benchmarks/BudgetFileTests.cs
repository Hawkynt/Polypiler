using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;
using Polypiler.Prototype.Testing;
using static Polypiler.Tests.Verification.Benchmarks.BenchTesting;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>The budget file format, and the product budgets of ADR 0006.</summary>
[TestFixture]
public sealed class BudgetFileTests {

  [Test]
  public void TheTestBudget_AllowsTenPercentOfWallTime() {
    var budgets = BudgetFile.Load(Path.Combine(FixtureFolder, "budgets-test.yaml"));
    Assert.That(budgets.MaxRelativeMad, Is.Null);
    Assert.That(budgets.Budgets, Is.EqualTo(new[] { new Budget("sum-loop", "default", "wallNs", 10) }));
    Assert.That(budgets.Find("sum-loop", "default", "wallNs"), Is.Not.Null);
    Assert.That(budgets.Find("sum-loop", "default", "cpuNs"), Is.Null);
    Assert.That(budgets.Find("sum-loop", "opt-speed", "wallNs"), Is.Null);
    Assert.That(budgets.Find("other", "default", "wallNs"), Is.Null);
  }

  /// <summary>
  /// ADR 0006: the product budgets name a benchmark and a variant that exist, and gate only a metric
  /// that repeats exactly between runs, at no increase; time and memory stay record only.
  /// </summary>
  [Test]
  public void TheProductBudgets_GateOnlyReproducibleMetricsOfExistingVariants_AtNoIncrease() {
    var budgets = BudgetFile.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "bench", "budgets.yaml"));
    Assert.That(budgets.MaxRelativeMad, Is.Null);
    Assert.That(budgets.Budgets, Is.Not.Empty);
    foreach (var budget in budgets.Budgets) {
      var definition = BenchmarkDefinition.Load(Path.Combine(PrototypeFixture.RepositoryRoot, BenchmarkDefinition.RepositoryFolder, budget.Id + BenchmarkDefinition.Extension));
      Assert.That(definition.Variants.Select(variant => variant.Name), Does.Contain(budget.Variant), budget.ToString());
      Assert.That(budget.Metric, Is.AnyOf("sourceBytes", "binaryBytes", "allocatedBytes"), budget.ToString());
      Assert.That(budget.MaxRegressionPercent, Is.Zero, budget.ToString());
    }
  }

  [Test]
  public void BudgetsDifferingInOneKeyField_AreDistinct()
    => Assert.That(BudgetFile.Parse("schemaVersion: 1\nbudgets:\n  - { id: a, variant: b, metric: c, maxRegressionPercent: 1 }\n  - { id: a, variant: x, metric: c, maxRegressionPercent: 1 }\n  - { id: a, variant: b, metric: x, maxRegressionPercent: 1 }\n  - { id: x, variant: b, metric: c, maxRegressionPercent: 1 }\n", "b.yaml").Budgets, Has.Count.EqualTo(4));

  [Test]
  public void MaxRelativeMad_AndFractionalPercentages_AreRead() {
    var budgets = BudgetFile.Parse("schemaVersion: 1\nmaxRelativeMad: 0.1\nbudgets:\n  - { id: a, variant: b, metric: c, maxRegressionPercent: 2.5 }\n", "b.yaml");
    Assert.That((budgets.MaxRelativeMad, budgets.Budgets[0].MaxRegressionPercent, budgets.File), Is.EqualTo(((decimal?)0.1m, 2.5m, "b.yaml")));
  }

  /// <summary>Test design 19: an invalid budget names the field.</summary>
  [TestCase("schemaVersion: 1\nbudgets:\n  - { id: a, variant: b, metric: c, maxRegressionPercent: -1 }\n", "'maxRegressionPercent' must not be negative, not -1")]
  [TestCase("schemaVersion: 1\nbudgets:\n  - { id: a, variant: b, metric: c, maxRegressionPercent: x }\n", "'maxRegressionPercent' must be a number")]
  [TestCase("schemaVersion: 1\nbudgets:\n  - { id: a, variant: b, metric: c }\n", "missing property 'maxRegressionPercent'")]
  [TestCase("schemaVersion: 1\nbudgets:\n  - { id: a, variant: b, metric: c, maxRegressionPercent: '10' }\n", "'maxRegressionPercent' must be a number")]
  [TestCase("schemaVersion: 1\nbudgets:\n  - { id: a, variant: b, metric: c, maxRegressionPercent: 1, owner: x }\n", "budget: unknown property 'owner'")]
  [TestCase("schemaVersion: 2\nbudgets: []\n", "'schemaVersion' must be at least 1 and at most 1, not 2")]
  [TestCase("budgets: []\n", "missing property 'schemaVersion'")]
  [TestCase("schemaVersion: 1\n", "missing property 'budgets'")]
  [TestCase("schemaVersion: 1\nmaxRelativeMad: -0.5\nbudgets: []\n", "'maxRelativeMad' must not be negative, not -0.5")]
  [TestCase("schemaVersion: 1\nbudgets:\n  - { id: a, variant: b, metric: c, maxRegressionPercent: 1 }\n  - { id: a, variant: b, metric: c, maxRegressionPercent: 2 }\n", "the budget of 'a/b' c is given more than once")]
  public void AnInvalidBudget_IsRefused_NamingTheField(string text, string problem)
    => Assert.That(() => BudgetFile.Parse(text, "b.yaml"), Throws.TypeOf<BenchmarkFileException>().With.Message.EqualTo(problem));

  [Test]
  public void AMissingBudgetFile_IsAFileProblem()
    => Assert.That(() => BudgetFile.Load(Path.Combine(FixtureFolder, "none.yaml")), Throws.TypeOf<BenchmarkFileException>().With.Message.StartsWith("cannot be read"));

}
