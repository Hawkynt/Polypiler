using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli.Verify;

namespace Polypiler.Tests.Cli.Verify;

/// <summary>Finding a toolchain's executable on a search path, as the shipped executable does.</summary>
[TestFixture]
public sealed class PathToolchainResolverTests {

  private string _first = "";
  private string _second = "";

  [SetUp]
  public void CreateTwoDirectories() {
    var root = Path.Combine(Path.GetTempPath(), "polypiler-path-" + Guid.NewGuid().ToString("N"));
    this._first = Path.Combine(root, "first");
    this._second = Path.Combine(root, "second");
    Directory.CreateDirectory(this._first);
    Directory.CreateDirectory(this._second);
  }

  [TearDown]
  public void DeleteThem() => Directory.Delete(Path.GetDirectoryName(this._first)!, true);

  private string SearchPath => this._first + Path.PathSeparator + " " + Path.PathSeparator + this._second;

  [Test]
  public void TheFirstDirectoryThatHasTheExecutable_Wins() {
    File.WriteAllText(Path.Combine(this._second, "node"), "");

    Assert.That(new PathToolchainResolver(this.SearchPath, []).Resolve("node").Executable, Is.EqualTo(Path.Combine(this._second, "node")));
  }

  [Test]
  public void AnExtension_IsTriedAfterTheBareName() {
    File.WriteAllText(Path.Combine(this._first, "node.exe"), "");

    Assert.That(new PathToolchainResolver(this.SearchPath, [".com", ".exe"]).Resolve("node").Executable, Is.EqualTo(Path.Combine(this._first, "node.exe")));
  }

  [TestCase(null)]
  [TestCase("")]
  [TestCase("own")]
  public void AToolchainNowhereOnThePath_IsNotInstalled(string? searchPath) {
    var problem = new PathToolchainResolver(searchPath == "own" ? this.SearchPath : searchPath, [".exe"]).Resolve("node").Problem!;

    Assert.That((problem.Code, problem.Message), Is.EqualTo(("PLY0021", "toolchain 'node' is not installed (probe: node --version): 'node' was not found on PATH")));
  }

  [TestCase(null, new[] { ".exe", ".cmd" })]
  [TestCase(" ", new[] { ".exe", ".cmd" })]
  [TestCase(".COM;.Exe; ;.BAT", new[] { ".com", ".exe", ".bat" })]
  public void Extensions_AreThoseOfPathExt(string? pathExt, string[] extensions)
    => Assert.That(PathToolchainResolver.Extensions(pathExt), Is.EqualTo(extensions));

  [Test]
  public void TheResolverOfThisProcess_FindsWhatItsPathHas() {
    var resolver = PathToolchainResolver.FromEnvironment();

    Assert.That(resolver.Resolve("no-such-toolchain-of-polypiler").Problem!.Code, Is.EqualTo("PLY0021"));
  }

  [Test]
  public void TheArguments_AreChecked() {
    Assert.That(() => new PathToolchainResolver("", null!), Throws.ArgumentNullException);
    Assert.That(() => new PathToolchainResolver("", []).Resolve(""), Throws.ArgumentException);
  }

}
