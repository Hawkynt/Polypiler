using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Dynamic;

/// <summary>
/// How sure the compiler is of what a place holds (PRD §4.6), from the weakest to the strongest; the
/// join of two facts takes the weaker certainty. Only <see cref="Proven"/> and checked
/// <see cref="Contextual"/> places are emitted typed without a fallback.
/// </summary>
public enum Certainty {

  /// <summary>Nothing is known: the place stays dynamic.</summary>
  Dynamic,

  /// <summary>A speculation with its guard and a valid fallback (#778).</summary>
  Guarded,

  /// <summary>A stated contract, checked where the value enters.</summary>
  Contextual,

  /// <summary>Holds on every path.</summary>
  Proven

}

/// <summary>The fact of a place: the element of the lattice and how sure it is.</summary>
/// <param name="Type">The element.</param>
/// <param name="Certainty">The certainty.</param>
public readonly record struct DynamicFact(DynamicType Type, Certainty Certainty) {

  /// <summary>A proven fact of an element.</summary>
  /// <param name="type">The element.</param>
  /// <returns>The fact.</returns>
  public static DynamicFact Proven(DynamicType type) => new(type, Certainty.Proven);

  /// <summary>The join of two facts: the join of the elements with the weaker certainty.</summary>
  /// <param name="left">One fact.</param>
  /// <param name="right">The other fact.</param>
  /// <param name="policy">The policy of the language.</param>
  /// <returns>The join.</returns>
  public static DynamicFact Join(DynamicFact left, DynamicFact right, IJoinPolicy policy)
    => new(DynamicType.Join(left.Type, right.Type, policy), (Certainty)Math.Min((int)left.Certainty, (int)right.Certainty));

  /// <summary>Whether a place of the fact is emitted with its type and no fallback: proven, below a variant, and with a type of the IR.</summary>
  public bool IsStatic => this.Certainty == Certainty.Proven && this.Type.Level is > 0 and < 6;

  /// <inheritdoc/>
  public override string ToString() => $"{this.Certainty.ToString().ToLowerInvariant()} {this.Type}";

}

/// <summary>
/// The abstract domain of <see cref="DynamicFact"/> under the join policy of one language: Unreachable
/// is the proven <see cref="DynamicType.Nothing"/>, Unknown the dynamic <see cref="DynamicType.Variant"/>.
/// Joins are those of <see cref="DynamicType.Join"/>, which are bounded except for shapes nested in
/// shapes; the widening turns a fact that grew past <see cref="DynamicType.MostDepth"/> levels of shapes
/// into a variant, so every chain of widenings ends.
/// </summary>
public sealed class DynamicFacts : IFactDomain<DynamicFact> {

  /// <summary>The domain of a language.</summary>
  /// <param name="policy">The join policy of the language.</param>
  public DynamicFacts(IJoinPolicy policy) {
    ArgumentNullException.ThrowIfNull(policy);
    this.Policy = policy;
  }

  /// <summary>The join policy of the language.</summary>
  public IJoinPolicy Policy { get; }

  /// <inheritdoc/>
  public string Name => "dynamic";

  /// <inheritdoc/>
  public DynamicFact Unknown => new(DynamicType.Variant, Certainty.Dynamic);

  /// <inheritdoc/>
  public DynamicFact Unreachable => DynamicFact.Proven(DynamicType.Nothing);

  /// <inheritdoc/>
  public DynamicFact Join(DynamicFact left, DynamicFact right) => DynamicFact.Join(left, right, this.Policy);

  /// <inheritdoc/>
  public DynamicFact Meet(DynamicFact left, DynamicFact right)
    => new(DynamicType.Meet(left.Type, right.Type, this.Policy), (Certainty)Math.Max((int)left.Certainty, (int)right.Certainty));

  /// <inheritdoc/>
  public DynamicFact Widen(DynamicFact previous, DynamicFact next) {
    var joined = this.Join(previous, next);
    return joined.Type.Depth > DynamicType.MostDepth ? joined with { Type = DynamicType.Variant } : joined;
  }

  /// <inheritdoc/>
  public bool Implies(DynamicFact stronger, DynamicFact weaker) => stronger.Type.LessOrEqual(weaker.Type, this.Policy) && stronger.Certainty >= weaker.Certainty;

  /// <summary>
  /// Whether a value a run observed is one the fact allows: an <see cref="IrVariant"/> by its tag, an
  /// <see cref="IrConst"/> by its type; any other value is not refuted.
  /// </summary>
  /// <param name="fact">The fact.</param>
  /// <param name="value">The observed value.</param>
  /// <returns><see langword="false"/> when the observation refutes the fact.</returns>
  public bool Contains(DynamicFact fact, object value) => value switch {
    IrVariant variant => Admits(fact.Type, variant.Tag),
    IrConst constant => Admits(fact.Type, constant.Type),
    _ => fact.Type is not DynamicNothing
  };

  /// <inheritdoc/>
  public string Describe(DynamicFact fact) => fact.ToString();

  /// <summary>Whether an element allows a value of a variant tag.</summary>
  private static bool Admits(DynamicType type, IrVariantTag tag) => type switch {
    DynamicVariant => true,
    DynamicNothing => false,
    DynamicOptional optional => tag switch {
      IrVariantTag.Null => (optional.Missing & DynamicMissing.Null) != 0,
      IrVariantTag.Empty => (optional.Missing & DynamicMissing.Empty) != 0,
      _ => Admits(optional.Value, tag)
    },
    DynamicUnion union => union.Members.Any(member => Admits(member, tag)),
    DynamicExact exact => exact.Tag is { } own ? own == tag : IrVariants.TagOf(exact.Type) == tag,
    _ => true
  };

  /// <summary>Whether an element allows a constant of a type.</summary>
  private static bool Admits(DynamicType type, IrType constant) => type switch {
    DynamicVariant => true,
    DynamicOptional optional => Admits(optional.Value, constant),
    DynamicUnion union => union.Members.Any(member => Admits(member, constant)),
    DynamicExact exact => exact.Type == constant,
    _ => false
  };

}

/// <summary>
/// The facts of the places of a function at one program point: an immutable map from a place to its
/// fact, where a place without an entry holds <see cref="Unbound"/>. States join place by place, and
/// widen place by place at the head of a loop.
/// </summary>
/// <typeparam name="TPlace">The type that names a place, such as <see cref="IrLocal"/>.</typeparam>
public sealed class DynamicState<TPlace> where TPlace : notnull {

  private readonly Dictionary<TPlace, DynamicFact> _facts;

  private DynamicState(Dictionary<TPlace, DynamicFact> facts) => this._facts = facts;

  /// <summary>A state with the facts given.</summary>
  /// <param name="facts">The facts by place.</param>
  /// <param name="unbound">What a place without a fact holds.</param>
  public DynamicState(IEnumerable<KeyValuePair<TPlace, DynamicFact>> facts, DynamicFact unbound) : this(new Dictionary<TPlace, DynamicFact>(facts)) => this.Unbound = unbound;

  /// <summary>What a place without an entry holds.</summary>
  public DynamicFact Unbound { get; private init; }

  /// <summary>The places that have an entry.</summary>
  public IEnumerable<TPlace> Places => this._facts.Keys;

  /// <summary>The fact of a place.</summary>
  /// <param name="place">The place.</param>
  /// <returns>The fact.</returns>
  public DynamicFact this[TPlace place] => this._facts.TryGetValue(place, out var fact) ? fact : this.Unbound;

  /// <summary>The state with one place holding another fact.</summary>
  /// <param name="place">The place.</param>
  /// <param name="fact">The fact.</param>
  /// <returns>The new state.</returns>
  public DynamicState<TPlace> With(TPlace place, DynamicFact fact) => new(new Dictionary<TPlace, DynamicFact>(this._facts) { [place] = fact }) { Unbound = this.Unbound };

  /// <summary>The join of two states, place by place, or the widening of the first by the second.</summary>
  /// <param name="other">The other state.</param>
  /// <param name="domain">The domain.</param>
  /// <param name="widen">Whether to widen, as at the head of a loop that has been passed through before.</param>
  /// <returns>The joined state.</returns>
  public DynamicState<TPlace> Join(DynamicState<TPlace> other, DynamicFacts domain, bool widen = false) {
    ArgumentNullException.ThrowIfNull(other);
    ArgumentNullException.ThrowIfNull(domain);
    var facts = new Dictionary<TPlace, DynamicFact>();
    foreach (var place in this._facts.Keys.Union(other._facts.Keys))
      facts[place] = widen ? domain.Widen(this[place], other[place]) : domain.Join(this[place], other[place]);
    return new(facts) { Unbound = domain.Join(this.Unbound, other.Unbound) };
  }

  /// <summary>Whether two states give every place the same fact.</summary>
  /// <param name="other">The other state.</param>
  /// <returns><see langword="true"/> for the same facts.</returns>
  public bool SameAs(DynamicState<TPlace> other) {
    ArgumentNullException.ThrowIfNull(other);
    return this.Unbound == other.Unbound && this._facts.Keys.Union(other._facts.Keys).All(place => this[place] == other[place]);
  }

}
