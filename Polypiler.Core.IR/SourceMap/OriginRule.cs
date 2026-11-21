using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The verifier's rule of origins. Every source a module declares and every origin of a node must be
/// well formed (PLY0116) and name a declared source (PLY0117). A module that declares its sources was
/// made by something that tracks them, so there every instruction - every statement that is no block -
/// must have an origin or be marked <see cref="Synthetic"/> (PLY0115). A module that declares none
/// comes from a frontend that records no locations yet, and the last part does not apply to it.
/// </summary>
public static class OriginRule {

  /// <summary>Checks the sources and origins of a module.</summary>
  /// <param name="module">The module.</param>
  /// <returns>One diagnostic per violation, in the order of the sources and of the instructions.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module) {
    var diagnostics = new List<Diagnostic>();
    foreach (var source in module.Sources.Where(source => source.Problem is not null))
      diagnostics.Add(Diagnostic.Create(SourceMapDiagnostics.MalformedLocation, null, source.Reference));

    var checkedExpressions = new HashSet<IrExpr>(ReferenceEqualityComparer.Instance);
    void Origins(IReadOnlyList<Origin> origins) {
      foreach (var origin in origins)
        if (origin.Problem is not null)
          diagnostics.Add(Diagnostic.Create(SourceMapDiagnostics.MalformedLocation, null, origin.Format()));
        else if (origin.SourceIndex >= module.Sources.Count)
          diagnostics.Add(Diagnostic.Create(SourceMapDiagnostics.UndeclaredSource, null, origin.SourceIndex, module.Sources.Count));
    }

    foreach (var function in module.Functions)
      foreach (var (number, statement) in IrOrigins.Instructions(function)) {
        if (module.Sources.Count > 0 && statement.Origins.Count == 0)
          diagnostics.Add(Diagnostic.Create(SourceMapDiagnostics.MissingOrigin, null, Name(number, statement), function.Name));

        Origins(statement.Origins);
        foreach (var expression in IrTree.Expressions(statement).SelectMany(IrTree.Nodes).Where(checkedExpressions.Add))
          Origins(expression.Origins);
      }

    return diagnostics;
  }

  /// <summary>How a diagnostic names an instruction: its number in the function and its kind, as in <c>#3 (Return)</c>.</summary>
  /// <param name="number">The number <see cref="IrOrigins.Instructions"/> gives it.</param>
  /// <param name="statement">The instruction.</param>
  /// <returns>The name.</returns>
  public static string Name(int number, IrStmt statement) => $"#{number} ({Kind(statement)})";

  /// <summary>The kind of a statement: the name of its class without the prefix <c>Ir</c>.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The kind, such as <c>Return</c>.</returns>
  public static string Kind(IrStmt statement) => statement.GetType().Name[2..];

}
