using System;
using System.Text;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>The diagnostics of the benchmark harness, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class BenchmarkDiagnostics {

  /// <summary>
  /// <c>PLY0072</c>: two results are not comparable, because their environments differ or one of them
  /// is unstable. Arguments: the ID, the variant, the reason.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotComparable = new("PLY0072", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "benchmark '{0}/{1}': {2}", "bench.comparable");

  /// <summary>
  /// <c>PLY0073</c>: a metric regressed beyond the budget an ADR set. Arguments: the ID, the variant,
  /// the metric, the regression in percent, the baseline, the current value, the budget in percent,
  /// the budget file.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor BudgetExceeded = new("PLY0073", DiagnosticSeverity.Error, ExitCode.EnvironmentError,
    "benchmark '{0}/{1}': {2} regressed by {3} % (baseline {4}, current {5}), budget {6} % from {7}", "bench.budget");

  /// <summary><c>PLY0078</c>: a benchmark definition, result or budget file does not follow its schema. Argument: the reason, naming the field.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor FileInvalid = new("PLY0078", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "bench.schema");

  /// <summary><c>PLY0079</c>: the measured program exited non-zero; no sample is recorded. Arguments: the ID, the variant, the exit code.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ProgramFailed = new("PLY0079", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "benchmark '{0}/{1}': the program exited with {2}; nothing is recorded", "bench.exit");

  /// <summary>
  /// The mismatch of a benchmark's output: a program whose output differs from the definition's
  /// <c>expectStdout</c> is not a measurement. It is the oracle framework's <c>PLY0051</c>
  /// (<see cref="OracleDiagnostics.ExactMismatch"/>, exit code 5) for the fixture <c>&lt;id&gt;/&lt;variant&gt;</c>
  /// and the vector <c>stdout</c>, both outputs as the hexadecimal of their UTF-8 bytes; the cause
  /// carries them as text.
  /// </summary>
  /// <param name="id">The benchmark.</param>
  /// <param name="variant">The variant.</param>
  /// <param name="expected">The expected output.</param>
  /// <param name="actual">The output the run printed.</param>
  public static Diagnostic OutputMismatch(string id, string variant, string expected, string actual) {
    var (reference, printed) = (Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    var offset = reference.AsSpan().CommonPrefixLength(printed);
    return Diagnostic.Create(OracleDiagnostics.ExactMismatch, null, $"{id}/{variant}", "stdout", Hex.Format(reference), Hex.Format(printed), offset)
      with { Cause = $"the program printed '{actual}', expected '{expected}'; nothing is recorded" };
  }

}
