using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Frontends.Batch;
using Polypiler.Core.IR;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Batch;

/// <summary>Verifies MS-DOS 6.22 COMMAND.COM multi-file CALL and transfer lifting against the booted guest.</summary>
[TestFixture, Layer("basic"), Category("Acceptance"), Category("ExternalInterop")]
public sealed class DosBatchBackendAcceptanceTests {

  [Test]
  public void LiftedDosCallAndTransferFixturesMatchBootedGuestTrace() {
    var guest = DosCommandOracleTests.Guest();
    var run = guest.Run(DosCommandOracle.Fixture("CALLDRV.BAT"), [
      new(DosCommandOracle.Fixture("CALL6.BAT"), "CASE.BAT"),
      new(DosCommandOracle.Fixture("CHILD.BAT"), "CHILD.BAT"),
      new(DosCommandOracle.Fixture("LEX.BAT"), "LEX.BAT"),
      new(DosCommandOracle.Fixture("CHAIN.BAT"), "CHAIN.BAT"),
      new(DosCommandOracle.Fixture("PLYRC.COM"), "PLYRC.COM")
    ], TimeSpan.FromSeconds(30));
    Assert.That(run.TimedOut, Is.False);

    var options = new BatchImportOptions {
      EntryInvocation = @"B:\CASE.BAT",
      CompanionFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["CHILD.BAT"] = File.ReadAllText(DosCommandOracle.Fixture("CHILD.BAT")),
        ["LEX.BAT"] = File.ReadAllText(DosCommandOracle.Fixture("LEX.BAT")),
        ["CHAIN.BAT"] = File.ReadAllText(DosCommandOracle.Fixture("CHAIN.BAT"))
      },
      StatusFixtures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
        ["PLYRC.COM"] = 42
      }
    };
    var imported = BatchFrontend.Import("CALL6.BAT", File.ReadAllText(DosCommandOracle.Fixture("CALL6.BAT")), BatchDialect.Dos622, options);
    Assert.That(imported.Diagnostics, Is.Empty);
    var output = new List<string>();
    var exitStatus = (int)IrInterpreter.Instantiate(imported.Module!, output.Add).Call("Run")!;
    Assert.That(exitStatus, Is.EqualTo(42));
    Assert.That(string.Join("\r\n", output) + "\r\nPLY_DONE\r\n", Is.EqualTo(run.Trace));
  }

}
