using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Fuzz;

/// <summary>Why the coverage-guided engine did not run.</summary>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Message">The reason.</param>
public sealed record AflProblem(string Code, string Message);

/// <summary>
/// The coverage-guided engine for nightly runs: SharpFuzz instruments a copy of the target's assembly,
/// AFL++ (<c>afl-fuzz</c>, toolchain key <c>aflplusplus</c>) runs it through the
/// <c>fuzz afl-worker</c> verb for a while, and every crash or hang AFL++ keeps is executed again
/// through a <see cref="FuzzEngine"/>, which minimizes and records it as the in-process engine does.
/// </summary>
/// <param name="locator">Finds <c>afl-fuzz</c> and checks its version against the lock.</param>
/// <param name="runner">Runs <c>afl-fuzz</c>.</param>
/// <param name="instrument">Instruments an assembly in place.</param>
public sealed class AflFuzzer(ToolchainLocator locator, IProcessRunner runner, Action<string> instrument) {

  /// <summary>The toolchain key of AFL++ in <c>tooling/toolchains.lock</c>.</summary>
  public const string ToolchainKey = "aflplusplus";

  /// <summary>The name of the engine the records carry.</summary>
  public const string Engine = "afl";

  /// <summary>How long <c>afl-fuzz</c> may run beyond the duration asked for before it is killed.</summary>
  public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

  /// <summary>Instruments an assembly in place with SharpFuzz, every type of it.</summary>
  public static void InstrumentWithSharpFuzz(string assembly) => SharpFuzz.Fuzzer.Instrument(assembly, _ => true, SharpFuzz.Options.Value);

  /// <summary>Checks that AFL++ is installed at the pinned version.</summary>
  /// <returns>The path of <c>afl-fuzz</c>, or the problem: <c>PLY0021</c> when it is missing or not used on this platform, <c>PLY0022</c> at another version.</returns>
  public (string? Path, AflProblem? Problem) Locate() {
    var probe = locator.Probe(ToolchainKey);
    if (probe.Status == ToolchainStatus.Ok)
      return (probe.Path, null);
    if (probe.Problem is { } problem)
      return (null, new(problem.Code, problem.Message));
    return (null, new(ToolchainDiagnostic.NotInstalled, $"toolchain '{ToolchainKey}' is not installed: {probe.Cause}"));
  }

  /// <summary>Runs AFL++ on a target and confirms what it found.</summary>
  /// <param name="aflFuzz">The path of <c>afl-fuzz</c>, as <see cref="Locate"/> found it.</param>
  /// <param name="target">The target.</param>
  /// <param name="corpus">The target's corpus.</param>
  /// <param name="workerCommand">The command line that starts <c>polypiler-tooling</c>, before its verb.</param>
  /// <param name="engine">Executes and records what AFL++ found.</param>
  /// <param name="workDirectory">Where the instrumented assembly, the input and the output of AFL++ go.</param>
  /// <param name="duration">How long AFL++ runs.</param>
  /// <param name="seed">The seed AFL++ is given and the records carry.</param>
  /// <param name="budget">The limits of one input.</param>
  /// <returns>The report of the confirmed findings, or the problem that kept AFL++ from running.</returns>
  public (FuzzReport? Report, AflProblem? Problem) Run(string aflFuzz, IFuzzTarget target, IReadOnlyList<FuzzCorpusEntry> corpus, IReadOnlyList<string> workerCommand,
    FuzzEngine engine, string workDirectory, TimeSpan duration, ulong seed, FuzzBudget budget) {
    var source = target.GetType().Assembly.Location;
    var instrumented = Path.Combine(workDirectory, "instrumented", Path.GetFileName(source));
    var input = Path.Combine(workDirectory, "input");
    var output = Path.Combine(workDirectory, "output");
    if (Directory.Exists(workDirectory))
      Directory.Delete(workDirectory, true);
    Directory.CreateDirectory(Path.GetDirectoryName(instrumented)!);
    Directory.CreateDirectory(input);
    File.Copy(source, instrumented);
    instrument(instrumented);
    for (var index = 0; index < corpus.Count; ++index)
      File.WriteAllBytes(Path.Combine(input, $"{index:D4}.bin"), corpus[index].Content);

    var seconds = Math.Max(1, (long)Math.Ceiling(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    var milliseconds = Math.Max(1, (long)Math.Ceiling(budget.PerInput.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);
    string[] argv = [
      "env", "AFL_SKIP_BIN_CHECK=1", "AFL_NO_UI=1",
      aflFuzz, "-i", input, "-o", output, "-V", seconds, "-t", milliseconds, "-s", seed.ToString(CultureInfo.InvariantCulture), "--",
      .. workerCommand, FuzzCommand.Verb, FuzzCommand.AflWorker, "--target", target.Id, "--assembly", instrumented, "--probe", Path.GetDirectoryName(source)!
    ];
    ProcessResult result;
    try {
      result = runner.Run(argv, duration + Grace);
    } catch (Win32Exception exception) {
      return (null, new(ToolchainDiagnostic.NotInstalled, $"toolchain '{ToolchainKey}' cannot be started: {exception.Message}"));
    }

    // A run that outlived its time was killed; what it had found by then is still worth confirming.
    if (!result.TimedOut && result.ExitCode != 0)
      return (null, new(FuzzCommand.EngineFailedCode, $"fuzz target '{target.Id}': afl-fuzz ended with exit code {result.ExitCode}: {FirstLine(result.StandardError, result.StandardOutput)}"));

    var found = new[] { "crashes", "hangs" }
      .Select(folder => Path.Combine(output, "default", folder))
      .Where(Directory.Exists)
      .SelectMany(folder => Directory.GetFiles(folder, "id*").Order(StringComparer.Ordinal))
      .Select(File.ReadAllBytes)
      .ToList();
    return (engine.Confirm(target, found, seed, budget), null);
  }

  private static string FirstLine(string error, string output)
    => (error + "\n" + output).Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "no output";

}
