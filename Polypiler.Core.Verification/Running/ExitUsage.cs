using System;
using System.Runtime.InteropServices;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// What a finished process used, as the kernel accounted it: the processor time of the process and of
/// every descendant it waited for, and the largest resident set of any of them.
/// </summary>
/// <param name="CpuTime">User and system time of the process and its waited-for descendants.</param>
/// <param name="PeakMemoryBytes">The largest resident set size of the process or one of those descendants.</param>
public readonly record struct ExitUsage(TimeSpan CpuTime, long PeakMemoryBytes) {

  private const int PidType = 1;
  private const int Exited = 4;
  private const int NoWait = 0x01000000;
  private const int SigInfoBytes = 128;
  private const int RusageBytes = 144;

  /// <summary>
  /// Waits for a child of this process to end and reads its accounting without reaping it, by
  /// <c>waitid(P_PID, pid, …, WEXITED | WNOWAIT, rusage)</c> (Linux, waitid(2)): the runtime still
  /// reaps the child as it always does. A short program ends between two samples of the process table;
  /// the kernel's account does not miss it.
  /// </summary>
  /// <param name="pid">The child's process ID.</param>
  /// <returns>The usage; <see langword="null"/> where the call is not available (not Linux on x64 or Arm64) or the runtime reaped the child first.</returns>
  public static ExitUsage? Wait(int pid) => Wait(pid, RuntimeInformation.ProcessArchitecture);

  /// <summary>Waits for a child as <see cref="Wait(int)"/> does, with the system call of the given architecture.</summary>
  /// <param name="pid">The child's process ID.</param>
  /// <param name="architecture">The architecture this process runs on.</param>
  /// <returns>The usage; <see langword="null"/> where the call is not available or the runtime reaped the child first.</returns>
  public static ExitUsage? Wait(int pid, Architecture architecture) {
    if (!OperatingSystem.IsLinux() || WaitIdNumber(architecture) is not { } number)
      return null;

    var info = new byte[SigInfoBytes];
    var usage = new byte[RusageBytes];
    return syscall(number, PidType, pid, info, Exited | NoWait, usage) == 0 ? Read(usage) : null;
  }

  /// <summary>The number of the <c>waitid</c> system call of an architecture; <see langword="null"/> for one this class does not know.</summary>
  public static long? WaitIdNumber(Architecture architecture) => architecture switch {
    Architecture.X64 => 247,
    Architecture.Arm64 => 95,
    _ => null
  };

  /// <summary>Reads a 64-bit <c>struct rusage</c>: <c>ru_utime</c> and <c>ru_stime</c> as (seconds, microseconds), then <c>ru_maxrss</c> in KiB.</summary>
  /// <param name="rusage">The structure's bytes, in the byte order of this machine.</param>
  public static ExitUsage Read(ReadOnlySpan<byte> rusage) {
    var fields = MemoryMarshal.Cast<byte, long>(rusage);
    var microseconds = fields[0] * 1_000_000 + fields[1] + fields[2] * 1_000_000 + fields[3];
    return new(TimeSpan.FromTicks(microseconds * TimeSpan.TicksPerMicrosecond), fields[4] * 1024);
  }

  [DllImport("libc", SetLastError = true)]
  private static extern long syscall(long number, int idType, int id, byte[] info, int options, byte[] usage);

}
