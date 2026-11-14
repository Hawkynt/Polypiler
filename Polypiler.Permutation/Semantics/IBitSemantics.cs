namespace Polypiler.Permutation.Semantics;

/// <summary>
/// What a bit transform means, as opposed to the network that implements it (PRD §20.2): a
/// <see cref="BitPermutation"/>, a <see cref="BitRouting"/> or a <see cref="BitBooleanTransform"/>, and
/// nothing else. A network of operations is never an <see cref="IBitSemantics"/>; it implements one.
/// </summary>
public interface IBitSemantics {

  /// <summary>How many input bits the transform reads.</summary>
  int InputWidth { get; }

  /// <summary>How many output bits the transform has.</summary>
  int OutputWidth { get; }

  /// <summary>The lanes the bits are grouped into.</summary>
  BitLayout Layout { get; }

  /// <summary>The class the value is declared as; being internal, it keeps other assemblies from implementing the interface.</summary>
  internal BitSemanticClass DeclaredClass { get; }

}
