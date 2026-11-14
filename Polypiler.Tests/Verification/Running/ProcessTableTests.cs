using System;
using System.Diagnostics;
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
