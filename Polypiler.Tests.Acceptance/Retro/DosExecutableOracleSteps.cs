using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Batch;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Retro;

/// <summary>Exercises the independent MS-DOS loader with pinned COM and MZ programs.</summary>
[Binding, Scope(Feature = "Verify native DOS executable loading")]
public sealed class DosExecutableOracleSteps {

  private DosCommandOracle.FixtureFile[] _executables = [];
  private string _probe = "";
  private string _expected = "";
  private string _trace = "";

  [Given("the pinned COM and MZ loader fixtures")]
  public void PinFixtures() {
    var com = DosCommandOracle.Fixture("PLYRC.COM");
    this._executables = [
      new(Pinned(com, "ef892afebf7fabb6142f6f024536a9c51bc48f3b2158a060c7ba1eae0dcaa956"), "EXIT42.COM"),
      new(Pinned(Fixture("EXIT42.EXE"), "a9e606566c64a323840f8277d9910305927089b0b229486e3648cf9f99279529"), "EXIT42.EXE"),
      new(Pinned(Fixture("RELOC.EXE"), "b87c2afac2d9cbe71aae7fddc37a5a94903459f7aacc603190ce663aedd1d976"), "RELOC.EXE"),
      new(Pinned(Fixture("NORELOC.EXE"), "30fe8c5fe01b2901b3f57cde396a302bc240873a986b9cdd51608f0f965460b8"), "NORELOC.EXE")
    ];
    this._probe = Pinned(Fixture("PROBE.BAT"), "fe6a8f507fe2eef16281f4387371ebfbafb0dc87c333b673c002df901900da1d");
    this._expected = File.ReadAllText(Pinned(Fixture("TRACE.TXT"), "715538d5ffd4810712c70f9bf8567930f220119d334232f9130407abf9b78142"));
  }

  [When("a fresh MS-DOS 6.22 guest runs the loader probe")]
  public void RunProbe() => this.Run(this._executables);

  [When("a fresh MS-DOS 6.22 guest runs the loader probe without EXIT42.EXE")]
  public void RunWithoutMz()
    => this.Run(this._executables.Where(file => file.GuestPath != "EXIT42.EXE").ToArray());

  [Then("^the (COM|MZ|RELOC|NORELOC) program exits with status ([0-9]+)$")]
  public void ExitsWith(string program, string status)
    => Assert.That(this._trace, Does.Contain($"\r\n{program}={status}\r\n"));

  [Then("the DOS loader trace matches the native reference")]
  public void MatchesNative() => Assert.That(this._trace, Is.EqualTo(this._expected));

  [Then("the probe reports the missing MZ program")]
  public void ReportsMissingMz()
    => Assert.That(this._trace, Is.EqualTo("\r\nMS-DOS Version 6.22\r\n\r\nCOM=42\r\nUnexpected\r\nPLY_DONE\r\n"));

  private static string Fixture(string name)
    => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "retro", "x86-16", "loader", name);

  private static string Pinned(string path, string expected) {
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), Is.EqualTo(expected), path);
    return path;
  }

  private void Run(DosCommandOracle.FixtureFile[] executables) {
    var run = DosCommandOracleTests.Guest().Run(this._probe, executables, TimeSpan.FromSeconds(20));
    this._trace = run.Trace;
    TestContext.Out.Write(this._trace);
    Assert.That(run.TimedOut, Is.False, "DOS loader probe did not complete.");
  }

}
