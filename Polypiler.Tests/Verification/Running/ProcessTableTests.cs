using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Verification.Running;

/// <summary>Reading the process table: Linux's <c>/proc</c> and the output of <c>ps</c>.</summary>
[TestFixture]
public sealed class ProcessTableTests {

  /// <summary>A stat line as proc(5) lays it out: utime 11, stime 12, cutime 13, cstime 14, starttime 19 and rss 21 fields behind the name.</summary>
  private static string Stat(int pid, string name, char state = 'S', int parent = 1, int rssPages = 10, long startTime = 777)
    => $"{pid} ({name}) {state} {parent} 1 1 0 -1 4194560 100 0 0 0 150 50 25 25 20 0 1 0 {startTime} 1000000 {rssPages} 18446744073709551615";

  [Test]
  public void AStatLine_IsRead() {
    var entry = ProcFileSystemTable.ParseStat(Stat(42, "dotnet", parent: 7), 4096)!;
    Assert.That(entry, Is.EqualTo(new ProcessEntry(42, 7, 1, "dotnet", 777, 40960, TimeSpan.FromSeconds(2.5))));
  }

  [Test]
  public void TheSameProcess_HasTheSameIdAndStartTime() {
    var entry = new ProcessEntry(42, 1, 1, "dotnet", 777, 0, TimeSpan.Zero);
    Assert.That(entry.IsSameProcess(entry with { ResidentBytes = 5 }), Is.True);
    Assert.That(entry.IsSameProcess(entry with { Pid = 43 }), Is.False);
    Assert.That(entry.IsSameProcess(entry with { StartTime = 778 }), Is.False);
  }

  [Test]
  public void ANameWithSpacesAndParentheses_IsReadUpToTheLastParenthesis()
    => Assert.That(ProcFileSystemTable.ParseStat(Stat(5, "a) (b c"), 4096)!.Name, Is.EqualTo("a) (b c"));

  [TestCase('Z')]
  [TestCase('X')]
  [TestCase('x')]
  public void AZombieOrADeadProcess_IsLeftOut(char state)
    => Assert.That(ProcFileSystemTable.ParseStat(Stat(5, "gone", state), 4096), Is.Null);

  [TestCase("", TestName = "An empty line")]
  [TestCase("42 dotnet S 1", TestName = "No parentheses")]
  [TestCase("x (dotnet) S 1", TestName = "No process ID")]
  [TestCase("(dotnet) S 1", TestName = "Nothing before the name")]
  [TestCase("42 )dotnet( S 1", TestName = "Parentheses the wrong way round")]
  [TestCase("42 (dotnet) S 1 1 1", TestName = "Too few fields")]
  public void ALineThatIsNoStatLine_IsLeftOut(string line)
    => Assert.That(ProcFileSystemTable.ParseStat(line, 4096), Is.Null);

  [Test]
  public void ADirectoryLaidOutLikeProc_IsRead() {
    var root = Directory.CreateTempSubdirectory("polypiler-proc-").FullName;
    try {
      foreach (var (pid, line) in new[] { ("10", Stat(10, "root", parent: 1)), ("11", Stat(11, "child", parent: 10)), ("12", Stat(12, "zombie", 'Z')) }) {
        Directory.CreateDirectory(Path.Combine(root, pid));
        File.WriteAllText(Path.Combine(root, pid, "stat"), line);
      }

      Directory.CreateDirectory(Path.Combine(root, "13"));
      Directory.CreateDirectory(Path.Combine(root, "self"));
      var entries = new ProcFileSystemTable(root).Snapshot();
      Assert.That(entries.Select(entry => (entry.Pid, entry.ParentPid, entry.Name)).Order(), Is.EqualTo(new[] { (10, 1, "root"), (11, 10, "child") }));
    } finally {
      Directory.Delete(root, true);
    }
  }

  /// <summary>Lays out processes like <c>/proc</c>: each with its stat line and, where given, one children file per thread.</summary>
  private static string Proc(params (int Pid, string Stat, string[]? Children)[] processes) {
    var root = Directory.CreateTempSubdirectory("polypiler-proc-").FullName;
    foreach (var (pid, stat, children) in processes) {
      var directory = Directory.CreateDirectory(Path.Combine(root, pid.ToString(CultureInfo.InvariantCulture))).FullName;
      File.WriteAllText(Path.Combine(directory, "stat"), stat);
      if (children is null)
        continue;
      for (var thread = 0; thread < children.Length; ++thread) {
        var task = Directory.CreateDirectory(Path.Combine(directory, "task", (pid * 100 + thread).ToString(CultureInfo.InvariantCulture))).FullName;
        File.WriteAllText(Path.Combine(task, "children"), children[thread]);
      }
    }

    return root;
  }

  /// <summary>A tree is read from its roots down, through the children of every thread, and nothing beside it is read.</summary>
  [Test]
  public void ATree_IsReadFromTheChildrenOfEveryThread() {
    var root = Proc(
      (1, Stat(1, "init", parent: 0), ["10 20 "]),
      (10, Stat(10, "root"), ["11 ", "12 "]),
      (11, Stat(11, "child", parent: 10), [""]),
      (12, Stat(12, "threaded", parent: 10), ["13 "]),
      (13, Stat(13, "grandchild", parent: 12), [""]),
      (20, Stat(20, "beside"), [""]),
      (30, Stat(30, "orphan"), [""]));
    try {
      var entries = new ProcFileSystemTable(root).SnapshotTree([10, 30, 10]);
      Assert.That(entries.Select(entry => entry.Pid).Order(), Is.EqualTo(new[] { 10, 11, 12, 13, 30 }));
    } finally {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A root that has ended, or that is a zombie, is left out with its children; a child that ended between two reads is no error.</summary>
  [Test]
  public void AGoneOrZombieProcess_IsLeftOutOfTheTree() {
    var root = Proc(
      (10, Stat(10, "root"), ["11 12 99 "]),
      (11, Stat(11, "zombie", 'Z', parent: 10), ["13 "]),
      (12, Stat(12, "child", parent: 10), null),
      (13, Stat(13, "under-zombie", parent: 11), [""]));
    try {
      Assert.That(new ProcFileSystemTable(root).SnapshotTree([10, 77]).Select(entry => entry.Pid).Order(), Is.EqualTo(new[] { 10, 12 }));
    } finally {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A kernel without the children files (<c>CONFIG_PROC_CHILDREN</c>) gets the whole table, which holds the tree too.</summary>
  [Test]
  public void WithoutChildrenFiles_TheWholeTableIsRead() {
    var root = Proc((10, Stat(10, "root"), null), (11, Stat(11, "child", parent: 10), null), (20, Stat(20, "beside"), null));
    try {
      Directory.CreateDirectory(Path.Combine(root, "10", "task", "1000"));
      Assert.That(new ProcFileSystemTable(root).SnapshotTree([10]).Select(entry => entry.Pid).Order(), Is.EqualTo(new[] { 10, 11, 20 }));
    } finally {
      Directory.Delete(root, true);
    }
  }

  /// <summary>A table that cannot follow a tree answers with all of its processes.</summary>
  [Test]
  public void ATableWithoutTrees_AnswersWithTheWholeTable() {
    IProcessTable table = new FixedTable([new(1, 0, 1, "init", 1, 0, TimeSpan.Zero), new(2, 1, 1, "other", 1, 0, TimeSpan.Zero)]);
    Assert.That(table.SnapshotTree([2]).Select(entry => entry.Pid), Is.EqualTo(new[] { 1, 2 }));
  }

  private sealed class FixedTable(IReadOnlyList<ProcessEntry> entries) : IProcessTable {

    public IReadOnlyList<ProcessEntry> Snapshot() => entries;

    public void Kill(int pid) { }

  }

  /// <summary>On Linux the tree of this process holds a child it started, and none of the processes beside it.</summary>
  [Test]
  [Category("OsIntegration")]
  public void TheMachinesProc_ShowsTheTreeOfThisProcess() {
    if (!OperatingSystem.IsLinux()) {
      Assert.Pass("only Linux has the process table in /proc");
      return;
    }

    var info = new ProcessStartInfo(RunnerTesting.Toolchains.Resolve(RunnerTesting.DotNet).Executable!) { UseShellExecute = false };
    foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, RunnerTesting.ProbeAssembly), "sleep", "60000" })
      info.ArgumentList.Add(argument);
    using var sleeper = Process.Start(info)!;
    try {
      var tree = new ProcFileSystemTable().SnapshotTree([Environment.ProcessId]);
      Assert.That(tree.Select(entry => entry.Pid), Does.Contain(Environment.ProcessId).And.Contain(sleeper.Id).And.Not.Contain(1));
    } finally {
      sleeper.Kill(true);
      sleeper.WaitForExit();
    }
  }

  [Test]
  public void TheMachinesProc_ShowsThisProcess() {
    if (!OperatingSystem.IsLinux()) {
      Assert.That(Directory.Exists("/proc/self/task"), Is.False, "only Linux has the process table in /proc");
      return;
    }

    Assert.That(new ProcFileSystemTable().Snapshot().Select(entry => entry.Pid), Does.Contain(Environment.ProcessId));
  }

  [Test]
  public void PsOutput_IsRead() {
    var entries = PsCommandTable.Parse("""
          1     0     1  13780 00:32:15 Ss   systemd
        200     1   200   2048 1-02:03:04 S    /usr/local/bin/my tool
        201   200   200      0 0:00.03 Z    gone
        abc     1     1      1 00:00:01 S    broken
        202   200   200   1024 nonsense S    broken
        203   200   200 

      """);
    Assert.That(entries, Is.EqualTo(new[] {
      new ProcessEntry(1, 0, 1, "systemd", 0, 13780 * 1024L, new TimeSpan(0, 0, 32, 15)),
      new ProcessEntry(200, 1, 200, "my tool", 0, 2048 * 1024L, new TimeSpan(1, 2, 3, 4))
    }));
  }

  [TestCase("0:00.03", 30L)]
  [TestCase("12:34", 754_000L)]
  [TestCase("01:02:03", 3_723_000L)]
  [TestCase("2-00:00:01", 172_801_000L)]
  public void APsTime_IsRead(string text, long milliseconds)
    => Assert.That(PsCommandTable.ParseTime(text), Is.EqualTo(TimeSpan.FromMilliseconds(milliseconds)));

  [TestCase("5")]
  [TestCase("1:2:3:4")]
  [TestCase("x-00:01")]
  [TestCase("a:01")]
  [TestCase("01:xx")]
  public void ATextThatIsNoPsTime_IsRefused(string text)
    => Assert.That(PsCommandTable.ParseTime(text), Is.Null);

  [Test]
  [Category("OsIntegration")]
  public void TheMachinesPs_ShowsThisProcess() {
    if (OperatingSystem.IsWindows()) {
      Assert.That(() => new PsCommandTable().Snapshot(), Throws.InvalidOperationException, "Windows has no /bin/ps");
      return;
    }

    Assert.That(new PsCommandTable().Snapshot().Select(entry => entry.Pid), Does.Contain(Environment.ProcessId));
  }

  [Test]
  public void APsThatCannotStart_IsAnInvalidOperation()
    => Assert.That(() => new PsCommandTable(Path.Combine(Path.GetTempPath(), "no-such-ps-polypiler")).Snapshot(), Throws.InvalidOperationException.With.Message.Contains("cannot be started"));

  [Test]
  [Category("OsIntegration")]
  public void APsThatFails_IsAnInvalidOperation() {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("no failing ps to start on Windows; the table is only used on macOS");
      return;
    }

    Assert.That(() => new PsCommandTable("/bin/false").Snapshot(), Throws.InvalidOperationException.With.Message.Contains("exited with 1"));
  }

  [Test]
  [Category("OsIntegration")]
  public void Killing_EndsAProcess_AndAProcessThatIsGoneIsNoError() {
    var info = new ProcessStartInfo(RunnerTesting.Toolchains.Resolve(RunnerTesting.DotNet).Executable!) { UseShellExecute = false };
    foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, RunnerTesting.ProbeAssembly), "sleep", "60000" })
      info.ArgumentList.Add(argument);
    using var sleeper = Process.Start(info)!;
    IProcessTable table = OperatingSystem.IsLinux() ? new ProcFileSystemTable() : new PsCommandTable();
    table.Kill(sleeper.Id);
    Assert.That(sleeper.WaitForExit(TimeSpan.FromSeconds(10)), Is.True);
    Assert.That(() => table.Kill(sleeper.Id), Throws.Nothing);
  }

}
