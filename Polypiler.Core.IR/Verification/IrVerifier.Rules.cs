using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>Checks every function of a module, with the rules of a registry beside the checks of types and declarations.</summary>
  /// <param name="module">The module to check.</param>
  /// <param name="rules">The rules to run on every function.</param>
  /// <returns>One diagnostic per violation, in function order; empty when the module is well-formed.</returns>
  public static IReadOnlyList<Diagnostic> Verify(IrModule module, VerifierRuleRegistry rules) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(rules);
    return Run(module, null, rules);
  }

  /// <summary>
  /// Checks only some functions of a module - those a pass changed - against the rest of it: the
  /// declarations of the module are read but not reported on, and no other function is visited.
  /// </summary>
  /// <param name="module">The module the functions belong to.</param>
  /// <param name="functions">The names of the functions to check.</param>
  /// <param name="rules">The rules to run; <see cref="VerifierRuleRegistry.Standard"/> when <see langword="null"/>.</param>
  /// <returns>The diagnostics of those functions, as <see cref="Verify(IrModule)"/> reports them for the same functions.</returns>
  /// <exception cref="ArgumentException">A name is no function of the module.</exception>
  public static IReadOnlyList<Diagnostic> Verify(IrModule module, IEnumerable<string> functions, VerifierRuleRegistry? rules = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(functions);
    var only = new HashSet<string>(functions, StringComparer.Ordinal);
    foreach (var name in only.Where(name => module.Functions.All(function => function.Name != name)))
      throw new ArgumentException($"'{name}' is no function of the module '{module.Name}'", nameof(functions));

    return Run(module, only, rules ?? VerifierRuleRegistry.Standard);
  }

  /// <summary>A diagnostic of a registered rule: the function, the node and the rule's id.</summary>
  internal static Diagnostic RuleError(string function, string rule, string node, string message)
    => Error(function, $"{node}: {message} [{rule}]");

  /// <summary>How a diagnostic names a node: its kind, and for a node that names something, the name.</summary>
  internal static string Describe(object node) => node switch {
    IrLocalRef reference => $"{nameof(IrLocalRef)} '{reference.Local.Name}'",
    IrDeclare declaration => $"{nameof(IrDeclare)} '{declaration.Local.Name}'",
    IrAssign assignment => $"{nameof(IrAssign)} '{assignment.Local.Name}'",
    IrCall call => $"{nameof(IrCall)} '{call.Function}'",
    _ => node.GetType().Name
  };

  /// <summary>Every statement and expression of a statement, itself first, those in the bodies of lambdas included, in pre-order.</summary>
  internal static IEnumerable<object> AllNodes(IrStmt statement) {
    yield return statement;
    foreach (var expression in IrTree.Expressions(statement))
      foreach (var node in AllNodes(expression))
        yield return node;

    var inner = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement);
    foreach (var nested in inner)
      foreach (var node in AllNodes(nested))
        yield return node;
  }

  /// <summary>Every node of an expression, itself first, the statements of a lambda's body included.</summary>
  internal static IEnumerable<object> AllNodes(IrExpr expression) {
    yield return expression;
    var inner = expression is IrLambda lambda ? AllNodes(lambda.Body) : IrTree.Operands(expression).SelectMany(AllNodes);
    foreach (var node in inner)
      yield return node;
  }

  private static void RunRules(IrModule module, IrFunction function, VerifierRuleRegistry rules, List<Diagnostic> diagnostics) {
    foreach (var rule in rules.Rules)
      rule.Check(new VerifierRuleContext(module, function, rule, diagnostics));
  }

}
