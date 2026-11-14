using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace Polypiler.Benchmarks;

/// <summary>
/// Runs the benchmarks the command line selects (<c>--filter</c>), in this process: inside the sandbox
/// there is no network to restore the project BenchmarkDotNet would otherwise generate and build.
/// Each run writes the full JSON export under <c>BenchmarkDotNet.Artifacts/results</c>.
/// </summary>
public static class Program {

  /// <summary>The configuration: a short run in process, with allocations and the full JSON export.</summary>
  public static IConfig Config { get; } = DefaultConfig.Instance
    .AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance))
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddExporter(JsonExporter.Full);

  /// <summary>Runs the selected benchmarks; 0 when each ran and succeeded, 1 when one failed, 2 when none was selected.</summary>
  public static int Main(string[] args) {
    var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, Config).ToList();
    if (summaries.Count == 0 || summaries.All(summary => summary.Reports.IsEmpty))
      return 2;

    return summaries.Any(summary => summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success)) ? 1 : 0;
  }

}
