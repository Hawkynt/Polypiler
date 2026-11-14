using System;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Permutation.Algebra;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;

namespace Polypiler.Tests.Permutation.Algebra;

/// <summary>
/// The distribution predicates, true and false for each operation, and the counterexamples behind every
/// false one, checked again with the evaluator.
/// </summary>
[TestFixture]
public sealed class DistributionTests {

  private static readonly BitwiseOperation[] Operations = [BitwiseOperation.And, BitwiseOperation.Or, BitwiseOperation.Xor, BitwiseOperation.Not];

  /// <summary>Checks that a counterexample is one: recomputed with the evaluator, the two sides differ on a required output.</summary>
  private static void AssertRefutes(BitMapping mapping, DistributionCounterexample example) {
    var inputMask = (BigInteger.One << mapping.InputWidth) - 1;
    var outputMask = (BigInteger.One << mapping.OutputWidth) - 1;
    var (a, b) = (MappingEvaluator.Evaluate(mapping, example.A), MappingEvaluator.Evaluate(mapping, example.B));
    var (routed, combined) = example.Operation switch {
      BitwiseOperation.And => (MappingEvaluator.Evaluate(mapping, example.A & example.B), a & b),
      BitwiseOperation.Or => (MappingEvaluator.Evaluate(mapping, example.A | example.B), a | b),
      BitwiseOperation.Xor => (MappingEvaluator.Evaluate(mapping, example.A ^ example.B), a ^ b),
      _ => (MappingEvaluator.Evaluate(mapping, example.A ^ inputMask), a ^ outputMask)
    };
    Assert.Multiple(() => {
      Assert.That(example.Routed, Is.EqualTo(routed));
      Assert.That(example.Combined, Is.EqualTo(combined));
      Assert.That(((routed ^ combined) & mapping.RequiredOutputMask).IsZero, Is.False, $"{example} refutes nothing");
    });
  }

  private static bool Predicate(BitMapping mapping, BitwiseOperation operation) => operation switch {
    BitwiseOperation.And or BitwiseOperation.Or => Distribution.OverAndOr(mapping),
    BitwiseOperation.Xor => Distribution.OverXor(mapping),
    _ => Distribution.OverNot(mapping)
  };

  /// <summary>Test design 5: the predicate of every operation, true and false, and a verified counterexample for each false one.</summary>
  [TestCase("8:[7,6,5,4,3,2,1,0]", true, true, true)]
  [TestCase("2:[~0,1]", false, false, true)]
  [TestCase("2:[0,=1]", true, false, false)]
  [TestCase("2:[=0,1]", true, true, false)]
  [TestCase("3:[0,0,?]", true, true, true)]
  [TestCase("8:[0,2,4,6]", true, true, true)]
  public void Predicates_AreExactAndRefutedByTheirCounterexamples(string text, bool andOr, bool xor, bool not) {
    var mapping = BitMapping.Parse(text);
    Assert.Multiple(() => {
      Assert.That(Distribution.OverAndOr(mapping), Is.EqualTo(andOr));
      Assert.That(Distribution.OverXor(mapping), Is.EqualTo(xor));
      Assert.That(Distribution.OverNot(mapping), Is.EqualTo(not));
    });
    foreach (var operation in Operations) {
      var example = Distribution.Counterexample(mapping, operation);
      if (Predicate(mapping, operation)) {
        Assert.That(example, Is.Null, $"{operation}");
      } else {
        Assert.That(example, Is.Not.Null, $"{operation}");
        Assert.That(example!.Operation, Is.EqualTo(operation));
        AssertRefutes(mapping, example);
      }
    }
  }

  /// <summary>The scenario "XOR does not distribute over a routing that sets a constant": a = b = 0 gives 2 routed and 0 combined.</summary>
  [Test]
  public void Xor_OverAConstantOne_IsRefutedByZeroAndZero() {
    var mapping = BitMapping.Parse("2:[0,=1]");
    Assert.Multiple(() => {
      Assert.That(Distribution.OverXor(mapping), Is.False);
      Assert.That(Distribution.Counterexample(mapping, BitwiseOperation.Xor), Is.EqualTo(new DistributionCounterexample(BitwiseOperation.Xor, BigInteger.Zero, BigInteger.Zero, new BigInteger(2), BigInteger.Zero)));
    });
  }

  /// <summary>The And and Or counterexamples of an inverted entry read the bit that entry inverts.</summary>
  [Test]
  public void AndAndOr_OverAnInvertedEntry_UseTheBitItReads() {
    var mapping = BitMapping.Parse("4:[0,~2]");
    Assert.Multiple(() => {
      Assert.That(Distribution.Counterexample(mapping, BitwiseOperation.And), Is.EqualTo(new DistributionCounterexample(BitwiseOperation.And, BigInteger.Zero, new BigInteger(4), new BigInteger(2), BigInteger.Zero)));
      Assert.That(Distribution.Counterexample(mapping, BitwiseOperation.Or), Is.EqualTo(new DistributionCounterexample(BitwiseOperation.Or, new BigInteger(4), BigInteger.Zero, BigInteger.Zero, new BigInteger(2))));
      Assert.That(Distribution.Counterexample(mapping, BitwiseOperation.Not), Is.Null);
    });
  }

  /// <summary>PermuteConstant is the image of the constant, and pushing it through a distributing mapping keeps the value.</summary>
  [Test]
  public void PermuteConstant_IsTheImageThatKeepsTheValue() {
    var mapping = BitMapping.Parse("8:[3,2,1,0,=0,=1,7,7]");
    var constant = new BigInteger(0x8C);
    var pushed = Distribution.PermuteConstant(mapping, constant);
    Assert.Multiple(() => {
      Assert.That(pushed, Is.EqualTo(new BigInteger(0xE3)));
      for (var x = 0; x < 256; ++x) {
        var input = new BigInteger(x);
        Assert.That(MappingEvaluator.Evaluate(mapping, input & constant), Is.EqualTo(MappingEvaluator.Evaluate(mapping, input) & pushed));
        Assert.That(MappingEvaluator.Evaluate(mapping, input | constant), Is.EqualTo(MappingEvaluator.Evaluate(mapping, input) | pushed));
      }
    });
  }

  /// <summary>PermuteConstant accepts 0 and all ones of the input width and refuses one bit more or a negative constant.</summary>
  [Test]
  public void PermuteConstant_RefusesAConstantOutsideTheInput() {
    var mapping = BitMapping.Parse("4:[0,1]");
    Assert.Multiple(() => {
      Assert.That(Distribution.PermuteConstant(mapping, BigInteger.Zero), Is.EqualTo(BigInteger.Zero));
      Assert.That(Distribution.PermuteConstant(mapping, new BigInteger(15)), Is.EqualTo(new BigInteger(3)));
      Assert.That(() => Distribution.PermuteConstant(mapping, new BigInteger(16)), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("constant"));
      Assert.That(() => Distribution.PermuteConstant(mapping, BigInteger.MinusOne), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("constant"));
    });
  }

  /// <summary>Missing mappings and undefined operations are refused.</summary>
  [Test]
  public void InvalidArguments_AreRefused() {
    var mapping = BitMapping.Parse("1:[0]");
    Assert.Multiple(() => {
      Assert.That(() => Distribution.OverAndOr(null!), Throws.ArgumentNullException);
      Assert.That(() => Distribution.OverXor(null!), Throws.ArgumentNullException);
      Assert.That(() => Distribution.OverNot(null!), Throws.ArgumentNullException);
      Assert.That(() => Distribution.PermuteConstant(null!, BigInteger.Zero), Throws.ArgumentNullException);
      Assert.That(() => Distribution.Counterexample(null!, BitwiseOperation.And), Throws.ArgumentNullException);
      Assert.That(() => Distribution.Counterexample(mapping, (BitwiseOperation)7), Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("operation"));
    });
  }

}
