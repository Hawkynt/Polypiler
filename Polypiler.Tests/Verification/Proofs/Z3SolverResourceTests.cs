using System;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Proofs;

namespace Polypiler.Tests.Verification.Proofs;

/// <summary>Native contexts must be released without waiting for managed allocation to trigger collection.</summary>
[TestFixture, NonParallelizable]
public sealed class Z3SolverResourceTests {

  private const string Child = "POLYPILER_SMT_LIFETIME_CHILD";
  private const string Completed = "SMT_NATIVE_LIFETIME_OK";

  [Test]
  public void ModelSelection_PreservesRequestedOrderAndDefaultsMissingConstants() {
    var result = new Z3Solver().Check("(declare-const x (_ BitVec 32))\n(declare-const extra (_ BitVec 32))\n(assert (= x (_ bv17 32)))\n(assert (= extra (_ bv6 32)))\n(check-sat)",
      ["missing", "x", "x"], TimeSpan.FromSeconds(1));
    Assert.That(result.Status, Is.EqualTo(SmtStatus.Sat));
    Assert.That(result.Model.Keys, Is.EqualTo(new[] { "missing", "x" }));
    Assert.That(result.Model["missing"], Is.Zero);
    Assert.That(result.Model["x"], Is.EqualTo(17UL));
  }

  [Test, Category("Slow"), Category("OsIntegration")]
  public async Task Queries_ReleaseNativeContextsBeforeManagedCollection() {
    if (Environment.GetEnvironmentVariable(Child) == "1") {
      var peak = CheckLifetime();
      TestContext.Progress.WriteLine($"{Completed} peak private growth: {peak / (1024 * 1024)} MiB");
      return;
    }

    var assembly = typeof(Z3SolverResourceTests).Assembly.Location;
    var info = new ProcessStartInfo("dotnet") {
      WorkingDirectory = Path.GetDirectoryName(assembly)!,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    info.ArgumentList.Add("vstest");
    info.ArgumentList.Add(assembly);
    info.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(Z3SolverResourceTests).FullName + "." + nameof(this.Queries_ReleaseNativeContextsBeforeManagedCollection));
    info.ArgumentList.Add("--Logger:console;verbosity=normal");
    info.Environment[Child] = "1";
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    try {
      await process.WaitForExitAsync(timeout.Token);
    } catch (OperationCanceledException) {
      process.Kill(entireProcessTree: true);
      await process.WaitForExitAsync();
      Assert.Fail("The isolated native-resource check exceeded one minute.");
    }

    var output = await stdout + await stderr;
    if (output.Contains("the runtime gave no allocation budget", StringComparison.Ordinal))
      Assert.Inconclusive("the child could not measure without a collection: " + output[output.IndexOf("the runtime gave no allocation budget", StringComparison.Ordinal)..].Split('\n')[0]);
    Assert.That(process.ExitCode, Is.Zero, output);
    Assert.That(output, Does.Contain(Completed), "the child must actually execute the check");
    TestContext.Out.WriteLine(output[output.IndexOf(Completed, StringComparison.Ordinal)..].Split('\n')[0]);
  }

  private static long CheckLifetime() {
    var solver = new Z3Solver();
    var queries = new[] {
      "(declare-const x (_ BitVec 32))\n(assert (distinct x x))\n(check-sat)",
      "(declare-const x (_ BitVec 32))\n(assert (= x (_ bv17 32)))\n(check-sat)",
      "(declare-const flag Bool)\n(assert flag)\n(check-sat)"
    };
    for (var index = 0; index < queries.Length; ++index)
      solver.Check(queries[index], index == 2 ? ["flag"] : ["x"], TimeSpan.FromSeconds(1));

    // Warm the API and clear pre-existing finalizers before isolating native ownership.
    for (var pass = 0; pass < 2; ++pass) {
      GC.Collect();
      GC.WaitForPendingFinalizers();
    }

    // A region without collections makes finalizers unable to free a context while the queries run. A
    // runtime may refuse the budget for a moment (the CI runners do now and then): it is asked again after
    // a collection, and where it keeps refusing, the measurement counts only a pass no collection ran in.
    var region = false;
    for (var attempt = 0; attempt < 5 && !region; ++attempt) {
      GC.Collect();
      GC.WaitForPendingFinalizers();
      region = GC.TryStartNoGCRegion(32 * 1024 * 1024);
    }

    long? peak = null;
    try {
      for (var pass = 0; pass < (region ? 1 : 5) && peak is null; ++pass) {
        var collections = GC.CollectionCount(0);
        var measured = Measure(solver, queries);
        if (region || GC.CollectionCount(0) == collections)
          peak = measured;
      }

      if (region)
        Assert.That(GCSettings.LatencyMode, Is.EqualTo(GCLatencyMode.NoGCRegion), "the check must not depend on finalizers freeing contexts");
    } finally {
      if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
        GC.EndNoGCRegion();
    }

    if (peak is null)
      Assert.Inconclusive("the runtime gave no allocation budget and collected during every pass: finalizers could have freed the contexts");

    // Context finalizers must also remain safe after all native handles have been released.
    for (var pass = 0; pass < 2; ++pass) {
      GC.Collect();
      GC.WaitForPendingFinalizers();
    }

    return peak.Value;
  }

  /// <summary>Runs 128 trivial queries and gives the largest growth of the private memory, failing where it passes 128 MiB.</summary>
  private static long Measure(Z3Solver solver, string[] queries) {
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var baseline = process.PrivateMemorySize64;
    var peak = 0L;
    for (var query = 0; query < 128; ++query) {
      var kind = query % queries.Length;
      var answer = solver.Check(queries[kind], kind == 2 ? ["flag"] : ["x"], TimeSpan.FromSeconds(1));
      Assert.That(answer.Status, Is.EqualTo(kind == 0 ? SmtStatus.Unsat : SmtStatus.Sat));
      if (kind != 0)
        Assert.That(answer.Model[kind == 2 ? "flag" : "x"], Is.EqualTo(kind == 2 ? 1UL : 17UL));

      process.Refresh();
      peak = Math.Max(peak, process.PrivateMemorySize64 - baseline);
      Assert.That(peak, Is.LessThan(128L * 1024 * 1024),
        $"native memory accumulated after {query + 1} trivial queries without a managed collection");
    }

    return peak;
  }
}
