using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Running;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Tooling.Toolchains.ToolchainTesting;

namespace Polypiler.Tests.Tooling.Toolchains;

/// <summary>The runner's toolchain port, answered by the locator of the lock.</summary>
[TestFixture]
public sealed class LockToolchainResolverTests {

  private ScratchFolder _folder = null!;
  private Dictionary<string, string> _environment = null!;

  [SetUp]
  public void CreateFolder() {
    this._folder = new();
    this._environment = new() { ["PATH"] = Path.Combine(this._folder.Path, "path"), ["NUGET_PACKAGES"] = Path.Combine(this._folder.Path, "nuget") };
  }

  [TearDown]
  public void DeleteFolder() => this._folder.Dispose();

  private LockToolchainResolver Resolver(ToolchainLock toolchains, IProcessRunner runner)
    => new(new ToolchainLocator(toolchains, runner, name => this._environment.GetValueOrDefault(name), "linux", TimeSpan.FromSeconds(30)));

  private static (string Code, string Message) Problem(ToolchainResolution resolution) {
    Assert.That(resolution.Executable, Is.Null);
    Assert.That(resolution.Problem!.ExitCode, Is.EqualTo(ExitCode.EnvironmentError));
    return (resolution.Problem.Code, resolution.Problem.Message);
  }

  [TestCase("fake 1.2.3", TestName = "An installed toolchain at its pin is found")]
  [TestCase("fake 9.9.9", TestName = "An installed toolchain at another version is found too")]
  public void AnInstalledToolchain_IsFound(string output) {
    var executable = this._folder.File(Path.Combine("path", "fake"));
    var runner = new ScriptedRunner(output);
    var resolver = this.Resolver(Parse(Entry("fake")), runner);
    Assert.That(resolver.Resolve("fake"), Is.EqualTo(ToolchainResolution.Found(executable)));
    Assert.That(resolver.Resolve("fake").Executable, Is.EqualTo(executable));
    Assert.That(runner.Calls, Has.Count.EqualTo(1), "each key is probed once");
  }

  [Test]
  public void AnExternalToolchain_IsFoundOutsideCi() {
    var executable = this._folder.File(Path.Combine("path", "fake"));
    Assert.That(this.Resolver(Parse(Entry("fake", extra: "kind: external")), new ScriptedRunner("fake 1.2.3")).Resolve("fake").Executable, Is.EqualTo(executable));
  }

  /// <summary>Test design 20: a toolchain that is not installed.</summary>
  [Test]
  public void AMissingToolchain_IsPLY0021()
    => Assert.That(Problem(this.Resolver(Parse(Entry("fake")), new ScriptedRunner("fake 1.2.3")).Resolve("fake")),
      Is.EqualTo(("PLY0021", "toolchain 'fake' is not installed (probe: fake --version): 'fake' was not found on PATH")));

  [Test]
  public void AToolchainOfAnotherPlatform_IsPLY0021()
    => Assert.That(Problem(this.Resolver(Parse(Entry("fake", platforms: "[windows]")), new ScriptedRunner("fake 1.2.3")).Resolve("fake")),
      Is.EqualTo(("PLY0021", "toolchain 'fake' is not installed (probe: fake --version): not used on linux")));

  [Test]
  public void APackageWithoutAnExecutable_IsPLY0021() {
    this._folder.File(Path.Combine("path", "fake"));
    this._folder.Folder(Path.Combine("nuget", "some.library", "1.2.3"));
    var toolchains = Parse(Entry("fake", "1.0.0", pattern: @"^(?<version>\S+)$"), Entry("lib", "1.2.3", "[Some.Library]", extra: "kind: nuget\nrequires: fake"));
    Assert.That(Problem(this.Resolver(toolchains, new ScriptedRunner("1.0.0")).Resolve("lib")),
      Is.EqualTo(("PLY0021", "toolchain 'lib' is not installed (probe: Some.Library): it has no executable of its own")));
  }

  [Test]
  public void AKeyTheLockLacks_IsPLY0020()
    => Assert.That(Problem(this.Resolver(Parse(Entry("fake")), new ScriptedRunner("fake 1.2.3")).Resolve("ghost")),
      Is.EqualTo(("PLY0020", "unknown toolchain key 'ghost'")));

  [Test]
  public void AResolution_IsEitherAnExecutableOrAProblem() {
    Assert.That(() => ToolchainResolution.Found(null!), Throws.ArgumentNullException);
    Assert.That(() => ToolchainResolution.Found(null!, []), Throws.ArgumentNullException);
    Assert.That(() => ToolchainResolution.Found("/x", null!), Throws.ArgumentNullException);
    Assert.That(() => ToolchainResolution.Failed(null!), Throws.ArgumentNullException);
  }

  /// <summary>The extra roots of a lock entry: the folders its variables name, where they are set to the full path of a folder that exists.</summary>
  [Test]
  public void TheRootsOfTheLock_AreTheFoldersTheirVariablesName() {
    var executable = this._folder.File(Path.Combine("path", "fake"));
    var miri = this._folder.Folder("miri");
    this._environment["RUST_MIRI_HOME"] = miri + Path.DirectorySeparatorChar;
    this._environment["RELATIVE"] = "miri";
    this._environment["ABSENT"] = Path.Combine(this._folder.Path, "absent");
    this._environment["EMPTY"] = "";
    var resolution = this.Resolver(Parse(Entry("fake", extra: "roots: [UNSET, EMPTY, RELATIVE, ABSENT, RUST_MIRI_HOME]")), new ScriptedRunner("fake 1.2.3")).Resolve("fake");
    Assert.That(resolution.Executable, Is.EqualTo(executable));
    Assert.That(resolution.Roots, Is.EqualTo(new[] { miri + Path.DirectorySeparatorChar }));
    Assert.That(this.Resolver(Parse(Entry("fake")), new ScriptedRunner("fake 1.2.3")).Resolve("fake").Roots, Is.Empty);
  }

}
