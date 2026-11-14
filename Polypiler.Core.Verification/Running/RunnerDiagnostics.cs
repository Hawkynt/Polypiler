namespace Polypiler.Core.Verification.Running;

/// <summary>The diagnostics of the cross-language runner, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class RunnerDiagnostics {

  /// <summary><c>PLY0030</c>: a build step exited non-zero; the step's standard error is the cause. Arguments: the step, its exit code.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor BuildFailed = new("PLY0030", DiagnosticSeverity.Error, ExitCode.TargetBuildFailure, "target build failed: '{0}' exited with {1}", "runner.build");

  /// <summary><c>PLY0031</c>: a step ran longer than its wall-time limit. Argument: the limit in milliseconds.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TimeLimitExceeded = new("PLY0031", DiagnosticSeverity.Error, ExitCode.BudgetExceeded, "time limit of {0} ms exceeded", "runner.time");

  /// <summary><c>PLY0032</c>: a step's process tree held more memory than its limit. Argument: the limit in bytes.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MemoryLimitExceeded = new("PLY0032", DiagnosticSeverity.Error, ExitCode.BudgetExceeded, "memory limit of {0} bytes exceeded", "runner.memory");

  /// <summary><c>PLY0033</c>: a step wrote more than its output limit. Argument: the limit in bytes.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OutputLimitExceeded = new("PLY0033", DiagnosticSeverity.Error, ExitCode.BudgetExceeded, "output limit of {0} bytes exceeded", "runner.output");

  /// <summary><c>PLY0034</c>: an untrusted plan was refused because the host cannot isolate it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor IsolationUnavailable = new("PLY0034", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "isolation is unavailable on this host; untrusted input was not executed", "runner.isolation");

}
