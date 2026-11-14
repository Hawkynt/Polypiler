using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// The rules of the semantic hierarchy, each violation a <c>PLY0805</c>:
/// H1 a <see cref="BitPermutation"/> is a bijection;
/// H2 a condition fits the widths and does not know a bit as both 0 and 1;
/// H3 a routing's condition requires exactly the outputs that are not don't-cares in its mapping;
/// H4 a <see cref="BitBooleanTransform"/> reads only inputs below its input width;
/// H5 the lane size divides both widths;
/// H6 narrowing is checked — <see cref="BitBooleanTransform.TryAsRouting"/> and
/// <see cref="BitRouting.TryAsPermutation"/> either prove the narrower class or refuse, so no value of
/// a narrower class can exist without it.
/// </summary>
public static class BitSemanticsVerifier {

  /// <summary>Checks every rule that applies to the value's class.</summary>
  /// <param name="semantics">The value.</param>
  /// <returns>The violations, empty when the value is sound.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="semantics"/> is <see langword="null"/>.</exception>
  public static IReadOnlyList<Diagnostic> Check(IBitSemantics semantics) {
    ArgumentNullException.ThrowIfNull(semantics);
    var found = new List<Diagnostic>();
    switch (semantics.DeclaredClass) {
      case BitSemanticClass.Permutation:
        found.AddRange(H1(((BitPermutation)semantics).Mapping));
        break;
      case BitSemanticClass.Routing:
        var routing = (BitRouting)semantics;
        found.AddRange(H2(routing.Condition, routing.InputWidth, routing.OutputWidth));
        found.AddRange(H3(routing.Mapping, routing.Condition));
        break;
      default:
        var transform = (BitBooleanTransform)semantics;
        found.AddRange(H2(transform.Condition, transform.InputWidth, transform.OutputWidth));
        found.AddRange(H4(transform));
        break;
    }
    found.AddRange(H5(semantics.Layout, semantics.InputWidth, semantics.OutputWidth));
    return found;
  }

  private static Diagnostic Violation(string reason) => Diagnostic.Create(BitSemanticsDiagnostics.ClassViolated, null, reason);

  /// <summary>H1: the mapping declared a permutation is a bijection.</summary>
  internal static IEnumerable<Diagnostic> H1(BitMapping mapping) {
    if (BitSemanticsClassifier.NonBijectivityReason(mapping) is { } reason)
      yield return Violation("mapping is declared BitPermutation but " + reason);
  }

  /// <summary>H2: the masks fit the widths and no input bit is known as both 0 and 1.</summary>
  private static IEnumerable<Diagnostic> H2(BitCondition condition, int inputWidth, int outputWidth) {
    if (Hex.Exceeds(condition.InputKnownZero, inputWidth) || Hex.Exceeds(condition.InputKnownOne, inputWidth))
      yield return Violation($"condition mask exceeds width {inputWidth}");
    if (Hex.Exceeds(condition.RequiredOutputMask, outputWidth))
      yield return Violation($"condition mask exceeds width {outputWidth}");

    var both = condition.InputKnownZero & condition.InputKnownOne;
    if (!both.IsZero)
      yield return Violation($"condition is contradictory on input bits {Hex.Format(both)}");
  }

  /// <summary>H3: the condition requires an output bit exactly when the mapping's entry is no don't-care.</summary>
  private static IEnumerable<Diagnostic> H3(BitMapping mapping, BitCondition condition) {
    for (var bit = 0; bit < mapping.OutputWidth; ++bit) {
      var dontCare = mapping.Outputs[bit].Kind == BitSourceKind.DontCare;
      if (dontCare == condition.Requires(bit))
        yield return Violation(dontCare
          ? $"output bit {bit} is don't-care in the mapping but required by the condition"
          : $"output bit {bit} is fixed in the mapping but not required by the condition");
    }
  }

  /// <summary>H4: no output reads an input at or above the input width; the smallest such input is named.</summary>
  private static IEnumerable<Diagnostic> H4(BitBooleanTransform transform) {
    for (var bit = 0; bit < transform.OutputWidth; ++bit) {
      var outside = transform.Outputs[bit].Inputs().GetViewBetween(transform.InputWidth, int.MaxValue);
      if (outside.Count > 0)
        yield return Violation($"output bit {bit} reads input {outside.Min}, input width is {transform.InputWidth}");
    }
  }

  /// <summary>H5: the lane size divides the input width and the output width.</summary>
  private static IEnumerable<Diagnostic> H5(BitLayout layout, int inputWidth, int outputWidth)
    => new[] { inputWidth, outputWidth }.Distinct().Where(width => !layout.Divides(width))
      .Select(width => Violation($"lane size {layout.LaneBits} does not divide width {width}"));

}
