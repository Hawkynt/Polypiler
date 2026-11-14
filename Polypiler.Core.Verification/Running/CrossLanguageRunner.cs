using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Polypiler.Core.Verification.Running;

/// <summary>Builds and executes a plan: the one way generated or foreign code is compiled and run (PRD §7.2).</summary>
public interface ICrossLanguageRunner {

  /// <summary>Runs a plan to its end.</summary>
  /// <param name="plan">The plan.</param>
  /// <param name="cancellationToken">Ends the run; its processes are killed and its directory removed before the exception is thrown.</param>
  /// <returns>How the run ended and what it produced.</returns>
  /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
  Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default);

}

/// <summary>
/// The runner: refuses an untrusted plan on a host that cannot isolate it, resolves every toolchain
/// before anything starts, writes the files into a fresh directory, runs the build steps in order and
/// the execute step after them, each in the sandbox under the plan's limits, hands back the files the
/// plan collects, and removes the directory again however the run ends.
/// </summary>
/// <param name="sandbox">The sandbox every step runs in.</param>
/// <param name="toolchains">Finds the executables of the toolchains the steps name.</param>
public sealed class CrossLanguageRunner(ISandbox sandbox, IToolchainResolver toolchains) : ICrossLanguageRunner {

  /// <inheritdoc />
  public async Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(plan);
    cancellationToken.ThrowIfCancellationRequested();
    if (plan.Trust == TrustLevel.Untrusted && sandbox.Isolation == IsolationLevel.Limited)
      return this.Refused([Diagnostic.Create(RunnerDiagnostics.IsolationUnavailable, null)]);

    var executables = new Dictionary<string, string>(StringComparer.Ordinal);
    var problems = new List<Diagnostic>();
    foreach (var key in plan.Steps.Select(step => step.Toolchain).Distinct(StringComparer.Ordinal)) {
      var resolution = toolchains.Resolve(key);
      if (resolution.Executable is { } executable)
        executables[key] = executable;
      else
        problems.Add(resolution.Problem!);
    }

    if (problems.Count > 0)
      return this.Refused(problems);

    var directory = RunDirectory.Create(plan.Files);
    try {
      var buildTime = TimeSpan.Zero;
      foreach (var build in plan.Build) {
        var step = await StepLauncher.RunAsync(sandbox, executables[build.Toolchain], build, directory, plan.Limits, cancellationToken).ConfigureAwait(false);
        buildTime += step.WallTime;
        if (step.End != StepEnd.Exited)
          return this.Violated(step, plan, directory) with { BuildTime = buildTime };

        if (step.ExitCode != 0) {
          var failure = Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, build, step.ExitCode) with { Cause = Cause(step) };
          return this.Result(RunOutcome.BuildFailed, -1, step with { Stdout = [], Stderr = [] }, [failure], plan, directory) with { BuildTime = buildTime };
        }
      }

      var run = await StepLauncher.RunAsync(sandbox, executables[plan.Execute.Toolchain], plan.Execute, directory, plan.Limits, cancellationToken).ConfigureAwait(false);
      var result = run.End == StepEnd.Exited ? this.Result(RunOutcome.Completed, run.ExitCode, run, [], plan, directory) : this.Violated(run, plan, directory);
      return result with { BuildTime = buildTime };
    } finally {
      RunDirectory.Delete(directory);
    }
  }

  /// <summary>The files of <see cref="RunPlan.Collect"/> that exist in the directory, a directory's files included.</summary>
  private static Dictionary<string, ReadOnlyMemory<byte>> Collect(RunPlan plan, string directory) {
    var collected = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    foreach (var name in plan.Collect) {
      var path = Path.Combine(directory, name);
      var files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : File.Exists(path) ? [path] : [];
      foreach (var file in files)
        collected[Path.GetRelativePath(directory, file).Replace('\\', '/')] = File.ReadAllBytes(file);
    }

    return collected;
  }

  /// <summary>The build step's standard error, followed by its standard output: compilers such as the .NET SDK write their errors there.</summary>
  private static string Cause(StepResult step) {
    var text = string.Join('\n', new[] { step.Stderr, step.Stdout }.Select(bytes => Encoding.UTF8.GetString(bytes).Trim()).Where(part => part.Length > 0));
    return text.Length > 0 ? text : "the step printed nothing";
  }

  private RunResult Violated(StepResult step, RunPlan plan, string directory) {
    var limits = plan.Limits;
    var (outcome, diagnostic) = step.End switch {
      StepEnd.TimedOut => (RunOutcome.TimedOut, Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, (long)limits.WallTime.TotalMilliseconds)),
      StepEnd.MemoryExceeded => (RunOutcome.MemoryExceeded, Diagnostic.Create(RunnerDiagnostics.MemoryLimitExceeded, null, limits.MemoryBytes)),
      _ => (RunOutcome.OutputExceeded, Diagnostic.Create(RunnerDiagnostics.OutputLimitExceeded, null, limits.OutputBytes))
    };
    return this.Result(outcome, -1, step, [diagnostic], plan, directory);
  }

  private RunResult Result(RunOutcome outcome, int exitCode, StepResult step, IReadOnlyList<Diagnostic> diagnostics, RunPlan plan, string directory)
    => new(outcome, exitCode, step.Stdout, step.Stderr, step.WallTime, step.CpuTime, step.PeakMemoryBytes, sandbox.Isolation, diagnostics) {
      WorkingDirectory = directory,
      Collected = Collect(plan, directory)
    };

  private RunResult Refused(IReadOnlyList<Diagnostic> diagnostics)
    => new(RunOutcome.Refused, -1, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, sandbox.Isolation, diagnostics);

}
