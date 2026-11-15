using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Tooling.Toolchains;
using Reqnroll;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-runner-toolchain-roots.feature</c>: the kotlinc of the
/// lock, copied as it is installed into a temporary home folder, compiles and runs a Kotlin program
/// untrusted in bubblewrap, with that folder as the home the sandbox hides.
/// </summary>
[Binding, Scope(Feature = "Toolchains installed under home run isolated")]
public sealed class ToolchainRootSteps : IDisposable {

  private static readonly RunLimits _limits = Limits(wallTime: TimeSpan.FromMinutes(5), memoryBytes: 4L << 30);

  private string? _home;
  private string _bwrap = "";
  private string _kotlinc = "";
  private RunResult? _result;

  private string Home => this._home ?? throw new InvalidOperationException("no home folder was made");

  private RunResult Result => this._result ?? throw new InvalidOperationException("no plan has run");

  private string[] Lines => Encoding.UTF8.GetString(this.Result.Stdout.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries);

  /// <summary>Removes the home folder.</summary>
  public void Dispose() {
    if (this._home != null && Directory.Exists(this._home))
      Directory.Delete(this._home, true);
  }

  [Given(@"^kotlinc (\S+) unpacked under a temporary home folder$")]
  public void KotlincUnderAHome(string version) {
    Assert.That(ToolchainLock.Load(Path.Combine(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath))["kotlinc"].Version, Is.EqualTo(version), "the version the lock pins");
    if (Bubblewrap == null || Toolchains.Resolve(SandboxFactory.BubblewrapToolchain).Executable is not { } bwrap) {
      Assert.Inconclusive("bubblewrap is not installed or cannot unshare namespaces on this host");
      return;
    }

    new RequiresToolchainAttribute("kotlinc").Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));
    var installed = Toolchains.Resolve("kotlinc").Executable!;
    var target = File.ResolveLinkTarget(installed, true)?.FullName ?? installed;
    var root = ToolchainRoots.Of(target, _ => true)[^1].Path;
    if (!File.Exists(Path.Combine(root, "lib", "kotlin-compiler.jar")))
      Assert.Inconclusive($"the kotlinc at '{target}' is not laid out as the release archive is, with bin/ beside lib/");

    this._bwrap = bwrap;
    this._home = Directory.CreateTempSubdirectory("polypiler-kotlin-home-").FullName;
    var copy = Path.Combine(this._home, "kotlinc");
    Copy(root, copy);
    this._kotlinc = copy;
    File.WriteAllText(Path.Combine(this._home, "secret.txt"), "secret");
  }

  [When("^a Kotlin fixture is compiled and run through the runner with an untrusted plan$")]
  [When("^the program tries to create a file in its toolchain's root$")]
  public async Task CompileAndRun() {
    var toolchains = new Dictionary<string, string>(StringComparer.Ordinal) {
      ["kotlinc"] = Path.Combine(this._kotlinc, "bin", "kotlinc"),
      ["kotlin"] = Path.Combine(this._kotlinc, "bin", "kotlin")
    };
    var plan = new RunPlan(
      FixtureFiles("kotlin-home"),
      [new("kotlinc", ["Main.kt", "-include-runtime", "-d", "main.jar"])],
      new("kotlin", ["main.jar", Path.Combine(this.Home, "secret.txt"), Path.Combine(this._kotlinc, "lib", "new.jar")]),
      _limits,
      TrustLevel.Untrusted);
    var sandbox = new BubblewrapSandbox(this._bwrap, new ProcFileSystemTable(), this.Home);
    this._result = await new CrossLanguageRunner(sandbox, new Fixed(toolchains)).RunAsync(plan);
  }

  [Then("^it runs in the bubblewrap sandbox and prints the expected output$")]
  public void RunsIsolated() {
    var error = Encoding.UTF8.GetString(this.Result.Stderr.Span) + string.Join('\n', this.Result.Diagnostics.Select(diagnostic => diagnostic.Message + " " + diagnostic.Cause));
    Assert.That((this.Result.Outcome, this.Result.ExitCode, this.Result.Isolation), Is.EqualTo((RunOutcome.Completed, 0, IsolationLevel.Isolated)), error);
    Assert.That(this.Lines.FirstOrDefault(), Is.EqualTo("hello from kotlin"), error);
  }

  [Then("^a file elsewhere in that home folder is not readable from the program$")]
  public void HomeIsHidden() => Assert.That(this.Lines.ElementAtOrDefault(1), Is.EqualTo("read: refused"));

  [Then("^the attempt fails with a read-only error$")]
  public void RootIsReadOnly() {
    this.RunsIsolated();
    Assert.That(this.Lines.ElementAtOrDefault(2), Does.StartWith("write: ").And.Contains("Read-only file system"));
    Assert.That(File.Exists(Path.Combine(this._kotlinc, "lib", "new.jar")), Is.False);
  }

  /// <summary>Copies an install folder with its executable bits.</summary>
  private static void Copy(string source, string destination) {
    foreach (var folder in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
      Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, folder)));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
      var copy = Path.Combine(destination, Path.GetRelativePath(source, file));
      File.Copy(file, copy);
      if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(copy, File.GetUnixFileMode(file));
    }
  }

  /// <summary>Resolves the keys of the plan to the copied install.</summary>
  private sealed class Fixed(IReadOnlyDictionary<string, string> executables) : IToolchainResolver {

    public ToolchainResolution Resolve(string toolchain) => ToolchainResolution.Found(executables[toolchain]);

  }

}
