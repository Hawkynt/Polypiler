using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Polypiler.Tests.Verification.Running.Support;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Tooling.Toolchains.ToolchainTesting;

namespace Polypiler.Tests.Tooling.Toolchains;

/// <summary><see cref="ProcessRunner"/> with the fake tool of the fixtures: <c>fake-tool.sh</c>, or <c>fake-tool.cmd</c> on Windows.</summary>
[TestFixture]
public sealed class ProcessRunnerTests {

  private static readonly string _fakeTool = Fixture(OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool.sh");

  private static ProcessResult Run(TimeSpan timeout, params string[] arguments)
    => new ProcessRunner().Run([_fakeTool, .. arguments], timeout);

  [Test]
  public void StandardOutput_IsCaptured()
    => Assert.That(Run(TimeSpan.FromSeconds(10), "fake 1.2.3"), Is.EqualTo(new ProcessResult(0, "fake 1.2.3" + Environment.NewLine, "", false)));

  [Test]
  public void StandardError_IsCaptured() {
    var result = Run(TimeSpan.FromSeconds(10), "fake 1.2.3", "err");
    Assert.That((result.StandardOutput, result.StandardError.TrimEnd()), Is.EqualTo(("", "fake 1.2.3")));
  }

  [Test]
  public void ExitCode_IsReported() => Assert.That(Run(TimeSpan.FromSeconds(10), "fake", "out", "0", "3").ExitCode, Is.EqualTo(3));

  /// <summary>How long the tool of <see cref="ProcessOutlivingItsTime_IsKilled"/> would run if nothing killed it.</summary>
  private const int OutlivingSeconds = 60;

  /// <summary>
  /// A process that outlives its time is killed and reported. What shows the kill is the order of two
  /// events, not a budget of the machine's speed: the runner returns before the tool could have ended
  /// by itself, which it does only after <see cref="OutlivingSeconds"/>; a kill that failed would wait
  /// for that end.
  /// </summary>
  [Test]
  public void ProcessOutlivingItsTime_IsKilled() {
    var clock = Stopwatch.StartNew();
    var result = Run(TimeSpan.FromMilliseconds(200), "fake 1.2.3", "out", OutlivingSeconds.ToString(CultureInfo.InvariantCulture));
    Assert.That(result, Is.EqualTo(new ProcessResult(-1, "", "", true)));
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(OutlivingSeconds)));
  }

  /// <summary>The kill does not depend on an idle machine: 20 runs under twice as many busy threads as processors all pass.</summary>
  [Test, NUnit.Framework.Category("Slow"), NonParallelizable]
  public void ProcessOutlivingItsTime_IsKilled_UnderLoad() {
    using var load = new CpuLoad(CpuLoad.TwiceTheProcessors);
    for (var run = 0; run < 20; ++run)
      this.ProcessOutlivingItsTime_IsKilled();
  }

  [Test]
  public void MissingExecutable_CannotBeStarted()
    => Assert.That(() => new ProcessRunner().Run([Path.Combine(FixtureFolder, "no-such-binary-polypiler")], TimeSpan.FromSeconds(1)), Throws.InstanceOf<Win32Exception>());

}
