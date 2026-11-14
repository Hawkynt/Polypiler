using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;

namespace Polypiler.Tests.Architecture;

/// <summary>
/// Holds the repository to <c>tooling/ci/module-rules.yaml</c> and checks the rule engine itself. The
/// expected layers are the table of the module issue, written from PRD §5.1 by hand — never derived
/// from the rule file or from the references that happen to exist.
/// </summary>
[TestFixture]
public sealed class DependencyDirectionTests {
  private const string RuleFile = "tooling/ci/module-rules.yaml";
  private const string FixtureFolder = "tests/fixtures/infra/modules";
  private static readonly string _root = RepositoryRoot();

  private static readonly string[] _scenarios = [
    "The repository satisfies its own module rules",
    "A reference from a lower to a higher layer is refused",
    "The documented same-layer exception is allowed",
    "A project the rules do not know is refused",
    "A reference cycle is reported once"
  ];

  private static readonly string[] _projects = [
    "Polypiler.Base",
    "Polypiler.Benchmarks",
    "Polypiler.Catalog",
    "Polypiler.Cli",
    "Polypiler.Core",
    "Polypiler.Core.Analysis",
    "Polypiler.Core.Backends.CSharp",
    "Polypiler.Core.Backends.JavaScript",
    "Polypiler.Core.BinaryFormats",
    "Polypiler.Core.Frontends.CSharp",
    "Polypiler.Core.Frontends.JavaScript",
    "Polypiler.Core.IR",
    "Polypiler.Core.Legalize",
    "Polypiler.Core.MachineSemantics",
    "Polypiler.Core.Optimize",
    "Polypiler.Core.Recognize",
    "Polypiler.Core.Recovery",
    "Polypiler.Core.Types",
    "Polypiler.Core.Verification",
    "Polypiler.Interop",
    "Polypiler.Permutation",
    "Polypiler.Tests",
    "Polypiler.Tests.Acceptance",
    "Polypiler.Tests.Probe",
    "Polypiler.Tooling"
  ];

  /// <summary>Ensures the feature and its NUnit bindings stay in agreement.</summary>
  [Test, Category("Acceptance")]
  public void FeatureScenarios_HaveBindings() {
    var feature = File.ReadAllText(Path.Combine(_root, "tests/features/infra/infra-modules.feature"));
    var scenarios = Regex.Matches(feature, @"(?m)^  Scenario(?: Outline)?: (.+)$")
      .Select(match => match.Groups[1].Value.Trim());
    Assert.That(scenarios, Is.EqualTo(_scenarios));
  }

  /// <summary>Scenario: The repository satisfies its own module rules (test design 1).</summary>
  [Test, Category("Acceptance")]
  public void Repository_SatisfiesItsOwnModuleRules() {
    var rules = Rules();
    var projects = ProjectFile.Discover(_root);
    var solution = XDocument.Load(Path.Combine(_root, "Polypiler.slnx")).Descendants("Project")
      .Select(project => Path.GetFileNameWithoutExtension((string)project.Attribute("Path")!));

    Assert.That(rules.Check(projects).Select(violation => violation.Message), Is.Empty);
    Assert.That(projects.Select(project => project.Name), Is.EqualTo(_projects));
    Assert.That(projects.Where(project => !rules.Covers(project.Name)), Is.Empty);
    Assert.That(solution, Is.EquivalentTo(_projects));
  }

  /// <summary>Scenario outline: A reference from a lower to a higher layer is refused (test design 3, 4, 6, 7).</summary>
  [TestCase("Polypiler.Core.IR", "Polypiler.Core.Analysis", "layer 2 -> 3")]
  [TestCase("Polypiler.Core.Types", "Polypiler.Core.IR", "layer 1 -> 2")]
  [TestCase("Polypiler.Core.Backends.Rust", "Polypiler.Core.Frontends.CSharp", "restricted")]
  [TestCase("Polypiler.Core.Recognize", "Polypiler.Core.Recovery", "same layer")]
  [TestCase("Polypiler.Core.Optimize", "Polypiler.Base", "standalone")]
  [Category("Acceptance")]
  public void WrongWayReference_IsRefused(string from, string to, string rule) {
    var violation = Check(Project(from, to)).Single();
    Assert.That(violation.Rule, Is.EqualTo(rule));
    Assert.That(violation.Message, Does.StartWith($"PLY0038: {from} must not reference {to}"));
    Assert.That((violation.From, violation.To), Is.EqualTo((from, to)));
  }

  /// <summary>Scenario: The documented same-layer exception is allowed (test design 5).</summary>
  [TestCase("Polypiler.Core.Recovery", "Polypiler.Core.Recognize")]
  [TestCase("Polypiler.Core.Legalize", "Polypiler.Core.Optimize")]
  [TestCase("Polypiler.Cli", "Polypiler.Tooling")]
  [Category("Acceptance")]
  public void SameLayerException_IsAllowed(string from, string to)
    => Assert.That(Check(Project(from, to)), Is.Empty);

  /// <summary>Scenario: A project the rules do not know is refused (test design 14).</summary>
  [Test, Category("Acceptance")]
  public void UnknownProject_IsRefused() {
    var violation = Check(Project("Polypiler.Experimental", "Polypiler.Core")).Single();
    Assert.That(violation.Message,
      Is.EqualTo("PLY0038: project Polypiler.Experimental is not covered by tooling/ci/module-rules.yaml"));
    Assert.That(violation.Rule, Is.EqualTo("uncovered"));
  }

  /// <summary>Scenario: A reference cycle is reported once.</summary>
  [Test, Category("Acceptance")]
  public void ReferenceCycle_IsReportedOnce() {
    var violations = Check(
      Project("Polypiler.Core.Optimize", "Polypiler.Core.Legalize"),
      Project("Polypiler.Core.Legalize", "Polypiler.Core.Optimize"));
    Assert.That(violations.Count(violation => violation.Rule == "cycle"), Is.EqualTo(1));
    Assert.That(violations.Select(violation => violation.Rule), Is.EqualTo(new[] { "same layer", "cycle" }));
  }

  /// <summary>The exact wording of the documented diagnostics.</summary>
  [TestCase("Polypiler.Core.Backends.Rust", "Polypiler.Core.Frontends.CSharp",
    "PLY0038: Polypiler.Core.Backends.Rust must not reference Polypiler.Core.Frontends.CSharp (rule: restricted)")]
  [TestCase("Polypiler.Core.IR", "Polypiler.Core.Analysis",
    "PLY0038: Polypiler.Core.IR must not reference Polypiler.Core.Analysis (rule: layer 2 -> 3)")]
  public void Violation_CarriesTheRegisteredText(string from, string to, string message)
    => Assert.That(Check(Project(from, to)).Select(violation => violation.Message), Is.EqualTo(new[] { message }));

  /// <summary>The layer table of the module issue, name by name (test design 8 for the patterns).</summary>
  [TestCase("Polypiler.Core", 0)]
  [TestCase("Polypiler.Core.Types", 1)]
  [TestCase("Polypiler.Core.IR", 2)]
  [TestCase("Polypiler.Core.Analysis", 3)]
  [TestCase("Polypiler.Core.Recognize", 4)]
  [TestCase("Polypiler.Core.Recovery", 4)]
  [TestCase("Polypiler.Core.MachineSemantics", 4)]
  [TestCase("Polypiler.Core.BinaryFormats", 4)]
  [TestCase("Polypiler.Permutation", 4)]
  [TestCase("Polypiler.Interop", 4)]
  [TestCase("Polypiler.Core.Optimize", 5)]
  [TestCase("Polypiler.Core.Legalize", 5)]
  [TestCase("Polypiler.Core.Frontends.CSharp", 6)]
  [TestCase("Polypiler.Core.Frontends.Kotlin", 6)]
  [TestCase("Polypiler.Core.Backends.CSharp", 6)]
  [TestCase("Polypiler.Core.Backends.Rust", 6)]
  [TestCase("Polypiler.Core.Verification", 6)]
  [TestCase("Polypiler.Catalog", 7)]
  [TestCase("Polypiler.Adapters.Ncnn", 7)]
  [TestCase("Polypiler.Cli", 8)]
  [TestCase("Polypiler.Tooling", 8)]
  [TestCase("Polypiler.Tests", 9)]
  [TestCase("Polypiler.Tests.Acceptance", 9)]
  public void LayerOf_FollowsTheLayerTable(string project, int layer) {
    Assert.That(Rules().LayerOf(project), Is.EqualTo(layer));
    Assert.That(Rules().Covers(project), Is.True);
  }

  /// <summary>A standalone project is covered without a layer; an unknown one is neither.</summary>
  [TestCase("Polypiler.Base", true)]
  [TestCase("Polypiler.Experimental", false)]
  [TestCase("Polypiler.Core.Frontends", false, TestName = "A pattern needs something after its prefix")]
  [TestCase("Polypiler.Core.Frontends.", false, TestName = "A pattern's star is not empty")]
  [TestCase("Polypiler.Adapters", false, TestName = "The adapter pattern does not cover the bare prefix")]
  [TestCase("polypiler.core", false, TestName = "Project names are case-sensitive")]
  [TestCase("Polypiler.Core.Typesx", false, TestName = "A name is not a prefix")]
  [TestCase("", false, TestName = "The empty name is not covered")]
  public void LayerOf_HasNoLayerOutsideTheTable(string project, bool covered) {
    Assert.That(Rules().LayerOf(project), Is.Null);
    Assert.That(Rules().Covers(project), Is.EqualTo(covered));
  }

  /// <summary>References that point downward, however far (test design 2, 11, 12).</summary>
  [TestCase("Polypiler.Core.Optimize", "Polypiler.Core.Types", TestName = "A reference may skip layers")]
  [TestCase("Polypiler.Core.Analysis", "Polypiler.Core.IR", TestName = "Adjacent layers, downward")]
  [TestCase("Polypiler.Tests", "Polypiler.Core", TestName = "Highest layer to lowest")]
  [TestCase("Polypiler.Cli", "Polypiler.Core.Frontends.CSharp", TestName = "Layer 8 may reference a frontend")]
  [TestCase("Polypiler.Tests", "Polypiler.Core.Backends.CSharp", TestName = "Layer 9 may reference a backend")]
  [TestCase("Polypiler.Catalog", "Polypiler.Base", TestName = "Layer 7 may reference the standalone project")]
  [TestCase("Polypiler.Adapters.Ncnn", "Polypiler.Base", TestName = "A pattern-matched layer 7 project may too")]
  [TestCase("Polypiler.Cli", "Polypiler.Base", TestName = "Layer 8 may reference the standalone project")]
  [TestCase("Polypiler.Tests", "Polypiler.Base", TestName = "Layer 9 may reference the standalone project")]
  [TestCase("Polypiler.Tests", "Polypiler.Cli", TestName = "Layer 9 may reference layer 8")]
  public void DownwardReference_IsAllowed(string from, string to)
    => Assert.That(Check(Project(from, to)), Is.Empty);

  /// <summary>A project with no reference breaks nothing (test design 11).</summary>
  [TestCase("Polypiler.Core")]
  [TestCase("Polypiler.Base")]
  public void ProjectWithoutReferences_IsAllowed(string project)
    => Assert.That(Check(Project(project)), Is.Empty);

  /// <summary>No projects, no violations.</summary>
  [Test]
  public void NoProjects_HaveNoViolations() => Assert.That(Check(), Is.Empty);

  /// <summary>References the rules forbid, one per class and boundary (test design 6, 7, 9, 10).</summary>
  [TestCase("Polypiler.Core.Frontends.CSharp", "Polypiler.Core.Backends.CSharp", "restricted")]
  [TestCase("Polypiler.Core.Verification", "Polypiler.Core.Frontends.CSharp", "restricted")]
  [TestCase("Polypiler.Catalog", "Polypiler.Core.Frontends.CSharp", "restricted")]
  [TestCase("Polypiler.Core.IR", "Polypiler.Core.Backends.CSharp", "restricted")]
  [TestCase("Polypiler.Base", "Polypiler.Core", "standalone")]
  [TestCase("Polypiler.Core.Verification", "Polypiler.Base", "standalone")]
  [TestCase("Polypiler.Core", "Polypiler.Base", "standalone")]
  [TestCase("Polypiler.Core", "Polypiler.Core.Types", "layer 0 -> 1")]
  [TestCase("Polypiler.Core.Verification", "Polypiler.Catalog", "layer 6 -> 7")]
  [TestCase("Polypiler.Catalog", "Polypiler.Cli", "layer 7 -> 8")]
  [TestCase("Polypiler.Cli", "Polypiler.Tests", "layer 8 -> 9")]
  [TestCase("Polypiler.Core", "Polypiler.Tests.Acceptance", "layer 0 -> 9")]
  [TestCase("Polypiler.Tests.Acceptance", "Polypiler.Tests", "same layer")]
  [TestCase("Polypiler.Tooling", "Polypiler.Cli", "same layer")]
  [TestCase("Polypiler.Core.Recognize", "Polypiler.Core.Recovery", "same layer")]
  [TestCase("Polypiler.Core.Optimize", "Polypiler.Core.Legalize", "same layer")]
  public void ForbiddenReference_IsRefused(string from, string to, string rule) {
    var violations = Check(Project(from, to));
    Assert.That(violations.Select(violation => violation.Rule), Is.EqualTo(new[] { rule }));
    Assert.That(violations[0].Message, Is.EqualTo($"PLY0038: {from} must not reference {to} (rule: {rule})"));
  }

  /// <summary>A project referencing itself is a cycle and nothing else (test design 13).</summary>
  [Test]
  public void SelfReference_IsACycle() {
    var violation = Check(Project("Polypiler.Core.IR", "Polypiler.Core.IR")).Single();
    Assert.That(violation.Rule, Is.EqualTo("cycle"));
    Assert.That(violation.Message, Is.EqualTo("PLY0038: reference cycle Polypiler.Core.IR -> Polypiler.Core.IR"));
  }

  /// <summary>An unknown project is reported once, whether it references or is referenced.</summary>
  [Test]
  public void UnknownProject_IsReportedOnce() {
    var violations = Check(
      Project("Polypiler.Core.IR", "Polypiler.Experimental"),
      Project("Polypiler.Experimental", "Polypiler.Core"),
      Project("Polypiler.Tests", "Polypiler.Experimental"));
    Assert.That(violations.Select(violation => (violation.From, violation.Rule)),
      Is.EqualTo(new[] { ("Polypiler.Experimental", "uncovered") }));
  }

  /// <summary>Every broken reference of a project is reported, a repeated one once.</summary>
  [Test]
  public void Violations_AreReportedPerReferenceInOrder() {
    var violations = Check(
      Project("Polypiler.Core.Types", "Polypiler.Core.IR", "Polypiler.Core", "Polypiler.Core.Analysis", "Polypiler.Core.IR"),
      Project("Polypiler.Core", "Polypiler.Core.Types"));
    Assert.That(violations.Select(violation => (violation.From, violation.To, violation.Rule)), Is.EqualTo(new[] {
      ("Polypiler.Core", "Polypiler.Core.Types", "layer 0 -> 1"),
      ("Polypiler.Core.Types", "Polypiler.Core.Analysis", "layer 1 -> 3"),
      ("Polypiler.Core.Types", "Polypiler.Core.IR", "layer 1 -> 2"),
      ("Polypiler.Core", "Polypiler.Core.Types", "cycle")
    }));
  }

  /// <summary>The cycle fixtures: two and three projects, one cycle each (test design 15).</summary>
  [TestCase("cycle2", "PLY0038: reference cycle Polypiler.A -> Polypiler.B -> Polypiler.A")]
  [TestCase("cycle3", "PLY0038: reference cycle Polypiler.A -> Polypiler.B -> Polypiler.C -> Polypiler.A")]
  public void CycleFixture_HasOneCycle(string fixture, string message) {
    var violations = Fixture("rules-cycle.yaml").Check(ProjectFile.Discover(FixturePath(fixture)));
    Assert.That(violations.Select(violation => (violation.Rule, violation.Message)),
      Is.EqualTo(new[] { ("cycle", message) }));
  }

  /// <summary>Two cycles that share a project are two violations; an acyclic diamond is none.</summary>
  [Test]
  public void Cycles_AreFoundPerBackEdge() {
    var rules = Fixture("rules-cycle.yaml");
    var diamond = rules.Check([
      Project("Polypiler.A", "Polypiler.B", "Polypiler.C"), Project("Polypiler.B", "Polypiler.D"),
      Project("Polypiler.C", "Polypiler.D"), Project("Polypiler.D")
    ]);
    var shared = rules.Check([
      Project("Polypiler.A", "Polypiler.B", "Polypiler.C"), Project("Polypiler.B", "Polypiler.A"),
      Project("Polypiler.C", "Polypiler.A")
    ]);
    Assert.That(diamond.Where(violation => violation.Rule == "cycle"), Is.Empty);
    Assert.That(shared.Where(violation => violation.Rule == "cycle").Select(violation => violation.Message), Is.EqualTo(new[] {
      "PLY0038: reference cycle Polypiler.A -> Polypiler.B -> Polypiler.A",
      "PLY0038: reference cycle Polypiler.A -> Polypiler.C -> Polypiler.A"
    }));
  }

  /// <summary>A project file read from disk is checked like one built in memory.</summary>
  [Test]
  public void UpwardFixture_IsRefused() {
    var projects = ProjectFile.Discover(FixturePath("upward"));
    Assert.That(projects.Select(project => project.Name),
      Is.EqualTo(new[] { "Polypiler.Core.Analysis", "Polypiler.Core.IR" }));
    Assert.That(projects[0].References, Is.Empty);
    Assert.That(Rules().Check(projects).Select(violation => violation.Message), Is.EqualTo(new[] {
      "PLY0038: Polypiler.Core.IR must not reference Polypiler.Core.Analysis (rule: layer 2 -> 3)"
    }));
  }

  /// <summary>A reference to a project file that does not exist (test design 19).</summary>
  [Test]
  public void MissingReferenceTarget_IsRefused() {
    var project = ProjectFile.Read(Path.Combine(FixturePath("missing"), "Polypiler.Core.Analysis.csproj"));
    Assert.That(project.References, Is.Empty);
    Assert.That(project.MissingReferences, Is.EqualTo(new[] { @"..\Missing\Missing.csproj" }));

    var violation = Rules().Check([project]).Single();
    Assert.That(violation.Rule, Is.EqualTo("missing"));
    Assert.That(violation.Message, Is.EqualTo(
      @"PLY0038: Polypiler.Core.Analysis references ..\Missing\Missing.csproj: reference target not found"));
  }

  /// <summary>Project references are read with either path separator; fixtures and build output are not searched.</summary>
  [Test]
  public void Discover_ReadsTheRepositoryProjects() {
    var projects = ProjectFile.Discover(_root).ToDictionary(project => project.Name);
    Assert.That(projects["Polypiler.Core"].References, Is.Empty);
    Assert.That(projects["Polypiler.Tests.Acceptance"].References,
      Is.EquivalentTo(new[] { "Polypiler.Cli", "Polypiler.Core.Backends.CSharp", "Polypiler.Core.Frontends.CSharp", "Polypiler.Core.Frontends.JavaScript", "Polypiler.Core.IR", "Polypiler.Core.Optimize", "Polypiler.Core.Verification", "Polypiler.Permutation", "Polypiler.Tooling", "Polypiler.Tests.Probe" }));
    Assert.That(projects["Polypiler.Tests"].References,
      Is.EquivalentTo(_projects.Except(["Polypiler.Tests", "Polypiler.Tests.Acceptance", "Polypiler.Benchmarks"])));
    Assert.That(projects.Values.SelectMany(project => project.MissingReferences), Is.Empty);
    Assert.That(projects.Keys, Has.None.EqualTo("Polypiler.A"));
  }

  /// <summary>A rule file that does not exist (test design 16).</summary>
  [Test]
  public void MissingRuleFile_IsReported() {
    var path = Path.Combine(FixturePath("rules-absent.yaml"));
    var exception = Assert.Throws<FileNotFoundException>(() => ModuleRules.Load(path))!;
    Assert.That(exception.Message, Does.Contain(path));
    Assert.That(exception.FileName, Is.EqualTo(path));
  }

  /// <summary>A rule file that is not a schema 1 rule file names the file, the line and the key (test design 17).</summary>
  [TestCase("rules-bad-schema.yaml", 1, "key 'schema': unsupported value '2' (supported: 1)")]
  [TestCase("rules-unknown-key.yaml", 4, "unknown key 'layers'")]
  [TestCase("rules-unknown-entry-key.yaml", 4, "unknown key 'tier'")]
  [TestCase("rules-missing-schema.yaml", 1, "missing key 'schema'")]
  [TestCase("rules-empty.yaml", 1, "missing key 'schema'")]
  [TestCase("rules-missing-layer.yaml", 3, "missing key 'layer'")]
  [TestCase("rules-negative-layer.yaml", 3, "'-1' is not a layer (a non-negative whole number)")]
  [TestCase("rules-text-layer.yaml", 3, "'low' is not a layer (a non-negative whole number)")]
  [TestCase("rules-name-and-pattern.yaml", 3, "expected exactly one of the keys 'name' and 'pattern'")]
  [TestCase("rules-no-selector.yaml", 3, "expected exactly one of the keys 'name' and 'pattern'")]
  [TestCase("rules-projects-not-a-list.yaml", 3, "key 'projects': expected a list")]
  [TestCase("rules-standalone-not-a-name.yaml", 3, "expected a single value")]
  [TestCase("rules-not-a-mapping.yaml", 1, "expected a mapping")]
  public void MalformedRuleFile_IsRejected(string fixture, int line, string message) {
    var exception = Assert.Throws<InvalidDataException>(() => Fixture(fixture))!;
    Assert.That(exception.Message, Is.EqualTo($"{FixtureFolder}/{fixture}:{line}: {message}"));
  }

  /// <summary>Text that is not YAML at all is reported with its position.</summary>
  [Test]
  public void UnparsableRuleFile_IsRejected() {
    var exception = Assert.Throws<InvalidDataException>(() => Fixture("rules-syntax-error.yaml"))!;
    Assert.That(exception.Message, Does.StartWith($"{FixtureFolder}/rules-syntax-error.yaml:"));
    Assert.That(exception.InnerException, Is.Not.Null);
  }

  /// <summary>A project matched by two rules (test design 18).</summary>
  [TestCase("Polypiler.Core.Frontends.CSharp", "Polypiler.Core.Frontends.CSharp, Polypiler.Core.Frontends.*")]
  [TestCase("Polypiler.Base", "Polypiler.Base, standalone")]
  public void ProjectMatchedByTwoRules_IsAmbiguous(string project, string matches) {
    var rules = Fixture("rules-ambiguous.yaml");
    var exception = Assert.Throws<InvalidDataException>(() => rules.Check([Project(project)]))!;
    Assert.That(exception.Message, Is.EqualTo(
      $"{FixtureFolder}/rules-ambiguous.yaml: ambiguous rule for project {project}: matched by {matches}"));
    Assert.That(() => rules.LayerOf(project), Throws.TypeOf<InvalidDataException>());
    Assert.That(rules.LayerOf("Polypiler.Core.Frontends.Kotlin"), Is.EqualTo(6));
  }

  /// <summary>A rule file outside the repository is named by its full path.</summary>
  [Test]
  public void RuleFileOutsideTheRepository_IsNamedByItsPath() {
    var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
    File.WriteAllText(path, "schema: 1\n");
    try {
      var violation = ModuleRules.Load(path).Check([Project("Polypiler.Core")]).Single();
      Assert.That(violation.Message, Is.EqualTo($"PLY0038: project Polypiler.Core is not covered by {Path.GetFullPath(path)}"));
    } finally {
      File.Delete(path);
    }
  }

  private static ModuleRules Rules() => ModuleRules.Load(Path.Combine(_root, RuleFile));

  private static ModuleRules Fixture(string name) => ModuleRules.Load(FixturePath(name));

  private static string FixturePath(string name) => Path.Combine(_root, FixtureFolder, name);

  private static ProjectFile Project(string name, params string[] references) => new(name, references);

  private static IReadOnlyList<RuleViolation> Check(params ProjectFile[] projects) => Rules().Check(projects);

  private static string RepositoryRoot([CallerFilePath] string source = "")
    => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
