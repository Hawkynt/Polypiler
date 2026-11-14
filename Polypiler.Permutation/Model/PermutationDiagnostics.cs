using Polypiler.Core;

namespace Polypiler.Permutation.Model;

/// <summary>
/// The diagnostics of the bit-mapping model, as <c>spec/diagnostics.yaml</c> registers them. The
/// permutation block <c>PLY0800</c>–<c>PLY0899</c> is divided there: model and hierarchy
/// <c>0800</c>–<c>0809</c>, networks and primitives <c>0810</c>–<c>0819</c>, search and equivalence
/// <c>0820</c>–<c>0829</c>, recovery <c>0830</c>–<c>0839</c>, target profiles <c>0840</c>–<c>0849</c>,
/// API and command line <c>0850</c>–<c>0859</c>.
/// </summary>
public static class PermutationDiagnostics {

  /// <summary><c>PLY0800</c>: an input or output width outside 1..4096. Argument: the width.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor WidthOutOfRange = new("PLY0800", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "mapping width {0} is outside 1..4096", "permutation.model.width");

  /// <summary><c>PLY0801</c>: an output bit reads an input bit the input does not have. Arguments: the output bit, the input bit, the input width.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SourceOutOfRange = new("PLY0801", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "output bit {0} reads input bit {1}, input width is {2}", "permutation.model.source-index");

  /// <summary>
  /// <c>PLY0802</c>: a mapping text or document that is not a mapping. Arguments: <c>text</c> or
  /// <c>document</c>, the text or the document's name, the reason.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Malformed = new("PLY0802", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "mapping {0} '{1}': {2}", "permutation.model.syntax");

}
