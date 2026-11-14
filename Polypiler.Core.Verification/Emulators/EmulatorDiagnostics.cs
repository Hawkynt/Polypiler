using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>The diagnostics of the Commodore emulator and lister oracles, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class EmulatorDiagnostics {

  /// <summary>
  /// <c>PLY3935</c>: a VICE run did not produce a result an oracle may use - the PRG is malformed, the
  /// emulator did not start or exited with an error, or BASIC was not reached again before the cycle
  /// limit where the return was required. Arguments: the program, the cause.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ViceRunFailed = new("PLY3935", DiagnosticSeverity.Error, ExitCode.OracleFailure, "VICE run of '{0}' failed: {1}", "tokenized-basic.oracle.vice");

  /// <summary>
  /// <c>PLY3936</c>: petcat did not list or tokenize a program - the dialect has no petcat mapping, or
  /// petcat failed. Arguments: the operation (<c>detokenize</c> or <c>tokenize</c>), the dialect, the cause.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor PetcatFailed = new("PLY3936", DiagnosticSeverity.Error, ExitCode.OracleFailure, "petcat {0} failed for dialect '{1}': {2}", "tokenized-basic.oracle.petcat");

  /// <summary>Diagnostics as the cause in another diagnostic: code and message of each, separated by <c>; </c>.</summary>
  /// <param name="diagnostics">The diagnostics.</param>
  public static string Describe(IEnumerable<Diagnostic> diagnostics) => string.Join("; ", diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

}
