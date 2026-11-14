using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Toolchains;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// Runs a command of <see cref="PythonCommands"/> or <see cref="PerlCommands"/> on the smoke fixtures of
/// the Python/Perl layer, through the cross-language runner and the sandbox of this host. Every run gets
/// a fresh copy of the fixtures, so nothing an interpreter writes reaches the fixture tree.
/// </summary>
public static class PyPerlSandbox {

  /// <summary>The fixtures, <c>tests/fixtures/infra/ci/pyperl</c>.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "ci", "pyperl");

  /// <summary>The scripts, modules and tests of the folder itself, by file name; the probe locks below it are not copied.</summary>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files { get; } = Directory.EnumerateFiles(FixtureFolder)
    .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path), StringComparer.Ordinal);

  /// <summary>
  /// Ends the test unless the command's interpreter is at its pinned version, as
  /// <see cref="RequiresToolchainAttribute"/> does: a failure in CI, inconclusive elsewhere.
  /// </summary>
  /// <param name="command">The command.</param>
  public static void Require(ProcessSpec command)
    => new RequiresToolchainAttribute(command.Toolchain).Check(Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable));

  /// <summary>Runs a command on a copy of the fixtures, after <see cref="Require"/>, and fails the test unless the run completed.</summary>
  /// <param name="command">The command.</param>
  /// <param name="collect">Names relative to the working directory to bring back in <see cref="RunResult.Collected"/>.</param>
  /// <returns>What the run produced.</returns>
  public static async Task<RunResult> RunAsync(ProcessSpec command, params IReadOnlyList<string> collect) {
    Require(command);
    var plan = new RunPlan(Files, [], command, RunLimits.Default, TrustLevel.Trusted) { Collect = collect };
    var result = await new CrossLanguageRunner(Host, Toolchains).RunAsync(plan);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), $"{command}: {string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}"))}");
    TestContext.Out.WriteLine($"$ {command}\nexit code {result.ExitCode}\n--- stdout\n{Text(result.Stdout)}--- stderr\n{Text(result.Stderr)}");
    return result;
  }

  /// <summary>Runs a command as <see cref="RunAsync"/> does and gives back exit code and both outputs as text.</summary>
  /// <param name="command">The command.</param>
  public static async Task<CommandResult> RunCommandAsync(ProcessSpec command) {
    var result = await RunAsync(command);
    return new(result.ExitCode, Text(result.Stdout), Text(result.Stderr));
  }

  /// <summary>
  /// The command of a line as a feature writes it: <c>python &lt;arguments&gt;</c> and
  /// <c>perl &lt;arguments&gt;</c> start the interpreter with the arguments as they are,
  /// <c>prove &lt;arguments&gt;</c> is <see cref="PerlCommands.Prove"/>.
  /// </summary>
  /// <param name="line">The command line, its words separated by spaces.</param>
  /// <exception cref="ArgumentException">The line starts with another word.</exception>
  public static ProcessSpec Parse(string line) {
    var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return words switch {
      ["python", .. var rest] => new(PythonCommands.Toolchain, rest),
      ["perl", .. var rest] => new(PerlCommands.Toolchain, rest),
      ["prove", .. var rest] => PerlCommands.Prove(rest),
      _ => throw new ArgumentException($"'{line}' starts neither with python, perl nor prove", nameof(line))
    };
  }

  /// <summary>Output bytes as text, line breaks as <c>\n</c>.</summary>
  public static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span).Replace("\r\n", "\n", StringComparison.Ordinal);

}
