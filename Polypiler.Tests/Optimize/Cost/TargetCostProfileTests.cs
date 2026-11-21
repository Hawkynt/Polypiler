using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Optimize.Cost;
using CostValue = Polypiler.Core.Optimize.Cost.Cost;

namespace Polypiler.Tests.Optimize.Cost;

/// <summary>
/// The loader of <c>cost.yaml</c>: the two test profiles read back as written, and every malformed
/// profile refused with <c>PLY0356</c> naming the key path (rows 21 to 24 of the test design of #155).
/// </summary>
[TestFixture]
public sealed class TargetCostProfileTests {

  private static Diagnostic Refusal(string yaml)
    => Assert.Throws<CostProfileException>(() => TargetCostProfile.Parse(yaml, "p.yaml"))!.Diagnostic;

  private static string ProblemOf(string yaml) => Refusal(yaml).Message;

  [Test]
  public void Wide_ReadsBackAsWritten() {
    var profile = TargetCostProfile.Load(CostFixtures.PathOf("wide"));
    Assert.That((profile.Name, profile.UnknownTripCount, profile.Default, profile.LoopOverhead), Is.EqualTo(("wide", 16L, new CostValue(2, 2), 2L)));
    Assert.That(profile.Operations[CostFixtures.Rotate], Is.EqualTo(new OperationForms(new CostValue(1, 1), new CostValue(3, 3), new HelperForm(new(12, 2), new(0, 20)))));
    Assert.That((profile.Swar, profile.Simd.Single(), profile.Threads), Is.EqualTo((new SwarForm(64, 4, 1), new SimdForm(128, 4, 1), new ThreadsForm(4, 10000))));
    Assert.That((profile.Gpu, profile.Npu, profile.Path), Is.EqualTo(((AcceleratorForm?)null, (AcceleratorForm?)null, CostFixtures.PathOf("wide"))));
  }

  [Test]
  public void Narrow_HasNoNativeRotateNoSimdAndNoThreads() {
    var profile = TargetCostProfile.Load(CostFixtures.PathOf("narrow"));
    Assert.That(profile.Operations[CostFixtures.Rotate].Native, Is.Null);
    Assert.That((profile.Simd.Count, profile.Threads), Is.EqualTo((0, (ThreadsForm?)null)));
  }

  [Test]
  public void OptionalCostComponents_AreRead() {
    var profile = TargetCostProfile.Parse(CostFixtures.Minimal.Replace("default: { latency: 2, size: 2 }", "default: { latency: 2, size: 2, memory: 3, allocations: 4 }") + "gpu: { transfer: 7, perElement: 8 }\nnpu: { transfer: 9, perElement: 10 }\n", "p.yaml");
    Assert.That((profile.Default, profile.Gpu, profile.Npu), Is.EqualTo((new CostValue(2, 2, 3, 4), new AcceleratorForm(7, 8), new AcceleratorForm(9, 10))));
    Assert.That(profile.Operations, Is.Empty);
  }

  // Row 21.
  [Test]
  public void TargetWithoutCostProfile_IsPly0355() {
    var exception = Assert.Throws<CostProfileException>(() => TargetCostProfile.ForTarget(Path.Combine(CostFixtures.Directory, "no-spec"), "csharp/net20"))!;
    Assert.That((exception.Diagnostic.Code, exception.Diagnostic.Message), Is.EqualTo(("PLY0355", "target profile 'csharp/net20' has no cost profile")));
    Assert.That(exception.Diagnostic.ExitCode, Is.EqualTo(ExitCode.UnsupportedConstruct));
    Assert.That(exception.Message, Is.EqualTo(exception.Diagnostic.Message));
  }

  [TestCase("csharp")]
  [TestCase("csharp/")]
  [TestCase("/net20")]
  [TestCase("csharp/net20/x")]
  public void PathOf_RefusesATargetThatIsNotLanguageSlashProfile(string target)
    => Assert.Throws<ArgumentException>(() => TargetCostProfile.PathOf("spec", target));

  [Test]
  public void PathOf_IsTheFileBesideTheTargetDocument() {
    Assert.That(TargetCostProfile.PathOf("spec", "csharp/net2.0"), Is.EqualTo(Path.Combine("spec", "targets", "csharp", "net2.0", "cost.yaml")));
    Assert.Throws<ArgumentNullException>(() => TargetCostProfile.PathOf(null!, "a/b"));
    Assert.Throws<ArgumentNullException>(() => TargetCostProfile.PathOf("spec", null!));
  }

  // Row 22.
  [Test]
  public void NegativeNumber_IsPly0356WithTheKeyPath() {
    var exception = Assert.Throws<CostProfileException>(() => TargetCostProfile.Load(CostFixtures.PathOf("invalid-negative")))!;
    var diagnostic = exception.Diagnostic;
    Assert.That((diagnostic.Code, diagnostic.ExitCode), Is.EqualTo(("PLY0356", (ExitCode?)ExitCode.InvalidSource)));
    Assert.That(diagnostic.Message, Is.EqualTo($"cost profile '{CostFixtures.PathOf("invalid-negative")}': operations.RotateLeft.u32.native.latency: a non-negative integer is required"));
    Assert.That((diagnostic.File, diagnostic.Line), Is.EqualTo((CostFixtures.PathOf("invalid-negative"), 7)));
  }

  // Row 23.
  [Test]
  public void MissingDefault_IsPly0356() => Assert.That(
    Assert.Throws<CostProfileException>(() => TargetCostProfile.Load(CostFixtures.PathOf("invalid-missing-default")))!.Diagnostic.Message,
    Does.EndWith(": default: required"));

  // Row 24.
  [Test]
  public void UnknownOperation_IsPly0356() => Assert.That(
    Assert.Throws<CostProfileException>(() => TargetCostProfile.Load(CostFixtures.PathOf("invalid-unknown-operation")))!.Diagnostic.Message,
    Does.EndWith(": operations.Rotat.u32: unknown operation"));

  [TestCase("operations:\n  RotateLeft:\n    native: { latency: 1, size: 1 }\n", "operations.RotateLeft: unknown operation")]
  [TestCase("operations:\n  RotateLeft.str:\n    native: { latency: 1, size: 1 }\n", "operations.RotateLeft.str: unknown operation")]
  [TestCase("operations:\n  RotateLeft.u32: {}\n", "operations.RotateLeft.u32: at least one of native, sequence, helper is required")]
  [TestCase("operations:\n  RotateLeft.u32:\n    fast: { latency: 1, size: 1 }\n", "operations.RotateLeft.u32.fast: unknown key")]
  [TestCase("operations:\n  RotateLeft.u32:\n    helper: { call: { latency: 1, size: 1 } }\n", "operations.RotateLeft.u32.helper.body: required")]
  [TestCase("operations:\n  RotateLeft.u32:\n    native: { latency: 1 }\n", "operations.RotateLeft.u32.native.size: required")]
  [TestCase("operations:\n  RotateLeft.u32:\n    native: { latency: 1.5, size: 1 }\n", "operations.RotateLeft.u32.native.latency: a non-negative integer is required")]
  [TestCase("operations:\n  RotateLeft.u32:\n    native: { latency: '1', size: 1 }\n", "operations.RotateLeft.u32.native.latency: a non-negative integer is required")]
  [TestCase("operations:\n  RotateLeft.u32:\n    native: { latency: [1], size: 1 }\n", "operations.RotateLeft.u32.native.latency: a non-negative integer is required")]
  [TestCase("operations:\n  RotateLeft.u32:\n    native: { latency: 99999999999999999999, size: 1 }\n", "operations.RotateLeft.u32.native.latency: a non-negative integer is required")]
  [TestCase("operations: [1]\n", "operations: a mapping is required")]
  [TestCase("simd: { width: 128 }\n", "simd: a list is required")]
  [TestCase("simd:\n  - { width: 0, setup: 1, perBlock: 1 }\n", "simd[0].width: a positive integer is required")]
  [TestCase("simd:\n  - { width: 128, setup: 1, perBlock: 1 }\n  - { width: 256, setup: 1 }\n", "simd[1].perBlock: required")]
  [TestCase("swar: { registerBits: 0, setup: 1, perBlock: 1 }\n", "swar.registerBits: a positive integer is required")]
  [TestCase("threads: { count: 0, spawn: 1 }\n", "threads.count: a positive integer is required")]
  [TestCase("threads: { count: 2 }\n", "threads.spawn: required")]
  [TestCase("gpu: { transfer: 1 }\n", "gpu.perElement: required")]
  [TestCase("npu: 3\n", "npu: a mapping is required")]
  [TestCase("tpu: { transfer: 1, perElement: 1 }\n", "tpu: unknown key")]
  [TestCase("? [a]\n: 1\n", "(document): every key must be a name")]
  public void MalformedProfile_IsPly0356NamingTheKey(string extra, string problem) {
    var diagnostic = Refusal(CostFixtures.Minimal + extra);
    Assert.That(diagnostic.Code, Is.EqualTo("PLY0356"));
    Assert.That(diagnostic.Message, Is.EqualTo($"cost profile 'p.yaml': {problem}"));
  }

  [TestCase("default: { latency: 2, size: 2 }\nloop: { overhead: 2 }\nunknownTripCount: 1\n", "profile: required")]
  [TestCase("profile: ''\nunknownTripCount: 16\ndefault: { latency: 2, size: 2 }\nloop: { overhead: 2 }\n", "profile: a name is required")]
  [TestCase("profile: [a]\nunknownTripCount: 16\ndefault: { latency: 2, size: 2 }\nloop: { overhead: 2 }\n", "profile: a name is required")]
  [TestCase("profile: x\ndefault: { latency: 2, size: 2 }\nloop: { overhead: 2 }\n", "unknownTripCount: required")]
  [TestCase("profile: x\nunknownTripCount: -3\ndefault: { latency: 2, size: 2 }\nloop: { overhead: 2 }\n", "unknownTripCount: a non-negative integer is required")]
  [TestCase("profile: x\nunknownTripCount: 16\ndefault: { latency: 2, size: 2 }\n", "loop: required")]
  [TestCase("profile: x\nunknownTripCount: 16\ndefault: { latency: 2, size: 2 }\nloop: {}\n", "loop.overhead: required")]
  [TestCase("profile: x\nunknownTripCount: 16\ndefault: 5\nloop: { overhead: 2 }\n", "default: a mapping is required")]
  [TestCase("- 1\n", "(document): a mapping is required")]
  [TestCase("", "(document): exactly one YAML document is required")]
  [TestCase("a: 1\n---\nb: 2\n", "(document): exactly one YAML document is required")]
  public void MalformedTopLevel_IsPly0356NamingTheKey(string yaml, string problem)
    => Assert.That(ProblemOf(yaml), Is.EqualTo($"cost profile 'p.yaml': {problem}"));

  [TestCase("a: [1\n")]
  [TestCase("a: 1\na: 2\n")]
  public void InvalidYaml_IsPly0356OfTheDocument(string yaml) {
    var diagnostic = Refusal(yaml);
    Assert.That(diagnostic.Message, Does.StartWith("cost profile 'p.yaml': (document): "));
    Assert.That(diagnostic.Line, Is.Positive);
  }

  [Test]
  public void Load_RefusesNullArguments() {
    Assert.Throws<ArgumentNullException>(() => TargetCostProfile.Load(null!));
    Assert.Throws<ArgumentNullException>(() => TargetCostProfile.Parse(null!, "p.yaml"));
    Assert.Throws<ArgumentNullException>(() => TargetCostProfile.Parse("", null!));
  }

  [Test]
  public void Load_OfAFileThatDoesNotExist_ThrowsAnIoError()
    => Assert.Throws<FileNotFoundException>(() => TargetCostProfile.Load(Path.Combine(CostFixtures.Directory, "absent.yaml")));

  [TestCase("RotateLeft.u32", "RotateLeft", "u32", true, 32)]
  [TestCase("Xor.u8", "Xor", "u8", true, 8)]
  [TestCase("Negate.f64", "Negate", "f64", true, 64)]
  [TestCase("Equal.bool", "Equal", "bool", true, 8)]
  [TestCase("PopCount.i16", "PopCount", "i16", true, 16)]
  [TestCase("Rotat.u32", "Rotat", "u32", false, 32)]
  [TestCase("Xor.str", "Xor", "str", false, 0)]
  [TestCase("A.B.u32", "A.B", "u32", false, 32)]
  public void OperationKey_ParsesOpDotType(string text, string op, string type, bool known, int bits) {
    var key = OperationKey.Parse(text);
    Assert.That((key.Operation, key.Type, key.IsKnown, key.ElementBits, key.ToString()), Is.EqualTo((op, type, known, bits, text)));
  }

  [TestCase("RotateLeft")]
  [TestCase(".u32")]
  [TestCase("RotateLeft.")]
  [TestCase("")]
  public void OperationKey_RefusesTextWithoutOpDotType(string text) {
    Assert.Throws<FormatException>(() => OperationKey.Parse(text));
    Assert.That(OperationKey.TryParse(text, out var key), Is.False);
    Assert.That(key, Is.EqualTo(default(OperationKey)));
  }

  [Test]
  public void OperationKey_RefusesNull() {
    Assert.Throws<ArgumentNullException>(() => OperationKey.Parse(null!));
    Assert.That(OperationKey.TryParse(null, out _), Is.False);
  }

}
