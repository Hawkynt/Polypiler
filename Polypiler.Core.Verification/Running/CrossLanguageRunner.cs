using System;
using System.Collections.Generic;
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
/// the execute step after them, each in the sandbox under the plan's limits, and removes the
/// directory again however the run ends.
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
      foreach (var build in plan.Build) {
        var step = await StepLauncher.RunAsync(sandbox, executables[build.Toolchain], build, directory, plan.Limits, cancellationToken).ConfigureAwait(false);
        if (step.End != StepEnd.Exited)
          return this.Violated(step, plan.Limits, directory);

        if (step.ExitCode != 0) {
          var failure = Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, build, step.ExitCode) with { Cause = Cause(step) };
          return this.Result(RunOutcome.BuildFailed, -1, step with { Stdout = [], Stderr = [] }, [failure], directory);
        }
      }

      var run = await StepLauncher.RunAsync(sandbox, executables[plan.Execute.Toolchain], plan.Execute, directory, plan.Limits, cancellationToken).ConfigureAwait(false);
      return run.End == StepEnd.Exited ? this.Result(RunOutcome.Completed, run.ExitCode, run, [], directory) : this.Violated(run, plan.Limits, directory);
    } finally {
      RunDirectory.Delete(directory);
    }
  }

  /// <summary>The build step's standard error, followed by its standard output: compilers such as the .NET SDK write their errors there.</summary>
  private static string Cause(StepResult step) {
    var text = string.Join('\n', new[] { step.Stderr, step.Stdout }.Select(bytes => Encoding.UTF8.GetString(bytes).Trim()).Where(part => part.Length > 0));
    return text.Length > 0 ? text : "the step printed nothing";
  }

  private RunResult Violated(StepResult step, RunLimits limits, string directory) {
    var (outcome, diagnostic) = step.End switch {
      StepEnd.TimedOut => (RunOutcome.TimedOut, Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, (long)limits.WallTime.TotalMilliseconds)),
      StepEnd.MemoryExceeded => (RunOutcome.MemoryExceeded, Diagnostic.Create(RunnerDiagnostics.MemoryLimitExceeded, null, limits.MemoryBytes)),
      _ => (RunOutcome.OutputExceeded, Diagnostic.Create(RunnerDiagnostics.OutputLimitExceeded, null, limits.OutputBytes))
    };
    return this.Result(outcome, -1, step, [diagnostic], directory);
  }

  private RunResult Result(RunOutcome outcome, int exitCode, StepResult step, IReadOnlyList<Diagnostic> diagnostics, string directory)
    => new(outcome, exitCode, step.Stdout, step.Stderr, step.WallTime, step.CpuTime, step.PeakMemoryBytes, sandbox.Isolation, diagnostics) { WorkingDirectory = directory };

  private RunResult Refused(IReadOnlyList<Diagnostic> diagnostics)
    => new(RunOutcome.Refused, -1, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, sandbox.Isolation, diagnostics);

}
