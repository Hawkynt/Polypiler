using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The labels of the traps of a function (#115): every label an assertion, a trap attribute or a
/// handler names is built in or declared in the module's trap table, and a trap attribute stands only
/// on an operation that can fail with its label's kind (<see cref="TrapKinds.Possible"/>) - both
/// <see cref="Code"/>. The test of a handler's labels (<see cref="IrTrapMatches"/>) stands only in the
/// filter of a catch clause, outside the lambdas there.
/// </summary>
internal sealed class TrapLabelRule : IVerifierRule {

  /// <summary>The code of a trap label that is not declared or stands where it cannot.</summary>
  public const string Code = "PLY0178";

  /// <summary>The registered diagnostic of <see cref="Code"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Descriptor = new(Code, DiagnosticSeverity.Error, ExitCode.InternalError, "{0}", "ir.verifier.traps.label-declared");

  /// <inheritdoc/>
  public string Id => "ir.verifier.traps.label-declared";

  /// <inheritdoc/>
  public string Family => "traps";

  /// <inheritdoc/>
  public string Description => "every trap label is built in or declared in the module's trap table, and a trap attribute stands on an operation that can fail with its kind";

  /// <inheritdoc/>
  public void Check(VerifierRuleContext context) {
    var filtered = new HashSet<IrTrapMatches>(ReferenceEqualityComparer.Instance);
    foreach (var node in IrVerifier.AllNodes(context.Function.Body)) {
      if (node is IrTry attempt)
        foreach (var filter in attempt.Catches.Select(clause => clause.Filter).OfType<IrExpr>())
          filtered.UnionWith(OutsideLambdas(filter).OfType<IrTrapMatches>());

      switch (node) {
        case IrAssert assertion:
          Declared(context, assertion.Label);
          break;
        case IrTrapAttribute attribute:
          Declared(context, attribute.Label);
          if (!TrapKinds.Possible(attribute.Operation).Contains(attribute.Label.Kind))
            context.Report(Descriptor, $"'{IrVerifier.Describe(attribute.Operation)}' cannot carry a trap attribute [trap {attribute.Label.Name}] in '{context.Function.Name}': it cannot fail with {attribute.Label.Kind}");
          break;
        case IrTrapMatches matches:
          foreach (var label in matches.Labels)
            Declared(context, label);
          break;
      }
    }

    foreach (var stray in IrVerifier.AllNodes(context.Function.Body).OfType<IrTrapMatches>().Where(matches => !filtered.Contains(matches)))
      context.Report(stray, "the labels of a handler are tested outside the filter of a catch clause");
  }

  /// <summary>A label is built in, or the very label the module's trap table declares under its name.</summary>
  private static void Declared(VerifierRuleContext context, TrapLabel label) {
    if (!label.IsDeclaredIn(context.Module))
      context.Report(Descriptor, $"trap label '{label.Name}' is not declared in module '{context.Module.Name}', in '{context.Function.Name}'");
  }

  /// <summary>The nodes of an expression, itself first, without the bodies of its lambdas, which run elsewhere.</summary>
  private static IEnumerable<IrExpr> OutsideLambdas(IrExpr expression) {
    yield return expression;
    if (expression is IrLambda)
      yield break;

    foreach (var operand in IrTree.Operands(expression))
      foreach (var node in OutsideLambdas(operand))
        yield return node;
  }

}
