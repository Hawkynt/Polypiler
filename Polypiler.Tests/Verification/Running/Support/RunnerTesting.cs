using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Verification.Running.Support;

/// <summary>
/// What the runner tests share: the probe program copied into a plan, the toolchains of the
/// repository lock, the sandboxes this host offers and the run directories left behind.
/// </summary>
internal static class RunnerTesting {

  /// <summary>The toolchain the probe runs on.</summary>
  public const string DotNet = "dotnet-sdk";

  /// <summary>The probe's assembly, as a plan names it.</summary>
  public const string ProbeAssembly = "polypiler-probe.dll";

  private static readonly Lazy<IToolchainResolver> _toolchains = new(() => Resolver(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath)));
  private static readonly Lazy<BubblewrapSandbox?> _bubblewrap = new(() => OperatingSystem.IsLinux() ? SandboxFactory.Bubblewrap(_toolchains.Value) : null);
  private static readonly Lazy<ISandbox> _host = new(() => (ISandbox?)_bubblewrap.Value ?? SandboxFactory.ForHost(_toolchains.Value));

  /// <summary>The fixtures of the runner.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "runner");

  /// <summary>The toolchains of the repository lock, each probed once for the whole test run.</summary>
  public static IToolchainResolver Toolchains => _toolchains.Value;

  /// <summary>The bubblewrap sandbox, when this host has a working bubblewrap.</summary>
  public static BubblewrapSandbox? Bubblewrap => _bubblewrap.Value;

  /// <summary>The sandbox of this host, as the runner chooses it.</summary>
  public static ISandbox Host => _host.Value;

  /// <summary>The polling sandbox of Linux, started through <c>setsid</c> as the factory does.</summary>
  public static PollingSandbox Polling(IProcessTable table) => new(table, Array.Find([.. PollingSandbox.SessionLaunchers], File.Exists));

  /// <summary>The resolver of a lock file.</summary>
  public static IToolchainResolver Resolver(string lockFile) => new LockToolchainResolver(new ToolchainLocator(ToolchainLock.Load(lockFile), new ProcessRunner()));

  /// <summary>The probe's files: its assembly and its runtime configuration, which <c>dotnet</c> needs to start it.</summary>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> ProbeFiles { get; } = new[] { ProbeAssembly, "polypiler-probe.runtimeconfig.json" }
    .ToDictionary(name => name, name => (ReadOnlyMemory<byte>)File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name)));

  /// <summary>The probe started with a verb and its arguments.</summary>
  public static ProcessSpec Probe(params string[] arguments) => new(DotNet, [ProbeAssembly, .. arguments]);

  /// <summary>The probe started with a verb, reading the given bytes on standard input.</summary>
  public static ProcessSpec Probe(ReadOnlyMemory<byte> stdin, params string[] arguments) => new(DotNet, [ProbeAssembly, .. arguments], stdin);

  /// <summary>A plan that runs the probe, with the default limits unless others are given.</summary>
  public static RunPlan ProbePlan(ProcessSpec execute, RunLimits? limits = null, TrustLevel trust = TrustLevel.Trusted, params ProcessSpec[] build)
    => new(ProbeFiles, build, execute, limits ?? RunLimits.Default, trust);

  /// <summary>Limits with one value changed from the defaults.</summary>
  public static RunLimits Limits(TimeSpan? wallTime = null, long? memoryBytes = null, long? outputBytes = null, int? processes = null) {
    var defaults = RunLimits.Default;
    return new(wallTime ?? defaults.WallTime, memoryBytes ?? defaults.MemoryBytes, outputBytes ?? defaults.OutputBytes, processes ?? defaults.Processes);
  }

  /// <summary>Every file of a fixture project, by its name relative to the project.</summary>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> FixtureFiles(string project) {
    var root = Path.Combine(FixtureFolder, project);
    return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
      .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
  }

  /// <summary>
  /// The processes still alive whose working directory is the run's directory. Linux shows a
  /// process's working directory in <c>/proc</c>; elsewhere the directory's deletion is the proof,
  /// because no system deletes a directory a live process works in without trace.
  /// </summary>
  public static IReadOnlyList<int> Survivors(string directory) {
    if (!OperatingSystem.IsLinux())
      return Directory.Exists(directory) ? [-1] : [];

    var found = new List<int>();
    foreach (var process in Directory.EnumerateDirectories("/proc")) {
      if (!int.TryParse(Path.GetFileName(process), out var pid))
        continue;
      try {
        if (new DirectoryInfo(Path.Combine(process, "cwd")).LinkTarget is { } cwd && cwd.StartsWith(directory, StringComparison.Ordinal))
          found.Add(pid);
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
        // The process ended, or it is not ours.
      }
    }

    return found;
  }

}
