using System.Collections.Concurrent;
using Polypiler.Core;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tooling.Toolchains;

/// <summary>
/// Resolves the runner's toolchain keys with the locator of <c>tooling/toolchains.lock</c>. A toolchain
/// that is installed is used even at another version than the pin: the version gate is the probe's
/// and <c>RequiresToolchain</c>'s, not the runner's. One that is missing, not used on this platform, or
/// only a package without an executable of its own resolves to <c>PLY0021</c>; a key the lock does not
/// have to <c>PLY0020</c>. Each key is probed once.
/// </summary>
/// <param name="locator">The locator.</param>
public sealed class LockToolchainResolver(ToolchainLocator locator) : IToolchainResolver {

  private readonly ConcurrentDictionary<string, ToolchainResolution> _resolved = new(System.StringComparer.Ordinal);

  /// <inheritdoc />
  public ToolchainResolution Resolve(string toolchain) => this._resolved.GetOrAdd(toolchain, this.Probe);

  private ToolchainResolution Probe(string key) {
    ToolchainProbe probe;
    try {
      probe = locator.Probe(key);
    } catch (ToolchainLockException exception) {
      return ToolchainResolution.Failed(Diagnostic.Create(ToolchainDiagnostic.LockInvalidDescriptor, null, exception.Diagnostics[0].Message));
    }

    if (probe.Status is ToolchainStatus.Ok or ToolchainStatus.Mismatch && probe.Path is { } path && locator.Lock[key].Kind is ToolchainKind.Tool or ToolchainKind.External)
      return ToolchainResolution.Found(path, locator.ReadOnlyRoots(key));

    var message = (probe with { Status = ToolchainStatus.Missing, Cause = probe.Cause ?? "it has no executable of its own" }).Problem!.Value.Message;
    return ToolchainResolution.Failed(Diagnostic.Create(ToolchainDiagnostic.NotInstalledDescriptor, null, message));
  }

}
