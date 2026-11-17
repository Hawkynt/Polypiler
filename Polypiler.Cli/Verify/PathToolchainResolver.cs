using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Cli.Verify;

/// <summary>
/// Finds the executable of a toolchain on a search path, the way a shell finds a command: the
/// executable is named like the toolchain's key (<c>node</c>), and on Windows it may carry one of
/// the extensions <c>PATHEXT</c> names. The shipped executable has no <c>tooling/toolchains.lock</c>
/// beside it, so it runs what the user has installed. A toolchain that is not found is <c>PLY0021</c>.
/// </summary>
public sealed class PathToolchainResolver : IToolchainResolver {

  private readonly IReadOnlyList<string> _directories;
  private readonly IReadOnlyList<string> _extensions;

  /// <summary>Creates a resolver over a search path.</summary>
  /// <param name="searchPath">The directories, separated by <see cref="Path.PathSeparator"/>; <see langword="null"/> or empty for none.</param>
  /// <param name="extensions">The extensions tried after the bare name, each with its dot, such as <c>.exe</c>; empty on Unix.</param>
  /// <exception cref="ArgumentNullException"><paramref name="extensions"/> is <see langword="null"/>.</exception>
  public PathToolchainResolver(string? searchPath, IReadOnlyList<string> extensions) {
    ArgumentNullException.ThrowIfNull(extensions);
    this._directories = [.. (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    this._extensions = [.. extensions];
  }

  /// <summary>The resolver of this process: its <c>PATH</c>, and on Windows its <c>PATHEXT</c>.</summary>
  public static PathToolchainResolver FromEnvironment()
    => new(Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows() ? Extensions(Environment.GetEnvironmentVariable("PATHEXT")) : []);

  /// <summary>The extensions of a <c>PATHEXT</c> value, lower-case; <c>.exe</c> and <c>.cmd</c> when it is not set.</summary>
  /// <param name="pathExt">The value, extensions separated by <c>;</c>.</param>
  public static IReadOnlyList<string> Extensions(string? pathExt)
    => string.IsNullOrWhiteSpace(pathExt) ? [".exe", ".cmd"] : [.. pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(extension => extension.ToLowerInvariant())];

  /// <inheritdoc />
  public ToolchainResolution Resolve(string toolchain) {
    ArgumentException.ThrowIfNullOrEmpty(toolchain);
    foreach (var directory in this._directories)
      foreach (var name in this._extensions.Select(extension => toolchain + extension).Prepend(toolchain)) {
        var candidate = Path.Combine(directory, name);
        if (File.Exists(candidate))
          return ToolchainResolution.Found(Path.GetFullPath(candidate));
      }

    return ToolchainResolution.Failed(Diagnostic.Create(VerifyDiagnostics.ToolchainMissing, null,
      $"toolchain '{toolchain}' is not installed (probe: {toolchain} --version): '{toolchain}' was not found on PATH"));
  }

}
