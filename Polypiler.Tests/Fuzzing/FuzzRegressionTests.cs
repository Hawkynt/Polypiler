using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli.Fuzzing;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Verification.Fuzzing;

namespace Polypiler.Tests.Fuzzing;

/// <summary>
/// Every input of a fixed finding, <c>tests/fixtures/fuzz/&lt;target&gt;/regressions/*.bin</c>, replayed
/// against its target under the default budget: a fixed crash stays fixed. A finding is promoted to a
/// regression by copying its <c>.bin</c> from <c>artifacts/fuzz/&lt;target&gt;/</c>; the file is never
/// deleted to make this test pass.
/// </summary>
[TestFixture]
public sealed class FuzzRegressionTests {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "fuzz");

  /// <summary>The targets of the test assembly and of the CLI.</summary>
  private static IReadOnlyDictionary<string, IFuzzTarget> Targets()
    => FuzzTargets.Discover(typeof(DemoMarkerTarget).Assembly).Concat(FuzzTargets.Discover(typeof(CliArgsFuzzTarget).Assembly))
      .ToDictionary(target => target.Id, StringComparer.Ordinal);

  [Test]
  public void EveryRegressionInput_PassesItsTarget() {
    var failures = FuzzRegressions.Replay(_fixtures, Targets(), target => new InProcessExecutor(target), FuzzBudget.Default);
    Assert.That(failures.Select(failure => failure.ToString()), Is.Empty);
  }

  [Test]
  public void EveryTargetDirectory_BelongsToATargetAndHasASeedCorpus() {
    var targets = Targets();
    var directories = Directory.GetDirectories(_fixtures).Select(directory => Path.GetFileName(directory)!).ToList();
    Assert.That(directories, Is.EquivalentTo(new[] { "cli-args", "demo-alloc", "demo-hang", "demo-marker", "demo-zero" }));
    foreach (var directory in directories) {
      Assert.That(targets, Does.ContainKey(directory));
      Assert.That(FuzzCorpus.Load(Path.Combine(_fixtures, directory)), Is.Not.Empty);
    }
  }

  [Test]
  public void TheSeedCorpora_HoldTheBytesTheIssueNames() {
    byte[] Seed(string target) => File.ReadAllBytes(Path.Combine(_fixtures, target, "corpus", "seed.bin"));
    Assert.That(Seed("demo-marker"), Is.EqualTo(new byte[] { 0x78, 0x78, 0x50, 0x4C, 0x59, 0x78, 0x78 }));
    Assert.That(Seed("demo-zero"), Is.EqualTo(new byte[] { 0x41 }));
    Assert.That(Seed("demo-hang"), Is.EqualTo(new byte[] { 0xFF }));
    Assert.That(Seed("demo-alloc"), Is.EqualTo(new byte[] { 0x01 }));
    var cli = FuzzCorpus.Load(Path.Combine(_fixtures, "cli-args"));
    Assert.That(cli.Select(entry => CliArgsFuzzTarget.Arguments(entry.Content)), Is.EqualTo(new[] {
      new[] { "--help" }, new[] { "frobnicate", "--force" }, new[] { "--version" }
    }));
  }

  /// <summary>Test design 15: a regression input that fails again fails the replay, naming the file.</summary>
  [Test]
  public void ARegressionThatFailsAgain_IsNamed() {
    using var workspace = new FuzzWorkspace();
    workspace.Regressions("demo-zero", ("back.bin", [0x00]), ("fixed.bin", [0x41]));
    workspace.Regressions("demo-marker", ("long.bin", [.. new byte[70000], .. "PLY"u8]));
    workspace.Regressions("no-such-target", ("orphan.bin", [1]));
    workspace.Corpus("demo-hang", ("seed.bin", [0xFF]));
    var failures = FuzzRegressions.Replay(workspace.Fixtures, Targets(), target => new InProcessExecutor(target), FuzzBudget.Default);
    Assert.That(failures.Select(failure => failure.ToString()), Is.EqualTo(new[] {
      $"{Path.Combine(workspace.Fixtures, "demo-zero", "regressions", "back.bin")}: Crash 'crash:System.ArgumentException@{typeof(DemoZeroTarget).FullName}.Execute'",
      $"{Path.Combine(workspace.Fixtures, "no-such-target", "regressions", "orphan.bin")}: no fuzz target 'no-such-target' is registered"
    }));
    Assert.That(failures[0].Target, Is.EqualTo("demo-zero"));
  }

  [Test]
  public void Replay_OfOneTargetOrOfNothing() {
    using var workspace = new FuzzWorkspace();
    workspace.Regressions("demo-zero", ("back.bin", [0x00]));
    var targets = Targets();
    Assert.That(FuzzRegressions.Replay(workspace.Fixtures, targets, target => new InProcessExecutor(target), FuzzBudget.Default, "demo-marker"), Is.Empty);
    Assert.That(FuzzRegressions.Replay(workspace.Fixtures, targets, target => new InProcessExecutor(target), FuzzBudget.Default, "demo-zero"), Has.Count.EqualTo(1));
    Assert.That(FuzzRegressions.Replay(Path.Combine(workspace.Root, "missing"), targets, target => new InProcessExecutor(target), FuzzBudget.Default), Is.Empty);
    Assert.That(() => FuzzRegressions.Replay(null!, targets, target => new InProcessExecutor(target), FuzzBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => FuzzRegressions.Replay(workspace.Fixtures, null!, target => new InProcessExecutor(target), FuzzBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => FuzzRegressions.Replay(workspace.Fixtures, targets, null!, FuzzBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => FuzzRegressions.Replay(workspace.Fixtures, targets, target => new InProcessExecutor(target), null!), Throws.ArgumentNullException);
  }

}
