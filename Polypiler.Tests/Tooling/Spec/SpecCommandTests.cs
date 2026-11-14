using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary><c>polypiler-tooling spec validate</c> in process: options, both diagnostic formats, the summary line and the exit codes.</summary>
[TestFixture]
public sealed class SpecCommandTests {

  private static readonly string _newLine = Environment.NewLine;

  private static string Fixtures => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "meta");

  private static (int ExitCode, string Output, string Error) Run(params string[] args) {
    var (output, error) = (new StringWriter(), new StringWriter());
    var exitCode = Program.Run([SpecCommand.Verb, .. args], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private static (int ExitCode, string Output, string Error) RunIn(string directory, params string[] args) {
    var (output, error) = (new StringWriter(), new StringWriter());
    var exitCode = SpecCommand.Run(args, output, error, directory, SpecFamilies.All);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void ValidTree_PrintsTheCountAndSucceeds()
    => Assert.That(Run("validate", "--root", Path.Combine(Fixtures, "valid")), Is.EqualTo((0, "1 documents, 0 errors" + _newLine, "")));

  /// <summary>The fixture trees of the acceptance scenarios, each with the one diagnostic it stands for.</summary>
  [TestCase("unreviewed", "demo/widget.yaml(10,19): error PLY0041: normative document must be reviewed at its current version (reviewedRevision 1, version 2)")]
  [TestCase("wrong-prefix", "demo/u32.yaml(1,5): error PLY0041: id 'TYPES.u32' does not start with family prefix 'DEMO'")]
  [TestCase("no-tests", "demo/widget.yaml(7,8): error PLY0041: normative document needs at least one test")]
  [TestCase("duplicate-id", "demo/b.yaml(1,5): error PLY0042: duplicate spec id 'DEMO.widget', first defined in demo/a.yaml")]
  [TestCase("missing-test", "demo/widget.yaml(7,9): error PLY0043: test path 'tests/features/spec/nowhere.feature' does not exist")]
  [TestCase("deprecated-early", "demo/widget.yaml(9,15): error PLY0044: deprecatedIn P1 is earlier than introducedIn P2")]
  [TestCase("over-reviewed", "demo/widget.yaml(10,19): error PLY0044: reviewedRevision 3 exceeds version 2")]
  [TestCase("unknown-family", "gadgets/thing.yaml(1,1): error PLY0047: unknown spec family 'gadgets'")]
  [TestCase("bad-type", "demo/widget.yaml(3,10): error PLY0040: /version: Value is \"string\" but should be \"integer\"")]
  [TestCase("broken-yaml", "demo/widget.yaml(5,8): error PLY0047: not valid YAML: ")]
  public void FixtureTree_ReportsItsOneDiagnostic(string tree, string expected) {
    var (exitCode, output, error) = Run("validate", "--root", Path.Combine(Fixtures, tree));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error.Split(_newLine, StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(1).And.One.StartsWith(expected));
    Assert.That(output, Does.EndWith(", 1 errors" + _newLine));
  }

  /// <summary>Test design 21.</summary>
  [Test]
  public void JsonDiagnostics_AreOneLineEachWithTheSourceRange() {
    var (exitCode, _, error) = Run("validate", "--root", Path.Combine(Fixtures, "unreviewed"), "--diagnostics", "json");
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Is.EqualTo("{\"code\":\"PLY0041\",\"severity\":\"error\",\"message\":\"normative document must be reviewed at its current version (reviewedRevision 1, version 2)\","
      + "\"source\":{\"file\":\"demo/widget.yaml\",\"startLine\":10,\"startColumn\":19,\"endLine\":10,\"endColumn\":19},\"rule\":\"spec.metadata\",\"cause\":null,\"workaround\":null,\"requiredCapability\":null,\"command\":null}" + _newLine));
  }

  [Test]
  public void TextDiagnostics_CanBeAskedFor()
    => Assert.That(Run("validate", "--root", Path.Combine(Fixtures, "unreviewed"), "--diagnostics", "text").Error, Does.StartWith("demo/widget.yaml(10,19): error PLY0041: "));

  [Test]
  public void DefaultRoot_IsSpecBelowTheCurrentDirectory() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document());
    Assert.That(RunIn(repository.Root, "validate"), Is.EqualTo((0, "1 documents, 0 errors" + _newLine, "")));
  }

  [Test]
  public void RepositoryRoot_IsTheCheckoutAboveARelativeRoot()
    => Assert.That(RunIn(PrototypeFixture.RepositoryRoot, "validate", "--root", "tests/fixtures/spec/meta/valid").ExitCode, Is.Zero);

  [Test]
  public void WithoutRepository_TestPathsAreRelativeToTheCurrentDirectory() {
    using var repository = new SpecRepository();
    Directory.Delete(Path.Combine(repository.Root, ".git"));
    Assume.That(SpecTree.FindRepositoryRoot(repository.Root), Is.Null, "the temporary folder lies inside a repository");
    repository.Document("demo/widget.yaml", SpecText.Document());
    Assert.That(RunIn(repository.Root, "validate").ExitCode, Is.Zero);
  }

  [Test]
  public void TheRepository_Validates()
    => Assert.That(RunIn(PrototypeFixture.RepositoryRoot, "validate").ExitCode, Is.Zero);

  /// <summary>
  /// The valid fixture trees, every one of them and the <c>kinds</c> tree of <c>verification</c>, validate
  /// whole: the documents of other families they refer to are real documents of those families, not
  /// stand-ins that pass only behind <c>--family</c>.
  /// </summary>
  private static string[] ValidFixtureTrees()
    => [.. Directory.GetDirectories(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec"))
        .Select(family => Path.Combine(family, "valid")).Where(Directory.Exists)
        .Append(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "verification", "kinds"))
        .Select(tree => Path.GetRelativePath(PrototypeFixture.RepositoryRoot, tree).Replace(Path.DirectorySeparatorChar, '/'))
        .Order(StringComparer.Ordinal)];

  [TestCaseSource(nameof(ValidFixtureTrees))]
  public void ValidFixtureTree_ValidatesWithoutFamily(string tree) {
    var (exitCode, output, error) = RunIn(PrototypeFixture.RepositoryRoot, "validate", "--root", tree);
    Assert.That((exitCode, error), Is.EqualTo((0, "")), output);
  }

  [Test]
  public void FamilyOption_ValidatesOnlyThatFamily() {
    using var repository = new SpecRepository();
    repository.Document("demo/widget.yaml", SpecText.Document()).Document("other/thing.yaml", "a: [");
    Assert.That(RunIn(repository.Root, "validate", "--family", "demo"), Is.EqualTo((0, "1 documents, 0 errors" + _newLine, "")));
    Assert.That(RunIn(repository.Root, "validate").ExitCode, Is.EqualTo(1));
  }

  /// <summary>Test design 19.</summary>
  [Test]
  public void UnknownFamilyOption_IsPLY0047()
    => Assert.That(Run("validate", "--root", Path.Combine(Fixtures, "valid"), "--family", "gadgets"),
      Is.EqualTo((1, "0 documents, 1 errors" + _newLine, "polypiler: error PLY0047: unknown spec family 'gadgets'" + _newLine)));

  [Test]
  public void RootThatDoesNotExist_IsAnEnvironmentError()
    => Assert.That(RunIn(PrototypeFixture.RepositoryRoot, "validate", "--root", "nowhere"),
      Is.EqualTo((1, "0 documents, 1 errors" + _newLine, "polypiler: error PLY0047: spec root 'nowhere' does not exist" + _newLine)));

  [TestCase(new string[0], "spec needs 'validate'")]
  [TestCase(new[] { "check" }, "unknown spec command 'check'")]
  [TestCase(new[] { "validate", "--lock", "x" }, "unknown option '--lock'")]
  [TestCase(new[] { "validate", "--root" }, "option '--root' requires a value")]
  [TestCase(new[] { "validate", "--family", "a", "--family", "b" }, "option '--family' is given more than once")]
  [TestCase(new[] { "validate", "--diagnostics", "xml" }, "option '--diagnostics' does not allow 'xml'")]
  public void CommandLine_NotUnderstood(string[] args, string message)
    => Assert.That(Run(args), Is.EqualTo((2, "", $"polypiler-tooling: error PLY0003: {message}" + _newLine)));

  [Test]
  public void NullArguments_AreRefused() {
    var writer = new StringWriter();
    Assert.Throws<ArgumentNullException>(() => SpecCommand.Run(null!, writer, writer));
    Assert.Throws<ArgumentNullException>(() => SpecCommand.Run([], null!, writer));
    Assert.Throws<ArgumentNullException>(() => SpecCommand.Run([], writer, null!));
  }

  [Test]
  public void Usage_ListsTheVerb() {
    var output = new StringWriter();
    Program.Run([], output, new StringWriter());
    Assert.That(output.ToString().Split(_newLine).Count(line => line.Contains("polypiler-tooling spec validate [--root <dir>] [--family <folder>] [--diagnostics text|json]", StringComparison.Ordinal)), Is.EqualTo(1));
  }

}
