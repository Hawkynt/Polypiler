using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The rules the verifier runs on every function, in the order they were registered. The
/// <see cref="Standard"/> registry holds the rules of the IR's own families and is frozen; a pass or a
/// test that needs more rules starts from <see cref="CreateStandard"/> and registers its own.
/// </summary>
public sealed class VerifierRuleRegistry {

  private readonly List<IVerifierRule> _rules = [];

  /// <summary>The rules of the IR's own operation families, frozen; what <see cref="IrVerifier.Verify(IrModule)"/> runs.</summary>
  public static VerifierRuleRegistry Standard { get; } = CreateStandard().Freeze();

  /// <summary>A new registry that holds the rules of the IR's own operation families and takes more.</summary>
  /// <returns>The registry, not frozen.</returns>
  public static VerifierRuleRegistry CreateStandard()
    => new VerifierRuleRegistry().Register(new DefiniteAssignmentRule())
      .Register(new RecordLayoutRule())
      .Register(new ByteOrderedFieldRule());

  /// <summary>Whether the registry takes no more rules.</summary>
  public bool IsFrozen { get; private set; }

  /// <summary>The rules, in the order they run.</summary>
  public IReadOnlyList<IVerifierRule> Rules => this._rules;

  /// <summary>Adds a rule, which runs after those registered before it.</summary>
  /// <param name="rule">The rule; its id must be new to the registry.</param>
  /// <returns>The registry, for chained registrations.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="rule"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">The rule has no id, or a rule with its id is registered.</exception>
  /// <exception cref="InvalidOperationException">The registry is frozen.</exception>
  public VerifierRuleRegistry Register(IVerifierRule rule) {
    ArgumentNullException.ThrowIfNull(rule);
    if (this.IsFrozen)
      throw new InvalidOperationException("the registry of verifier rules is frozen; start from VerifierRuleRegistry.CreateStandard()");

    if (string.IsNullOrWhiteSpace(rule.Id))
      throw new ArgumentException("a verifier rule needs an id", nameof(rule));

    if (this.Find(rule.Id) is not null)
      throw new ArgumentException($"the verifier rule '{rule.Id}' is registered already", nameof(rule));

    this._rules.Add(rule);
    return this;
  }

  /// <summary>The rule with an id.</summary>
  /// <param name="id">The id.</param>
  /// <returns>The rule, or <see langword="null"/> when none has the id.</returns>
  public IVerifierRule? Find(string id) => this._rules.FirstOrDefault(rule => rule.Id == id);

  /// <summary>Makes the registry take no more rules.</summary>
  /// <returns>The registry.</returns>
  public VerifierRuleRegistry Freeze() {
    this.IsFrozen = true;
    return this;
  }

}

/// <summary>
/// A rule about one kind of node: <see cref="Check(VerifierRuleContext, TNode)"/> runs once for every
/// node of the type <typeparamref name="TNode"/> in the function, statements and expressions alike, those
/// in the bodies of lambdas included, in pre-order.
/// </summary>
/// <typeparam name="TNode">The statement or expression type the rule is about.</typeparam>
public abstract class IrNodeRule<TNode> : IVerifierRule where TNode : class {

  /// <inheritdoc/>
  public abstract string Id { get; }

  /// <inheritdoc/>
  public abstract string Family { get; }

  /// <inheritdoc/>
  public abstract string Description { get; }

  /// <inheritdoc/>
  public void Check(VerifierRuleContext context) {
    ArgumentNullException.ThrowIfNull(context);
    foreach (var node in IrVerifier.AllNodes(context.Function.Body).OfType<TNode>())
      this.Check(context, node);
  }

  /// <summary>Checks one node.</summary>
  /// <param name="context">The function and the sink of the diagnostics.</param>
  /// <param name="node">The node.</param>
  protected abstract void Check(VerifierRuleContext context, TNode node);

}
