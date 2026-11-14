using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;
using static Polypiler.Tests.Verification.Benchmarks.BenchTesting;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary><c>summary.md</c>: one row per benchmark and variant, every one recorded only.</summary>
[TestFixture]
public sealed class BenchmarkSummaryTests {

  [Test]
  public void EachVariant_IsOneRow_InIdAndVariantOrder_RecordedOnly() {
    var markdown = BenchmarkSummary.ToMarkdown([
      Result("b", "default", null, 10, 12, 11),
      Result("a", "opt-speed", null, 5) with { BuildNs = 7, BinaryBytes = 9, AllocatedBytes = 4 },
      Result("a", "opt-none", null) with { SourceBytes = null }
    ]);
    var lines = markdown.Split('\n');
    Assert.That(lines[0], Does.StartWith("| Benchmark | Variant | Runs | Wall median (ns) |"));
    Assert.That(lines[2], Is.EqualTo("| a | opt-none | 0 |  |  |  |  |  |  |  |  | record only |"));
    Assert.That(lines[3], Is.EqualTo("| a | opt-speed | 1 | 5000000 | 0 | 4500000 | 50000000 | 7 | 3 | 9 | 4 | record only |"));
    Assert.That(lines[4], Is.EqualTo("| b | default | 3 | 11000000 | 1000000 | 9900000 | 50000000 |  | 3 |  |  | record only |"));
    Assert.That(lines, Has.Length.EqualTo(6));
  }

}
