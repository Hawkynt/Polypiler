using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Ci;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Ci;

/// <summary>The layer files of <c>tooling/ci/layers</c>: schema, name and path selection.</summary>
[TestFixture]
public sealed class CiLayerTests {

  private const string Core = "schema: 1\nlayer: core\ntoolchains: [dotnet-sdk]\nos: [ubuntu-latest]\npaths: [\"**\"]\nalways: true\n";

  private static string Messages(string text, string file = "tooling/ci/layers/core.yaml")
    => string.Join("\n", CiLayer.Parse(text, file, out _).Select(problem => $"{problem.Code}: {problem.Message}"));

  [Test]
  public void Parse_ValidFile_GivesTheLayer() {
    Assert.That(CiLayer.Parse(Core, "tooling/ci/layers/core.yaml", out var layer), Is.Empty);
    Assert.That(layer, Is.Not.Null);
    Assert.That((layer!.Name, layer.File, layer.Job, layer.Always), Is.EqualTo(("core", "tooling/ci/layers/core.yaml", "layer-core", true)));
    Assert.That((layer.Toolchains, layer.Os, layer.Paths), Is.EqualTo((new[] { "dotnet-sdk" }, new[] { "ubuntu-latest" }, new[] { "**" })));
  }

  /// <summary>Test design 10: one toolchain, every toolchain the lock holds, and none.</summary>
  [Test]
  public void Parse_ToolchainCount_OneAndAllAreValid_NoneIsASchemaError() {
    var all = ToolchainLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath)).Entries.Select(entry => entry.Key);
    Assert.That(CiLayer.Parse(Core.Replace("[dotnet-sdk]", $"[{string.Join(", ", all)}]"), "core.yaml", out var layer), Is.Empty);
    Assert.That(layer!.Toolchains, Has.Count.GreaterThan(26));
    Assert.That(Messages(Core.Replace("[dotnet-sdk]", "[]")), Does.StartWith("PLY0075: tooling/ci/layers/core.yaml: /toolchains: "));
  }

  [TestCase("schema: 2\n", "/schema: ")]
  [TestCase("extra: 1\n", "/extra: ")]
  [TestCase("os: []\n", "/os: ")]
  [TestCase("always: yes\n", "/always: ")]
  public void Parse_SchemaViolation_NamesTheValue(string replacement, string pointer) {
    var key = replacement.Split(':')[0];
    var text = Core.Contains(key + ":") ? string.Join("\n", Core.Split('\n').Select(line => line.StartsWith(key + ":") ? replacement.TrimEnd('\n') : line)) : Core + replacement;
    Assert.That(Messages(text), Does.Contain("PLY0075: tooling/ci/layers/core.yaml: " + pointer));
  }

  [Test]
  public void Parse_MissingKey_IsReportedAtTheRoot()
    => Assert.That(Messages("schema: 1\nlayer: core\n"), Does.StartWith("PLY0075: tooling/ci/layers/core.yaml: /: "));

  [Test]
  public void Parse_EmptyDocument_IsNotAnObject()
    => Assert.That(Messages(""), Does.StartWith("PLY0075: tooling/ci/layers/core.yaml: /: "));

  [Test]
  public void Parse_NotYaml_IsReported()
    => Assert.That(Messages("layer: [core"), Does.StartWith("PLY0075: tooling/ci/layers/core.yaml: not valid YAML"));

  [Test]
  public void Parse_NameOtherThanTheFile_IsReported()
    => Assert.That(Messages(Core, "tooling/ci/layers/rust.yaml"), Is.EqualTo("PLY0075: tooling/ci/layers/rust.yaml: layer 'core' does not match the file name 'rust'"));

  /// <summary>Test design 7: zero layer files, and exactly one.</summary>
  [Test]
  public void LoadAll_NoLayerFile_IsReported_OneIsLoaded() {
    using var tree = new CiTree();
    tree.Delete("tooling/ci/layers/core.yaml").Delete("tooling/ci/layers/rust.yaml");
    var (none, problems) = CiLayer.LoadAll(tree.Root);
    Assert.That((none, problems.Select(problem => problem.Message)), Is.EqualTo((new CiLayer[0], new[] { "no layer files found in tooling/ci/layers" })));
    tree.Write("tooling/ci/layers/core.yaml", Core);
    var (one, clean) = CiLayer.LoadAll(tree.Root);
    Assert.That((one.Select(layer => layer.Name), clean), Is.EqualTo((new[] { "core" }, new object[0])));
  }

  [Test]
  public void LoadAll_NoFolder_IsReported() {
    var (layers, problems) = CiLayer.LoadAll(Path.Combine(CiTesting.Fixtures, "warn"));
    Assert.That((layers.Count, problems.Single().Code), Is.EqualTo((0, "PLY0075")));
  }

  [Test]
  public void LoadAll_InvalidFile_KeepsTheValidOnesSorted() {
    using var tree = new CiTree();
    tree.Write("tooling/ci/layers/broken.yaml", "schema: 1\n");
    var (layers, problems) = CiLayer.LoadAll(tree.Root);
    Assert.That(layers.Select(layer => layer.Name), Is.EqualTo(new[] { "core", "rust" }));
    Assert.That(problems.Select(problem => problem.Message), Has.All.StartWith("tooling/ci/layers/broken.yaml: "));
  }

  /// <summary>Test design 9: <c>**</c> matches a file at the root and a deeply nested one.</summary>
  [TestCase("a")]
  [TestCase("a/b/c/d.cs")]
  [TestCase("a\\b\\c\\d.cs")]
  public void Selects_DoubleStar_MatchesEveryDepth(string path)
    => Assert.That(new CiLayer("x", "x.yaml", ["dotnet-sdk"], ["ubuntu-latest"], ["**"], false).Selects(path), Is.True);

  [TestCase("Polypiler.Core.Backends.Rust/Emitter.cs", true)]
  [TestCase("Polypiler.Core.Backends.Rust/Deep/Emitter.cs", true)]
  [TestCase("README.md", false)]
  public void Selects_Glob_MatchesItsFolderOnly(string path, bool selected)
    => Assert.That(new CiLayer("rust", "rust.yaml", ["rust"], ["ubuntu-latest"], ["Polypiler.Core.Backends.Rust/**"], false).Selects(path), Is.EqualTo(selected));

  [Test]
  public void Selects_Always_WhateverChanged()
    => Assert.That(new CiLayer("core", "core.yaml", ["dotnet-sdk"], ["ubuntu-latest"], [], true).Selects("anything"), Is.True);

}
