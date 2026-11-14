using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// The sandbox of Windows. The command runs as it is; the process is put into a Job Object
/// (Microsoft's documentation of <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>) that caps the number of
/// active processes, measures the committed memory and the processor time of the whole tree, and
/// kills every process of the job when it is closed. Windows offers no isolation from the network or
/// the file system here, so the sandbox is <see cref="IsolationLevel.Limited"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JobObjectSandbox : ISandbox {

  /// <inheritdoc />
  public IsolationLevel Isolation => IsolationLevel.Limited;

  /// <inheritdoc />
  public IReadOnlyList<string> CommandLine(SandboxCommand command) => [command.Executable, .. command.Arguments];

  /// <inheritdoc />
  /// <exception cref="Win32Exception">The job cannot be created or the process not put into it.</exception>
  public IProcessTreeMonitor Watch(Process process, RunLimits limits) => new JobMonitor(process, limits.Processes);

  /// <summary>One Job Object holding the step's process tree.</summary>
  private sealed class JobMonitor : IProcessTreeMonitor {

    private const int BasicAccountingInformation = 1;
    private const int ExtendedLimitInformation = 9;
    private const uint LimitActiveProcess = 0x00000008;
    private const uint LimitKillOnJobClose = 0x00002000;

    private readonly IntPtr _job;

    public JobMonitor(Process process, int processes) {
      this._job = CreateJobObjectW(IntPtr.Zero, null);
      if (this._job == IntPtr.Zero)
        throw new Win32Exception(Marshal.GetLastWin32Error());

      var limits = new ExtendedLimit { Basic = { LimitFlags = LimitActiveProcess | LimitKillOnJobClose, ActiveProcessLimit = (uint)processes } };
      if (!SetInformationJobObject(this._job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimit>())
          || !AssignProcessToJobObject(this._job, process.Handle)) {
        var error = Marshal.GetLastWin32Error();
        CloseHandle(this._job);
        throw new Win32Exception(error);
      }
    }

    public ResourceSample Sample() {
      QueryInformationJobObject(this._job, ExtendedLimitInformation, out ExtendedLimit limits, Marshal.SizeOf<ExtendedLimit>(), IntPtr.Zero);
      QueryInformationJobObject(this._job, BasicAccountingInformation, out BasicAccounting accounting, Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero);
      return new((long)limits.PeakJobMemoryUsed, TimeSpan.FromTicks(accounting.TotalUserTime + accounting.TotalKernelTime));
    }

    public void KillAll() => TerminateJobObject(this._job, unchecked((uint)-1));

    public void Dispose() => CloseHandle(this._job);

  }

  [StructLayout(LayoutKind.Sequential)]
  private struct BasicLimit {
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct IoCounters {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ExtendedLimit {
    public BasicLimit Basic;
    public IoCounters IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct BasicAccounting {
    public long TotalUserTime;
    public long TotalKernelTime;
    public long ThisPeriodTotalUserTime;
    public long ThisPeriodTotalKernelTime;
    public uint TotalPageFaultCount;
    public uint TotalProcesses;
    public uint ActiveProcesses;
    public uint TotalTerminatedProcesses;
  }

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimit information, int length);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out ExtendedLimit information, int length, IntPtr returnLength);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out BasicAccounting information, int length, IntPtr returnLength);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseHandle(IntPtr handle);

}
