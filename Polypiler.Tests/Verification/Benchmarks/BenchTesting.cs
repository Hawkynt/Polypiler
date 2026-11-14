using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Benchmarks;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>
/// What the benchmark tests share: a runner that answers from a script instead of starting processes,
/// so that times are chosen, never measured; results and environments built from a few numbers.
/// </summary>
internal static class BenchTesting {

  /// <summary>The fixtures of the harness.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "bench");

  /// <summary>An environment of a machine with the given cores and node version.</summary>
  public static BenchmarkEnvironment Machine(int cores = 8, string node = "24.22.2")
    => new("linux", "6.8.0", "x64", "Test CPU @ 3.00GHz", cores, new Dictionary<string, string> { ["node"] = node });

  /// <summary>A result whose wall-time samples are the given milliseconds.</summary>
  public static BenchmarkResult Result(string id, string variant, BenchmarkEnvironment? environment, params long[] wallMs)
    => new(id, variant, "0123456789abcdef", environment ?? Machine(), [.. wallMs.Select(ms => new BenchmarkSample(ms * 1_000_000, ms * 900_000, 50_000_000))],
      null, 3, null, null, null, new Dictionary<string, long>());

  /// <summary>A completed run with the given output and times.</summary>
  public static RunResult Completed(string stdout, long wallMs, long cpuMs = 1, long peak = 1 << 20, int exitCode = 0, long buildMs = 0, IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? collected = null)
    => new(RunOutcome.Completed, exitCode, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(exitCode == 0 ? "" : "it broke"), TimeSpan.FromMilliseconds(wallMs), TimeSpan.FromMilliseconds(cpuMs), peak, IsolationLevel.Isolated, []) {
      BuildTime = TimeSpan.FromMilliseconds(buildMs),
      Collected = collected ?? new Dictionary<string, ReadOnlyMemory<byte>>()
    };

}

/// <summary>A runner that answers each plan with the next scripted result and records the plans.</summary>
internal sealed class ScriptedBenchRunner(params RunResult[] results) : ICrossLanguageRunner {

  private int _next;

  /// <summary>The plans run, in order.</summary>
  public List<RunPlan> Plans { get; } = [];

  /// <inheritdoc />
  public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
    this.Plans.Add(plan);
    var result = results[Math.Min(this._next++, results.Length - 1)];
    return Task.FromResult(result);
  }

}
