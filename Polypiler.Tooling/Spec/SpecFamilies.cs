using System.Collections.Generic;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tooling.Spec;

/// <summary>The families <c>polypiler-tooling spec validate</c> knows. A family issue adds its family here, one line each.</summary>
public static class SpecFamilies {

  /// <summary>Every family, one per folder under <c>spec/</c>.</summary>
  public static IReadOnlyList<ISpecFamily> All { get; } = [
    new DemoSpecFamily(),
    new BinarySpecFamily(),
    new EffectsSpecFamily(),
    new IrSpecFamily(),
  ];

}
