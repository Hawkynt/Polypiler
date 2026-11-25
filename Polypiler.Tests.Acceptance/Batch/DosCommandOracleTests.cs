using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Checks the guest runner against an independently captured DOS trace.</summary>
[TestFixture, Layer("basic"), Category("Acceptance"), Category("ExternalInterop")]
public sealed class DosCommandOracleTests {

  [Test]
  public void RefusesTheWrongGuestBeforeLaunchingTools() {
    var location = Directory.CreateTempSubdirectory();
    try {
      var disk = Path.Combine(location.FullName, "boot.img");
      File.WriteAllBytes(disk, [0, 1, 2]);
      var oracle = new DosCommandOracle(disk, null);
      Assert.That(() => oracle.Run(Fixture("PROBE.BAT"), [], TimeSpan.FromSeconds(1)),
        Throws.TypeOf<InvalidDataException>().With.Message.Contains("boot image SHA-256"));
    } finally {
      location.Delete(recursive: true);
    }
  }

  [Test]
  public void RefusesAMissingGuestBeforeLaunchingTools() {
    var oracle = new DosCommandOracle("", null);
    Assert.That(() => oracle.Run(Fixture("PROBE.BAT"), [], TimeSpan.FromSeconds(1)),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("boot image"));
  }

  [Test]
  public void TheSuppliedCommandComProducesThePinnedTraceOnEachBoot() {
    var oracle = Guest();
    for (var repeat = 0; repeat < 2; ++repeat) {
      var run = oracle.Run(Fixture("PROBE.BAT"), [At("PLYCALL.BAT"), At("PLYRC.COM")], TimeSpan.FromSeconds(20));
      TestContext.Out.Write(run.Trace);
      Assert.That(run.TimedOut, Is.False);
      Assert.That(run.Trace, Is.EqualTo("\r\nMS-DOS Version 6.22\r\n\r\nFOR=6\r\nGOTO=hit\r\nCALL=42\r\n"
        + "RC43=no\r\nRC42=yes\r\nRC41=yes\r\nPLY_DONE\r\n"));
    }
  }

  [Test]
  public void SupportFilesStayInTheirStagedDirectoriesAndOrder() {
    var oracle = Guest();
    for (var repeat = 0; repeat < 2; ++repeat) {
      var run = oracle.Run(Fixture("tree/DRIVER.BAT"),
        [new(Fixture("tree/CASE.BAT"), "CASE.BAT"),
          new(Fixture("tree/DATA/TWO.TXT"), @"data\two.txt"), new(Fixture("tree/DATA/ONE.TXT"), "DATA/ONE.TXT")],
        TimeSpan.FromSeconds(20));
      Assert.That(run.TimedOut, Is.False);
      Assert.That(run.Trace, Is.EqualTo("FILE=DATA\\TWO.TXT\r\nFILE=DATA\\ONE.TXT\r\nPLY_DONE\r\n"));
    }
  }

  [Test]
  public void AGuestThatNeverFinishesReportsATimeoutAndItsPartialTrace() {
    var run = Guest().Run(Fixture("TIMEOUT.BAT"), [], TimeSpan.FromSeconds(6));
    Assert.That(run.TimedOut, Is.True);
    Assert.That(run.Trace, Is.EqualTo("START\r\n"));
  }

  [TestCase(@"..\ONE.TXT")]
  [TestCase(@"B:\ONE.TXT")]
  [TestCase(@"\ONE.TXT")]
  [TestCase("DATA//ONE.TXT")]
  [TestCase("123456789.TXT")]
  [TestCase("ONE.TXTX")]
  [TestCase("ONE.")]
  [TestCase("ONE..TXT")]
  [TestCase("*.TXT")]
  public void InvalidGuestPathsAreRefusedWithoutTheGuest(string target) {
    var oracle = new DosCommandOracle("", null);
    Assert.That(() => oracle.Run(Fixture("PROBE.BAT"), [new(Fixture("PLYCALL.BAT"), target)], TimeSpan.FromSeconds(1)),
      Throws.ArgumentException.With.Message.Contains("Invalid DOS 8.3 fixture path"));
  }

  [TestCase("TRACE.TXT", "OTHER.BAT")]
  [TestCase("FIRST.BAT", "first.bat")]
  [TestCase("DATA", "DATA/ONE.TXT")]
  [TestCase("DATA/ONE.TXT", "DATA")]
  public void ConflictingGuestPathsAreRefusedWithoutTheGuest(string first, string second) {
    var oracle = new DosCommandOracle("", null);
    Assert.That(() => oracle.Run(Fixture("PROBE.BAT"),
      [new(Fixture("PLYCALL.BAT"), first), new(Fixture("PLYCALL.BAT"), second)], TimeSpan.FromSeconds(1)),
      Throws.ArgumentException.With.Message.Contains("fixture"));
  }

  private static DosCommandOracle.FixtureFile At(string name) => new(Fixture(name), name);

  internal static DosCommandOracle Guest() {
    var image = Environment.GetEnvironmentVariable("POLYPILER_MSDOS622_BOOT_IMAGE");
    if (string.IsNullOrEmpty(image))
      Assert.Inconclusive("Set POLYPILER_MSDOS622_BOOT_IMAGE to the licensed MS-DOS 6.22 boot floppy image.");
    new RequiresToolchainAttribute("qemu-x86").Check("1");
    new RequiresToolchainAttribute("mtools").Check("1");
    return new(image!, Environment.GetEnvironmentVariable("POLYPILER_MSDOS622_QEMU_DATA"));
  }

  private static string Fixture(string name) => DosCommandOracle.Fixture(name);

}
