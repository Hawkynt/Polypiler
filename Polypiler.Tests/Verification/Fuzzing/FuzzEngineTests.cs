using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Core.Verification.Generators;

namespace Polypiler.Tests.Verification.Fuzzing;

/// <summary>The deterministic engine run in process: findings, de-duplication, inputs, records and the test design's boundaries.</summary>
[TestFixture]
public sealed class FuzzEngineTests {

  private static readonly byte[] _seed = [0x78, 0x78, 0x50, 0x4C, 0x59, 0x78, 0x78];

  private FuzzWorkspace _workspace = null!;

  [SetUp]
  public void CreateWorkspace() => this._workspace = new FuzzWorkspace();

  [TearDown]
  public void DeleteWorkspace() => this._workspace.Dispose();

  private FuzzReport Run(IFuzzTarget target, long iterations, ulong seed = 1, params (string, byte[])[] corpus)
    => this._workspace.InProcessEngine().Run(target, this._workspace.Corpus(target.Id, corpus), seed, iterations, FuzzBudget.Default);

  /// <summary>Test design 1: a robust target yields nothing in 10 000 iterations.</summary>
  [Test]
  public void ARobustTarget_YieldsNoFinding() {
    var report = this.Run(new DemoCleanTarget(), 10000, 1, ("seed.bin", [0x41]));
    Assert.That(report.Findings, Is.Empty);
    Assert.That(report.Executed, Is.EqualTo(10000));
    Assert.That(report.WorkerStarts, Is.EqualTo(1));
    Assert.That(report.ExecutionsPerSecond, Is.GreaterThan(0));
    Assert.That(Directory.Exists(this._workspace.Artifacts), Is.False);
  }

  /// <summary>Test design 2 and the first scenario: the crash in the corpus entry, minimized and recorded.</summary>
  [Test]
  public void ACrashingCorpusEntry_IsReportedMinimizedAndRecorded() {
    var report = this.Run(new DemoMarkerTarget(), 1, 1, ("seed.bin", _seed));
    var finding = report.Findings.Single();
    Assert.That(finding.Target, Is.EqualTo("demo-marker"));
    Assert.That(finding.Kind, Is.EqualTo(FindingKind.Crash));
    Assert.That(finding.Iteration, Is.Zero);
    Assert.That(finding.Seed, Is.EqualTo(1));
    Assert.That(finding.Input, Is.EqualTo(_seed));
    Assert.That(finding.Minimized, Is.EqualTo(new byte[] { 0x50, 0x4C, 0x59 }));
    Assert.That(finding.CorpusEntry, Is.EqualTo("corpus/seed.bin"));
    Assert.That(finding.CorpusEntrySha256, Is.EqualTo(FuzzCorpus.Sha256(_seed)));

    var record = report.Records.Single();
    Assert.That(record.WriteError, Is.Null);
    Assert.That(record.JsonPath, Is.EqualTo(Path.Combine(this._workspace.Artifacts, "fuzz", "demo-marker", "b8d17c5a6e84d640.json")));
    Assert.That(File.ReadAllBytes(record.BinaryPath), Is.EqualTo(new byte[] { 0x50, 0x4C, 0x59 }));

    using var json = JsonDocument.Parse(File.ReadAllText(record.JsonPath));
    var root = json.RootElement;
    Assert.That(root.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] {
      "target", "kind", "signature", "seed", "iteration", "engine", "originalSha256", "minimizedLength", "corpusEntry", "corpusEntrySha256", "upstream"
    }));
    Assert.That(root.GetProperty("target").GetString(), Is.EqualTo("demo-marker"));
    Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo("Crash"));
    Assert.That(root.GetProperty("signature").GetString(), Is.EqualTo(finding.Signature));
    Assert.That(root.GetProperty("seed").GetUInt64(), Is.EqualTo(1));
    Assert.That(root.GetProperty("iteration").GetInt64(), Is.Zero);
    Assert.That(root.GetProperty("engine").GetString(), Is.EqualTo("inprocess"));
    Assert.That(root.GetProperty("originalSha256").GetString(), Is.EqualTo(FuzzCorpus.Sha256(_seed)));
    Assert.That(root.GetProperty("minimizedLength").GetInt32(), Is.EqualTo(3));
    Assert.That(root.GetProperty("corpusEntry").GetString(), Is.EqualTo("corpus/seed.bin"));
    Assert.That(root.GetProperty("corpusEntrySha256").GetString(), Is.EqualTo(FuzzCorpus.Sha256(_seed)));
    Assert.That(root.GetProperty("upstream").ValueKind, Is.EqualTo(JsonValueKind.Null));
  }

  [Test]
  public void AnEntryTakenFromUpstream_CarriesItsOriginIntoTheRecord() {
    var directory = this._workspace.Corpus("demo-marker", ("seed.bin", _seed));
    File.WriteAllText(Path.Combine(directory, "corpus", "seed.bin.upstream"), "owner/repo@0123456789abcdef0123456789abcdef01234567:tests/seed.bin\n");
    var report = this._workspace.InProcessEngine().Run(new DemoMarkerTarget(), directory, 1, 1, FuzzBudget.Default);
    using var json = JsonDocument.Parse(File.ReadAllText(report.Records.Single().JsonPath));
    Assert.That(json.RootElement.GetProperty("upstream").GetString(), Is.EqualTo("owner/repo@0123456789abcdef0123456789abcdef01234567:tests/seed.bin"));
  }

  /// <summary>The second scenario and test design 18: mutation finds the zero byte, at the same iteration every time.</summary>
  [Test]
  public void Mutation_FindsAnInputTheCorpusDoesNotContain() {
    var first = this.Run(new DemoZeroTarget(), 10000, 1, ("seed.bin", [0x41]));
    var finding = first.Findings.Single();
    Assert.That(finding.Kind, Is.EqualTo(FindingKind.Crash));
    Assert.That(finding.Minimized, Is.EqualTo(new byte[] { 0x00 }));
    Assert.That(finding.Iteration, Is.GreaterThanOrEqualTo(1));
    Assert.That(finding.CorpusEntry, Is.EqualTo("corpus/seed.bin"));
    Assert.That(Path.GetFileName(first.Records.Single().JsonPath), Is.EqualTo("6e340b9cffb37a98.json"));

    var second = this.Run(new DemoZeroTarget(), 10000, 1, ("seed.bin", [0x41]));
    Assert.That(second.Findings.Single().Iteration, Is.EqualTo(finding.Iteration));
    Assert.That(second.Findings.Single().Input, Is.EqualTo(finding.Input));
  }

  /// <summary>Test design 3: a differential target's disagreement is a mismatch.</summary>
  [Test]
  public void ADisagreement_IsAMismatch() {
    var finding = this.Run(new DemoMismatchTarget(), 1, 1, ("seed.bin", "xxMxx"u8.ToArray())).Findings.Single();
    Assert.That(finding.Kind, Is.EqualTo(FindingKind.Mismatch));
    Assert.That(finding.Minimized, Is.EqualTo("M"u8.ToArray()));
  }

  /// <summary>The fourth scenario, in process: allocation beyond the budget.</summary>
  [Test]
  public void RunawayAllocation_IsAFinding() {
    var finding = this.Run(new DemoAllocTarget(), 1, 1, ("seed.bin", [0x01])).Findings.Single();
    Assert.That((finding.Kind, finding.Signature, finding.Iteration), Is.EqualTo((FindingKind.Allocation, "allocation", 0L)));
    Assert.That(finding.Minimized, Is.EqualTo(new byte[] { 0x01 }));
  }

  /// <summary>Test design 5: a hundred crashes alike are one finding, at the first iteration.</summary>
  [Test]
  public void CrashesWithOneSignature_AreOneFinding() {
    var report = this.Run(new DemoAlwaysTarget(), 100, 1, ("seed.bin", [1, 2, 3]));
    var finding = report.Findings.Single();
    Assert.That(finding.Iteration, Is.Zero);
    Assert.That(finding.Minimized, Is.Empty);
    Assert.That(report.Executed, Is.EqualTo(100));
  }

  /// <summary>Test design 7: 0, 1, the corpus size and one more iteration for a corpus of 3.</summary>
  [Test]
  public void Iterations_AtTheBoundariesOfTheCorpus() {
    byte[][] corpus = [[0x0A], [0x0B, 0x0B], [0x0C, 0x0C, 0x0C]];
    var target = new FuzzOutcomeTests.RecordingTarget();
    var directory = this._workspace.Corpus(target.Id, ("a.bin", corpus[0]), ("b.bin", corpus[1]), ("c.bin", corpus[2]));
    var engine = this._workspace.InProcessEngine();

    var none = engine.Run(target, directory, 1, 0, FuzzBudget.Default);
    Assert.That((none.Executed, target.Lengths.Count), Is.EqualTo((0L, 0)));

    Assert.That(FuzzEngine.Input(corpus, 1, 0, 100), Is.EqualTo(corpus[0]));
    Assert.That(FuzzEngine.Input(corpus, 1, 2, 100), Is.EqualTo(corpus[2]));
    Assert.That(FuzzEngine.Input(corpus, 1, 3, 100), Is.EqualTo(Mutators.Mutate(corpus[0], new SeededRandom(1 + 3), corpus)));
    Assert.That(FuzzEngine.Input(corpus, 1, 4, 100), Is.EqualTo(Mutators.Mutate(corpus[1], new SeededRandom(1 + 4), corpus)));
    Assert.That(FuzzEngine.Input(corpus, 1, 6, 100), Is.EqualTo(Mutators.Mutate(corpus[0], new SeededRandom(1 + 6), corpus)));

    engine.Run(target, directory, 1, 4, FuzzBudget.Default);
    Assert.That(target.Lengths.Take(3), Is.EqualTo(new[] { 1, 2, 3 }));
    Assert.That(target.Lengths, Has.Count.EqualTo(4));
  }

  [Test]
  public void TheInputOfAnIteration_DoesNotChangeTheCorpus() {
    byte[][] corpus = [[0x0A, 0x0B]];
    FuzzEngine.Input(corpus, 1, 0, 100)[0] = 0xFF;
    for (var iteration = 1; iteration < 50; ++iteration)
      FuzzEngine.Input(corpus, 1, iteration, 100);
    Assert.That(corpus[0], Is.EqualTo(new byte[] { 0x0A, 0x0B }));
  }

  /// <summary>Test design 18: the same seed gives the same inputs; another seed the same corpus entries, then other mutations.</summary>
  [Test]
  public void Inputs_DependOnlyOnTheSeedTheIterationAndTheCorpus() {
    byte[][] corpus = [[1, 2, 3], [4, 5]];
    var seedOne = Enumerable.Range(0, 200).Select(i => Convert.ToHexString(FuzzEngine.Input(corpus, 1, i, 100))).ToList();
    var again = Enumerable.Range(0, 200).Select(i => Convert.ToHexString(FuzzEngine.Input(corpus, 1, i, 100))).ToList();
    var seedTwo = Enumerable.Range(0, 200).Select(i => Convert.ToHexString(FuzzEngine.Input(corpus, 2, i, 100))).ToList();
    Assert.That(again, Is.EqualTo(seedOne));
    Assert.That(seedTwo.Take(2), Is.EqualTo(seedOne.Take(2)));
    Assert.That(seedTwo.Skip(2).Zip(seedOne.Skip(2)).Count(pair => pair.First != pair.Second), Is.GreaterThan(100));
  }

  /// <summary>Test design 8: a zero-length entry is executed as an empty span.</summary>
  [Test]
  public void AnEmptyEntry_IsExecutedAsAnEmptySpan() {
    var target = new FuzzOutcomeTests.RecordingTarget();
    this.Run(target, 1, 1, ("empty.bin", []));
    Assert.That(target.Lengths, Is.EqualTo(new[] { 0 }));
  }

  /// <summary>Test design 8: a corpus without entries, or without a directory, cannot be fuzzed.</summary>
  [Test]
  public void AnEmptyOrMissingCorpus_IsRefused() {
    var directory = this._workspace.Corpus("demo-clean");
    File.WriteAllText(Path.Combine(directory, "corpus", "notes.txt"), "not an entry");
    var engine = this._workspace.InProcessEngine();
    Assert.That(() => engine.Run(new DemoCleanTarget(), directory, 1, 1, FuzzBudget.Default),
      Throws.InstanceOf<FuzzCorpusException>().With.Message.StartsWith("corpus is empty"));
    Assert.That(() => engine.Run(new DemoCleanTarget(), Path.Combine(this._workspace.Fixtures, "missing"), 1, 1, FuzzBudget.Default),
      Throws.InstanceOf<FuzzCorpusException>().With.Message.Contains("does not exist"));
  }

  /// <summary>Test design 9: an entry of exactly the length limit runs whole; a mutation beyond it is cut.</summary>
  [Test]
  public void Inputs_AtAndAboveTheLengthLimit() {
    var entry = new byte[65536];
    entry.AsSpan().Fill(0x41);
    byte[][] corpus = [entry];
    Assert.That(FuzzEngine.Input(corpus, 1, 0, 65536), Has.Length.EqualTo(65536));

    var growing = Enumerable.Range(1, 100).First(i => Mutators.Mutate(entry, new SeededRandom(1 + (ulong)i), corpus).Length > 65536);
    var mutated = Mutators.Mutate(entry, new SeededRandom(1 + (ulong)growing), corpus);
    Assert.That(mutated.Length, Is.GreaterThanOrEqualTo(65537));
    Assert.That(FuzzEngine.Input(corpus, 1, growing, 65536), Is.EqualTo(mutated[..65536]));
  }

  [Test]
  public void ALongEntry_IsCutToTheLimitAsWell() {
    var target = new FuzzOutcomeTests.RecordingTarget();
    this._workspace.InProcessEngine().Run(target, this._workspace.Corpus(target.Id, ("long.bin", new byte[10])), 1, 1, new FuzzBudget(TimeSpan.FromSeconds(1), 1 << 20, 4));
    Assert.That(target.Lengths, Is.EqualTo(new[] { 4 }));
  }

  /// <summary>Test design 17: a record that cannot be written leaves the finding reported, with the reason.</summary>
  [Test]
  public void ARecordThatCannotBeWritten_KeepsTheFinding() {
    File.WriteAllText(this._workspace.Artifacts, "a file where the directory should be");
    var report = this.Run(new DemoMarkerTarget(), 1, 1, ("seed.bin", _seed));
    var record = report.Records.Single();
    Assert.That(record.Finding.Minimized, Is.EqualTo(new byte[] { 0x50, 0x4C, 0x59 }));
    Assert.That(record.WriteError, Does.StartWith($"the output directory '{Path.Combine(this._workspace.Artifacts, "fuzz", "demo-marker")}' cannot be written: "));
  }

  /// <summary>Inputs found elsewhere are treated as iterations of a run.</summary>
  [Test]
  public void Confirm_TreatsEachInputAsAnIteration() {
    var engine = new FuzzEngine(target => new InProcessExecutor(target), this._workspace.Artifacts, "afl");
    var report = engine.Confirm(new DemoMarkerTarget(), [[1], _seed, "PLYPLY"u8.ToArray()], 9, new FuzzBudget(TimeSpan.FromSeconds(1), 1 << 20, 5));
    Assert.That(engine.Engine, Is.EqualTo("afl"));
    var finding = report.Findings.Single();
    Assert.That((finding.Iteration, finding.Seed, finding.CorpusEntry, finding.CorpusEntrySha256), Is.EqualTo((1L, 9UL, (string?)null, (string?)null)));
    Assert.That(finding.Input, Is.EqualTo(_seed[..5]));
    using var json = JsonDocument.Parse(File.ReadAllText(report.Records.Single().JsonPath));
    Assert.That(json.RootElement.GetProperty("engine").GetString(), Is.EqualTo("afl"));
  }

  [Test]
  public void InvalidArguments_AreRefused() {
    var engine = this._workspace.InProcessEngine();
    var directory = this._workspace.Corpus("demo-clean", ("seed.bin", [1]));
    Assert.That(() => engine.Run(null!, directory, 1, 1, FuzzBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => engine.Run(new DemoCleanTarget(), directory, 1, 1, null!), Throws.ArgumentNullException);
    Assert.That(() => engine.Run(new DemoCleanTarget(), directory, 1, -1, FuzzBudget.Default), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => engine.Run(new DemoCleanTarget(), null!, 1, 1, FuzzBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => engine.Confirm(null!, [], 1, FuzzBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => engine.Confirm(new DemoCleanTarget(), null!, 1, FuzzBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => engine.Confirm(new DemoCleanTarget(), [], 1, null!), Throws.ArgumentNullException);
    Assert.That(() => FuzzEngine.Input(null!, 1, 0, 1), Throws.ArgumentNullException);
    Assert.That(() => FuzzEngine.Input([], 1, 0, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => FuzzEngine.Input([[1]], 1, -1, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Test]
  public void TheCorpus_IsSortedByPathWithTheRegressionsAfterTheSeeds() {
    var directory = this._workspace.Corpus("demo-clean", ("b.bin", [2]), ("a.bin", [1]));
    this._workspace.Regressions("demo-clean", ("0.bin", [3]));
    var entries = FuzzCorpus.Load(directory);
    Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(new[] { "corpus/a.bin", "corpus/b.bin", "regressions/0.bin" }));
    Assert.That(entries.Select(entry => entry.Content[0]), Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.That(entries.All(entry => entry.Upstream == null), Is.True);
    Assert.That(() => FuzzCorpus.Load(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void AnEngineThatFindsNothing_ReportsNoExecutionsPerSecondForNoTime()
    => Assert.That(new FuzzReport("t", 1, 0, new List<FuzzFindingRecord>(), 0, TimeSpan.Zero).ExecutionsPerSecond, Is.Zero);

}
