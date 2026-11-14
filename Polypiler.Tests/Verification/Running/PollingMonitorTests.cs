using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Running;

/// <summary>The polling monitor against a scripted process table: who belongs to the tree, what it uses and what is killed.</summary>
[TestFixture]
public sealed class PollingMonitorTests {

  /// <summary>A process table whose snapshots the test writes; a kill takes the process out of the next snapshot unless the test says it survives.</summary>
  private sealed class ScriptedTable : IProcessTable {

    public List<ProcessEntry> Processes { get; } = [];

    public List<int> Killed { get; } = [];

    public bool KillsWork { get; set; } = true;

    public int Snapshots { get; private set; }

    public int TreeSnapshots { get; private set; }

    public IReadOnlyList<ProcessEntry> Snapshot() {
      ++this.Snapshots;
      return [.. this.Processes];
    }

    /// <summary>The roots and their descendants by the parent IDs the table holds now, as the children files of /proc show them.</summary>
    public IReadOnlyList<ProcessEntry> SnapshotTree(IReadOnlyCollection<int> roots) {
      ++this.TreeSnapshots;
      var tree = this.Processes.Where(entry => roots.Contains(entry.Pid)).ToList();
      for (var grown = true; grown;) {
        var children = this.Processes.Where(entry => !tree.Contains(entry) && tree.Any(member => member.Pid == entry.ParentPid)).ToList();
        tree.AddRange(children);
        grown = children.Count > 0;
      }

      return tree;
    }

    public void Kill(int pid) {
      this.Killed.Add(pid);
      if (this.KillsWork)
        this.Processes.RemoveAll(entry => entry.Pid == pid);
    }

  }

  private static ProcessEntry Entry(int pid, int parent, ulong start = 1, long memory = 100, int cpuMs = 10, string name = "dotnet", int group = 1)
    => new(pid, parent, group, name, start, memory, TimeSpan.FromMilliseconds(cpuMs));

  [Test]
  public void TheTree_IsTheRootAndEveryDescendant_AndItsUseIsTheirSum() {
    var table = new ScriptedTable { Processes = { Entry(1, 0), Entry(10, 1), Entry(11, 10), Entry(12, 11, memory: 1000), Entry(20, 1), Entry(13, 12) } };
    var monitor = new PollingMonitor(table, 10, 64, null);
    var sample = monitor.Sample();
    Assert.That(monitor.Members.Select(member => member.Pid).Order(), Is.EqualTo(new[] { 10, 11, 12, 13 }));
    Assert.That(sample, Is.EqualTo(new ResourceSample(1300, TimeSpan.FromMilliseconds(40))));
    Assert.That(table.Killed, Is.Empty);
    monitor.Dispose();
  }

  [Test]
  public void ARootNotYetOrNoLongerInTheTable_LeavesTheTreeEmpty() {
    var table = new ScriptedTable { Processes = { Entry(1, 0) } };
    var monitor = new PollingMonitor(table, 10, 64, null);
    Assert.That(monitor.Sample(), Is.EqualTo(new ResourceSample(0, TimeSpan.Zero)));
    Assert.That(monitor.Members, Is.Empty);
  }

  /// <summary>Test design 17: a child that outlives its parent stays in the tree and is killed with the run.</summary>
  [Test]
  public void AnOrphan_StaysAMemberAndIsKilled() {
    var table = new ScriptedTable { Processes = { Entry(10, 1), Entry(11, 10) } };
    var monitor = new PollingMonitor(table, 10, 64, null);
    monitor.Sample();
    table.Processes.RemoveAll(entry => entry.Pid == 10);
    table.Processes[0] = table.Processes[0] with { ParentPid = 1 };
    monitor.Sample();
    Assert.That(monitor.Members.Select(member => member.Pid), Is.EqualTo(new[] { 11 }));
    monitor.KillAll();
    Assert.That(table.Killed, Is.EqualTo(new[] { 11 }));
    Assert.That(monitor.Members, Is.Empty);
  }

  /// <summary>
  /// A sample reads the tree from the root and the members, not the whole table; with a root that leads
  /// its group every eighth sample reads the whole table, which finds an orphan the tree never saw:
  /// not in the seventh sample, in the eighth.
  /// </summary>
  [Test]
  public void ARootLeadingItsOwnGroup_FindsAnOrphanTheTreeNeverSaw_EveryEighthSample() {
    var table = new ScriptedTable();
    var monitor = new PollingMonitor(table, 10, 64, null, true);
    monitor.Sample();
    table.Processes.Add(Entry(12, 1, group: 10));
    table.Processes.Add(Entry(13, 12, group: 13));
    table.Processes.Add(Entry(14, 1, group: 1));
    for (var sample = 2; sample < PollingMonitor.FullSampleInterval; ++sample)
      monitor.Sample();
    Assert.That(monitor.Members, Is.Empty);
    Assert.That((table.Snapshots, table.TreeSnapshots), Is.EqualTo((0, PollingMonitor.FullSampleInterval - 1)));
    monitor.Sample();
    Assert.That(monitor.Members.Select(member => member.Pid).Order(), Is.EqualTo(new[] { 12, 13 }));
    Assert.That((table.Snapshots, table.TreeSnapshots), Is.EqualTo((1, PollingMonitor.FullSampleInterval - 1)));
  }

  /// <summary>Without a group of its own the tree is all there is to find: no sample reads the whole table.</summary>
  [Test]
  public void ARootThatLeadsNoGroup_IsSampledAsATreeOnly() {
    var table = new ScriptedTable { Processes = { Entry(10, 1), Entry(11, 10), Entry(20, 1) } };
    var monitor = new PollingMonitor(table, 10, 64, null);
    for (var sample = 0; sample < 2 * PollingMonitor.FullSampleInterval; ++sample)
      monitor.Sample();
    Assert.That((table.Snapshots, table.TreeSnapshots), Is.EqualTo((0, 2 * PollingMonitor.FullSampleInterval)));
    Assert.That(monitor.Members.Select(member => member.Pid).Order(), Is.EqualTo(new[] { 10, 11 }));
  }

  [Test]
  public void ARootThatLeadsNoGroup_DoesNotClaimOne() {
    var table = new ScriptedTable { Processes = { Entry(10, 1, group: 10), Entry(11, 1, group: 10) } };
    var monitor = new PollingMonitor(table, 10, 64, null);
    monitor.Sample();
    Assert.That(monitor.Members.Select(member => member.Pid), Is.EqualTo(new[] { 10 }));
  }

  [Test]
  public void AReusedProcessId_IsNotTheSameProcess() {
    var table = new ScriptedTable { Processes = { Entry(10, 1), Entry(11, 10) } };
    var monitor = new PollingMonitor(table, 10, 64, null);
    monitor.Sample();
    table.Processes.Clear();
    table.Processes.Add(Entry(11, 1, start: 99));
    table.Processes.Add(Entry(10, 1, start: 99));
    monitor.Sample();
    Assert.That(monitor.Members, Is.Empty, "neither the reused root ID nor the reused child ID belongs to the run");
    monitor.KillAll();
    Assert.That(table.Killed, Is.Empty);
  }

  /// <summary>Test design 11: the newest processes beyond the limit are killed, the ones within it are not.</summary>
  [Test]
  public void ProcessesBeyondTheLimit_AreKilled_NewestFirst() {
    var table = new ScriptedTable { Processes = { Entry(10, 1, start: 1), Entry(14, 10, start: 5), Entry(11, 10, start: 2), Entry(13, 10, start: 4), Entry(12, 10, start: 3) } };
    var monitor = new PollingMonitor(table, 10, 3, null);
    monitor.Sample();
    Assert.That(table.Killed, Is.EqualTo(new[] { 14, 13 }));
  }

  [Test]
  public void ProcessesWithTheSameStartTime_AreKilledHighestIdFirst() {
    var table = new ScriptedTable { Processes = { Entry(10, 1, start: 0), Entry(12, 10, start: 0), Entry(11, 10, start: 0) } };
    new PollingMonitor(table, 10, 2, null).Sample();
    Assert.That(table.Killed, Is.EqualTo(new[] { 12 }));
  }

  [Test]
  public void ATreeAtTheLimit_LosesNothing() {
    var table = new ScriptedTable { Processes = { Entry(10, 1), Entry(11, 10), Entry(12, 10) } };
    new PollingMonitor(table, 10, 3, null).Sample();
    Assert.That(table.Killed, Is.Empty);
  }

  [Test]
  public void TheSandboxsOwnProcesses_DoNotCountAgainstTheLimit() {
    var table = new ScriptedTable { Processes = { Entry(10, 1, name: "bwrap"), Entry(11, 10, name: "bwrap"), Entry(12, 11, start: 2), Entry(13, 12, start: 3) } };
    var monitor = new PollingMonitor(table, 10, 2, "bwrap");
    monitor.Sample();
    Assert.That(table.Killed, Is.Empty);
    table.Processes.Add(Entry(14, 12, start: 4));
    monitor.Sample();
    Assert.That(table.Killed, Is.EqualTo(new[] { 14 }));
  }

  [Test]
  public void KillAll_KillsEveryMember_AndStopsWhenNoneIsLeft() {
    var table = new ScriptedTable { Processes = { Entry(1, 0), Entry(10, 1), Entry(11, 10) } };
    var monitor = new PollingMonitor(table, 10, 64, null);
    monitor.Sample();
    monitor.KillAll();
    Assert.That(table.Killed.Order(), Is.EqualTo(new[] { 10, 11 }));
    Assert.That(table.Processes.Select(entry => entry.Pid), Is.EqualTo(new[] { 1 }));
    Assert.That((table.TreeSnapshots, table.Snapshots), Is.EqualTo((1, 2)));
  }

  [Test]
  public void KillAll_GivesUpOnAProcessThatWillNotDie() {
    var table = new ScriptedTable { Processes = { Entry(10, 1) }, KillsWork = false };
    var monitor = new PollingMonitor(table, 10, 64, null);
    monitor.KillAll();
    Assert.That(table.Killed, Has.Count.EqualTo(50));
  }

}
