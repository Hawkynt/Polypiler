using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Polypiler.Core.Verification.Running;

/// <summary>Chooses the sandbox of the host the runner is on.</summary>
public static class SandboxFactory {

  /// <summary>The toolchain key of bubblewrap in <c>tooling/toolchains.lock</c>.</summary>
  public const string BubblewrapToolchain = "bubblewrap";

  private static readonly TimeSpan _checkTimeout = TimeSpan.FromSeconds(10);

  /// <summary>
  /// The sandbox of this host: a Job Object on Windows; bubblewrap on Linux where it is installed
  /// and the kernel lets it unshare its namespaces, otherwise the polling sandbox; the polling
  /// sandbox on macOS. Only bubblewrap is <see cref="IsolationLevel.Isolated"/>.
  /// </summary>
  /// <param name="toolchains">Finds bubblewrap.</param>
  public static ISandbox ForHost(IToolchainResolver toolchains) {
    if (OperatingSystem.IsWindows())
      return new JobObjectSandbox();
    if (OperatingSystem.IsLinux())
      return (ISandbox?)Bubblewrap(toolchains) ?? new PollingSandbox(new ProcFileSystemTable(), PollingSandbox.SessionLaunchers.FirstOrDefault(File.Exists));

    return new PollingSandbox(new PsCommandTable());
  }

  /// <summary>The bubblewrap sandbox, when bubblewrap is installed and works on this host.</summary>
  /// <param name="toolchains">Finds bubblewrap.</param>
  /// <returns>The sandbox; <see langword="null"/> when bubblewrap is missing or cannot isolate here.</returns>
  public static BubblewrapSandbox? Bubblewrap(IToolchainResolver toolchains)
    => toolchains.Resolve(BubblewrapToolchain).Executable is { } bwrap && CanIsolate(bwrap) ? new BubblewrapSandbox(bwrap, new ProcFileSystemTable()) : null;

  /// <summary>
  /// Whether bubblewrap can unshare every namespace here: a kernel or a security module that forbids
  /// unprivileged user namespaces makes it fail, and then the host is not isolated.
  /// </summary>
  /// <param name="bwrap">The path of <c>bwrap</c>.</param>
  public static bool CanIsolate(string bwrap) {
    var info = new ProcessStartInfo(bwrap) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in new[] { "--unshare-all", "--die-with-parent", "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--", bwrap, "--version" })
      info.ArgumentList.Add(argument);

    try {
      using var process = Process.Start(info)!;
      _ = process.StandardOutput.ReadToEndAsync();
      _ = process.StandardError.ReadToEndAsync();
      if (!process.WaitForExit(_checkTimeout)) {
        process.Kill(true);
        return false;
      }

      return process.ExitCode == 0;
    } catch (Win32Exception) {
      return false;
    }
  }

}
