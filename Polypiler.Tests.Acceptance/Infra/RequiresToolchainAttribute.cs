using System;
using System.Collections.Concurrent;
using System.IO;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// Marks a test that needs a toolchain of <c>tooling/toolchains.lock</c>. Before the test runs, the
/// toolchain is probed: at its pinned version the test runs; absent or at another version it fails
/// with the probe's <c>PLY0021</c> or <c>PLY0022</c> when <c>POLYPILER_REQUIRE_TOOLCHAINS=1</c> (set
/// in CI), and is inconclusive otherwise. It is never skipped. A toolchain not used on this platform
/// leaves the test inconclusive everywhere.
/// </summary>
/// <param name="key">The toolchain's key in the lock.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresToolchainAttribute(string key) : NUnitAttribute, ITestAction {

  /// <summary>Probes once per lock and key: every test that needs a toolchain would otherwise start its version command again.</summary>
  private static readonly ConcurrentDictionary<(string Lock, string Key), Lazy<ToolchainProbe>> _probes = new();

  /// <summary>The toolchain's key in the lock.</summary>
  public string Key { get; } = key;

  /// <summary>The lock, relative to the repository root; the repository lock unless a test names a fixture.</summary>
  public string Lock { get; set; } = ToolchainLock.RepositoryPath;

  /// <inheritdoc />
  public ActionTargets Targets => ActionTargets.Test;

  /// <inheritdoc />
  public void BeforeTest(ITest test) => this.Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));

  /// <inheritdoc />
  public void AfterTest(ITest test) { }

  /// <summary>Probes the toolchain and ends the test unless it is at its pinned version.</summary>
  /// <param name="require">The value of <c>POLYPILER_REQUIRE_TOOLCHAINS</c>: <c>1</c> turns an absent toolchain into a failure.</param>
  internal void Check(string? require) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, this.Lock);
    var probe = _probes.GetOrAdd((path, this.Key), entry => new(() => new ToolchainLocator(ToolchainLock.Load(entry.Lock), new ProcessRunner()).Probe(entry.Key))).Value;
    if (probe.Status == ToolchainStatus.Ok)
      return;

    if (probe.Problem is not { } problem) {
      Assert.Inconclusive($"toolchain '{this.Key}' is not used on {ToolchainLocator.CurrentPlatform}");
      return;
    }

    var text = $"{problem.Code}: {problem.Message}";
    if (require == "1")
      Assert.Fail(text);
    else
      Assert.Inconclusive($"toolchain '{this.Key}' is not available ({text}); with {ToolchainLocator.RequireVariable}=1 this test fails");
  }

}
