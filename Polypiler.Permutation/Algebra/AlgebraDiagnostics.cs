using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Algebra;

/// <summary>
/// The diagnostics of the mapping algebra, as <c>spec/diagnostics.yaml</c> registers them inside the
/// model and hierarchy range <c>PLY0800</c>–<c>PLY0809</c> of the permutation block.
/// </summary>
public static class AlgebraDiagnostics {

  /// <summary><c>PLY0803</c>: an inverse was requested for a mapping that is no bijection. Argument: the reason of rule H1.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotInvertible = new("PLY0803", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "mapping is not a bijection ({0}); it has no inverse", "permutation.algebra.inverse");

  /// <summary><c>PLY0804</c>: two mappings whose widths do not meet were composed. Arguments: the inner output width, the outer input width.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor WidthMismatch = new("PLY0804", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "cannot compose: the inner mapping has {0} output bits, the outer mapping reads {1} input bits", "permutation.algebra.compose");

  /// <summary>Throws <c>PLY0804</c> unless the inner output width is the outer input width.</summary>
  internal static void RequireComposable(int innerOutputWidth, int outerInputWidth) {
    if (innerOutputWidth != outerInputWidth)
      throw new PermutationModelException(Diagnostic.Create(WidthMismatch, null, innerOutputWidth, outerInputWidth));
  }

}
