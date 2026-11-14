using System.Collections.Generic;
using System.Diagnostics;

namespace Polypiler.Core.Verification.Running;

/// <summary>
/// The sandbox of a host that cannot isolate: macOS, and Linux without a working bubblewrap. The
/// command runs in its own directory with the scrubbed environment, started through util-linux's
/// <c>setsid</c> where there is one, so that it leads a session and a process group of its own; the
/// limits hold by sampling the process table. It is <see cref="IsolationLevel.Limited"/>, so untrusted
/// plans are refused on it.
/// </summary>
/// <param name="table">The process table the monitor samples.</param>
/// <param name="sessionLauncher">The path of <c>setsid</c>, which starts the command as the leader of a new session; <see langword="null"/> to start it as it is.</param>
public sealed class PollingSandbox(IProcessTable table, string? sessionLauncher = null) : ISandbox {

  /// <summary>Where util-linux installs <c>setsid</c>, in the order they are tried.</summary>
  public static IReadOnlyList<string> SessionLaunchers { get; } = ["/usr/bin/setsid", "/bin/setsid"];

  /// <inheritdoc />
  public IsolationLevel Isolation => IsolationLevel.Limited;

  /// <inheritdoc />
  /// <remarks><c>setsid</c> replaces itself with the command instead of forking, because the runner never starts it as a group leader; <c>--wait</c> keeps the exit code should it fork all the same.</remarks>
  public IReadOnlyList<string> CommandLine(SandboxCommand command)
    => sessionLauncher == null ? [command.Executable, .. command.Arguments] : [sessionLauncher, "--wait", command.Executable, .. command.Arguments];

  /// <inheritdoc />
  public IProcessTreeMonitor Watch(Process process, RunLimits limits) => new PollingMonitor(table, process.Id, limits.Processes, null, sessionLauncher != null);

}
