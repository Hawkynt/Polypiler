using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CpuArchitecture = System.Runtime.InteropServices.Architecture;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Running;

/// <summary>The kernel's account of a finished child, which catches a program shorter than a sample interval.</summary>
[TestFixture]
public sealed class ExitUsageTests {

  [Test]
  public void Read_TakesTheTimesAndTheLargestResidentSetOfARusage() {
    long[] fields = [1, 500_000, 0, 250_000, 2048, .. new long[13]];
    var usage = ExitUsage.Read(MemoryMarshal.AsBytes(fields.AsSpan()));
    Assert.That(usage, Is.EqualTo(new ExitUsage(TimeSpan.FromMilliseconds(1750), 2048 * 1024)));
  }

  [TestCase(CpuArchitecture.X64, 247L)]
  [TestCase(CpuArchitecture.Arm64, 95L)]
  [TestCase(CpuArchitecture.X86, null)]
  public void WaitIdNumber_IsKnownForX64AndArm64Only(CpuArchitecture architecture, long? number)
    => Assert.That(ExitUsage.WaitIdNumber(architecture), Is.EqualTo(number));

  [Test]
  public void Wait_ForAProcessThatIsNoChild_HasNoAnswer()
    => Assert.That(ExitUsage.Wait(1), Is.Null);

  [Test]
  public void Wait_OnAnUnknownArchitecture_HasNoAnswer()
    => Assert.That(ExitUsage.Wait(Environment.ProcessId, CpuArchitecture.X86), Is.Null);

  [Test]
  [Category("OsIntegration")]
  public void Wait_ForAChild_AccountsItWithoutReapingIt() {
    using var process = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var usage = ExitUsage.Wait(process.Id);
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, "the runtime still reaps the child and sees its exit code");
    // Elsewhere, or when the runtime reaped the child first, there is no account to check.
    if (usage is { } accounted)
      Assert.That(accounted.PeakMemoryBytes, Is.Positive);
  }

  /// <summary>Up to the launcher's peak the kernel's figure may be inherited at exec and is ignored; above it, the larger of both counts.</summary>
  [TestCase(100L, 150L, 200L, 100L)]
  [TestCase(100L, 200L, 200L, 100L)]
  [TestCase(100L, 201L, 200L, 201L)]
  [TestCase(500L, 300L, 200L, 500L)]
  public void Peak_IgnoresTheKernelFigureUpToTheLauncherPeak(long sampled, long accounted, long launcherPeak, long expected)
    => Assert.That(new ExitUsage(TimeSpan.Zero, accounted).Peak(sampled, launcherPeak), Is.EqualTo(expected));

}
