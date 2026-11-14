using System;
using System.Numerics;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Tests.Permutation.Semantics;

/// <summary>
/// The independent oracle of issue #267: a <see cref="BoolExpr"/> evaluated one input value at a time,
/// straight from the definitions of the operators. It shares no code with the folding or the
/// bit-sliced truth table of the product.
/// </summary>
internal static class BoolOracle {

  /// <summary>The value of an expression for input <paramref name="x"/>; an input bit not set in <paramref name="x"/> is 0.</summary>
  internal static bool Value(BoolExpr expression, long x) => expression switch {
    BoolExpr.Input input => input.Index < 63 && ((x >> input.Index) & 1) == 1,
    BoolExpr.Const constant => constant.Value,
    BoolExpr.Not not => !Value(not.Operand, x),
    BoolExpr.And and => Value(and.Left, x) && Value(and.Right, x),
    BoolExpr.Or or => Value(or.Left, x) || Value(or.Right, x),
    BoolExpr.Xor xor => Value(xor.Left, x) != Value(xor.Right, x),
    _ => throw new ArgumentException($"unknown expression {expression}", nameof(expression))
  };

  /// <summary>The value of every output of a transform for input <paramref name="x"/>, output bit i in bit i.</summary>
  internal static BigInteger Outputs(BitBooleanTransform transform, long x) {
    var value = BigInteger.Zero;
    for (var bit = 0; bit < transform.OutputWidth; ++bit)
      if (Value(transform.Outputs[bit], x))
        value += BigInteger.Pow(2, bit);
    return value;
  }

  /// <summary>Whether two expressions agree on every input below 2^width.</summary>
  internal static bool Equivalent(BoolExpr left, BoolExpr right, int width) {
    for (long x = 0; x < 1L << width; ++x)
      if (Value(left, x) != Value(right, x))
        return false;
    return true;
  }

  /// <summary>How many input bits below <paramref name="width"/> the function of the expression really depends on.</summary>
  internal static int EssentialInputs(BoolExpr expression, int width) {
    var count = 0;
    for (var bit = 0; bit < width; ++bit) {
      for (long x = 0; x < 1L << width; ++x)
        if (((x >> bit) & 1) == 0 && Value(expression, x) != Value(expression, x | (1L << bit))) {
          ++count;
          break;
        }
    }
    return count;
  }

}
