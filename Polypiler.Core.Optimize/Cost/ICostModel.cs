using System.Collections.Generic;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>
/// The one service a pass asks which of several forms it has proven equivalent is cheapest on the
/// selected target. The model ranks; it never changes IR and never decides legality.
/// </summary>
public interface ICostModel {

  /// <summary>The cost of one use of an operation in the first form the profile lists of native, sequence, helper; the profile's <c>default</c> where it lists none.</summary>
  /// <param name="op">The operation.</param>
  /// <returns>The cost.</returns>
  Cost Of(OperationKey op);

  /// <summary>
  /// The cheapest alternative: the lowest score under <paramref name="objective"/>; on equal scores the lower
  /// score under the Balanced weights; if still equal the one listed first.
  /// </summary>
  /// <param name="alternatives">The alternatives, at least one.</param>
  /// <param name="objective">What to minimise.</param>
  /// <returns>The decision.</returns>
  Decision Choose(IReadOnlyList<Alternative> alternatives, Objective objective);

  /// <summary>Whether the profile offers a form; a form it does not list is never offered.</summary>
  /// <param name="form">The form, such as <c>threads</c>, <c>threads-4</c>, <c>simd-128</c> or <c>native</c>.</param>
  /// <returns><see langword="true"/> when the form is available.</returns>
  bool IsAvailable(string form);

}
