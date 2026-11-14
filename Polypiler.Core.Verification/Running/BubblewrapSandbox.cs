using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// The sandbox of a Linux host with bubblewrap (<c>bwrap</c>, LGPL-2.0-or-later, started as a separate
/// process and never linked). The options follow bwrap(1): every namespace unshared, so there is no
/// network; the root file system bound read-only; a private <c>/tmp</c>; the caller's home hidden
/// behind an empty read-only file system; the toolchain's directory and the working directory bound
/// back in, the working directory being the only writable place. The limits hold by sampling the
/// process table, where bubblewrap's own two processes do not count against the process limit.
/// </summary>
public sealed class BubblewrapSandbox : ISandbox {

  /// <summary>The name bubblewrap's processes have in the process table.</summary>
  public const string LauncherName = "bwrap";

  private readonly string _bwrap;
  private readonly IProcessTable _table;
  private readonly string? _home;

  /// <summary>Creates the sandbox for the caller of this process.</summary>
  /// <param name="bwrap">The path of <c>bwrap</c>.</param>
  /// <param name="table">The process table the monitor samples.</param>
  public BubblewrapSandbox(string bwrap, IProcessTable table) : this(bwrap, table, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

  /// <summary>Creates the sandbox for a given home directory.</summary>
  /// <param name="bwrap">The path of <c>bwrap</c>.</param>
  /// <param name="table">The process table the monitor samples.</param>
  /// <param name="home">The caller's home directory, which the command must not see; <see langword="null"/> or empty when there is none to hide.</param>
  public BubblewrapSandbox(string bwrap, IProcessTable table, string? home) {
    this._bwrap = bwrap;
    this._table = table;
    this._home = string.IsNullOrEmpty(home) || home == "/" ? null : Path.TrimEndingDirectorySeparator(home);
  }

  /// <inheritdoc />
  public IsolationLevel Isolation => IsolationLevel.Isolated;

  /// <inheritdoc />
  public IReadOnlyList<string> CommandLine(SandboxCommand command) {
    var line = new List<string> { this._bwrap, "--unshare-all", "--die-with-parent", "--new-session", "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--tmpfs", "/tmp" };
    if (this._home != null)
      line.AddRange(["--tmpfs", this._home]);

    foreach (var directory in ToolchainDirectories(command.Executable).Where(this.IsHidden))
      line.AddRange(["--ro-bind", directory, directory]);

    line.AddRange(["--bind", command.WorkingDirectory, command.WorkingDirectory]);
    if (this._home != null)
      line.AddRange(["--remount-ro", this._home]);

    line.AddRange(["--chdir", command.WorkingDirectory, "--", command.Executable]);
    line.AddRange(command.Arguments);
    return line;
  }

  /// <inheritdoc />
  public IProcessTreeMonitor Watch(Process process, RunLimits limits) => new PollingMonitor(this._table, process.Id, limits.Processes, LauncherName);

  /// <summary>Whether a directory lies in what the sandbox replaces, <c>/tmp</c> or the caller's home, and so has to be bound back in.</summary>
  private bool IsHidden(string directory)
    => IsWithin(directory, "/tmp") || (this._home != null && IsWithin(directory, this._home));

  private static bool IsWithin(string directory, string root)
    => directory == root || directory.StartsWith(root + "/", StringComparison.Ordinal);

  /// <summary>The directory of the executable and, when it is a link, the directory of its final target: both must stay visible.</summary>
  private static IEnumerable<string> ToolchainDirectories(string executable) {
    var target = File.Exists(executable) ? File.ResolveLinkTarget(executable, true)?.FullName ?? executable : executable;
    return new[] { executable, target }.Select(path => Path.GetDirectoryName(path)!).Distinct(StringComparer.Ordinal);
  }

}
