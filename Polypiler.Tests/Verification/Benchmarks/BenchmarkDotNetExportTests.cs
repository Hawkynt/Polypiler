using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Benchmarks;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>The conversion of BenchmarkDotNet's full JSON export into samples and allocations.</summary>
[TestFixture]
public sealed class BenchmarkDotNetExportTests {

  private const string Export = """
    {"Title":"t","Benchmarks":[{"FullName":"Polypiler.Benchmarks.CliStartupBenchmark.Version",
      "Statistics":{"OriginalValues":[1500.5,1499.5,1600.25],"Median":1500.5},
      "Memory":{"Gen0Collections":1,"TotalOperations":1024,"BytesAllocatedPerOperation":4312}}]}
    """;

  [Test]
  public void TheIterations_AreTheSamples_AndTheAllocatedBytesAreKept() {
    var measurement = BenchmarkDotNetExport.Convert(Export, "e.json");
    Assert.That(measurement.Samples, Is.EqualTo(new[] { new BenchmarkSample(1500, null, null), new BenchmarkSample(1500, null, null), new BenchmarkSample(1600, null, null) }));
    Assert.That(measurement.AllocatedBytes, Is.EqualTo(4312));
  }

  [TestCase("""{"Benchmarks":[{"Statistics":{"OriginalValues":[1]}}]}""")]
  [TestCase("""{"Benchmarks":[{"Statistics":{"OriginalValues":[1]},"Memory":null}]}""")]
  [TestCase("""{"Benchmarks":[{"Statistics":{"OriginalValues":[1]},"Memory":{}}]}""")]
  [TestCase("""{"Benchmarks":[{"Statistics":{"OriginalValues":[1]},"Memory":{"BytesAllocatedPerOperation":"x"}}]}""")]
  [TestCase("""{"Benchmarks":[{"Statistics":{"OriginalValues":[1]},"Memory":{"BytesAllocatedPerOperation":1.5}}]}""")]
  public void WithoutTheMemoryDiagnoser_ThereAreNoAllocatedBytes(string json)
    => Assert.That(BenchmarkDotNetExport.Convert(json, "e.json").AllocatedBytes, Is.Null);

  [TestCase("{", "not valid JSON")]
  [TestCase("{}", "'Benchmarks' must be a non-empty array")]
  [TestCase("""{"Benchmarks":{}}""", "'Benchmarks' must be a non-empty array")]
  [TestCase("""{"Benchmarks":[]}""", "'Benchmarks' must be a non-empty array")]
  [TestCase("""{"Benchmarks":[{}]}""", "'Benchmarks[0].Statistics.OriginalValues' must be a non-empty array")]
  [TestCase("""{"Benchmarks":[{"Statistics":[]}]}""", "'Benchmarks[0].Statistics.OriginalValues' must be a non-empty array")]
  [TestCase("""{"Benchmarks":[{"Statistics":{}}]}""", "'Benchmarks[0].Statistics.OriginalValues' must be a non-empty array")]
  [TestCase("""{"Benchmarks":[{"Statistics":{"OriginalValues":{}}}]}""", "'Benchmarks[0].Statistics.OriginalValues' must be a non-empty array")]
  [TestCase("""{"Benchmarks":[{"Statistics":{"OriginalValues":[]}}]}""", "'Benchmarks[0].Statistics.OriginalValues' must be a non-empty array")]
  [TestCase("""{"Benchmarks":[{"Statistics":{"OriginalValues":["x"]}}]}""", "'OriginalValues' must hold numbers")]
  public void AnExportWithoutMeasuredValues_IsRefused(string json, string problem)
    => Assert.That(() => BenchmarkDotNetExport.Convert(json, "e.json"), Throws.TypeOf<BenchmarkFileException>().With.Message.Contains(problem));

  [Test]
  public void Find_TakesTheFirstFullExportByName() {
    var collected = new Dictionary<string, ReadOnlyMemory<byte>> {
      ["BenchmarkDotNet.Artifacts/results/B-report-full.json"] = Encoding.UTF8.GetBytes("b"),
      ["BenchmarkDotNet.Artifacts/results/A-report-full.json"] = Encoding.UTF8.GetBytes("a"),
      ["BenchmarkDotNet.Artifacts/results/A-report.csv"] = Encoding.UTF8.GetBytes("c")
    };
    Assert.That(BenchmarkDotNetExport.Find(collected), Is.EqualTo("a"));
    Assert.That(BenchmarkDotNetExport.Find(new Dictionary<string, ReadOnlyMemory<byte>>()), Is.Null);
  }

}
