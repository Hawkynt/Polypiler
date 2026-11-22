using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Scev;

/// <summary>
/// Scalar evolution through the fact facade (#90): the evolution and trip count of a loop of the facade's
/// function, from the constant and range facts registered with it. Like the facade, nothing is cached:
/// each query analyses the loops anew.
/// </summary>
public static class ScalarEvolutionFacts {

  /// <summary>The evolution of a value read where a pass of a loop starts.</summary>
  /// <param name="facts">The facade.</param>
  /// <param name="value">An integer expression over the variables in scope at the loop.</param>
  /// <param name="loop">A loop of the facade's function.</param>
  /// <returns>The evolution.</returns>
  public static Evolution EvolutionOf(this ValueFacts facts, IrExpr value, IrStmt loop) => new ScalarEvolution(facts).Of(value, loop);

  /// <summary>How often the body of a loop runs.</summary>
  /// <param name="facts">The facade.</param>
  /// <param name="loop">A loop of the facade's function.</param>
  /// <returns>The trip count.</returns>
  public static TripCount TripCountOf(this ValueFacts facts, IrStmt loop) => new ScalarEvolution(facts).TripCountOf(loop);

}
