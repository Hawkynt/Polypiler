using System;
using System.Numerics;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Algebra;

/// <summary>
/// Whether a mapping <c>M</c> commutes with a bitwise operation (PRD §20.4): <c>M(a op b) = M(a) op M(b)</c>
/// for every pair of inputs, or <c>M(~a) = ~M(a)</c> for every input, compared on the outputs that are
/// not don't-cares. The predicates are decided from the entries, never by sampling, and they are exact:
/// output bits are independent, so the law holds exactly when it holds for every entry kind present —
/// a plain source commutes with every operation, an inverted source only with <c>Not</c>, <c>=0</c> with
/// <c>And</c>, <c>Or</c> and <c>Xor</c>, <c>=1</c> with <c>And</c> and <c>Or</c>. Where a predicate is
/// false, <see cref="Counterexample"/> names inputs that show it.
/// </summary>
public static class Distribution {

  /// <summary>Whether <c>M(a &amp; b) = M(a) &amp; M(b)</c> and <c>M(a | b) = M(a) | M(b)</c>: no entry is inverted.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static bool OverAndOr(BitMapping mapping) => Offender(mapping, BitwiseOperation.And) < 0;

  /// <summary>Whether <c>M(a ^ b) = M(a) ^ M(b)</c>: no entry is inverted and none is <c>=1</c>.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static bool OverXor(BitMapping mapping) => Offender(mapping, BitwiseOperation.Xor) < 0;

  /// <summary>Whether <c>M(~a) = ~M(a)</c>: no entry is a constant.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static bool OverNot(BitMapping mapping) => Offender(mapping, BitwiseOperation.Not) < 0;

  /// <summary>
  /// The constant that replaces <paramref name="constant"/> when an operation with it is pushed through
  /// the mapping: <c>M(x op c) = M(x) op M(c)</c> for every operation the mapping distributes over, so
  /// the constant is the mapping's image of it.
  /// </summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="constant">The constant on the input side, 0 to <c>2^InputWidth - 1</c>.</param>
  /// <returns>The constant on the output side.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="constant"/> is negative or wider than the input.</exception>
  public static BigInteger PermuteConstant(BitMapping mapping, BigInteger constant) {
    ArgumentNullException.ThrowIfNull(mapping);
    if (constant.Sign < 0 || constant.GetBitLength() > mapping.InputWidth)
      throw new ArgumentOutOfRangeException(nameof(constant), constant, $"a constant of the input lies in 0..2^{mapping.InputWidth}-1");

    return MappingEvaluator.Evaluate(mapping, constant);
  }

  /// <summary>
  /// Inputs on which the mapping does not distribute over <paramref name="operation"/>, or
  /// <see langword="null"/> when it does. They are built from the first entry that breaks the law and
  /// evaluated with <see cref="MappingEvaluator"/>; <c>And</c> and <c>Or</c> share the predicate
  /// <see cref="OverAndOr"/> but each has its own counterexample.
  /// </summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="operation">The operation.</param>
  /// <returns>The counterexample, or <see langword="null"/>.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not defined.</exception>
  public static DistributionCounterexample? Counterexample(BitMapping mapping, BitwiseOperation operation) {
    var bit = Offender(mapping, operation);
    if (bit < 0)
      return null;

    var read = mapping.Outputs[bit].ReadsInput ? BigInteger.One << mapping.Outputs[bit].Index : BigInteger.Zero;
    var (a, b) = operation switch {
      BitwiseOperation.And => (BigInteger.Zero, read),
      BitwiseOperation.Or => (read, BigInteger.Zero),
      _ => (BigInteger.Zero, BigInteger.Zero)
    };
    var inputMask = (BigInteger.One << mapping.InputWidth) - 1;
    var outputMask = (BigInteger.One << mapping.OutputWidth) - 1;
    var (left, right) = (MappingEvaluator.Evaluate(mapping, a), MappingEvaluator.Evaluate(mapping, b));
    return operation switch {
      BitwiseOperation.And => new(operation, a, b, MappingEvaluator.Evaluate(mapping, a & b), left & right),
      BitwiseOperation.Or => new(operation, a, b, MappingEvaluator.Evaluate(mapping, a | b), left | right),
      BitwiseOperation.Xor => new(operation, a, b, MappingEvaluator.Evaluate(mapping, a ^ b), left ^ right),
      _ => new(operation, a, b, MappingEvaluator.Evaluate(mapping, a ^ inputMask), left ^ outputMask)
    };
  }

  /// <summary>The first output bit whose entry breaks the law of <paramref name="operation"/>, or -1.</summary>
  private static int Offender(BitMapping mapping, BitwiseOperation operation) {
    ArgumentNullException.ThrowIfNull(mapping);
    if (!Enum.IsDefined(operation))
      throw new ArgumentOutOfRangeException(nameof(operation), operation, "not a bitwise operation");

    for (var bit = 0; bit < mapping.OutputWidth; ++bit)
      if (!Commutes(mapping.Outputs[bit].Kind, operation))
        return bit;
    return -1;
  }

  /// <summary>Whether one entry kind commutes with the operation.</summary>
  private static bool Commutes(BitSourceKind kind, BitwiseOperation operation) => kind switch {
    BitSourceKind.InvertedSource => operation == BitwiseOperation.Not,
    BitSourceKind.Zero => operation != BitwiseOperation.Not,
    BitSourceKind.One => operation is BitwiseOperation.And or BitwiseOperation.Or,
    _ => true
  };

}
