using Polypiler.Core;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// The diagnostics of the semantic hierarchy, as <c>spec/diagnostics.yaml</c> registers them inside the
/// model and hierarchy range <c>PLY0800</c>–<c>PLY0809</c> of the permutation block.
/// </summary>
public static class BitSemanticsDiagnostics {

  /// <summary>The largest input width whose truth table is enumerated: 2^16 input values.</summary>
  public const int TruthTableLimit = 16;

  /// <summary><c>PLY0805</c>: a value does not hold the class or the rule it claims. Argument: the reason.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ClassViolated = new("PLY0805", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "permutation.hierarchy.class");

  /// <summary><c>PLY0806</c>: an input outside the input domain of a condition. Argument: the input in hexadecimal.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OutsideDomain = new("PLY0806", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "input {0} is outside the declared input domain", "permutation.hierarchy.domain");

  /// <summary><c>PLY0807</c>: a truth table was requested for more than 16 inputs. Arguments: the input width, the limit.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TruthTableTooLarge = new("PLY0807", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "truth table of {0} inputs exceeds the limit of {1}", "permutation.hierarchy.truth-table");

}
