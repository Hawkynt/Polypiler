namespace Polypiler.Permutation.Semantics;

/// <summary>The three semantic classes of a bit transform (PRD §20.2), narrowest first.</summary>
public enum BitSemanticClass {

  /// <summary>A bijection of the bits: square, plain sources only, every input read exactly once; it can be inverted.</summary>
  Permutation,

  /// <summary>Every output is an input bit, an inverted input bit, a constant or a don't-care; duplication and dropped inputs are allowed.</summary>
  Routing,

  /// <summary>Some output is a Boolean function of more than one input bit.</summary>
  BooleanTransform

}
