using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Running;

/// <summary>The limits of a run and their defaults in <c>tooling/ci/runner.yaml</c>.</summary>
[TestFixture]
public sealed class RunLimitsTests {

  private const string Valid = """
    schema: 1
    limits:
      wallTimeMs: 500
      memoryBytes: 1024
      outputBytes: 0
      processes: 1
    """;

  [Test]
  public void TheDefaults_AreTheCeilingsOfTheIssue() {
    var limits = RunLimits.Default;
    Assert.That(limits.WallTime, Is.EqualTo(TimeSpan.FromSeconds(60)));
    Assert.That(limits.MemoryBytes, Is.EqualTo(1L << 30));
    Assert.That(limits.OutputBytes, Is.EqualTo(16L << 20));
    Assert.That(limits.Processes, Is.EqualTo(64));
  }

  [Test]
  public void TheEmbeddedDefaults_AreTheRepositoryFile()
    => Assert.That(RunLimits.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "ci", "runner.yaml"))), Is.EqualTo(RunLimits.Default));

  [Test]
  public void AFile_IsRead_AtTheSmallestValuesAllowed()
    => Assert.That(RunLimits.Parse(Valid), Is.EqualTo(new RunLimits(TimeSpan.FromMilliseconds(500), 1024, 0, 1)));

  [TestCase("", TestName = "An empty file")]
  [TestCase("schema: 2\nlimits: { wallTimeMs: 1, memoryBytes: 1, outputBytes: 1, processes: 1 }", TestName = "Another schema")]
  [TestCase("schema: 1", TestName = "No limits")]
  [TestCase("schema: 1\nlimits: { memoryBytes: 1, outputBytes: 1, processes: 1 }", TestName = "No wall time")]
  [TestCase("schema: 1\nlimits: { wallTimeMs: 1, outputBytes: 1, processes: 1 }", TestName = "No memory")]
  [TestCase("schema: 1\nlimits: { wallTimeMs: 1, memoryBytes: 1, processes: 1 }", TestName = "No output")]
  [TestCase("schema: 1\nlimits: { wallTimeMs: 1, memoryBytes: 1, outputBytes: 1 }", TestName = "No process count")]
  public void AnIncompleteFile_IsAFormatException(string text)
    => Assert.That(() => RunLimits.Parse(text), Throws.TypeOf<FormatException>().With.Message.Contains("expected schema 1"));

  [TestCase("schema: 1\ncolour: red", "colour", TestName = "An unknown key")]
  [TestCase("schema: [1", "", TestName = "Broken YAML")]
  [TestCase("schema: 1\nlimits: { processes: many }", "", TestName = "A word for a number")]
  public void AnUnreadableFile_IsAFormatExceptionWithItsPosition(string text, string named)
    => Assert.That(() => RunLimits.Parse(text), Throws.TypeOf<FormatException>().With.Message.StartsWith("runner.yaml(").And.Message.Contains(named));

  [TestCase(0, 1, 0, 1, "wall time")]
  [TestCase(1, 0, 0, 1, "memory limit")]
  [TestCase(1, 1, -1, 1, "output limit")]
  [TestCase(1, 1, 0, 0, "process limit")]
  public void AValueOutOfRange_IsRefused(long wall, long memory, long output, int processes, string named) {
    Assert.That(() => new RunLimits(TimeSpan.FromMilliseconds(wall), memory, output, processes), Throws.TypeOf<ArgumentOutOfRangeException>().With.Message.Contains(named));
    Assert.That(() => RunLimits.Parse($"schema: 1\nlimits: {{ wallTimeMs: {wall}, memoryBytes: {memory}, outputBytes: {output}, processes: {processes} }}"),
      Throws.TypeOf<FormatException>().With.Message.Contains(named));
  }

}
