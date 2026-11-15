using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// The sandbox of a Linux host with bubblewrap (<c>bwrap</c>, LGPL-2.0-or-later, started as a separate
/// process and never linked). The options follow bwrap(1): every namespace unshared, so there is no
/// network; the root file system bound read-only; a private <c>/tmp</c>; the caller's home hidden
/// behind an empty read-only file system; the install root of the toolchain (<see cref="ToolchainRoots"/>)
/// and the extra roots of its lock entry bound back in read-only, and the working directory bound back
/// in as the only writable place. The limits hold by sampling the process table, where bubblewrap's
/// own two processes do not count against the process limit.
/// </summary>
public sealed class BubblewrapSandbox : ISandbox {

  /// <summary>The name bubblewrap's processes have in the process table.</summary>
  public const string LauncherName = "bwrap";

  private readonly string _bwrap;
  private readonly IProcessTable _table;
  private readonly string? _home;
  private readonly ConcurrentDictionary<string, bool> _noted = new(StringComparer.Ordinal);

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

  /// <summary>
  /// Where the sandbox says, once per reason, why it shows less of a toolchain than a root rule would:
  /// a toolchain without a root marker, a root that would show the whole home folder, an extra root it
  /// refuses. Standard error unless set.
  /// </summary>
  public Action<string> Log { get; init; } = Console.Error.WriteLine;

  /// <inheritdoc />
  public IsolationLevel Isolation => IsolationLevel.Isolated;

  /// <inheritdoc />
  public IReadOnlyList<string> CommandLine(SandboxCommand command) {
    var line = new List<string> { this._bwrap, "--unshare-all", "--die-with-parent", "--new-session", "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--tmpfs", "/tmp" };
    if (this._home != null)
      line.AddRange(["--tmpfs", this._home]);

    foreach (var root in this.Roots(command))
      line.AddRange(["--ro-bind", root, root]);

    line.AddRange(["--bind", command.WorkingDirectory, command.WorkingDirectory]);
    if (this._home != null)
      line.AddRange(["--remount-ro", this._home]);

    line.AddRange(["--chdir", command.WorkingDirectory, "--", command.Executable]);
    line.AddRange(command.Arguments);
    return line;
  }

  /// <inheritdoc />
  public IProcessTreeMonitor Watch(Process process, RunLimits limits) => new PollingMonitor(this._table, process.Id, limits.Processes, LauncherName);

  /// <summary>
  /// What the command sees again of what the sandbox hides, all of it read-only: the roots of its
  /// toolchain and its extra roots, each once and none inside another.
  /// </summary>
  /// <param name="command">The command.</param>
  public IReadOnlyList<string> Roots(SandboxCommand command) {
    ArgumentNullException.ThrowIfNull(command);
    var roots = new List<string>();
    foreach (var root in ToolchainRoots.Of(command.Executable, this.MayShow).Where(root => this.IsHidden(root.Path))) {
      if (root.Reason != null)
        this.Note($"sandbox: of the toolchain '{command.Executable}' only '{root.Path}' is shown: {root.Reason}");
      roots.Add(root.Path);
    }

    foreach (var extra in command.ReadOnlyRoots) {
      var full = Path.IsPathFullyQualified(extra) ? Path.TrimEndingDirectorySeparator(extra) : null;
      if (full == null || !this.MayShow(full))
        this.Note($"sandbox: the extra root '{extra}' of the toolchain '{command.Executable}' is not shown: it is not a full path, or it would show too much");
      else if (this.IsHidden(full))
        roots.Add(full);
    }

    var distinct = roots.Distinct(StringComparer.Ordinal).ToList();
    return [.. distinct.Where(root => !distinct.Any(other => other != root && IsWithin(root, other)))];
  }

  private void Note(string message) {
    if (this._noted.TryAdd(message, true))
      this.Log(message);
  }

  /// <summary>Whether a path lies in what the sandbox replaces, <c>/tmp</c> or the caller's home, and so has to be bound back in.</summary>
  private bool IsHidden(string path)
    => IsWithin(path, "/tmp") || (this._home != null && IsWithin(path, this._home));

  /// <summary>
  /// Whether a folder may be shown: never the whole root, <c>/tmp</c>, the home folder or one above
  /// it, nor <c>~/.local</c>, the prefix all of a user's own installs share.
  /// </summary>
  private bool MayShow(string folder)
    => folder != "/" && folder != "/tmp" && (this._home == null || (!IsWithin(this._home, folder) && folder != this._home + "/.local"));

  private static bool IsWithin(string path, string root)
    => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

}
