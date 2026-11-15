using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// What the tests of the Rust layer share: the commands of the repository lock, the smoke crates, and
/// one run of a crate through the cross-language runner, logged to the test output as evidence.
/// </summary>
internal static class RustLayerRun {

  private static readonly Lazy<RustCommands> _commands = new(() => RustCommands.FromLock(
    ToolchainLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, ToolchainLock.RepositoryPath)), Environment.GetEnvironmentVariable));

  private static readonly Lazy<ISandbox> _sandbox = new(ChooseSandbox);

  /// <summary>The folder of the smoke crates.</summary>
  public const string Fixtures = "tests/fixtures/infra/ci/rust";

  /// <summary>The commands, at the versions of <c>tooling/toolchains.lock</c>.</summary>
  public static RustCommands Commands => _commands.Value;

  /// <summary>
  /// The sandbox of the runs: bubblewrap where it works and the toolchain's folders stay visible in it;
  /// where rustup keeps them in the caller's home or under <c>/tmp</c>, which bubblewrap hides, the host's
  /// limiting sandbox. The crates are the repository's own, so a trusted plan may run in either.
  /// </summary>
  public static ISandbox Sandbox => _sandbox.Value;

  /// <summary>Every file of a crate, by name relative to it; a build's <c>target</c> folder or lock file left behind by hand is not part of it.</summary>
  /// <param name="crate">The crate's folder, relative to the repository root.</param>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Crate(string crate) {
    var root = Path.Combine(PrototypeFixture.RepositoryRoot, crate);
    return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
      .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
      .Where(name => !name.StartsWith("target/", StringComparison.Ordinal) && name != "Cargo.lock")
      .ToDictionary(name => name, name => (ReadOnlyMemory<byte>)File.ReadAllBytes(Path.Combine(root, name)));
  }

  /// <summary>Ends the test unless the toolchain is at its pin, as <see cref="RequiresToolchainAttribute"/> does before a test.</summary>
  /// <param name="key">The toolchain's key.</param>
  public static void Require(string key) => new RequiresToolchainAttribute(key).Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));

  /// <summary>Runs one command on a fresh copy of a crate, after the build steps, and logs what it did.</summary>
  /// <param name="files">The crate's files.</param>
  /// <param name="execute">The command whose exit code and output the result carries.</param>
  /// <param name="build">Steps that must succeed before it.</param>
  public static async Task<RunResult> RunAsync(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, ProcessSpec execute, params ProcessSpec[] build) {
    var result = await new CrossLanguageRunner(Sandbox, Toolchains).RunAsync(new RunPlan(files, build, execute, RunLimits.Default, TrustLevel.Trusted));
    var log = TestContext.Out;
    log.WriteLine($"$ {string.Join(" && ", build.Append(execute))}");
    log.WriteLine($"outcome {result.Outcome}, exit code {result.ExitCode}, isolation {result.Isolation}, {result.BuildTime.TotalSeconds + result.WallTime.TotalSeconds:F1} s");
    log.WriteLine($"stdout:\n{Stdout(result)}");
    log.WriteLine($"stderr:\n{Stderr(result)}");
    foreach (var diagnostic in result.Diagnostics)
      log.WriteLine($"{diagnostic}: {diagnostic.Cause}");
    return result;
  }

  /// <summary>The run's standard output as text.</summary>
  public static string Stdout(RunResult result) => Encoding.UTF8.GetString(result.Stdout.Span);

  /// <summary>The run's standard error as text.</summary>
  public static string Stderr(RunResult result) => Encoding.UTF8.GetString(result.Stderr.Span);

  private static ISandbox ChooseSandbox() {
    if (Bubblewrap is { } bubblewrap && !Commands.HostFolders.Any(HiddenByBubblewrap))
      return bubblewrap;

    return OperatingSystem.IsLinux() ? Polling(new ProcFileSystemTable()) : Host;
  }

  private static bool HiddenByBubblewrap(string folder) {
    var home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    return new[] { "/tmp", home }.Any(root => folder == root || folder.StartsWith(root + "/", StringComparison.Ordinal));
  }

}
