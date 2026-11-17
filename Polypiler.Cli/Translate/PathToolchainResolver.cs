using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Cli;

/// <summary>
/// Finds the toolchains <c>translate --verify</c> runs on the search path of the machine, as a user
/// installed them: the repository's lock pins versions for its own tests, while the command runs
/// wherever the product is installed. A key maps to the executable its lock entry probes.
/// </summary>
/// <param name="path">The search path, its folders separated by <see cref="Path.PathSeparator"/>.</param>
/// <param name="windows">Whether executables carry the extension <c>.exe</c>.</param>
public sealed class PathToolchainResolver(string path, bool windows) : IToolchainResolver {

  /// <summary>The executable of every toolchain key the command uses.</summary>
  public static IReadOnlyDictionary<string, string> Executables { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
    ["node"] = "node",
    [SandboxFactory.BubblewrapToolchain] = "bwrap"
  };

  /// <summary>The resolver of this process: its <c>PATH</c>, on its operating system.</summary>
  public static PathToolchainResolver ForHost() => new(Environment.GetEnvironmentVariable("PATH") ?? "", OperatingSystem.IsWindows());

  /// <inheritdoc />
  public ToolchainResolution Resolve(string toolchain) {
    ArgumentNullException.ThrowIfNull(toolchain);
    var name = Executables.GetValueOrDefault(toolchain, toolchain);
    var file = windows ? name + ".exe" : name;
    var found = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
      .Select(folder => Path.Combine(folder, file))
      .FirstOrDefault(File.Exists);
    return found is not null
      ? ToolchainResolution.Found(Path.GetFullPath(found))
      : ToolchainResolution.Failed(Diagnostic.Create(TranslateDiagnostics.ToolchainMissing, null, string.Format(CultureInfo.InvariantCulture, TranslateDiagnostics.NotInstalled, toolchain, name)));
  }

}
