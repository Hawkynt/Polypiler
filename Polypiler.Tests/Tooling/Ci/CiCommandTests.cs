using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling;
using Polypiler.Tooling.Ci;
using Polypiler.Tooling.Commands;

namespace Polypiler.Tests.Tooling.Ci;

/// <summary><c>polypiler-tooling ci audit|matrix|env</c>: command line, output and exit codes.</summary>
[TestFixture]
public sealed class CiCommandTests {

  private static (int ExitCode, string Output, string Error) Invoke(string currentDirectory, params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = CiCommand.Run(args, output, error, currentDirectory);
    return (exitCode, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
  }

  [TestCase(new string[0], "ci needs 'audit', 'matrix' or 'env'")]
  [TestCase(new[] { "lint" }, "unknown ci command 'lint'")]
  [TestCase(new[] { "audit", "--layer", "core" }, "unknown option '--layer'")]
  [TestCase(new[] { "audit", "--root" }, "option '--root' requires a value")]
  [TestCase(new[] { "audit", "--root", ".", "--root", "." }, "option '--root' is given more than once")]
  [TestCase(new[] { "matrix", "--all", "--all" }, "option '--all' is given more than once")]
  [TestCase(new[] { "matrix" }, "ci matrix needs either '--changed <file>' or '--all'")]
  [TestCase(new[] { "matrix", "--all", "--changed", "x" }, "ci matrix needs either '--changed <file>' or '--all'")]
  [TestCase(new[] { "env" }, "ci env needs '--layer <name>'")]
  public void CommandLineNotUnderstood_IsAUsageError(string[] args, string message)
    => Assert.That(Invoke(CiTesting.Tree("audit-ok"), args), Is.EqualTo((2, "", $"polypiler-tooling: error PLY0003: {message}\n")));

  [Test]
  public void Audit_ConsistentTree_PrintsLayersAndSummary() {
    var (exitCode, output, error) = Invoke(CiTesting.Fixtures, "audit", "--root", "audit-ok");
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output.Split('\n')[^2], Is.EqualTo("3 categories, 0 problems"));
  }

  [Test]
  public void Audit_Problems_GoToStandardError() {
    var (exitCode, output, error) = Invoke(CiTesting.Tree("audit-missing-job"), "audit");
    Assert.That((exitCode, error), Is.EqualTo((1, "polypiler-tooling: error PLY0075: layer 'rust' has no job 'layer-rust' in .github/workflows/ci.yml\n")));
    Assert.That(output, Does.EndWith("3 categories, 1 problems\n"));
  }

  [Test]
  public void Audit_MissingWorkflow_IsAnEnvironmentError() {
    using var tree = new CiTree();
    tree.Delete(".github/workflows/ci.yml");
    Assert.That(Invoke(tree.Root, "audit"), Is.EqualTo((1, "", "polypiler-tooling: error PLY0004: '.github/workflows/ci.yml' cannot be read: the file does not exist\n")));
  }

  /// <summary>Test design 5 and 8: one layer matches, several, none, every layer, an empty list.</summary>
  [TestCase("Polypiler.Core.Backends.Rust/Emitter.cs\nREADME.md\n", "[\"core\",\"rust\"]")]
  [TestCase("Polypiler.Core.Backends.Rust/Emitter.cs\nPolypiler.Core.Backends.JavaScript/Emitter.cs\n", "[\"core\",\"js\",\"rust\"]")]
  [TestCase("README.md", "[\"core\"]")]
  [TestCase("", "[\"core\"]")]
  [TestCase("\n  \n", "[\"core\"]")]
  [TestCase(null, "[\"core\",\"js\",\"rust\"]")]
  public void Matrix_SelectsTheLayersOfTheChangedPaths(string? changed, string expected) {
    using var tree = new CiTree();
    tree.Write("tooling/ci/layers/js.yaml", "schema: 1\nlayer: js\ntoolchains: [node]\nos: [ubuntu-latest]\npaths: [\"Polypiler.Core.Backends.JavaScript/**\"]\nalways: false\n");
    if (changed is not null)
      tree.Write("changed.txt", changed);
    var (exitCode, output, error) = changed is null ? Invoke(tree.Root, "matrix", "--all") : Invoke(tree.Root, "matrix", "--changed", "changed.txt");
    Assert.That((exitCode, output, error), Is.EqualTo((0, expected + "\n", "")));
  }

  /// <summary>Test design 16: a list of changed paths that does not exist.</summary>
  [Test]
  public void Matrix_ChangedFileMissing_IsAnEnvironmentError()
    => Assert.That(Invoke(CiTesting.Tree("audit-ok"), "matrix", "--changed", "nowhere.txt"), Is.EqualTo((1, "", "polypiler-tooling: error PLY0004: 'nowhere.txt' cannot be read: the file does not exist\n")));

  [Test]
  public void Matrix_InvalidLayerFile_IsReported() {
    using var tree = new CiTree();
    tree.Write("tooling/ci/layers/core.yaml", "schema: 1\n");
    var (exitCode, output, error) = Invoke(tree.Root, "matrix", "--all");
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0075: tooling/ci/layers/core.yaml: "));
  }

  [Test]
  public void Env_PrintsTheVersionsOfTheLayer_InKeyOrder() {
    using var tree = new CiTree();
    tree.Write("tooling/ci/layers/core.yaml", "schema: 1\nlayer: core\ntoolchains: [node, dotnet-sdk]\nos: [ubuntu-latest]\npaths: []\nalways: true\n");
    Assert.That(Invoke(tree.Root, "env", "--layer", "core"), Is.EqualTo((0, "dotnet-sdk=10.0.404\nnode=24.22.2\n", "")));
  }

  [Test]
  public void Env_Repository_NamesTheLockedVersions()
    => Assert.That(Invoke(PrototypeFixture.RepositoryRoot, "env", "--layer", "core").Output, Does.Match(@"^dotnet-sdk=\d+\.\d+\.\d+\nnode=\d+\.\d+\.\d+\n$"));

  /// <summary>Test design 15: an unknown layer.</summary>
  [Test]
  public void Env_UnknownLayer_IsReported()
    => Assert.That(Invoke(CiTesting.Tree("audit-ok"), "env", "--layer", "nope"), Is.EqualTo((1, "", "polypiler-tooling: error PLY0075: unknown layer 'nope'\n")));

  [Test]
  public void Env_UnknownToolchain_IsReported()
    => Assert.That(Invoke(CiTesting.Tree("audit-unknown-toolchain"), "env", "--layer", "rust"), Is.EqualTo((1, "", "polypiler-tooling: error PLY0075: layer 'rust' names unknown toolchain 'cargo'\n")));

  [Test]
  public void Env_MissingLock_IsAnEnvironmentError() {
    using var tree = new CiTree();
    tree.Delete("tooling/toolchains.lock");
    var (exitCode, _, error) = Invoke(tree.Root, "env", "--layer", "core");
    Assert.That((exitCode, error), Is.EqualTo((1, error)));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0004: 'tooling/toolchains.lock' cannot be read: "));
  }

  [Test]
  public void Program_DispatchesTheVerb() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["ci"], output, error), Is.EqualTo(2));
    Assert.That(error.ToString(), Does.Contain("ci needs 'audit', 'matrix' or 'env'"));
    using var usage = new StringWriter();
    Program.Run([], usage, error);
    Assert.That(usage.ToString(), Does.Contain("polypiler-tooling ci audit [--root <dir>]"));
  }

  [Test]
  public void Run_CurrentDirectoryOverload_ReadsTheProcessDirectory() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(CiCommand.Run(["lint"], output, error), Is.EqualTo(2));
  }

  [Test]
  public void Render_IsTheToolingLine()
    => Assert.That(CiDiagnostics.Render(CiDiagnostics.UnrunWith("x")), Is.EqualTo("polypiler-tooling: error PLY0076: x"));

}
