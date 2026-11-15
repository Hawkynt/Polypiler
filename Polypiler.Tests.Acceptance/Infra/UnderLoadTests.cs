using System;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The scenarios that once failed only on a busy machine, each run 20 times through its own steps while
/// twice as many threads as the machine has processors keep it busy: every run passes, or the scenario
/// still depends on the machine's load. The unit test of the same kind is
/// <c>ProcessRunnerTests.ProcessOutlivingItsTime_IsKilled_UnderLoad</c>. Each test runs alone, so its
/// load does not reach the tests beside it.
/// </summary>
[TestFixture, Category("Slow"), NonParallelizable]
public sealed class UnderLoadTests {

  private const int Runs = 20;

  /// <summary>Runs a scenario's steps in a workspace of their own, once per run, under the load.</summary>
  private static void UnderLoad(Action<ScenarioWorkspace> scenario) {
    using var load = new CpuLoad(CpuLoad.TwiceTheProcessors);
    for (var run = 0; run < Runs; ++run) {
      var workspace = ScenarioWorkspace.Create();
      try {
        scenario(workspace);
      } finally {
        workspace.Delete();
      }
    }
  }

  /// <summary>Scenario "A program is measured and its numbers are recorded, not judged" of <c>infra-bench.feature</c>.</summary>
  [Test]
  public void AProgramIsMeasuredAndItsNumbersAreRecorded_UnderLoad() => UnderLoad(workspace => {
    var steps = new BenchSteps(workspace, new CommandLog());
    steps.Definition("tests/fixtures/bench/sum-loop.bench.yaml", 1, 5, "node");
    steps.BenchRun("sum-loop");
    steps.ContainsSamples("artifacts/bench/sum-loop/default.json", 5);
    steps.EverySampleIsRecorded("wallNs", "cpuNs", "peakRssBytes");
    steps.EachRunPrinted("499999500000");
    steps.SummaryRow("sum-loop", "record only");
    steps.ExitCode(0);
  });

  /// <summary>Scenario "A byte swap is a library call where the library has it" of <c>integer-operations.feature</c>.</summary>
  [Test]
  public void AByteSwapIsALibraryCallWhereTheLibraryHasIt_UnderLoad() => UnderLoad(workspace => {
    var log = new CommandLog();
    var identity = new ScenarioIdentity("../tests/features/prototype", "The complete integer and bit operation set", "A byte swap is a library call where the library has it", []);
    var scenario = new PrototypeScenario(workspace, new PolypilerRunner(workspace, identity, log, new TestCommandSet()), log);
    var steps = new IntegerOperationsSteps(scenario);
    new PrototypeSteps(scenario).CSharpMethod("public static uint F(uint x) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x);");
    steps.OutputCalls("net10.0", "BinaryPrimitives.ReverseEndianness");
    steps.OutputDeclaresAPrivateHelper("net2.0", "BinaryPrimitives");
    steps.OutputDeclaresAHelper("es5");
    steps.AllReturn("78563412", "F(0x12345678)");
  });

}
