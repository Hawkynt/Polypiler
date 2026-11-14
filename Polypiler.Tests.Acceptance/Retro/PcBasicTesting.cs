using System;
using System.IO;
using Polypiler.Core.Verification.Emulators;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Verification.Running.Support;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Retro;

/// <summary>What the PC-BASIC tests share: the fixtures, the adapter on this host's sandbox and the toolchain check.</summary>
internal static class PcBasicTesting {

  /// <summary>The fixtures of the GW-BASIC oracle.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "retro", "gwbasic", "oracle");

  /// <summary>A fixture file by name.</summary>
  public static string Fixture(string name) => Path.Combine(FixtureFolder, name);

  /// <summary>The adapter on the runner of this host; the fixtures are part of the repository and so trusted.</summary>
  public static PcBasic Adapter() => new(new CrossLanguageRunner(RunnerTesting.Host, RunnerTesting.Toolchains)) { Trust = TrustLevel.Trusted };

  /// <summary>Ends the test as inconclusive, or as failed in CI, unless PC-BASIC is at its pinned version.</summary>
  public static void RequirePcBasic() => new RequiresToolchainAttribute(PcBasic.Toolchain).Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));

}
