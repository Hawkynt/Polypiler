using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Tests.Verification.Manifests;

/// <summary><see cref="ManifestWriter"/>: canonical, deterministic text, written all or nothing.</summary>
[TestFixture]
public sealed class ManifestWriterTests {

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-manifest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteDirectory() {
    if (!OperatingSystem.IsWindows())
      File.SetUnixFileMode(this._directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    Directory.Delete(this._directory, true);
  }

  [Test]
  public void TheSameManifest_GivesTheSameBytes_WhateverTheOrderOfItsSortedLists() {
    var manifest = ManifestFixtures.Model("verified.json");
    var reversed = manifest with { Outputs = [.. manifest.Outputs.Reverse()] };
    var text = ManifestWriter.Serialize(reversed);
    Assert.That((text, ManifestWriter.Serialize(reversed)), Is.EqualTo((ManifestFixtures.Text("verified.json"), text)));
    Assert.That(JsonNode.Parse(text)!["outputs"]!.AsArray().Select(output => output!["path"]!.GetValue<string>()), Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
  }

  [Test]
  public void TheSortedLists_AreSortedOrdinally() {
    var manifest = ManifestFixtures.Minimal with {
      Inputs = [new("b.cs", ManifestFixtures.Abc, null), new("B.cs", ManifestFixtures.Abc, null), new("a.cs", ManifestFixtures.Abc, null)],
      Intrinsics = ["INTRINSICS.Sin", "INTRINSICS.Cos", "INTRINSICS.Abs"],
      Helpers = [new("$b", "MIT", "o", 1, 1, []), new("$a", "MIT", "o", 1, 1, [])],
      Toolchains = [new("node", "24"), new("dotnet-sdk", "10")],
      Caveats = [new("Z", "z"), new("A", "a")]
    };
    var tree = JsonNode.Parse(ManifestWriter.Serialize(manifest))!;
    string[] Values(string list, string? member) => [.. tree[list]!.AsArray().Select(item => (member is null ? item : item![member])!.GetValue<string>())];
    Assert.That(new[] { Values("inputs", "path"), Values("intrinsics", null), Values("helpers", "symbol"), Values("toolchains", "key"), Values("caveats", "code") },
      Is.EqualTo(new[] { new[] { "B.cs", "a.cs", "b.cs" }, ["INTRINSICS.Abs", "INTRINSICS.Cos", "INTRINSICS.Sin"], ["$a", "$b"], ["dotnet-sdk", "node"], ["Z", "A"] }));
  }

  [Test]
  public void EmptyLists_AreWrittenAsEmptyLists_AndTheManifestValidates() {
    var text = ManifestWriter.Serialize(ManifestFixtures.Minimal);
    Assert.That(text, Does.Contain("\"helpers\": [],\n  \"nativeDependencies\": [],"));
    Assert.That(ManifestFixtures.Parse(text).Diagnostics, Is.Empty);
  }

  [Test]
  public void AbsentSections_AreLeftOut_NotWrittenAsNull() {
    var keys = JsonNode.Parse(ManifestWriter.Serialize(ManifestFixtures.Minimal))!.AsObject().Select(member => member.Key);
    Assert.That(keys.Intersect(["floatingPoint", "build", "execution", "verification", "benchmarks", "assumptions", "resources", "objectModel", "profile", "budget"]), Is.Empty);
  }

  [Test]
  public void TheSectionsOfOtherAreas_FollowTheStateInTheirOrder() {
    var manifest = ManifestFixtures.Minimal with {
      Benchmarks = [new JsonObject { ["name"] = "b" }],
      Budget = new JsonObject { ["code"] = 1 },
      Profile = new JsonObject { ["name"] = "p" },
      ObjectModel = new JsonObject { ["features"] = new JsonArray() },
      Resources = new JsonObject { ["ram"] = 1 },
      Assumptions = [new JsonObject { ["target"] = "f" }]
    };
    var text = ManifestWriter.Serialize(manifest);
    var keys = JsonNode.Parse(text)!.AsObject().Select(member => member.Key).ToList();
    Assert.That(keys.SkipWhile(key => key != "benchmarks"), Is.EqualTo(new[] { "benchmarks", "commands", "state", "assumptions", "resources", "objectModel", "profile", "budget" }));
    Assert.That(ManifestFixtures.Parse(text).Manifest is { } read && ManifestWriter.Serialize(read) == text, Is.True);
  }

  [Test]
  public void TheText_IsUtf8WithoutBom_WithLfAndAFinalLineBreak() {
    ManifestWriter.Write(ManifestFixtures.Model("verified.json"), this._directory);
    var bytes = File.ReadAllBytes(Path.Combine(this._directory, Manifest.FileName));
    Assert.That((bytes[0], bytes.Contains((byte)'\r'), bytes[^1], bytes[^2] != (byte)'\n'), Is.EqualTo(((byte)'{', false, (byte)'\n', true)));
    Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo(ManifestFixtures.Text("verified.json")));
  }

  [Test]
  public void TheText_HoldsNoAbsolutePathAndNoTimestamp() {
    var text = ManifestWriter.Serialize(ManifestFixtures.Model("verified.json"));
    Assert.That(Regex.IsMatch(text, "\"(/|[A-Za-z]:[\\\\/])"), Is.False);
    Assert.That(Regex.IsMatch(text, @"\d{4}-\d{2}-\d{2}"), Is.False);
    Assert.That(text, Does.Not.Contain(Environment.MachineName));
  }

  [Test]
  public void HashesAndPaths_AreWrittenCanonically() {
    var manifest = ManifestFixtures.Minimal with {
      Inputs = [new("..\\src\\a.cs", ManifestFixtures.Abc.ToUpperInvariant(), new("o/r", new string('a', 40), "src\\a.cs"))],
      Outputs = [new("sub\\a.mjs", ManifestFixtures.Empty, ManifestOutputRole.Source)]
    };
    var tree = JsonNode.Parse(ManifestWriter.Serialize(manifest))!;
    Assert.That((tree["inputs"]![0]!["path"]!.GetValue<string>(), tree["inputs"]![0]!["sha256"]!.GetValue<string>(), tree["inputs"]![0]!["provenance"]!["path"]!.GetValue<string>(),
      tree["outputs"]![0]!["path"]!.GetValue<string>()), Is.EqualTo(("../src/a.cs", ManifestFixtures.Abc, "src/a.cs", "sub/a.mjs")));
  }

  [TestCase("", ManifestFixtures.Empty)]
  [TestCase("abc", ManifestFixtures.Abc)]
  public void Sha256_IsTheFips180_4Value(string text, string hash)
    => Assert.That(ManifestWriter.Sha256(Encoding.ASCII.GetBytes(text)), Is.EqualTo(hash));

  [TestCase(ManifestOutputRole.Source, "source")]
  [TestCase(ManifestOutputRole.Test, "test")]
  [TestCase(ManifestOutputRole.Helper, "helper")]
  [TestCase(ManifestOutputRole.Build, "build")]
  [TestCase(ManifestOutputRole.Notice, "notice")]
  public void EveryRole_HasItsName(ManifestOutputRole role, string name)
    => Assert.That(ManifestWriter.RoleName(role), Is.EqualTo(name));

  [Test]
  public void Write_ReplacesAnExistingManifest_AndLeavesNoTemporaryFile() {
    File.WriteAllText(Path.Combine(this._directory, Manifest.FileName), "old");
    ManifestWriter.Write(ManifestFixtures.Minimal, this._directory);
    Assert.That((Directory.GetFiles(this._directory).Select(Path.GetFileName).Single(), File.ReadAllText(Path.Combine(this._directory, Manifest.FileName))),
      Is.EqualTo((Manifest.FileName, ManifestWriter.Serialize(ManifestFixtures.Minimal))));
  }

  [Test]
  public void Write_IntoADirectoryThatCannotBeWritten_FailsWithoutAPartialFile() {
    if (OperatingSystem.IsWindows() || Environment.UserName == "root") {
      Assert.Ignore("a directory is read-only only on Unix, and not for root");
      return;
    }

    File.SetUnixFileMode(this._directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    Assert.That(() => ManifestWriter.Write(ManifestFixtures.Minimal, this._directory), Throws.InstanceOf<UnauthorizedAccessException>().Or.InstanceOf<IOException>());
    Assert.That(Directory.GetFiles(this._directory), Is.Empty);
  }

  [Test]
  public void Write_WhenTheRenameFails_RemovesTheTemporaryFile() {
    // A directory of the manifest's name cannot be replaced by the renamed file.
    Directory.CreateDirectory(Path.Combine(this._directory, Manifest.FileName));
    Assert.That(() => ManifestWriter.Write(ManifestFixtures.Minimal, this._directory), Throws.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
    Assert.That(Directory.GetFiles(this._directory), Is.Empty);
  }

  [Test]
  public void TheArguments_AreChecked() {
    Assert.That(() => ManifestWriter.Serialize(null!), Throws.ArgumentNullException);
    Assert.That(() => ManifestWriter.Write(ManifestFixtures.Minimal, null!), Throws.ArgumentNullException);
    Assert.That(() => ManifestWriter.PortablePath(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void TheDiagnostics_AreRegisteredWithExitCode1() {
    Assert.That((ManifestDiagnostics.Invalid.Code, ManifestDiagnostics.Invalid.ExitCode, ManifestDiagnostics.StateUnsupported.Code, ManifestDiagnostics.StateUnsupported.ExitCode),
      Is.EqualTo(("PLY0053", (ExitCode?)ExitCode.EnvironmentError, "PLY0054", (ExitCode?)ExitCode.EnvironmentError)));
    Assert.That((int)ExitCode.EnvironmentError, Is.EqualTo(1));
  }

}
