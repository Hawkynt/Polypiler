using Polypiler.Core;

namespace Polypiler.Permutation.Networks;

/// <summary>The diagnostics of candidate networks and their primitives, <c>PLY0810</c>–<c>PLY0812</c> of the permutation block.</summary>
public static class NetworkDiagnostics {

  /// <summary>
  /// <c>PLY0810</c>: an ill-formed network, or a term that breaks the term contract. Arguments: the step
  /// number (0 for the network as a whole, or the term's number in an assembly), the operation, the problem.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor IllFormed = new("PLY0810", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "network step {0} ({1}): {2}", "permutation.network.well-formed");

  /// <summary><c>PLY0811</c>: a primitive the cost table has no entry for. Arguments: the primitive, the cost table.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unavailable = new("PLY0811", DiagnosticSeverity.Error, ExitCode.InvalidSource, "primitive '{0}' is not available in cost table '{1}'", "permutation.network.primitive-available");

  /// <summary><c>PLY0812</c>: a primitive asked for a width it does not support. Arguments: the primitive, the width.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedWidth = new("PLY0812", DiagnosticSeverity.Error, ExitCode.InvalidSource, "primitive '{0}' does not support width {1}", "permutation.network.primitive-width");

}
