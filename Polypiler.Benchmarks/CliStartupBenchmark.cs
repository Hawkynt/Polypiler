using System.IO;
using BenchmarkDotNet.Attributes;

namespace Polypiler.Benchmarks;

/// <summary>The cost of the smallest command, <c>polypiler --version</c>, in process: argument handling and output, without a translation.</summary>
[MemoryDiagnoser]
public class CliStartupBenchmark {

  /// <summary>Runs <c>polypiler --version</c> against a writer that discards everything.</summary>
  [Benchmark]
  public int Version() => Cli.Program.Run(["--version"], TextWriter.Null, TextWriter.Null);

}
