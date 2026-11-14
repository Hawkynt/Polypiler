using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Steps;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// The harness that binds the feature files: the static check over every feature file with the
/// bindings this assembly really has, the categories of the generated tests, the routing of command
/// lines, the escapes of file content, the workspace and the observer port.
/// </summary>
[TestFixture]
public sealed class SupportTests {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  private static readonly Assembly _assembly = typeof(SupportTests).Assembly;

  /// <summary>Every step of every feature file has exactly one binding of this assembly, every feature a scenario, every file a conforming name.</summary>
  [Test]
  public void EveryFeatureFile_PassesTheStaticCheck() {
    var problems = BindingCheck.Check(_root, FeatureFiles.Under(_root, "tests/features"), AssemblyBindings.Of(_assembly), BindingCheck.Areas(_root));
    Assert.That(problems, Is.Empty, string.Join("\n", problems));
  }

  /// <summary>Every feature file has a generated test class with one test case per scenario and outline row.</summary>
  [Test]
  public void EveryFeatureFile_IsAGeneratedTestClass() {
    var generated = AssemblyBindings.GeneratedFeatures(_assembly).ToDictionary(AssemblyBindings.TitleOf, AssemblyBindings.TestCasesOf);
    var expected = FeatureFiles.Under(_root, "tests/features").Select(path => FeatureFiles.Parse(Path.Combine(_root, path)))
      .ToDictionary(FeatureFiles.TitleOf, document => FeatureFiles.Cases(document).Count);
    Assert.That(generated, Is.EquivalentTo(expected));
  }

  /// <summary>Test design 5 and 6: a slow-tier tag is the NUnit category of the same name; every generated test is also in <c>Acceptance</c>.</summary>
  [Test]
  public void Tags_BecomeCategories() {
    string[] tiers = ["Slow", "EndToEnd", "OsIntegration", "ExternalInterop", "PolyglotInterop", "Performance"];
    Assert.That(_assembly.GetCustomAttributes<CategoryAttribute>().Select(category => category.Name), Is.EqualTo(new[] { "Acceptance" }));
    foreach (var feature in AssemblyBindings.GeneratedFeatures(_assembly)) {
      var title = AssemblyBindings.TitleOf(feature);
      var path = FeatureFiles.Under(_root, "tests/features").Single(candidate => FeatureFiles.TitleOf(FeatureFiles.Parse(Path.Combine(_root, candidate))) == title);
      var tags = FeatureFiles.Parse(Path.Combine(_root, path)).Feature.Tags.Select(tag => tag.Name.TrimStart('@')).ToList();
      var categories = feature.GetCustomAttributes<CategoryAttribute>().Select(category => category.Name).ToList();
      Assert.That(categories, Is.EquivalentTo(tags.Where(tiers.Contains)), title);
    }
  }

  /// <summary>Test design 4: <c>polypiler</c> is dropped, a tooling verb goes to the tooling, anything else goes to the compiler as it is.</summary>
  [TestCase("polypiler translate a.cs", CommandTarget.Polypiler, new[] { "translate", "a.cs" })]
  [TestCase("toolchains list", CommandTarget.Tooling, new[] { "toolchains", "list" })]
  [TestCase("sbom", CommandTarget.Tooling, new[] { "sbom" })]
  [TestCase("polypiler-tooling toolchains validate", CommandTarget.Tooling, new[] { "toolchains", "validate" })]
  [TestCase("polypiler-tooling", CommandTarget.Tooling, new string[0])]
  [TestCase("--version", CommandTarget.Polypiler, new[] { "--version" })]
  [TestCase("polypiler", CommandTarget.Polypiler, new string[0])]
  [TestCase("", CommandTarget.Polypiler, new string[0])]
  public void CommandLine_IsRouted(string line, CommandTarget target, string[] arguments)
    => Assert.That(CommandSteps.Route([.. CommandLine.Split(line)]), Is.EqualTo((target, arguments)));

  /// <summary>Test design 2: a tooling verb starts the <c>polypiler-tooling</c> executable in the workspace; a <c>polypiler</c> line runs the compiler.</summary>
  [Test]
  public void ToolingVerb_RunsTheTooling() {
    var workspace = ScenarioWorkspace.Create();
    try {
      var log = new CommandLog();
      var identity = new ScenarioIdentity("../tests/features/infra", "Feature files are bound to NUnit", "A refused command line reports its exit code and message", []);
      var steps = new CommandSteps(new PolypilerRunner(workspace, identity, log), workspace, log);
      steps.Run("toolchains list");
      Assert.That(log.Last, Is.EqualTo(new CommandResult(2, "", "polypiler-tooling: error PLY0003: unknown toolchains command 'list'" + Environment.NewLine)));
      steps.Run("polypiler 'frob nicate'");
      Assert.That(log.Last.Stderr, Does.Contain("unknown command 'frob nicate'"));
      Assert.That(log.Commands, Has.Count.EqualTo(2));
    } finally {
      workspace.Delete();
    }
  }

  /// <summary>Test design 9 and 1: no arguments is the help text, <c>--version</c> the version, in process as with the executable.</summary>
  [Test]
  public void CommandLineWithoutFiles_RunsInProcess() {
    var workspace = ScenarioWorkspace.Create();
    try {
      var help = PolypilerRunner.InProcess(workspace.Path, []);
      Assert.That(help.ExitCode, Is.Zero);
      Assert.That(help.Stdout, Does.Contain("usage: polypiler"));
      Assert.That(help, Is.EqualTo(Prototype.Toolchain.Executable(workspace.Path)));
      var version = PolypilerRunner.InProcess(workspace.Path, ["--version"]);
      Assert.That(CommandSteps.WithoutTrailingNewline(version.Stdout), Is.EqualTo(ProductInfo.Version));
      Assert.That(version, Is.EqualTo(Prototype.Toolchain.Executable(workspace.Path, "--version")));
    } finally {
      workspace.Delete();
    }
  }

  /// <summary>A translation that names a file but cannot run in process falls back to the executable.</summary>
  [Test]
  public void TranslationWithoutOutput_StartsTheExecutable() {
    var workspace = ScenarioWorkspace.Create();
    try {
      File.Copy(PrototypeFixture.PathOf("Adler32"), workspace.Resolve("Adler32.cs"));
      var result = PolypilerRunner.InProcess(workspace.Path, ["translate", "Adler32.cs", "--from", "csharp", "--to", "csharp"]);
      Assert.That(result.ExitCode, Is.EqualTo(2));
      Assert.That(result.Stderr, Does.Contain("the option '--output' is missing"));
    } finally {
      workspace.Delete();
    }
  }

  /// <summary>Test design 7: the escapes of file content.</summary>
  [Test]
  public void FileContent_TakesItsEscapes()
    => Assert.That(System.Text.Encoding.UTF8.GetBytes(StepText.Unescape(@"a\tb\\c\""d\n")), Is.EqualTo(new byte[] { 0x61, 0x09, 0x62, 0x5C, 0x63, 0x22, 0x64, 0x0A }));

  /// <summary>An escape that is not one of the four, and a backslash at the end, are refused.</summary>
  [TestCase(@"a\x", "unknown escape '\\x' at position 1")]
  [TestCase(@"a\", "a backslash ends the text at position 1")]
  public void FileContent_RefusesAnUnknownEscape(string text, string message)
    => Assert.That(() => StepText.Unescape(text), Throws.TypeOf<FormatException>().With.Message.EqualTo(message));

  /// <summary>A workspace is a fresh directory under the temporary folder, and deleting it twice is harmless.</summary>
  [Test]
  public void Workspace_IsFreshAndDeletedOnce() {
    var first = ScenarioWorkspace.Create();
    var second = ScenarioWorkspace.Create();
    Assert.That(first.Path, Is.Not.EqualTo(second.Path));
    Assert.That(Path.GetFileName(first.Path), Does.StartWith(ScenarioWorkspace.Prefix));
    Assert.That(Path.GetDirectoryName(first.Path), Is.EqualTo(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)));
    Assert.That(Directory.GetFileSystemEntries(first.Path), Is.Empty);
    first.Write("a/b.txt", "x");
    Assert.That(File.ReadAllText(Path.Combine(first.Path, "a", "b.txt")), Is.EqualTo("x"));
    first.Delete();
    first.Delete();
    second.Delete();
    Assert.That(Directory.Exists(first.Path), Is.False);
  }

  /// <summary>Test design 18: an observer is told of a failed scenario before the workspace goes.</summary>
  [Test]
  public void Observer_SeesAFailedScenarioAndItsWorkspace() {
    var workspace = ScenarioWorkspace.Create();
    var observer = new Recorder(workspace);
    var record = new ScenarioRecord("f", "s", "tests/features/infra/infra-bdd.feature", false, [new CommandResult(2, "", "x")], workspace.Path);
    ScenarioHooks.Complete(record, [observer], workspace);
    Assert.That(observer.Records, Is.EqualTo(new[] { record }));
    Assert.That(observer.WorkspaceExisted, Is.True);
    Assert.That(Directory.Exists(workspace.Path), Is.False);
  }

  /// <summary>Test design 19: an observer that throws fails the scenario, the others still run and the workspace is still deleted.</summary>
  [Test]
  public void ThrowingObserver_FailsTheScenario_AndTheWorkspaceIsStillDeleted() {
    var workspace = ScenarioWorkspace.Create();
    var after = new Recorder(workspace);
    var record = new ScenarioRecord("f", "s", "p", true, [], workspace.Path);
    var exception = Assert.Throws<AggregateException>(() => ScenarioHooks.Complete(record, [new Thrower(), after], workspace))!;
    Assert.That(exception.InnerExceptions.Single().Message, Is.EqualTo("observer failed"));
    Assert.That(exception.Message, Does.StartWith("an observer of the scenario f/s failed"));
    Assert.That(after.Records, Has.Count.EqualTo(1));
    Assert.That(Directory.Exists(workspace.Path), Is.False);
  }

  /// <summary>Test design 8: the rows of an outline are told apart by their number.</summary>
  [Test]
  public void OutlineRows_AreNumbered() {
    var identities = new[] { "A shift/or pair becomes a rotate", "O0100 recognize rotate-left", "--opt none  vs. --opt speed!" }
      .Select((title, index) => new ScenarioIdentity("../tests/features/infra", "Feature files are bound to NUnit", "Titles become stable slugs", [title, Slug.Of(title)]))
      .ToList();
    Assert.That(identities.Select(identity => identity.ScenarioSlug), Is.EqualTo(new[] { "titles-become-stable-slugs-1", "titles-become-stable-slugs-2", "titles-become-stable-slugs-3" }));
    Assert.That(identities.Select(identity => identity.IsFirstOfItsScenario), Is.EqualTo(new[] { true, false, false }));
    Assert.That(identities[0].FeaturePath, Is.EqualTo("tests/features/infra/infra-bdd.feature"));
    Assert.That(identities[0].FeatureSlug, Is.EqualTo("feature-files-are-bound-to-nunit"));
    var plain = new ScenarioIdentity("../tests/features/infra", "Feature files are bound to NUnit", "Each scenario gets its own workspace", []);
    Assert.That((plain.Row, plain.ScenarioSlug, plain.IsFirstOfItsScenario), Is.EqualTo((0, "each-scenario-gets-its-own-workspace", true)));
  }

  /// <summary>A scenario that is not in the feature, or a feature that is not in the folder, is an error that names it.</summary>
  [Test]
  public void UnknownScenarioOrFeature_IsNamed() {
    Assert.That(() => new ScenarioIdentity("../tests/features/infra", "Feature files are bound to NUnit", "No such scenario", []).Row,
      Throws.InvalidOperationException.With.Message.Contains("has no scenario 'No such scenario'"));
    Assert.That(() => new ScenarioIdentity("../tests/features/infra", "No such feature", "x", []).Row,
      Throws.InvalidOperationException.With.Message.Contains("no feature 'No such feature'"));
  }

  /// <summary>A sweep checks every target, the first one first, and fails with each target that failed.</summary>
  [Test]
  public void Sweep_ChecksEveryTargetAndNamesTheFailures() {
    var checkedTargets = new System.Collections.Concurrent.ConcurrentQueue<int>();
    var exception = Assert.Throws<AssertionException>(() => Sweep.Each([1, 2, 3, 4], target => {
      checkedTargets.Enqueue(target);
      Assert.That(target % 2, Is.Zero);
    }))!;
    Assert.That(checkedTargets.First(), Is.EqualTo(1));
    Assert.That(checkedTargets, Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
    Assert.That(exception.Message, Does.Contain("2 of 4 targets failed").And.Contain("1: ").And.Contain("3: ").And.Not.Contain("2: ").And.Not.Contain("4: "));
    Assert.That(() => Sweep.Each(Array.Empty<int>(), _ => { }), Throws.TypeOf<AssertionException>().With.Message.Contains("at least one target"));
    Sweep.Each([2, 4], target => Assert.That(target % 2, Is.Zero));
  }

  /// <summary>The log of a scenario without a command line has no last one.</summary>
  [Test]
  public void EmptyCommandLog_HasNoLastCommand()
    => Assert.That(() => new CommandLog().Last, Throws.InvalidOperationException.With.Message.EqualTo("no command line was run in this scenario"));

  private sealed class Recorder(ScenarioWorkspace workspace) : IScenarioObserver {

    public List<ScenarioRecord> Records { get; } = [];

    public bool WorkspaceExisted { get; private set; }

    public void Completed(ScenarioRecord record) {
      this.Records.Add(record);
      this.WorkspaceExisted = Directory.Exists(workspace.Path);
    }

  }

  private sealed class Thrower : IScenarioObserver {

    public void Completed(ScenarioRecord record) => throw new InvalidOperationException("observer failed");

  }

}
