using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis;

/// <summary>The answer to a yes-or-no question about a program point; <see cref="Unknown"/> is never a proof.</summary>
public enum Truth {

  /// <summary>Neither answer is proven.</summary>
  Unknown,

  /// <summary>The answer is yes on every execution that reaches the point.</summary>
  True,

  /// <summary>The answer is no on every execution that reaches the point.</summary>
  False

}

/// <summary>
/// A fact of one abstract domain about a value. The fact type names its domain, so that the facade can
/// answer the domain's <see cref="IFactDomain{TFact}.Unknown"/> for a query no analysis serves.
/// </summary>
/// <typeparam name="TSelf">The fact type itself.</typeparam>
public interface IFact<TSelf> where TSelf : IFact<TSelf> {

  /// <summary>The domain the facts of this type belong to.</summary>
  static abstract IFactDomain<TSelf> Domain { get; }

}

/// <summary>
/// An abstract domain: a lattice of facts about values, with <see cref="Unknown"/> as its top (every
/// value is possible) and <see cref="Unreachable"/> as its bottom (no execution reaches the point).
/// Vocabulary per Cousot and Cousot, "Abstract Interpretation" (POPL 1977).
/// </summary>
/// <typeparam name="TFact">The fact type.</typeparam>
public interface IFactDomain<TFact> {

  /// <summary>The name of the domain, such as <c>constant</c>, which diagnostics and descriptions use.</summary>
  string Name { get; }

  /// <summary>The fact that claims nothing: every value is possible.</summary>
  TFact Unknown { get; }

  /// <summary>The fact of a point no execution reaches.</summary>
  TFact Unreachable { get; }

  /// <summary>The least fact both facts imply: what holds where control comes from either.</summary>
  /// <param name="left">One fact.</param>
  /// <param name="right">The other fact.</param>
  /// <returns>The join.</returns>
  TFact Join(TFact left, TFact right);

  /// <summary>The greatest fact that implies both: what holds when both hold.</summary>
  /// <param name="left">One fact.</param>
  /// <param name="right">The other fact.</param>
  /// <returns>The meet.</returns>
  TFact Meet(TFact left, TFact right);

  /// <summary>A fact at least as weak as the join of both that a chain of widenings reaches in finitely many steps.</summary>
  /// <param name="previous">The fact of the previous iteration.</param>
  /// <param name="next">The fact of this iteration.</param>
  /// <returns>The widened fact.</returns>
  TFact Widen(TFact previous, TFact next);

  /// <summary>Whether every value the stronger fact allows the weaker allows too.</summary>
  /// <param name="stronger">The fact that may be stronger.</param>
  /// <param name="weaker">The fact that may be weaker.</param>
  /// <returns><see langword="true"/> when <paramref name="stronger"/> implies <paramref name="weaker"/>.</returns>
  bool Implies(TFact stronger, TFact weaker);

  /// <summary>
  /// Whether a value a run observed is one the fact allows: an <see cref="IrConst"/> for an integer or a
  /// truth value, an <see cref="IrObservedPointer"/> for a pointer (<see cref="IrObservation.Value"/>).
  /// </summary>
  /// <param name="fact">The fact.</param>
  /// <param name="value">The observed value.</param>
  /// <returns><see langword="false"/> when the observation refutes the fact.</returns>
  bool Contains(TFact fact, object value);

  /// <summary>The fact in words, such as <c>constant 25</c>.</summary>
  /// <param name="fact">The fact.</param>
  /// <returns>The text.</returns>
  string Describe(TFact fact);

}

/// <summary>An analysis that answers facts of one domain for the values of one function.</summary>
/// <typeparam name="TFact">The fact type.</typeparam>
public interface IFactProvider<TFact> {

  /// <summary>
  /// The fact of a value at a point. A provider never answers a fact stronger than what holds on every
  /// execution reaching the point, and answers <see cref="IFactDomain{TFact}.Unreachable"/> only for a
  /// point no execution reaches.
  /// </summary>
  /// <param name="value">The value: an expression over the variables of the function.</param>
  /// <param name="point">The point.</param>
  /// <returns>The fact.</returns>
  /// <exception cref="FactUnavailableException">The provider gives up on the query.</exception>
  TFact At(IrExpr value, ProgramPoint point);

  /// <summary>The statements the fact of a value at a point was derived from, for a proof reference.</summary>
  /// <param name="value">The value.</param>
  /// <param name="point">The point.</param>
  /// <returns>The explanation.</returns>
  FactExplanation Explain(IrExpr value, ProgramPoint point);

}

/// <summary>A provider that can also decide a comparison at a point.</summary>
public interface IComparisonDecider {

  /// <summary>Whether a comparison is true at a point on every execution reaching it.</summary>
  /// <param name="comparison">An expression of type <see cref="IrType.Bool"/>.</param>
  /// <param name="point">The point.</param>
  /// <returns>The answer; <see cref="Truth.Unknown"/> when it cannot tell.</returns>
  Truth Decide(IrExpr comparison, ProgramPoint point);

}
