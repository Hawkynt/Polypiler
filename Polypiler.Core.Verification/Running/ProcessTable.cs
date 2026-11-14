using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Polypiler.Core.Verification.Running;

/// <summary>One process as a snapshot of the process table shows it.</summary>
/// <param name="Pid">The process ID.</param>
/// <param name="ParentPid">The parent's process ID.</param>
/// <param name="Group">The ID of its process group; a process the runner made a group leader passes it on to everything it starts.</param>
/// <param name="Name">The short name of its executable.</param>
/// <param name="StartTime">When it started, in the table's own unit; with the ID it tells a process from a later one that reuses the ID. 0 when the table does not know.</param>
/// <param name="ResidentBytes">Its resident memory.</param>
/// <param name="CpuTime">The processor time it and the children it has waited for have used.</param>
public sealed record ProcessEntry(int Pid, int ParentPid, int Group, string Name, ulong StartTime, long ResidentBytes, TimeSpan CpuTime) {

  /// <summary>Whether two entries are the same process: same ID and same start time.</summary>
  public bool IsSameProcess(ProcessEntry other) => this.Pid == other.Pid && this.StartTime == other.StartTime;

}

/// <summary>The processes of the machine, read whenever a monitor samples.</summary>
public interface IProcessTable {

  /// <summary>Every live process; processes that have ended but not been reaped are left out.</summary>
  IReadOnlyList<ProcessEntry> Snapshot();

  /// <summary>Kills a process at once; a process that has already ended is no error.</summary>
  /// <param name="pid">The process ID.</param>
  void Kill(int pid);

}

/// <summary>The process table of Linux, read from <c>/proc/&lt;pid&gt;/stat</c> (proc(5)).</summary>
public sealed class ProcFileSystemTable : IProcessTable {

  /// <summary><c>USER_HZ</c>, the unit of the times in <c>/proc/&lt;pid&gt;/stat</c>: 100 on every Linux ABI (proc(5), times(2)).</summary>
  public const int ClockTicksPerSecond = 100;

  private readonly string _root;

  /// <summary>Reads the machine's <c>/proc</c>.</summary>
  public ProcFileSystemTable() : this("/proc") { }

  /// <summary>Reads a directory laid out like <c>/proc</c>.</summary>
  /// <param name="root">The directory.</param>
  public ProcFileSystemTable(string root) => this._root = root;

  /// <inheritdoc />
  public IReadOnlyList<ProcessEntry> Snapshot() {
    var entries = new List<ProcessEntry>();
    foreach (var directory in Directory.EnumerateDirectories(this._root)) {
      if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        continue;

      string line;
      try {
        line = File.ReadAllText(Path.Combine(directory, "stat"));
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
        continue;
      }

      if (ParseStat(line, Environment.SystemPageSize) is { } entry)
        entries.Add(entry);
    }

    return entries;
  }

  /// <inheritdoc />
  public void Kill(int pid) => ProcessKiller.Kill(pid);

  /// <summary>Reads one line of <c>/proc/&lt;pid&gt;/stat</c>.</summary>
  /// <param name="line">The line.</param>
  /// <param name="pageSize">The size of a memory page, the unit of the resident set.</param>
  /// <returns>The process; <see langword="null"/> for a zombie, a dead process, or a line that is not a stat line.</returns>
  public static ProcessEntry? ParseStat(string line, int pageSize) {
    // The name stands in parentheses and may itself contain spaces and parentheses: the fields
    // after it start behind the last ')'.
    var open = line.IndexOf('(');
    var close = line.LastIndexOf(')');
    if (open < 1 || close < open || !int.TryParse(line.AsSpan(0, open).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
      return null;

    var fields = line[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (fields.Length < 22 || fields[0] is "Z" or "X" or "x")
      return null;

    long Field(int index) => long.Parse(fields[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    var ticks = Field(11) + Field(12) + Field(13) + Field(14);
    return new(pid, (int)Field(1), (int)Field(2), line[(open + 1)..close], (ulong)Field(19), Field(21) * pageSize, TimeSpan.FromSeconds((double)ticks / ClockTicksPerSecond));
  }

}

/// <summary>
/// The process table as <c>ps</c> prints it, for systems without <c>/proc</c> such as macOS. It knows
/// no start times, so a process is told from a later one with the same ID by its name only.
/// </summary>
public sealed class PsCommandTable : IProcessTable {

  private readonly string _ps;

  /// <summary>Runs <c>/bin/ps</c>.</summary>
  public PsCommandTable() : this("/bin/ps") { }

  /// <summary>Runs the given <c>ps</c>.</summary>
  /// <param name="ps">The path of <c>ps</c>.</param>
  public PsCommandTable(string ps) => this._ps = ps;

  /// <inheritdoc />
  /// <exception cref="InvalidOperationException"><c>ps</c> cannot be started or fails.</exception>
  public IReadOnlyList<ProcessEntry> Snapshot() {
    // Standard error is not captured and standard output is read on this thread: a monitor samples
    // while the thread pool may be busy, and a read that waits for the pool would stall ps on a full pipe.
    var info = new ProcessStartInfo(this._ps) { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in new[] { "-A", "-o", "pid=,ppid=,pgid=,rss=,time=,stat=,comm=" })
      info.ArgumentList.Add(argument);

    Process ps;
    try {
      ps = Process.Start(info)!;
    } catch (Win32Exception exception) {
      throw new InvalidOperationException($"'{this._ps}' cannot be started: {exception.Message}", exception);
    }

    using (ps) {
      var output = ps.StandardOutput.ReadToEnd();
      ps.WaitForExit();
      if (ps.ExitCode != 0)
        throw new InvalidOperationException($"'{this._ps}' exited with {ps.ExitCode}");

      return Parse(output);
    }
  }

  /// <inheritdoc />
  public void Kill(int pid) => ProcessKiller.Kill(pid);

  /// <summary>Reads the output of <c>ps -A -o pid=,ppid=,pgid=,rss=,time=,stat=,comm=</c>; a zombie and a line that does not fit are left out.</summary>
  /// <param name="output">The output.</param>
  public static IReadOnlyList<ProcessEntry> Parse(string output)
    => [.. output.Split('\n').Select(ParseLine).OfType<ProcessEntry>()];

  private static ProcessEntry? ParseLine(string line) {
    var fields = line.Trim().Split((char[]?)null, 7, StringSplitOptions.RemoveEmptyEntries);
    if (fields.Length < 7
        || fields[5].StartsWith('Z')
        || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
        || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
        || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var group)
        || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var kibibytes)
        || ParseTime(fields[4]) is not { } cpu)
      return null;

    return new(pid, parent, group, Path.GetFileName(fields[6].Trim()), 0, kibibytes * 1024, cpu);
  }

  /// <summary>Reads a processor time as <c>ps</c> prints it: <c>[[dd-]hh:]mm:ss[.ff]</c>.</summary>
  /// <param name="text">The time.</param>
  /// <returns>The time; <see langword="null"/> when the text is not one.</returns>
  public static TimeSpan? ParseTime(string text) {
    var days = 0;
    var dash = text.IndexOf('-');
    if (dash >= 0) {
      if (!int.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out days))
        return null;
      text = text[(dash + 1)..];
    }

    var parts = text.Split(':');
    if (parts.Length is < 2 or > 3 || !double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
      return null;

    var total = seconds;
    var unit = 60.0;
    for (var index = parts.Length - 2; index >= 0; --index, unit *= 60) {
      if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        return null;
      total += value * unit;
    }

    return TimeSpan.FromSeconds(total) + TimeSpan.FromDays(days);
  }

}

/// <summary>Kills a single process by ID, without its children: the monitor finds those itself.</summary>
internal static class ProcessKiller {

  public static void Kill(int pid) {
    try {
      using var process = Process.GetProcessById(pid);
      process.Kill(false);
    } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) {
      // It has already ended, or it is not ours to kill.
    }
  }

}
