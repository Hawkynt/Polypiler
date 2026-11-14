using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// Watches a process tree by reading the process table on every sample. A process belongs to the tree
/// when it is the root, when its parent was a member when it was first seen, or, when the sandbox
/// started the root as the leader of a process group of its own, when it is in that group. It stays a
/// member when its parent ends and it is handed to another, so a child that outlives the program it
/// came from is still killed with the run; the group finds such a child even when the program ended
/// before a sample saw either of them. The resident memory and the processor time are the sums over the
/// members alive at the sample. A process beyond the process limit is killed, the newest first, so
/// that to the program it looks like a process that could not be started.
/// </summary>
public sealed class PollingMonitor : IProcessTreeMonitor {

  private const int KillRounds = 50;
  private static readonly TimeSpan _killPause = TimeSpan.FromMilliseconds(10);

  private readonly IProcessTable _table;
  private readonly int _rootPid;
  private readonly int _processLimit;
  private readonly string? _uncounted;
  private readonly Dictionary<int, ProcessEntry> _members = [];
  private readonly bool _rootLeadsGroup;
  private bool _rootSeen;

  /// <summary>Starts watching a tree.</summary>
  /// <param name="table">The process table.</param>
  /// <param name="rootPid">The ID of the process the runner started.</param>
  /// <param name="processLimit">How many processes the tree may have at once.</param>
  /// <param name="uncounted">The name of the sandbox's own launcher processes, which do not count against the limit; <see langword="null"/> when there are none.</param>
  /// <param name="rootLeadsGroup">Whether the root leads a process group whose ID is its own process ID, so that every process of that group is a member.</param>
  public PollingMonitor(IProcessTable table, int rootPid, int processLimit, string? uncounted, bool rootLeadsGroup = false) {
    this._rootLeadsGroup = rootLeadsGroup;
    this._table = table;
    this._rootPid = rootPid;
    this._processLimit = processLimit;
    this._uncounted = uncounted;
  }

  /// <summary>The members of the tree that were alive at the last sample.</summary>
  public IReadOnlyCollection<ProcessEntry> Members => this._members.Values;

  /// <inheritdoc />
  public ResourceSample Sample() {
    this.Update(this._table.Snapshot());
    var counted = this._members.Values.Where(member => member.Name != this._uncounted).ToList();
    foreach (var excess in counted.OrderByDescending(member => member.StartTime).ThenByDescending(member => member.Pid).Take(counted.Count - this._processLimit))
      this._table.Kill(excess.Pid);

    return new(this._members.Values.Sum(member => member.ResidentBytes), TimeSpan.FromTicks(this._members.Values.Sum(member => member.CpuTime.Ticks)));
  }

  /// <summary>Kills the members until none is left in the process table, for at most 50 rounds 10 ms apart: a killed process takes a moment to go.</summary>
  public void KillAll() {
    for (var round = 0; round < KillRounds; ++round) {
      this.Update(this._table.Snapshot());
      if (this._members.Count == 0)
        return;

      foreach (var member in this._members.Values)
        this._table.Kill(member.Pid);
      Thread.Sleep(_killPause);
    }
  }

  /// <inheritdoc />
  public void Dispose() { }

  /// <summary>Brings the members up to date with a snapshot: drops the ones that ended, adds the root and every new child of a member.</summary>
  private void Update(IReadOnlyList<ProcessEntry> snapshot) {
    var current = new Dictionary<int, ProcessEntry>();
    foreach (var entry in snapshot)
      current[entry.Pid] = entry;

    foreach (var member in this._members.Values.ToList())
      if (current.TryGetValue(member.Pid, out var now) && now.IsSameProcess(member))
        this._members[member.Pid] = now;
      else
        this._members.Remove(member.Pid);

    if (!this._rootSeen && current.TryGetValue(this._rootPid, out var root)) {
      this._members[root.Pid] = root;
      this._rootSeen = true;
    }


    for (var grown = true; grown;) {
      grown = false;
      foreach (var entry in snapshot)
        if (!this._members.ContainsKey(entry.Pid) && (this._members.ContainsKey(entry.ParentPid) || (this._rootLeadsGroup && entry.Group == this._rootPid))) {
          this._members[entry.Pid] = entry;
          grown = true;
        }
    }
  }

}
