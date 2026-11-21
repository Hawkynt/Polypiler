using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis;

/// <summary>The diagnostics of the fact facade (#88), as <c>spec/diagnostics.yaml</c> records them.</summary>
public static class AnalysisDiagnostics {

  /// <summary>PLY0300: a query required a domain no analysis provides, a defect of the caller.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MissingDomain = new("PLY0300", DiagnosticSeverity.Error, ExitCode.InternalError, "no analysis provides facts of domain '{0}'", "analysis.facade.require");

  /// <summary>PLY0301: an analysis stopped at its budget; the facts it would have answered are Unknown.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Budget = new("PLY0301", DiagnosticSeverity.Info, null, "analysis '{0}' stopped at its budget in '{1}'; affected facts are Unknown", "analysis.facade.budget");

  /// <summary>PLY0302: a fact a run refutes, or two analyses that contradict each other.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsound = new("PLY0302", DiagnosticSeverity.Error, ExitCode.InternalError, "unsound fact: {0}", "analysis.facade.soundness");

  /// <summary>A value in words: variables by name, constants by value and the operators between them.</summary>
  /// <param name="value">The value.</param>
  /// <returns>The text, such as <c>a + 1</c>.</returns>
  public static string Describe(IrExpr value) => value switch {
    IrLocalRef reference => reference.Local.Name,
    IrConst constant => constant.Value.ToString(CultureInfo.InvariantCulture),
    IrUnary unary => $"{unary.Op}({Describe(unary.Operand)})",
    IrBinary binary => $"{binary.Op}({Describe(binary.Left)}, {Describe(binary.Right)})",
    _ => $"{value.GetType().Name}({string.Join(", ", IrTree.Operands(value).Select(Describe))})"
  };

}
