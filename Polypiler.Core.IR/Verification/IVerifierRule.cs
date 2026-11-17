using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// One rule of the verifier: an invariant of the tree an operation family owns, checked function by
/// function. A rule registers in a <see cref="VerifierRuleRegistry"/>, which <see cref="IrVerifier"/>
/// runs on every function it verifies, after the checks of types and declarations.
/// </summary>
public interface IVerifierRule {

  /// <summary>The stable id of the rule, <c>ir.verifier.&lt;family&gt;.&lt;name&gt;</c>; every diagnostic of the rule names it.</summary>
  string Id { get; }

  /// <summary>The operation family that owns the rule, such as <c>locals</c>.</summary>
  string Family { get; }

  /// <summary>What the rule requires, in one sentence.</summary>
  string Description { get; }

  /// <summary>Checks one function and reports every violation through <paramref name="context"/>; never throws on bad IR.</summary>
  /// <param name="context">The function, its module, and the sink of the diagnostics.</param>
  void Check(VerifierRuleContext context);

}

/// <summary>What a rule checks - one function of a module - and where it reports what it finds.</summary>
public sealed class VerifierRuleContext {

  private readonly IVerifierRule _rule;
  private readonly List<Diagnostic> _diagnostics;

  internal VerifierRuleContext(IrModule module, IrFunction function, IVerifierRule rule, List<Diagnostic> diagnostics) {
    this.Module = module;
    this.Function = function;
    this._rule = rule;
    this._diagnostics = diagnostics;
  }

  /// <summary>The module the function belongs to.</summary>
  public IrModule Module { get; }

  /// <summary>The function checked.</summary>
  public IrFunction Function { get; }

  /// <summary>Reports a violation at a node of the function, as a <see cref="IrVerifier.Code"/> diagnostic naming the function, the node and the rule.</summary>
  /// <param name="node">The statement or expression that violates the rule.</param>
  /// <param name="message">What is wrong, without the function, the node or the rule.</param>
  public void Report(object node, string message) {
    ArgumentNullException.ThrowIfNull(node);
    this._diagnostics.Add(IrVerifier.RuleError(this.Function.Name, this._rule.Id, IrVerifier.Describe(node), message));
  }

  /// <summary>Reports a violation under a diagnostic of its own rather than <see cref="IrVerifier.Code"/>, with the message as it stands.</summary>
  /// <param name="descriptor">The registered diagnostic, whose message format takes the message as its one argument.</param>
  /// <param name="message">The whole message.</param>
  public void Report(DiagnosticDescriptor descriptor, string message) {
    ArgumentNullException.ThrowIfNull(descriptor);
    ArgumentNullException.ThrowIfNull(message);
    this._diagnostics.Add(descriptor.At(message));
  }

}
