using System;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// The narrowest class of a mapping or a Boolean transform (PRD §20.2), and the reason shown in
/// reports. Classification never changes the value it looks at.
/// </summary>
public static class BitSemanticsClassifier {

  /// <summary><see cref="BitSemanticClass.Permutation"/> when the mapping is a bijection, otherwise <see cref="BitSemanticClass.Routing"/>.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static BitSemanticClass Classify(BitMapping mapping) {
    ArgumentNullException.ThrowIfNull(mapping);
    return mapping.IsBijective ? BitSemanticClass.Permutation : BitSemanticClass.Routing;
  }

  /// <summary>The class of the routing <see cref="BitBooleanTransform.TryAsRouting"/> gives, or <see cref="BitSemanticClass.BooleanTransform"/> when it refuses.</summary>
  /// <param name="transform">The transform.</param>
  /// <exception cref="ArgumentNullException"><paramref name="transform"/> is <see langword="null"/>.</exception>
  public static BitSemanticClass Classify(BitBooleanTransform transform) {
    ArgumentNullException.ThrowIfNull(transform);
    return transform.TryAsRouting(out var routing, out _) ? Classify(routing.Mapping) : BitSemanticClass.BooleanTransform;
  }

  /// <summary>Why the mapping has its class: the first reason it is no bijection (as in rule H1), or that every input is read once.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static string Explain(BitMapping mapping) {
    ArgumentNullException.ThrowIfNull(mapping);
    return NonBijectivityReason(mapping) ?? $"each of the {mapping.InputWidth} input bits is read exactly once";
  }

  /// <summary>Why the transform has its class: the explanation of its routing, or why it is none.</summary>
  /// <param name="transform">The transform.</param>
  /// <exception cref="ArgumentNullException"><paramref name="transform"/> is <see langword="null"/>.</exception>
  public static string Explain(BitBooleanTransform transform) {
    ArgumentNullException.ThrowIfNull(transform);
    return transform.TryAsRouting(out var routing, out var reason) ? Explain(routing.Mapping) : reason;
  }

  /// <summary>
  /// The first reason a mapping is no bijection, or <see langword="null"/> when it is one: the widths
  /// differ; else an input bit is read more than once; else an output bit is inverted, a constant or a
  /// don't-care. A square mapping of plain sources without duplication reads every input exactly once.
  /// </summary>
  internal static string? NonBijectivityReason(BitMapping mapping) {
    if (!mapping.IsSquare)
      return $"widths differ ({mapping.InputWidth} in, {mapping.OutputWidth} out)";

    for (var bit = 0; bit < mapping.InputWidth; ++bit)
      if (mapping.UseCount(bit) > 1)
        return $"input bit {bit} is used {mapping.UseCount(bit)} times";

    for (var bit = 0; bit < mapping.OutputWidth; ++bit) {
      var what = mapping.Outputs[bit].Kind switch {
        BitSourceKind.Source => null,
        BitSourceKind.InvertedSource => "inverted",
        BitSourceKind.DontCare => "a don't-care",
        _ => "a constant"
      };
      if (what is not null)
        return $"output bit {bit} is {what}";
    }
    return null;
  }

}
