namespace Polypiler.Core.Analysis.Dynamic;

/// <summary>The diagnostics of caller contracts (<c>spec/ir/assumptions.md</c>): <c>PLY0512</c> to <c>PLY0516</c>.</summary>
public static class ContractDiagnostics {

  /// <summary>The rule every diagnostic of a caller contract enforces.</summary>
  public const string Rule = "prd-4.6.assumptions";

  /// <summary><c>PLY0512</c>: an unchecked contract is trusted; once per such contract, never silent.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unchecked = new("PLY0512", DiagnosticSeverity.Warning, null,
    "assumption '{0}' is trusted without a run-time check; results are undefined for calls that violate it", Rule);

  /// <summary><c>PLY0513</c>: a contract contradicts what the module proves; the contract, the fact and where it holds.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Contradiction = new("PLY0513", DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "assumption '{0}' contradicts a proven fact: {1} at {2}", Rule);

  /// <summary><c>PLY0514</c>: a contract that cannot be read or names nothing of the module; the text and the problem.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Invalid = new("PLY0514", DiagnosticSeverity.Error, ExitCode.UsageError, "invalid assumption '{0}': {1}", Rule);

  /// <summary><c>PLY0515</c>: a contract the module already proves at least as precisely.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoEffect = new("PLY0515", DiagnosticSeverity.Warning, null, "assumption '{0}' had no effect", Rule);

  /// <summary><c>PLY0516</c>: a checked contract no test of constant cost can check.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotCheckable = new("PLY0516", DiagnosticSeverity.Error, ExitCode.UsageError,
    "assumption '{0}' cannot be checked at run time in O(1); use --assume-unchecked to accept it explicitly", Rule);

}
