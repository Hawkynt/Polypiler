using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Verification.Running;

/// <summary>
/// What the benchmark harness needs from the runner on top of a run's output: the time the build
/// steps took, the files a plan collects before its directory is removed, and variables added to a
/// step's environment.
/// </summary>
[TestFixture]
public sealed class RunMeasurementTests {

  private static RunPlan Plan(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, ProcessSpec execute, params ProcessSpec[] build)
    => new(files, build, execute, RunLimits.Default, TrustLevel.Trusted);

  [Test]
  public void ProcessSpec_HasNoExtraEnvironment_UnlessGiven()
    => Assert.That(new ProcessSpec(DotNet, "--version").Environment, Is.Empty);

  [Test]
  public void ProcessSpec_KeepsACopyOfItsEnvironment() {
    var environment = new Dictionary<string, string> { ["OPT"] = "speed" };
    var spec = new ProcessSpec(DotNet, "--version") { Environment = environment };
    environment["OPT"] = "none";
    Assert.That(spec.Environment, Is.EqualTo(new Dictionary<string, string> { ["OPT"] = "speed" }));
  }

  [TestCase("")]
  [TestCase("A=B")]
  public void ProcessSpec_RefusesAnInvalidVariableName(string name)
    => Assert.That(() => new ProcessSpec(DotNet, "--version") { Environment = new Dictionary<string, string> { [name] = "x" } }, Throws.ArgumentException);

  [Test]
  public void ProcessSpec_RefusesANullEnvironment()
    => Assert.That(() => new ProcessSpec(DotNet, "--version") { Environment = null! }, Throws.ArgumentNullException);

  [Test]
  public void RunPlan_CollectsNothing_UnlessGiven()
    => Assert.That(ProbePlan(Probe("exit", "0")).Collect, Is.Empty);

  [TestCase("")]
  [TestCase("/etc/passwd")]
  [TestCase("../outside")]
  public void RunPlan_RefusesACollectedNameOutsideTheDirectory(string name)
    => Assert.That(() => ProbePlan(Probe("exit", "0")) with { Collect = [name] }, Throws.ArgumentException);

  [Test]
  public void RunPlan_RefusesANullCollectList()
    => Assert.That(() => ProbePlan(Probe("exit", "0")) with { Collect = null! }, Throws.ArgumentNullException);

  [Test]
  public void RunResult_DefaultsToNoBuildTimeAndNothingCollected() {
    var result = new RunResult(RunOutcome.Completed, 0, default, default, TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, []);
    Assert.That((result.BuildTime, result.Collected.Count), Is.EqualTo((TimeSpan.Zero, 0)));
  }

  [Test]
  [Category("OsIntegration")]
  public async Task ARunWithoutBuildSteps_TookNoBuildTime() {
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(ProbePlan(Probe("exit", "0")));
    Assert.That((result.Outcome, result.BuildTime), Is.EqualTo((RunOutcome.Completed, TimeSpan.Zero)));
  }

  [Test]
  [Category("OsIntegration")]
  public async Task TheBuildSteps_TimeIsTheirSum_AndTheCollectedFilesOutliveTheDirectory() {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(ProbeFiles) { ["sub/kept.txt"] = Encoding.UTF8.GetBytes("abc") };
    var plan = Plan(files, Probe("exit", "0"), Probe("write", "built.bin"), Probe("sleep", "10")) with { Collect = ["built.bin", "sub", "missing.bin"] };
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(plan);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), string.Join("\n", result.Diagnostics));
    Assert.That(result.BuildTime, Is.GreaterThan(TimeSpan.Zero));
    Assert.That(result.Collected.Keys.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "built.bin", "sub/kept.txt" }));
    Assert.That(Encoding.UTF8.GetString(result.Collected["built.bin"].Span), Is.EqualTo("written by the probe\n"));
    Assert.That(result.Collected["sub/kept.txt"].Length, Is.EqualTo(3));
  }

  [Test]
  [Category("OsIntegration")]
  public async Task AFailingBuildStep_StillReportsItsTime() {
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(ProbePlan(Probe("exit", "0"), null, TrustLevel.Trusted, Probe("exit", "3")));
    Assert.That((result.Outcome, result.BuildTime > TimeSpan.Zero), Is.EqualTo((RunOutcome.BuildFailed, true)));
  }

  [Test]
  [Category("OsIntegration")]
  public async Task ABuildStepBeyondALimit_StillReportsItsTime() {
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(ProbePlan(Probe("exit", "0"), Limits(wallTime: TimeSpan.FromMilliseconds(300)), TrustLevel.Trusted, Probe("sleep", "5000")));
    Assert.That((result.Outcome, result.BuildTime > TimeSpan.Zero), Is.EqualTo((RunOutcome.TimedOut, true)));
  }

  [Test]
  [Category("OsIntegration")]
  public async Task AStepSeesTheVariablesItAdds() {
    if (Toolchains.Resolve("node").Executable is null)
      Assert.Inconclusive("node is not installed on this host");

    var execute = new ProcessSpec("node", "-e", "process.stdout.write(`${process.env.POLYPILER_OPT}|${process.env.LANG}`)") {
      Environment = new Dictionary<string, string> { ["POLYPILER_OPT"] = "speed", ["LANG"] = "C" }
    };
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(Plan(new Dictionary<string, ReadOnlyMemory<byte>>(), execute));
    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Is.EqualTo("speed|C"), string.Join("\n", result.Diagnostics));
  }

}
