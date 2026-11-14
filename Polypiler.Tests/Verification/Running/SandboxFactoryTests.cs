using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Verification.Running;

/// <summary>Choosing the sandbox of the host.</summary>
[TestFixture]
[Category("OsIntegration")]
public sealed class SandboxFactoryTests {

  private static readonly IToolchainResolver _ghost = Resolver(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "toolchains", "ghost.lock"));

  /// <summary>Test design 4: the sandbox of each system.</summary>
  [Test]
  public void TheHost_GetsTheSandboxOfItsSystem() {
    var sandbox = SandboxFactory.ForHost(Toolchains);
    if (OperatingSystem.IsWindows())
      Assert.That(sandbox, Is.InstanceOf<JobObjectSandbox>());
    else if (OperatingSystem.IsLinux())
      Assert.That(sandbox, Is.InstanceOf(Bubblewrap is null ? typeof(PollingSandbox) : typeof(BubblewrapSandbox)));
    else
      Assert.That(sandbox, Is.InstanceOf<PollingSandbox>());
  }

  [Test]
  public void ALinuxHostWithoutBubblewrap_IsLimited() {
    if (!OperatingSystem.IsLinux()) {
      Assert.That(SandboxFactory.ForHost(_ghost).Isolation, Is.EqualTo(IsolationLevel.Limited), "only bubblewrap isolates");
      return;
    }

    Assert.That(SandboxFactory.Bubblewrap(_ghost), Is.Null);
    Assert.That(SandboxFactory.ForHost(_ghost), Is.InstanceOf<PollingSandbox>());
  }

  [Test]
  public void ABubblewrapThatCannotStartOrFails_CannotIsolate() {
    Assert.That(SandboxFactory.CanIsolate(Path.Combine(Path.GetTempPath(), "no-such-bwrap-polypiler")), Is.False);
    if (!OperatingSystem.IsWindows())
      Assert.That(SandboxFactory.CanIsolate("/bin/false"), Is.False);
  }

}
