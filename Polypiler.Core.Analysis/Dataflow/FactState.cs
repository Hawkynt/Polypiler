using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Dataflow;

/// <summary>
/// What an analysis knows of the variables of a function at one reachable point: a fact per variable and
/// the statements each fact rests on. A variable it holds nothing for is Unknown; so is a variable that
/// escapes - captured by a lambda or with its address taken - whatever was assigned to it.
/// </summary>
/// <typeparam name="TFact">The fact type.</typeparam>
public sealed class FactState<TFact> where TFact : IFact<TFact> {

  private readonly ImmutableDictionary<IrLocal, (TFact Fact, ImmutableList<IrStmt> Because)> _facts;
  private readonly IReadOnlySet<IrLocal> _escaping;

  internal FactState(IReadOnlySet<IrLocal> escaping) : this(ImmutableDictionary<IrLocal, (TFact, ImmutableList<IrStmt>)>.Empty, escaping) { }

  private FactState(ImmutableDictionary<IrLocal, (TFact, ImmutableList<IrStmt>)> facts, IReadOnlySet<IrLocal> escaping) {
    this._facts = facts;
    this._escaping = escaping;
  }

  private static IFactDomain<TFact> Domain => TFact.Domain;

  /// <summary>The fact of a variable.</summary>
  /// <param name="local">The variable.</param>
  /// <returns>Its fact; the domain's Unknown when nothing is known.</returns>
  public TFact Fact(IrLocal local) => this._facts.TryGetValue(local, out var entry) ? entry.Fact : Domain.Unknown;

  /// <summary>The statements the fact of a variable rests on.</summary>
  /// <param name="local">The variable.</param>
  /// <returns>The statements, empty for an Unknown variable.</returns>
  public IReadOnlyList<IrStmt> Because(IrLocal local) => this._facts.TryGetValue(local, out var entry) ? entry.Because : [];

  /// <summary>The state with a new fact for a variable; an escaping variable stays Unknown.</summary>
  /// <param name="local">The variable.</param>
  /// <param name="fact">Its new fact.</param>
  /// <param name="because">What the fact rests on.</param>
  /// <returns>The new state.</returns>
  public FactState<TFact> With(IrLocal local, TFact fact, IEnumerable<IrStmt> because) {
    if (this._escaping.Contains(local) || EqualityComparer<TFact>.Default.Equals(fact, Domain.Unknown))
      return this.Without(local);

    return new(this._facts.SetItem(local, (fact, Distinct(because))), this._escaping);
  }

  /// <summary>The state with nothing known of a variable.</summary>
  /// <param name="local">The variable.</param>
  /// <returns>The new state.</returns>
  public FactState<TFact> Without(IrLocal local) => this._facts.ContainsKey(local) ? new(this._facts.Remove(local), this._escaping) : this;

  /// <summary>The state with nothing known of several variables.</summary>
  internal FactState<TFact> Without(IEnumerable<IrLocal> locals) => locals.Aggregate(this, (state, local) => state.Without(local));

  /// <summary>Where control comes from either state: per variable the join, Unknown where only one knows it.</summary>
  internal static FactState<TFact>? Join(FactState<TFact>? left, FactState<TFact>? right)
    => Combine(left, right, Domain.Join);

  /// <summary>The widening of the previous state of a loop head by the next.</summary>
  internal static FactState<TFact>? Widen(FactState<TFact>? previous, FactState<TFact>? next)
    => Combine(previous, next, Domain.Widen);

  /// <summary>Whether two states know the same facts for the same reasons; two unreachable ones are the same.</summary>
  internal static bool Same(FactState<TFact>? left, FactState<TFact>? right) {
    if (left is null || right is null)
      return left is null && right is null;

    return left._facts.Count == right._facts.Count && left._facts.All(pair => right._facts.TryGetValue(pair.Key, out var other)
      && EqualityComparer<TFact>.Default.Equals(pair.Value.Fact, other.Fact) && pair.Value.Because.SequenceEqual(other.Because, ReferenceEqualityComparer.Instance));
  }

  private static FactState<TFact>? Combine(FactState<TFact>? left, FactState<TFact>? right, System.Func<TFact, TFact, TFact> combine) {
    if (left is null)
      return right;

    if (right is null)
      return left;

    var state = new FactState<TFact>(left._escaping);
    foreach (var (local, entry) in left._facts.OrderBy(pair => pair.Key.Name, System.StringComparer.Ordinal))
      if (right._facts.TryGetValue(local, out var other))
        state = state.With(local, combine(entry.Fact, other.Fact), entry.Because.Concat(other.Because));

    return state;
  }

  private static ImmutableList<IrStmt> Distinct(IEnumerable<IrStmt> because) {
    var seen = new HashSet<IrStmt>(ReferenceEqualityComparer.Instance);
    return [.. because.Where(seen.Add)];
  }

}
