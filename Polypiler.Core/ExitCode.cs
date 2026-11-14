namespace Polypiler.Core;

/// <summary>
/// The process exit codes of every Polypiler command (PRD §10.1, ADR 0003). A command's exit code is
/// the one <see cref="DiagnosticBag.ExitCode"/> derives from what it reported; the numbers are
/// stable and never reused.
/// </summary>
public enum ExitCode {

  /// <summary>The command did what it was asked; no error was reported.</summary>
  Success = 0,

  /// <summary>Something outside the input failed: a file that cannot be read or written, a missing toolchain, an invalid repository file.</summary>
  EnvironmentError = 1,

  /// <summary>The command line could not be understood.</summary>
  UsageError = 2,

  /// <summary>The input uses a construct outside the supported subset, or one the chosen target cannot express.</summary>
  UnsupportedConstruct = 3,

  /// <summary>The input is not valid in its own language.</summary>
  InvalidSource = 4,

  /// <summary>The translation computes something other than the source.</summary>
  SemanticMismatch = 5,

  /// <summary>The generated code did not build with the target's toolchain.</summary>
  TargetBuildFailure = 6,

  /// <summary>The reference oracle failed or is missing.</summary>
  OracleFailure = 7,

  /// <summary>A licence forbids the use or cannot be determined.</summary>
  LicenseBlocker = 8,

  /// <summary>A native or model dependency is missing.</summary>
  MissingDependency = 9,

  /// <summary>A time, memory, output or proof budget was exceeded.</summary>
  BudgetExceeded = 10,

  /// <summary>Polypiler itself failed (BSD <c>sysexits.h</c> <c>EX_SOFTWARE</c>).</summary>
  InternalError = 70

}
