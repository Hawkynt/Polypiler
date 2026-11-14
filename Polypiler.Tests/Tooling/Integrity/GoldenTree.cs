using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Integrity;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Integrity;

/// <summary>
/// A synthetic repository for the golden-vector gate: a folder holding the repository's toolchain and
/// corpus locks, into which tests put vector files - the ones of <c>tests/fixtures/infra/integrity</c>
/// are named <c>*.json</c> so the repository's own gate does not take them for golden files - seal
/// them, and commit with git. Shared as source with the acceptance tests.
/// </summary>
internal sealed class GoldenTree {

  /// <summary>The folder of the integrity fixtures.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "integrity");

  /// <summary>Where the trees keep the Adler-32 vector file, as the repository does.</summary>
  public const string Adler32 = "tests/fixtures/verify/adler32.vectors.json";

  /// <summary>The source file a commit changes together with a vector file.</summary>
  public const string CompilerFile = "Polypiler.Core.IR/Module.cs";

  private readonly GitProcess _git = new();

  /// <summary>Puts the repository's toolchain and corpus locks into a folder, which becomes the tree's root.</summary>
  public GoldenTree(string root) {
    this.Root = root;
    foreach (var path in new[] { ToolchainLock.RepositoryPath, CorpusLock.RepositoryPath })
      this.Write(path, File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar))));
  }

  /// <summary>The root of the tree.</summary>
  public string Root { get; }

  /// <summary>The text of the repository's own Adler-32 vector file.</summary>
  public static string RepositoryAdler32 => File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, Adler32.Replace('/', Path.DirectorySeparatorChar)));

  /// <summary>The text of a fixture of <c>tests/fixtures/infra/integrity</c>.</summary>
  public static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureFolder, name));

  /// <summary>The full path of a file of the tree.</summary>
  public string Resolve(string path) => Path.Combine(this.Root, path.Replace('/', Path.DirectorySeparatorChar));

  /// <summary>Writes a file of the tree as UTF-8 without a byte-order mark.</summary>
  public GoldenTree Write(string path, string content) {
    var full = this.Resolve(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
    return this;
  }

  /// <summary>Reads a file of the tree.</summary>
  public string Read(string path) => File.ReadAllText(this.Resolve(path));

  /// <summary>Seals files through the gate and asserts that it did.</summary>
  public GoldenTree Seal(params string[] paths) {
    var problems = new IntegrityChecker(this.Root).Seal(paths);
    Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    return this;
  }

  /// <summary>Appends a line to the change log.</summary>
  public GoldenTree Declare(string line) {
    var log = this.Resolve(GoldenLock.ChangeLogPath);
    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
    File.AppendAllText(log, line + "\n");
    return this;
  }

  /// <summary>Makes the tree a git repository.</summary>
  public GoldenTree Init() {
    this.Git("init", "--quiet", "--initial-branch=main", this.Root);
    return this;
  }

  /// <summary>Commits every change of the tree and tags the commit.</summary>
  public string Commit(string tag) {
    this.Git("-C", this.Root, "add", "--all");
    this.Git("-C", this.Root, "-c", "user.name=Polypiler Tests", "-c", "user.email=tests@polypiler.invalid", "-c", "commit.gpgsign=false",
      "commit", "--quiet", "--no-verify", "--allow-empty", "--message", tag);
    this.Git("-C", this.Root, "-c", "tag.gpgsign=false", "tag", tag);
    return Encoding.UTF8.GetString(this.Git("-C", this.Root, "rev-parse", "HEAD").Output).Trim();
  }

  /// <summary>Runs git in the tree.</summary>
  public GitResult Git(params string[] arguments) {
    var result = this._git.Run(null, arguments);
    Assert.That(result.ExitCode, Is.Zero, $"git {string.Join(' ', arguments)}: {result.Error}");
    return result;
  }

  /// <summary>
  /// Builds the history of the change-rule scenarios: commit <c>c1</c> holds the Adler-32 file without
  /// its vector <c>a</c>, sealed, and the compiler file; the work tree then has the vector added back
  /// and, when asked, the compiler file changed, neither committed yet.
  /// </summary>
  public GoldenTree History(bool changeCompiler) {
    this.Init();
    this.Write(Adler32, WithoutVectorA(RepositoryAdler32)).Write(CompilerFile, "namespace Polypiler.Core.IR;\n").Seal(Adler32);
    this.Write(GoldenLock.ChangeLogPath, "# Golden file changes\n\n").Declare($"- {Adler32}: add the Adler-32 vectors (source: python)");
    this.Commit("c1");
    this.Write(Adler32, RepositoryAdler32);
    if (changeCompiler)
      this.Write(CompilerFile, "namespace Polypiler.Core.IR;\n\n// changed\n");
    return this;
  }

  /// <summary>A vector file's text without the line of its vector <c>a</c>.</summary>
  public static string WithoutVectorA(string text)
    => string.Join('\n', text.Split('\n').Where(line => !line.Contains("\"name\": \"a\"", StringComparison.Ordinal)));

}
