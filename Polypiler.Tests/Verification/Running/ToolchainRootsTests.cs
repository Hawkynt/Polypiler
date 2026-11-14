using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Running;

/// <summary>
/// The root rules of <see cref="ToolchainRoots"/> on toolchains laid out as their installers lay them
/// out, in a temporary folder: one case per kind, the fallback without a marker, and the narrowing of a
/// root that may not be shown.
/// </summary>
[TestFixture]
public sealed class ToolchainRootsTests {

  private static readonly Func<string, bool> _anything = _ => true;

  private string _home = "";

  [SetUp]
  public void CreateHome() {
    if (OperatingSystem.IsWindows())
      Assert.Ignore("the root rules serve bubblewrap, which is Linux only");
    this._home = Directory.CreateTempSubdirectory("polypiler-roots-").FullName;
  }

  [TearDown]
  public void DeleteHome() {
    if (this._home.Length > 0)
      Directory.Delete(this._home, true);
  }

  private string Make(params string[] files) {
    foreach (var file in files) {
      var path = Path.Combine(this._home, file);
      if (file.EndsWith('/'))
        Directory.CreateDirectory(path);
      else {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
      }
    }

    return Path.Combine(this._home, files[0]);
  }

  private string At(string relative) => Path.Combine(this._home, relative);

  private static (string Path, ToolchainRootKind Kind)[] Roots(string executable, Func<string, bool>? allowed = null)
    => [.. ToolchainRoots.Of(executable, allowed ?? _anything).Select(root => (root.Path, root.Kind))];

  [Test]
  public void AJdk_IsTheFolderWithItsReleaseFile() {
    var java = this.Make("jdk/bin/java", "jdk/release");
    Assert.That(Roots(java), Is.EqualTo(new[] { (this.At("jdk"), ToolchainRootKind.Jdk) }));
  }

  [Test]
  public void TheJreOfAJdk_IsTheJdk() {
    var java = this.Make("jdk8/jre/bin/java", "jdk8/release");
    Assert.That(Roots(java), Is.EqualTo(new[] { (this.At("jdk8"), ToolchainRootKind.Jdk) }));
  }

  [Test]
  public void ARustupToolchain_IsTheFolderWithLibRustlib() {
    var cargo = this.Make("rustup/toolchains/1.99.0-x86_64-unknown-linux-gnu/bin/cargo", "rustup/toolchains/1.99.0-x86_64-unknown-linux-gnu/lib/rustlib/");
    Assert.That(Roots(cargo), Is.EqualTo(new[] { (this.At("rustup/toolchains/1.99.0-x86_64-unknown-linux-gnu"), ToolchainRootKind.RustToolchain) }));
  }

  [Test]
  public void ARustupProxy_IsItsFolder_CargosCaches_AndTheDefaultRustupHome() {
    var cargo = this.Make(".cargo/bin/cargo", ".cargo/bin/rustup", ".cargo/registry/", ".cargo/git/", ".cargo/credentials.toml", ".rustup/toolchains/");
    Assert.That(Roots(cargo), Is.EqualTo(new[] {
      (this.At(".cargo/bin"), ToolchainRootKind.Rustup),
      (this.At(".cargo/registry"), ToolchainRootKind.Rustup),
      (this.At(".cargo/git"), ToolchainRootKind.Rustup),
      (this.At(".rustup"), ToolchainRootKind.Rustup)
    }), "the credentials beside the caches stay hidden");
  }

  [Test]
  public void ARustupProxy_WithoutCachesOrADefaultRustupHome_IsItsFolder() {
    var cargo = this.Make("cargo/bin/cargo", "cargo/bin/rustup");
    Assert.That(Roots(cargo), Is.EqualTo(new[] { (this.At("cargo/bin"), ToolchainRootKind.Rustup) }));
  }

  [Test]
  public void APythonEnvironment_IsItsFolder_AndThePrefixOfItsBaseInterpreter() {
    var tool = this.Make("venv/bin/pcbasic", "base/bin/python3");
    File.WriteAllText(this.At("venv/pyvenv.cfg"), $"home = {this.At("base/bin")}/\ninclude-system-site-packages = false\nversion = 3.13.1\n");
    Assert.That(Roots(tool), Is.EqualTo(new[] { (this.At("venv"), ToolchainRootKind.PythonEnvironment), (this.At("base"), ToolchainRootKind.PythonBase) }));
  }

  [Test]
  public void APythonEnvironment_WhoseHomeIsNoBinFolder_ShowsThatFolder() {
    var tool = this.Make("venv/bin/tool", "python/");
    File.WriteAllText(this.At("venv/pyvenv.cfg"), $"home={this.At("python")}\n");
    Assert.That(Roots(tool), Is.EqualTo(new[] { (this.At("venv"), ToolchainRootKind.PythonEnvironment), (this.At("python"), ToolchainRootKind.PythonBase) }));
  }

  [TestCase("", TestName = "A pyvenv.cfg without home names no base interpreter")]
  [TestCase("home = relative/bin\n", TestName = "A pyvenv.cfg with a relative home names no base interpreter")]
  [TestCase("homes = /x\nnothing\n", TestName = "A pyvenv.cfg with only other keys names no base interpreter")]
  [TestCase("home = /polypiler-no-such-python/bin\n", TestName = "A pyvenv.cfg whose base interpreter is gone names none")]
  public void APythonEnvironment_WithoutAUsableHome_IsItsFolderOnly(string configuration) {
    var tool = this.Make("venv/bin/tool");
    File.WriteAllText(this.At("venv/pyvenv.cfg"), configuration);
    Assert.That(Roots(tool), Is.EqualTo(new[] { (this.At("venv"), ToolchainRootKind.PythonEnvironment) }));
  }

  [Test]
  public void Node_IsTheFolderAboveBin() {
    var node = this.Make("node-v24.22.2-linux-x64/bin/node", "node-v24.22.2-linux-x64/lib/node_modules/npm/package.json");
    Assert.That(Roots(node), Is.EqualTo(new[] { (this.At("node-v24.22.2-linux-x64"), ToolchainRootKind.Node) }));
  }

  [Test]
  public void AnyOtherToolInABinFolder_IsTheFolderAboveIt() {
    var kotlinc = this.Make("kotlinc/bin/kotlinc", "kotlinc/lib/kotlin-preloader.jar", "kotlinc/build.txt");
    Assert.That(Roots(kotlinc), Is.EqualTo(new[] { (this.At("kotlinc"), ToolchainRootKind.Generic) }));
  }

  [Test]
  public void AToolWithoutARootMarker_IsItsOwnFolder_AndSaysWhy() {
    var tool = this.Make("tools/tool");
    var root = ToolchainRoots.Of(tool, _anything).Single();
    Assert.That((root.Path, root.Kind), Is.EqualTo((this.At("tools"), ToolchainRootKind.Folder)));
    Assert.That(root.Reason, Does.Contain("no root marker").And.Contain(this.At("tools")));
  }

  [Test]
  public void AToolInTheRootFolder_IsTheRootFolder_AndSaysWhy() {
    var root = ToolchainRoots.Of("/tool", _anything).Single();
    Assert.That((root.Path, root.Kind), Is.EqualTo(("/", ToolchainRootKind.Folder)));
    Assert.That(root.Reason, Does.Contain("no folder above it"));
  }

  [Test]
  public void ALinkedTool_HasTheRootsOfItsTargetToo() {
    var node = this.Make("node/bin/node");
    Directory.CreateDirectory(this.At("links"));
    File.CreateSymbolicLink(this.At("links/node"), node);
    Assert.That(Roots(this.At("links/node")), Is.EqualTo(new[] { (this.At("links"), ToolchainRootKind.Folder), (this.At("node"), ToolchainRootKind.Node) }));
  }

  [Test]
  public void ARootThatMayNotBeShown_NarrowsToTheToolsFolder() {
    var tool = this.Make("bin/tool");
    var root = ToolchainRoots.Of(tool, folder => folder != this._home).Single();
    Assert.That((root.Path, root.Kind), Is.EqualTo((this.At("bin"), ToolchainRootKind.Folder)));
    Assert.That(root.Reason, Does.Contain($"'{this._home}' would show too much"));
  }

  [Test]
  public void AFolderThatMayNotBeShown_NarrowsToTheToolItself() {
    var tool = this.Make("tool");
    var root = ToolchainRoots.Of(tool, folder => folder != this._home).Single();
    Assert.That((root.Path, root.Kind), Is.EqualTo((tool, ToolchainRootKind.File)));
    Assert.That(root.Reason, Does.Contain("would show too much"));
  }

  [Test]
  public void ABinFolderThatMayNotBeShownEither_NarrowsToTheToolItself() {
    var tool = this.Make("bin/tool");
    Assert.That(Roots(tool, folder => !folder.StartsWith(this._home, StringComparison.Ordinal)), Is.EqualTo(new[] { (tool, ToolchainRootKind.File) }));
  }

  [Test]
  public void AToolThatDoesNotExist_IsJudgedByItsPathAlone()
    => Assert.That(Roots(this.At("absent/bin/tool")), Is.EqualTo(new[] { (this.At("absent"), ToolchainRootKind.Generic) }));

  [Test]
  public void TheArguments_AreChecked() {
    Assert.That(() => ToolchainRoots.Of("", _anything), Throws.ArgumentException);
    Assert.That(() => ToolchainRoots.Of("/tool", null!), Throws.ArgumentNullException);
  }

}
