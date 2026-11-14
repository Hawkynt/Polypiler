using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Algebra;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Tests.Permutation.Algebra;

/// <summary>
/// Composition, inverse, power, cycles and order of bit permutations against the lists worked out by
/// hand in the test design of the algebra, with the evaluator as the oracle of every composed value.
/// </summary>
[TestFixture]
public sealed class BitPermutationAlgebraTests {

  private const string RotateLeft3 = "8:[5,6,7,0,1,2,3,4]";
  private const string Reverse8 = "8:[7,6,5,4,3,2,1,0]";

  private static BitPermutation P(string text) => BitPermutation.From(BitMapping.Parse(text));

  private static ulong Eval(BitPermutation permutation, ulong input) => MappingEvaluator.Evaluate(permutation.Mapping, input);

  /// <summary>
  /// Test design 1: reverse ∘ rotate-3. <c>result[i] = P1[P2[i]] = P1[7−i]</c>: i = 0 … 4 read P1[7] … P1[3]
  /// = 4, 3, 2, 1, 0 and i = 5 … 7 read P1[2] … P1[0] = 7, 6, 5.
  /// </summary>
  [Test]
  public void Compose_ReverseAfterRotation_IsTheHandDerivedList() {
    var (p1, p2) = (P(RotateLeft3), P(Reverse8));
    var composed = BitPermutationAlgebra.Compose(p2, p1);
    Assert.Multiple(() => {
      Assert.That(composed.Mapping, Is.EqualTo(BitMapping.Parse("8:[4,3,2,1,0,7,6,5]")));
      Assert.That(Eval(composed, 0xB4), Is.EqualTo(0xA5UL));
      Assert.That(Eval(composed, 0xB4), Is.EqualTo(Eval(p2, Eval(p1, 0xB4))));
    });
  }

  /// <summary>
  /// Test design 2: rotate-3 ∘ reverse. <c>result[i] = P2[P1[i]] = 7 − P1[i]</c> = 2, 1, 0, 7, 6, 5, 4, 3 —
  /// not the list of design 1, so the order of the operands is observable.
  /// </summary>
  [Test]
  public void Compose_RotationAfterReverse_DiffersFromTheOtherOrder() {
    var (p1, p2) = (P(RotateLeft3), P(Reverse8));
    var composed = BitPermutationAlgebra.Compose(p1, p2);
    Assert.Multiple(() => {
      Assert.That(composed.Mapping, Is.EqualTo(BitMapping.Parse("8:[2,1,0,7,6,5,4,3]")));
      Assert.That(Eval(composed, 0xB4), Is.EqualTo(0x69UL));
      Assert.That(Eval(composed, 0xB4), Is.EqualTo(Eval(p1, Eval(p2, 0xB4))));
      Assert.That(composed.Mapping, Is.Not.EqualTo(BitPermutationAlgebra.Compose(p2, p1).Mapping));
    });
  }

  /// <summary>The scenario "the inverse of a rotation cancels it": rotate-3 is undone by rotate-5, and its order is 8.</summary>
  [Test]
  public void Inverse_OfARotation_CancelsIt() {
    var p = P(RotateLeft3);
    var inverse = BitPermutationAlgebra.Inverse(p);
    Assert.Multiple(() => {
      Assert.That(inverse.Mapping, Is.EqualTo(BitMapping.Parse("8:[3,4,5,6,7,0,1,2]")));
      Assert.That(BitPermutationAlgebra.Compose(p, inverse).Mapping, Is.EqualTo(BitMapping.Identity(8)));
      Assert.That(BitPermutationAlgebra.Compose(inverse, p).Mapping, Is.EqualTo(BitMapping.Identity(8)));
      Assert.That(BitPermutationAlgebra.Order(p), Is.EqualTo(new BigInteger(8)));
      Assert.That(BitPermutationAlgebra.Cycles(p), Is.EqualTo(new[] { new[] { 0, 5, 2, 7, 4, 1, 6, 3 } }));
      Assert.That(BitPermutationAlgebra.IsInvolution(p), Is.False);
    });
  }

  /// <summary>Test design 8: width 1 is the identity, its own inverse, of order 1, one fixed point and an involution.</summary>
  [Test]
  public void Width1_IsItsOwnInverseOfOrder1() {
    var p = P("1:[0]");
    Assert.Multiple(() => {
      Assert.That(BitPermutationAlgebra.Inverse(p).Mapping, Is.EqualTo(p.Mapping));
      Assert.That(BitPermutationAlgebra.Order(p), Is.EqualTo(BigInteger.One));
      Assert.That(BitPermutationAlgebra.Cycles(p), Is.EqualTo(new[] { new[] { 0 } }));
      Assert.That(BitPermutationAlgebra.IsInvolution(p), Is.True);
      Assert.That(BitPermutationAlgebra.Compose(p, p).Mapping, Is.EqualTo(p.Mapping));
    });
  }

  /// <summary>Test design 11: four transpositions — 4 cycles of length 2, order 2, an involution.</summary>
  [Test]
  public void Cycles_OfAdjacentSwaps_AreFourTranspositions() {
    var p = P("8:[1,0,3,2,5,4,7,6]");
    Assert.Multiple(() => {
      Assert.That(BitPermutationAlgebra.Cycles(p), Is.EqualTo(new[] { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 } }));
      Assert.That(BitPermutationAlgebra.Order(p), Is.EqualTo(new BigInteger(2)));
      Assert.That(BitPermutationAlgebra.IsInvolution(p), Is.True);
      Assert.That(BitPermutationAlgebra.Inverse(p).Mapping, Is.EqualTo(p.Mapping));
    });
  }

  /// <summary>Cycles of mixed lengths start at their smallest element, are listed by it, include fixed points, and give the least common multiple.</summary>
  [Test]
  public void Cycles_OfMixedLengths_AreOrderedAndTheOrderIsTheirLcm() {
    var p = P("7:[2,4,3,0,1,5,6]");
    Assert.Multiple(() => {
      Assert.That(BitPermutationAlgebra.Cycles(p), Is.EqualTo(new[] { new[] { 0, 2, 3 }, new[] { 1, 4 }, new[] { 5 }, new[] { 6 } }));
      Assert.That(BitPermutationAlgebra.Order(p), Is.EqualTo(new BigInteger(6)));
      Assert.That(BitPermutationAlgebra.IsInvolution(p), Is.False);
    });
  }

  /// <summary>Power: 0 is the identity, 1 the permutation, 2 the self-composition, -1 the inverse, the order and its multiples the identity.</summary>
  [Test]
  public void Power_AgreesWithCompositionAndInverse() {
    var p = P("7:[2,4,3,0,1,5,6]");
    var identity = BitMapping.Identity(7);
    Assert.Multiple(() => {
      Assert.That(BitPermutationAlgebra.Power(p, 0).Mapping, Is.EqualTo(identity));
      Assert.That(BitPermutationAlgebra.Power(p, 1).Mapping, Is.EqualTo(p.Mapping));
      Assert.That(BitPermutationAlgebra.Power(p, 2).Mapping, Is.EqualTo(BitPermutationAlgebra.Compose(p, p).Mapping));
      Assert.That(BitPermutationAlgebra.Power(p, -1).Mapping, Is.EqualTo(BitPermutationAlgebra.Inverse(p).Mapping));
      Assert.That(BitPermutationAlgebra.Power(p, -2).Mapping, Is.EqualTo(BitPermutationAlgebra.Power(BitPermutationAlgebra.Inverse(p), 2).Mapping));
      Assert.That(BitPermutationAlgebra.Power(p, 6).Mapping, Is.EqualTo(identity));
      Assert.That(BitPermutationAlgebra.Power(p, 7).Mapping, Is.EqualTo(p.Mapping));
      Assert.That(BitPermutationAlgebra.Power(p, int.MinValue).Mapping, Is.EqualTo(BitPermutationAlgebra.Power(p, (long)int.MinValue % 6 + 6).Mapping));
      Assert.That(BitPermutationAlgebra.Power(p, BigInteger.Pow(6, 40) + 1).Mapping, Is.EqualTo(p.Mapping));
    });
  }

  /// <summary>Operations keep the layout of their operand; a composition keeps a shared layout and falls back to the default otherwise.</summary>
  [Test]
  public void Layout_IsKeptWhenShared() {
    var bytes = new BitLayout(8, ByteOrder.LittleEndian);
    var p = BitPermutation.From(BitMapping.Parse("16:[1,0,2,3,4,5,6,7,8,9,10,11,12,13,14,15]"), bytes);
    var plain = BitPermutation.From(BitMapping.Identity(16));
    Assert.Multiple(() => {
      Assert.That(BitPermutationAlgebra.Inverse(p).Layout, Is.EqualTo(bytes));
      Assert.That(BitPermutationAlgebra.Power(p, 3).Layout, Is.EqualTo(bytes));
      Assert.That(BitPermutationAlgebra.Compose(p, p).Layout, Is.EqualTo(bytes));
      Assert.That(BitPermutationAlgebra.Compose(p, plain).Layout, Is.EqualTo(BitLayout.Default(16, 16)));
    });
  }

  /// <summary>Test design 13 for permutations: widths that differ do not compose (<c>PLY0804</c>).</summary>
  [Test]
  public void Compose_OfDifferentWidths_IsRefusedWithPly0804() {
    var refusal = Assert.Throws<PermutationModelException>(() => BitPermutationAlgebra.Compose(P(Reverse8), P("4:[3,2,1,0]")))!;
    Assert.Multiple(() => {
      Assert.That(refusal.Diagnostic.Code, Is.EqualTo("PLY0804"));
      Assert.That(refusal.Diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error));
      Assert.That(refusal.Diagnostic.ExitCode, Is.EqualTo(ExitCode.UnsupportedConstruct));
      Assert.That(refusal.Message, Is.EqualTo("PLY0804: cannot compose: the inner mapping has 4 output bits, the outer mapping reads 8 input bits"));
    });
  }

  /// <summary>TryInverse of a bijection gives the inverse and no refusal.</summary>
  [Test]
  public void TryInverse_OfABijection_Succeeds() {
    Assert.Multiple(() => {
      Assert.That(BitPermutationAlgebra.TryInverse(BitMapping.Parse(RotateLeft3), out var inverse, out var refusal), Is.True);
      Assert.That(inverse!.Mapping, Is.EqualTo(BitMapping.Parse("8:[3,4,5,6,7,0,1,2]")));
      Assert.That(refusal, Is.Null);
    });
  }

  /// <summary>Test design 12: every way of not being a bijection is refused with <c>PLY0803</c> and the reason of rule H1.</summary>
  [TestCase("8:[0,0,2,3,4,5,6,7]", "input bit 0 is used 2 times")]
  [TestCase("2:[0,~1]", "output bit 1 is inverted")]
  [TestCase("2:[=1,1]", "output bit 0 is a constant")]
  [TestCase("2:[=0,1]", "output bit 0 is a constant")]
  [TestCase("2:[0,?]", "output bit 1 is a don't-care")]
  [TestCase("8:[0,2,4,6]", "widths differ (8 in, 4 out)")]
  public void TryInverse_OfANonBijection_IsRefusedWithPly0803(string text, string reason) {
    Assert.Multiple(() => {
      Assert.That(BitPermutationAlgebra.TryInverse(BitMapping.Parse(text), out var inverse, out var refusal), Is.False);
      Assert.That(inverse, Is.Null);
      Assert.That(refusal!.Code, Is.EqualTo("PLY0803"));
      Assert.That(refusal.Severity, Is.EqualTo(DiagnosticSeverity.Error));
      Assert.That(refusal.ExitCode, Is.EqualTo(ExitCode.UnsupportedConstruct));
      Assert.That(refusal.Message, Is.EqualTo($"mapping is not a bijection ({reason}); it has no inverse"));
    });
  }

  /// <summary>Every entry point refuses a missing argument.</summary>
  [Test]
  public void NullArguments_AreRefused() {
    var p = P(Reverse8);
    Assert.Multiple(() => {
      Assert.That(() => BitPermutationAlgebra.Compose(null!, p), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("outer"));
      Assert.That(() => BitPermutationAlgebra.Compose(p, null!), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("inner"));
      Assert.That(() => BitPermutationAlgebra.Inverse(null!), Throws.ArgumentNullException);
      Assert.That(() => BitPermutationAlgebra.TryInverse(null!, out _, out _), Throws.ArgumentNullException);
      Assert.That(() => BitPermutationAlgebra.Power(null!, 1), Throws.ArgumentNullException);
      Assert.That(() => BitPermutationAlgebra.Cycles(null!), Throws.ArgumentNullException);
      Assert.That(() => BitPermutationAlgebra.Order(null!), Throws.ArgumentNullException);
      Assert.That(() => BitPermutationAlgebra.IsInvolution(null!), Throws.ArgumentNullException);
    });
  }

  /// <summary>The inverse is offered on the permutation type only: no public member of the algebra inverts a routing or an untyped mapping except TryInverse.</summary>
  [Test]
  public void Inverse_IsOfferedForPermutationsOnly() {
    var inverting = typeof(BitPermutationAlgebra).Assembly.GetExportedTypes()
      .SelectMany(type => type.GetMethods())
      .Where(method => method.Name.Contains("Inverse", StringComparison.Ordinal))
      .Select(method => $"{method.DeclaringType!.Name}.{method.Name}({string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name))})");
    Assert.That(inverting, Is.EquivalentTo(new[] { "BitPermutationAlgebra.Inverse(BitPermutation)", "BitPermutationAlgebra.TryInverse(BitMapping,BitPermutation&,Diagnostic&)" }));
  }

}
