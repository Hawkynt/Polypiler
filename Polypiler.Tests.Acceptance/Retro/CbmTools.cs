using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Emulators;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Verification.Running.Support;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Retro;

/// <summary>
/// VICE and petcat as the Commodore tests run them: through the runner and sandbox of this host, with
/// the toolchains of the repository lock, each required the way <c>RequiresToolchain</c> requires it.
/// </summary>
internal static class CbmTools {

  /// <summary>The fixtures of the Commodore BASIC oracles.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "retro", "cbmbasic", "oracle");

  /// <summary>A fixture's bytes.</summary>
  public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixtureFolder, name));

  private static CrossLanguageRunner Runner => new(RunnerTesting.Host, RunnerTesting.Toolchains);

  /// <summary>The VICE runner, after the emulator of the machine and, on Linux, the virtual display were required.</summary>
  public static ViceRunner Vice(CbmMachine machine) {
    Require(machine switch {
      CbmMachine.C64 => "vice",
      CbmMachine.C128 => "vice-x128",
      _ => "vice-xvic"
    });
    if (OperatingSystem.IsLinux())
      Require(ViceRunner.DisplayToolchain);

    return new(Runner);
  }

  /// <summary>petcat, after it was required.</summary>
  public static Petcat Petcat() {
    Require(Core.Verification.Emulators.Petcat.Toolchain);
    return new(Runner) { Trust = TrustLevel.Trusted };
  }

  /// <summary>Ends the test unless the toolchain is at its pinned version: inconclusive, or failed with <c>POLYPILER_REQUIRE_TOOLCHAINS=1</c>.</summary>
  public static void Require(string key) => new RequiresToolchainAttribute(key).Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));

  /// <summary>A run of a repository fixture: trusted, as every file of the repository is.</summary>
  public static ViceRequest Request(CbmMachine machine, byte[] prg, long cycleLimit = 20_000_000, string? input = null, params AddressRange[] ranges)
    => new(machine, prg, input, [.. ranges], cycleLimit) { Trust = TrustLevel.Trusted };

  /// <summary>Runs a request and writes how it ended, the banner and the screen text to the test's output, the evidence of the run.</summary>
  public static ViceResult Run(ViceRunner runner, ViceRequest request) {
    var result = runner.RunAsync(request).GetAwaiter().GetResult();
    TestContext.Out.WriteLine($"{request.Machine}: {result.Status} at cycle {result.Cycles}; banner: {result.Banner.Replace('\n', '|')}; screen: {result.ScreenText.Replace('\n', '|')}");
    return result;
  }

}
