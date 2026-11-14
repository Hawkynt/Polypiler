using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Tooling.Toolchains;

/// <summary>
/// Finds the toolchains of a lock on this machine and compares their versions with the pins. A probe
/// only reads: it runs the toolchain's version command, or looks into the NuGet package folder, and
/// never installs, downloads or writes anything.
/// </summary>
public sealed class ToolchainLocator {

  /// <summary>The environment variable CI sets to <c>1</c>: a missing toolchain then fails a test instead of leaving it inconclusive.</summary>
  public const string RequireVariable = "POLYPILER_REQUIRE_TOOLCHAINS";

  /// <summary>How long a version command may run before it counts as missing.</summary>
  public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

  private readonly IProcessRunner _runner;
  private readonly Func<string, string?> _environment;
  private readonly string _platform;
  private readonly TimeSpan _timeout;

  /// <summary>Creates a locator for this machine: its environment, its platform and the 30 s time-out.</summary>
  public ToolchainLocator(ToolchainLock toolchains, IProcessRunner runner)
    : this(toolchains, runner, Environment.GetEnvironmentVariable, CurrentPlatform, DefaultTimeout) { }

  /// <summary>Creates a locator with everything it reads from the machine given.</summary>
  /// <param name="toolchains">The lock.</param>
  /// <param name="runner">Runs the version commands.</param>
  /// <param name="environment">Reads an environment variable: <c>PATH</c>, <c>PATHEXT</c>, the <c>home</c> variables, <c>NUGET_PACKAGES</c>, <c>HOME</c>, <c>USERPROFILE</c> and <see cref="RequireVariable"/>.</param>
  /// <param name="platform">The platform probed for: <c>linux</c>, <c>windows</c> or <c>macos</c>.</param>
  /// <param name="timeout">How long a version command may run.</param>
  public ToolchainLocator(ToolchainLock toolchains, IProcessRunner runner, Func<string, string?> environment, string platform, TimeSpan timeout) {
    this.Lock = toolchains;
    this._runner = runner;
    this._environment = environment;
    this._platform = platform;
    this._timeout = timeout;
  }

  /// <summary>The platform this process runs on, as the lock names it.</summary>
  public static string CurrentPlatform { get; } = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

  /// <summary>The lock the locator probes.</summary>
  public ToolchainLock Lock { get; }

  /// <summary>Whether CI asked for every toolchain to be present.</summary>
  public bool ToolchainsRequired => this._environment(RequireVariable) == "1";

  /// <summary>Finds a toolchain and compares its version with the pin.</summary>
  /// <param name="key">The toolchain's key.</param>
  /// <exception cref="ToolchainLockException">The lock has no such key (<c>PLY0020</c>).</exception>
  public ToolchainProbe Probe(string key) {
    var entry = this.Lock[key];
    var unknown = new ToolchainProbe(key, ToolchainStatus.Missing, entry.Version, null, null) { Command = entry.Probe };
    if (!entry.Platforms.Contains(this._platform))
      return unknown with { Status = ToolchainStatus.NotOnThisPlatform, Cause = $"not used on {this._platform}" };
    if (entry.Kind == ToolchainKind.External && this.ToolchainsRequired)
      return unknown with { Status = ToolchainStatus.NotOnThisPlatform, Cause = "an external toolchain, which CI runners do not install" };

    var home = entry.Home;
    if (entry.Requires is { } hostKey) {
      var host = this.Probe(hostKey);
      if (host.Status is ToolchainStatus.Missing or ToolchainStatus.NotOnThisPlatform)
        return unknown with { Status = host.Status, Cause = $"its host toolchain '{hostKey}' is {host.Status}" };
      home ??= this.Lock[hostKey].Home;
    }

    if (entry.Kind == ToolchainKind.NuGet)
      return this.ProbePackageFolder(entry, unknown);

    var executable = this.Resolve(entry.Probe[0], home, out var where);
    if (executable == null)
      return unknown with { Cause = $"'{entry.Probe[0]}' was not found {where}" };

    var found = unknown with { Path = executable };
    ProcessResult result;
    try {
      result = this._runner.Run([executable, .. entry.Probe.Skip(1)], this._timeout);
    } catch (Win32Exception exception) {
      return found with { Cause = $"it cannot be started: {exception.Message}" };
    }

    if (result.TimedOut)
      return found with { Cause = "probe timed out" };

    var actual = Recognise(entry.VersionPattern, result.StandardOutput, result.StandardError);
    if (actual == null)
      return found with { Status = ToolchainStatus.Mismatch, Cause = "version not recognised" };

    return found with { Status = actual == entry.Version ? ToolchainStatus.Ok : ToolchainStatus.Mismatch, ActualVersion = actual };
  }

  /// <summary>
  /// The folders a toolchain reads besides its install root: the value of each variable of its
  /// <c>roots</c> that is set to the full path of an existing folder. A variable that is unset or
  /// names nothing adds no folder.
  /// </summary>
  /// <param name="key">The toolchain's key.</param>
  /// <exception cref="ToolchainLockException">The lock has no such key (<c>PLY0020</c>).</exception>
  public IReadOnlyList<string> ReadOnlyRoots(string key)
    => [.. this.Lock[key].Roots
      .Select(this._environment)
      .OfType<string>()
      .Where(folder => Path.IsPathFullyQualified(folder) && Directory.Exists(folder))
      .Select(Path.GetFullPath)];

  /// <summary>The version the pattern captures from the first line it matches, standard output first.</summary>
  private static string? Recognise(string pattern, string output, string error) {
    var regex = new Regex(pattern, RegexOptions.CultureInvariant);
    return (output + "\n" + error).Split('\n')
      .Select(line => regex.Match(line.TrimEnd('\r')))
      .FirstOrDefault(match => match.Success)?.Groups["version"].Value;
  }

  /// <summary>Looks for the pinned version of a NuGet package in the global packages folder.</summary>
  private ToolchainProbe ProbePackageFolder(ToolchainEntry entry, ToolchainProbe unknown) {
    var root = this._environment("NUGET_PACKAGES") is { Length: > 0 } configured
      ? configured
      : Path.Combine(this._environment(this._platform == "windows" ? "USERPROFILE" : "HOME") ?? "", ".nuget", "packages");
    var folder = Path.Combine(root, entry.Probe[0].ToLowerInvariant());
    var pinned = Path.Combine(folder, entry.Version.ToLowerInvariant());
    if (Directory.Exists(pinned))
      return unknown with { Status = ToolchainStatus.Ok, ActualVersion = entry.Version, Path = pinned };

    var versions = Directory.Exists(folder) ? Directory.GetDirectories(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray() : [];
    if (versions.Length == 0)
      return unknown with { Cause = $"'{entry.Probe[0]}' is not in the NuGet package folder '{root}'" };
    return unknown with { Status = ToolchainStatus.Mismatch, ActualVersion = string.Join(",", versions), Path = folder };
  }

  /// <summary>Finds an executable in the <c>home</c> folder, when its variable is set, or on <c>PATH</c>.</summary>
  private string? Resolve(string name, string? homeVariable, out string where) {
    var names = this._platform == "windows" && Path.GetExtension(name).Length == 0
      ? [.. (this._environment("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(extension => name + extension.ToLowerInvariant()), name]
      : new[] { name };

    string[] directories;
    if (name.IndexOfAny(['/', '\\']) >= 0) {
      directories = [""];
      where = "at that path";
    } else if (homeVariable != null && this._environment(homeVariable) is { Length: > 0 } home) {
      directories = [home, Path.Combine(home, "bin")];
      where = $"in {homeVariable} ({home})";
    } else {
      directories = (this._environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
      where = "on PATH";
    }

    return directories.SelectMany(directory => names.Select(candidate => Path.GetFullPath(Path.Combine(directory, candidate))))
      .FirstOrDefault(File.Exists);
  }

}
