using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Verification.Generators;
using Polypiler.Permutation.Algebra;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;
using Polypiler.Tests.Properties;

namespace Polypiler.Tests.Permutation.Algebra;

/// <summary>
/// The laws of PRD §20.4, each side evaluated by <see cref="MappingEvaluator"/>: 10 000 seeded random
/// permutations per width with seed 803, seeded random routings, and on 8-bit mappings every input — and
/// for the distribution predicates every pair of inputs, read from the evaluator's truth table.
/// </summary>
[TestFixture]
public sealed class AlgebraLawTests {

  private const ulong Seed = 803;
  private const int Count = 10_000;

  /// <summary>Three permutations of one width and an input.</summary>
  public sealed record PermutationCase(BitPermutation A, BitPermutation B, BitPermutation C, BigInteger X);

  /// <summary>Three routings that compose as <c>A ∘ B ∘ C</c> and an input of <c>C</c>.</summary>
  public sealed record RoutingCase(BitRouting A, BitRouting B, BitRouting C, BigInteger X);

  private static IEnumerable<T> NoShrink<T>(T _) => [];

  private static BigInteger RandomValue(SeededRandom random, int width) {
    var value = BigInteger.Zero;
    for (var bit = 0; bit < width; bit += 64)
      value |= new BigInteger(random.NextBits(Math.Min(64, width - bit))) << bit;
    return value;
  }

  private static BitPermutation RandomPermutation(SeededRandom random, int width) {
    var map = Enumerable.Range(0, width).ToArray();
    for (var bit = width - 1; bit > 0; --bit) {
      var other = random.NextBelow(bit + 1);
      (map[bit], map[other]) = (map[other], map[bit]);
    }
    return BitPermutation.From(BitMapping.Create(width, [.. map.Select(BitSource.From)]));
  }

  private static BitMapping RandomMapping(SeededRandom random, int inputWidth, int outputWidth) {
    var outputs = new BitSource[outputWidth];
    for (var bit = 0; bit < outputWidth; ++bit)
      outputs[bit] = random.NextBelow(8) switch {
        0 => BitSource.Zero,
        1 => BitSource.One,
        2 => BitSource.DontCare,
        3 or 4 => BitSource.Not(random.NextBelow(inputWidth)),
        _ => BitSource.From(random.NextBelow(inputWidth))
      };
    return BitMapping.Create(inputWidth, outputs);
  }

  /// <summary>An 8-bit mapping whose entries come from a random subset of the kinds, so that each predicate is true for some and false for others.</summary>
  private static BitMapping RandomByteMapping(SeededRandom random) {
    var kinds = Enum.GetValues<BitSourceKind>().Where(_ => random.NextBoolean()).Append(BitSourceKind.Source).ToArray();
    var outputs = new BitSource[8];
    for (var bit = 0; bit < 8; ++bit)
      outputs[bit] = kinds[random.NextBelow(kinds.Length)] switch {
        BitSourceKind.Zero => BitSource.Zero,
        BitSourceKind.One => BitSource.One,
        BitSourceKind.DontCare => BitSource.DontCare,
        BitSourceKind.InvertedSource => BitSource.Not(random.NextBelow(8)),
        _ => BitSource.From(random.NextBelow(8))
      };
    return BitMapping.Create(8, outputs);
  }

  private static IEnumerable<PermutationCase> Permutations(int width, ulong seed) {
    var random = new SeededRandom(seed ^ (ulong)width);
    while (true)
      yield return new(RandomPermutation(random, width), RandomPermutation(random, width), RandomPermutation(random, width), RandomValue(random, width));
  }

  private static IEnumerable<RoutingCase> Routings(ulong seed) {
    var random = new SeededRandom(seed);
    while (true) {
      var widths = Enumerable.Range(0, 4).Select(_ => 1 + random.NextBelow(24)).ToArray();
      yield return new(
        BitRouting.From(RandomMapping(random, widths[1], widths[0])),
        BitRouting.From(RandomMapping(random, widths[2], widths[1])),
        BitRouting.From(RandomMapping(random, widths[3], widths[2])),
        RandomValue(random, widths[3]));
    }
  }

  private static bool IsIdentity(BitPermutation permutation) => permutation.Mapping == BitMapping.Identity(permutation.Width);

  /// <summary>
  /// <c>Inverse(Inverse(p)) = p</c>; <c>p ∘ p⁻¹ = p⁻¹ ∘ p = id</c>; associativity; the evaluation of a
  /// composition is the composition of evaluations; <c>p⁻¹(p(x)) = x</c>; <c>p^Order(p) = id</c> and no
  /// smaller cycle-length power is; the cycles partition the bits.
  /// </summary>
  [TestCase(8)]
  [TestCase(16)]
  [TestCase(32)]
  [TestCase(64)]
  [TestCase(128)]
  [Category("Slow")]
  public void PermutationLaws_HoldOnSeededRandomPermutations(int width) {
    var held = PropertyRunner.Default.ForAll(Seed, Count, seed => Permutations(width, seed), NoShrink, sample => {
      var (a, b, c, x) = (sample.A, sample.B, sample.C, sample.X);
      var inverse = BitPermutationAlgebra.Inverse(a);
      var order = BitPermutationAlgebra.Order(a);
      var cycles = BitPermutationAlgebra.Cycles(a);
      return BitPermutationAlgebra.Inverse(inverse).Mapping == a.Mapping
        && IsIdentity(BitPermutationAlgebra.Compose(a, inverse))
        && IsIdentity(BitPermutationAlgebra.Compose(inverse, a))
        && BitPermutationAlgebra.Compose(BitPermutationAlgebra.Compose(a, b), c).Mapping == BitPermutationAlgebra.Compose(a, BitPermutationAlgebra.Compose(b, c)).Mapping
        && MappingEvaluator.Evaluate(BitPermutationAlgebra.Compose(a, b).Mapping, x) == MappingEvaluator.Evaluate(a.Mapping, MappingEvaluator.Evaluate(b.Mapping, x))
        && MappingEvaluator.Evaluate(inverse.Mapping, MappingEvaluator.Evaluate(a.Mapping, x)) == x
        && IsIdentity(BitPermutationAlgebra.Power(a, order))
        && (order == BigInteger.One || !IsIdentity(BitPermutationAlgebra.Power(a, order - 1)))
        && BitPermutationAlgebra.Power(a, -1).Mapping == inverse.Mapping
        && cycles.SelectMany(cycle => cycle).Order().SequenceEqual(Enumerable.Range(0, width))
        && BitPermutationAlgebra.IsInvolution(a) == (order <= 2);
    }, $"{nameof(this.PermutationLaws_HoldOnSeededRandomPermutations)}_{width}");
    Assert.That(held, Is.EqualTo(Count));
  }

  /// <summary>Routing composition is associative and its evaluation agrees with the composed evaluations on the composed required outputs.</summary>
  [Test]
  public void RoutingLaws_HoldOnSeededRandomRoutings() {
    var held = PropertyRunner.Default.ForAll(Seed, 2_000, Routings, NoShrink, sample => {
      var (a, b, c, x) = (sample.A, sample.B, sample.C, sample.X);
      var ab = BitRoutingAlgebra.Compose(a, b);
      var bc = BitRoutingAlgebra.Compose(b, c);
      var direct = MappingEvaluator.Evaluate(a.Mapping, MappingEvaluator.Evaluate(b.Mapping, MappingEvaluator.Evaluate(c.Mapping, x)));
      var abc = BitRoutingAlgebra.Compose(ab, c);
      return abc.Mapping == BitRoutingAlgebra.Compose(a, bc).Mapping
        && MappingEvaluator.AgreesOnRequired(abc.Mapping, x, direct)
        && MappingEvaluator.AgreesOnRequired(bc.Mapping, x, MappingEvaluator.Evaluate(b.Mapping, MappingEvaluator.Evaluate(c.Mapping, x)))
        && BitSemanticsVerifier.Check(abc).Count == 0;
    });
    Assert.That(held, Is.EqualTo(2_000));
  }

  /// <summary>On 8-bit routings the composition law holds for all 256 inputs.</summary>
  [Test]
  public void RoutingComposition_HoldsExhaustivelyOn8Bits() {
    var random = new SeededRandom(Seed);
    for (var sample = 0; sample < 200; ++sample) {
      var (outer, inner) = (BitRouting.From(RandomMapping(random, 8, 8)), BitRouting.From(RandomMapping(random, 8, 8)));
      var composed = BitRoutingAlgebra.Compose(outer, inner);
      for (var x = 0UL; x < 256; ++x)
        if (!MappingEvaluator.AgreesOnRequired(composed.Mapping, x, MappingEvaluator.Evaluate(outer.Mapping, MappingEvaluator.Evaluate(inner.Mapping, x))))
          Assert.Fail($"{outer} ∘ {inner} = {composed} disagrees at x = {x}");
    }
  }

  /// <summary>
  /// The distribution predicates are exact on 8-bit mappings: true means the law holds on every pair of
  /// the 65 536 (or every input of the 256 for Not), false means the counterexample refutes it.
  /// </summary>
  [Test, Category("Slow")]
  public void DistributionPredicates_AreExactOn8Bits() {
    var random = new SeededRandom(Seed);
    var (holding, refuted) = (0, 0);
    for (var sample = 0; sample < 300; ++sample) {
      var mapping = RandomByteMapping(random);
      var table = MappingEvaluator.TruthTable(mapping).Select(value => (int)value).ToArray();
      var required = (int)mapping.RequiredOutputMask;
      foreach (var operation in Enum.GetValues<BitwiseOperation>()) {
        var claimed = operation switch {
          BitwiseOperation.And or BitwiseOperation.Or => Distribution.OverAndOr(mapping),
          BitwiseOperation.Xor => Distribution.OverXor(mapping),
          _ => Distribution.OverNot(mapping)
        };
        var holds = Holds(table, required, operation);
        Assert.That(claimed, Is.EqualTo(holds), $"{operation} over {mapping}");
        Assert.That(Distribution.Counterexample(mapping, operation) is { } example
          && ((example.Routed ^ example.Combined) & mapping.RequiredOutputMask) != 0
          && example.Routed == table[(int)Apply(operation, (int)example.A, (int)example.B, 0xFF)]
          && example.Combined == Apply(operation, table[(int)example.A], table[(int)example.B], 0xFF), Is.EqualTo(!holds), $"counterexample of {operation} over {mapping}");
        if (holds)
          ++holding;
        else
          ++refuted;
      }
    }
    Assert.That(Math.Min(holding, refuted), Is.GreaterThan(100), $"{holding} laws held, {refuted} were refuted");
  }

  private static int Apply(BitwiseOperation operation, int a, int b, int mask) => operation switch {
    BitwiseOperation.And => a & b,
    BitwiseOperation.Or => a | b,
    BitwiseOperation.Xor => a ^ b,
    _ => a ^ mask
  };

  private static bool Holds(int[] table, int required, BitwiseOperation operation) {
    for (var a = 0; a < 256; ++a)
      for (var b = 0; b < (operation == BitwiseOperation.Not ? 1 : 256); ++b)
        if (((table[Apply(operation, a, b, 0xFF)] ^ Apply(operation, table[a], table[b], 0xFF)) & required) != 0)
          return false;
    return true;
  }

}
