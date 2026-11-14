using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// Test design 2 of INFRA-BENCH: the in-process .NET benchmark <c>cli-startup</c> of the repository,
/// run by <c>polypiler-tooling bench run</c> through BenchmarkDotNet in the sandbox, records the
/// iterations as samples and the bytes allocated per operation. It needs the build output of
/// <c>Polypiler.Benchmarks</c> in the configuration of these tests, which building the solution makes.
/// </summary>
[TestFixture]
[Category("Performance")]
public sealed class BenchDotNetTests {

  [Test]
  public void TheCliStartupBenchmark_RecordsItsIterationsAndAllocations() {
    var output = Path.Combine(Path.GetTempPath(), "polypiler-bench-" + Guid.NewGuid().ToString("N"));
    try {
      var run = Toolchain.Tooling(PrototypeFixture.RepositoryRoot, "bench", "run", "--id", "cli-startup", "--out", output);
      Assert.That(run.ExitCode, Is.Zero, run.Output);
      var result = BenchmarkResult.Load(Path.Combine(output, "cli-startup", "default.json"));
      Assert.That(result.Samples, Is.Not.Empty);
      Assert.That(result.Samples, Has.All.Matches<BenchmarkSample>(sample => sample.WallNs > 0 && sample.CpuNs == null));
      Assert.That(result.AllocatedBytes, Is.Not.Null.And.Positive);
      Assert.That(result.Environment.Toolchains.Keys, Is.EquivalentTo(new[] { "dotnet-sdk" }));
    } finally {
      if (Directory.Exists(output))
        Directory.Delete(output, true);
    }
  }

}
