using System.Linq;
using NUnit.Framework;
using Polypiler.Tooling.Ci;

namespace Polypiler.Tests.Tooling.Ci;

/// <summary><c>ci audit</c>: layer files, workflow, lock, categories, tests and decision records against each other.</summary>
[TestFixture]
public sealed class CiAuditTests {

  private static string[] Problems(string root) => [.. CiAudit.Run(root).Problems.Select(problem => $"{problem.Code}: {problem.Message}")];

  private static string[] Problems(CiTree tree) => Problems(tree.Root);

  /// <summary>Test design 1 and 18: a consistent tree, with decision records at the edges of their blocks, has no problem.</summary>
  [TestCase("audit-ok")]
  [TestCase("audit-adr-block-edges")]
  public void ConsistentTree_HasNoProblem(string fixture) {
    var result = CiAudit.Run(CiTesting.Tree(fixture));
    Assert.That(result.Problems, Is.Empty);
    Assert.That(result.Categories, Is.EqualTo(3));
    Assert.That(result.Layers, Is.EqualTo(new[] {
      "layer core: tooling/ci/layers/core.yaml, job layer-core, toolchains dotnet-sdk, 2 acceptance tests",
      "layer rust: tooling/ci/layers/rust.yaml, job layer-rust, toolchains rust, 1 acceptance tests"
    }));
  }

  /// <summary>Test design 3 and 17: one fixture tree per message of the scenarios and of the decision records.</summary>
  [TestCase("audit-missing-job", "PLY0075: layer 'rust' has no job 'layer-rust' in .github/workflows/ci.yml")]
  [TestCase("audit-unknown-toolchain", "PLY0075: layer 'rust' names unknown toolchain 'cargo'")]
  [TestCase("audit-unrun-category", "PLY0076: category 'Performance' is not run by any step: no 'dotnet test' of step 'test' of job 'layer-core' selects TestCategory=Performance")]
  [TestCase("audit-untagged-test", "PLY0076: test 'Demo.Tests.RunsNowhere' has no Layer category")]
  [TestCase("audit-adr-duplicate", "PLY0075: ADR number 0005 is used by 0005-a.md and 0005-b.md")]
  [TestCase("audit-adr-unlisted", "PLY0075: ADR file 0007-x.md is not listed in docs/adr/README.md")]
  [TestCase("audit-adr-missing-file", "PLY0075: docs/adr/README.md lists 0008-y.md, which does not exist")]
  [TestCase("audit-adr-outside-block", "PLY0075: ADR file 0040-z.md lies outside the block 0001–0029 of its area 'foundation'")]
  public void FixtureTree_NamesItsProblem(string fixture, string problem) => Assert.That(Problems(CiTesting.Tree(fixture)), Does.Contain(problem));

  [Test]
  public void JobWithoutLayerFile_IsReported_UnlessItsFileIsInvalid() {
    using var tree = new CiTree();
    tree.Write(".github/workflows/ci.yml", System.IO.File.ReadAllText(System.IO.Path.Combine(tree.Root, ".github/workflows/ci.yml")) + "  layer-go:\n    runs-on: ubuntu-latest\n  layer-bad:\n    runs-on: ubuntu-latest\n");
    tree.Write("tooling/ci/layers/bad.yaml", "schema: 1\n");
    var problems = Problems(tree);
    Assert.That(problems, Does.Contain("PLY0075: job 'layer-go' has no layer file"));
    Assert.That(problems.Where(problem => problem.Contains("layer-bad")), Is.Empty);
    Assert.That(problems, Has.Some.StartWith("PLY0075: tooling/ci/layers/bad.yaml: "));
  }

  /// <summary>Test design 4: a category without tests is accepted while pending and reported otherwise.</summary>
  [Test]
  public void CategoryWithoutTests_IsReportedUnlessPending() {
    using var tree = new CiTree();
    tree.Write("tooling/ci/categories.yaml", "schema: 1\ncategories:\n  - { name: IrVerifier, job: layer-core, step: ir-verifier }\n");
    Assert.That(Problems(tree), Is.EqualTo(new[] { "PLY0076: category 'IrVerifier' has no tests and is not pending" }));
  }

  [TestCase("{ name: Slow, job: layer-go, step: test }", "category 'Slow' is not run by any step: .github/workflows/ci.yml has no job 'layer-go'")]
  [TestCase("{ name: Slow, job: layer-core, step: nowhere }", "category 'Slow' is not run by any step: job 'layer-core' has no step 'nowhere'")]
  [TestCase("{ name: Slow, job: layer-core, step: ir-verifier }", "category 'Slow' is not run by any step: no 'dotnet test' of step 'ir-verifier' of job 'layer-core' selects TestCategory=Slow")]
  [TestCase("{ name: Slow, job: layer-rust, step: test }", null)]
  [TestCase("{ name: Slow, job: lint, step: broken }", "category 'Slow' is not run by any step: step 'broken' of job 'lint': missing ')' at")]
  [TestCase("{ name: Slow, job: lint, step: everything }", null)]
  public void CategoryStep_MustSelectTheCategory(string entry, string? problem) {
    using var tree = new CiTree();
    tree.Write(".github/workflows/ci.yml", System.IO.File.ReadAllText(System.IO.Path.Combine(tree.Root, ".github/workflows/ci.yml"))
      + "  lint:\n    runs-on: ubuntu-latest\n    steps:\n      - name: broken\n        run: dotnet test --filter \"(TestCategory=Slow\"\n      - name: everything\n        run: dotnet test\n");
    tree.Write("tooling/ci/categories.yaml", $"schema: 1\ncategories:\n  - {entry}\n");
    var problems = Problems(tree);
    if (problem is null)
      Assert.That(problems, Is.Empty);
    else
      Assert.That(problems.Single(), Does.StartWith("PLY0076: " + problem));
  }

  /// <summary>Test design 6 and 11: one Layer category, none, two, an unknown layer; the toolchains a test may require.</summary>
  [TestCase("[Acceptance, Layer.rust], toolchains: [rust, dotnet-sdk]", null)]
  [TestCase("[Acceptance, Layer.rust], toolchains: [rust, node]", "test 'Demo.Tests.Probe' requires toolchain 'node', which layer 'rust' does not provide")]
  [TestCase("[Acceptance]", "test 'Demo.Tests.Probe' has no Layer category")]
  [TestCase("[Acceptance, Layer.core, Layer.rust]", "test 'Demo.Tests.Probe' has 2 Layer categories: Layer.core, Layer.rust")]
  [TestCase("[Acceptance, Layer.go]", "test 'Demo.Tests.Probe' names the layer 'go', which has no layer file")]
  [TestCase("[Layer.go, Layer.rust]", null)]
  public void AcceptanceTest_NeedsOneKnownLayerThatProvidesItsToolchains(string categories, string? problem) {
    using var tree = new CiTree();
    tree.Write("test-listing.yaml", System.IO.File.ReadAllText(System.IO.Path.Combine(tree.Root, "test-listing.yaml")) + $"  - {{ name: Demo.Tests.Probe, categories: {categories} }}\n");
    var problems = Problems(tree);
    if (problem is null)
      Assert.That(problems, Is.Empty);
    else
      Assert.That(problems, Is.EqualTo(new[] { "PLY0076: " + problem }));
  }

  /// <summary>Test design 7: zero layer files.</summary>
  [Test]
  public void NoLayerFile_IsReported() {
    using var tree = new CiTree();
    tree.Delete("tooling/ci/layers/core.yaml").Delete("tooling/ci/layers/rust.yaml");
    var problems = Problems(tree);
    Assert.That(problems[0], Is.EqualTo("PLY0075: no layer files found in tooling/ci/layers"));
    Assert.That(problems, Does.Contain("PLY0076: test 'Demo.Tests.Translates' names the layer 'core', which has no layer file"));
  }

  [TestCase("tooling/toolchains.lock", "'tooling/toolchains.lock' cannot be read: ")]
  [TestCase(".github/workflows/ci.yml", "'.github/workflows/ci.yml' cannot be read: the file does not exist")]
  public void MissingFile_IsAnEnvironmentError(string file, string message) {
    using var tree = new CiTree();
    tree.Delete(file);
    var exception = Assert.Throws<CiEnvironmentException>(() => CiAudit.Run(tree.Root))!;
    Assert.That((exception.Diagnostic.Code, exception.Diagnostic.Message), Is.EqualTo(("PLY0004", exception.Diagnostic.Message)));
    Assert.That(exception.Diagnostic.Message, Does.StartWith(message));
  }

}
