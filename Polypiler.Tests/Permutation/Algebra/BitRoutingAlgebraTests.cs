using System.Numerics;
using NUnit.Framework;
using Polypiler.Permutation.Algebra;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Tests.Permutation.Algebra;

/// <summary>Composition of routings, entry kind by entry kind, with the evaluator as the oracle.</summary>
[TestFixture]
public sealed class BitRoutingAlgebraTests {

  private const string Mixed = "8:[0,0,~1,=1,?,7,6,5]";

  private static BitRouting R(string text) => BitRouting.From(BitMapping.Parse(text));

  /// <summary>
  /// Test design 3, reverse ∘ R: output i reads R[7−i], so the list is R backwards,
  /// <c>[5,6,7,?,=1,~1,0,0]</c>.
  /// </summary>
  [Test]
  public void Compose_ReverseAfterRouting_ReversesTheEntries() {
    var (outer, inner) = (R("8:[7,6,5,4,3,2,1,0]"), R(Mixed));
    var composed = BitRoutingAlgebra.Compose(outer, inner);
    Assert.Multiple(() => {
      Assert.That(composed.Mapping, Is.EqualTo(BitMapping.Parse("8:[5,6,7,?,=1,~1,0,0]")));
      for (var x = 0UL; x < 256; ++x)
        Assert.That(MappingEvaluator.AgreesOnRequired(composed.Mapping, x, MappingEvaluator.Evaluate(outer.Mapping, MappingEvaluator.Evaluate(inner.Mapping, x))), Is.True, $"x = {x}");
    });
  }

  /// <summary>
  /// Test design 3, R ∘ reverse: each source j of R becomes 7 − j, the inverted ~1 becomes ~6, and the
  /// constant and the don't-care stay: <c>[7,7,~6,=1,?,0,1,2]</c>; 0xB4 gives 0x8F.
  /// </summary>
  [Test]
  public void Compose_RoutingAfterReverse_RenumbersTheSources() {
    var (outer, inner) = (R(Mixed), R("8:[7,6,5,4,3,2,1,0]"));
    var composed = BitRoutingAlgebra.Compose(outer, inner);
    Assert.Multiple(() => {
      Assert.That(composed.Mapping, Is.EqualTo(BitMapping.Parse("8:[7,7,~6,=1,?,0,1,2]")));
      Assert.That(MappingEvaluator.Evaluate(composed.Mapping, 0xB4UL), Is.EqualTo(0x8FUL));
      Assert.That(MappingEvaluator.Evaluate(composed.Mapping, 0xB4UL), Is.EqualTo(MappingEvaluator.Evaluate(outer.Mapping, MappingEvaluator.Evaluate(inner.Mapping, 0xB4UL))));
    });
  }

  /// <summary>Test design 4 and the rest of the complement: an inverted outer entry flips every kind of inner entry, and a don't-care stays one.</summary>
  [TestCase("1:[=0]", "1:[=1]")]
  [TestCase("1:[=1]", "1:[=0]")]
  [TestCase("1:[?]", "1:[?]")]
  [TestCase("1:[0]", "1:[~0]")]
  [TestCase("1:[~0]", "1:[0]")]
  public void Compose_InvertedOuterEntry_ComplementsTheInnerEntry(string inner, string expected)
    => Assert.That(BitRoutingAlgebra.Compose(R("1:[~0]"), R(inner)).Mapping, Is.EqualTo(BitMapping.Parse(expected)));

  /// <summary>Outer constants and don't-cares do not read the inner routing; the widths follow the inner input and the outer output.</summary>
  [Test]
  public void Compose_KeepsOuterConstantsAndTakesTheOuterWidths() {
    var composed = BitRoutingAlgebra.Compose(R("4:[=0,=1,?,3,2]"), R("8:[0,2,4,6]"));
    Assert.Multiple(() => {
      Assert.That(composed.Mapping, Is.EqualTo(BitMapping.Parse("8:[=0,=1,?,6,4]")));
      Assert.That(composed.InputWidth, Is.EqualTo(8));
      Assert.That(composed.OutputWidth, Is.EqualTo(5));
    });
  }

  /// <summary>The composed condition keeps the inner input condition and requires exactly the outputs that are not don't-cares.</summary>
  [Test]
  public void Compose_KeepsTheInnerInputCondition() {
    var innerMapping = BitMapping.Parse("4:[0,1,?,3]");
    var inner = BitRouting.From(innerMapping, new BitCondition(new BigInteger(0x4), new BigInteger(0x1), innerMapping.RequiredOutputMask));
    var outerMapping = BitMapping.Parse("4:[2,3,0,1]");
    var outer = BitRouting.From(outerMapping, new BitCondition(new BigInteger(0x8), BigInteger.Zero, outerMapping.RequiredOutputMask));
    var composed = BitRoutingAlgebra.Compose(outer, inner);
    Assert.Multiple(() => {
      Assert.That(composed.Mapping, Is.EqualTo(BitMapping.Parse("4:[?,3,0,1]")));
      Assert.That(composed.Condition, Is.EqualTo(new BitCondition(new BigInteger(0x4), new BigInteger(0x1), new BigInteger(0xE))));
      Assert.That(BitSemanticsVerifier.Check(composed), Is.Empty);
    });
  }

  /// <summary>A shared layout is kept, different layouts give the default one.</summary>
  [Test]
  public void Compose_KeepsASharedLayout() {
    var bytes = new BitLayout(8, ByteOrder.BigEndian);
    var mapping = BitMapping.Identity(16);
    var laned = BitRouting.From(mapping, null, bytes);
    Assert.Multiple(() => {
      Assert.That(BitRoutingAlgebra.Compose(laned, laned).Layout, Is.EqualTo(bytes));
      Assert.That(BitRoutingAlgebra.Compose(laned, BitRouting.From(mapping)).Layout, Is.EqualTo(BitLayout.Default(16, 16)));
    });
  }

  /// <summary>A permutation composes as a routing through the implicit widening.</summary>
  [Test]
  public void Compose_AcceptsPermutations() {
    var composed = BitRoutingAlgebra.Compose(BitPermutation.From(BitMapping.Reverse(4)), R("4:[=1,0,0,~3]"));
    Assert.That(composed.Mapping, Is.EqualTo(BitMapping.Parse("4:[~3,0,0,=1]")));
  }

  /// <summary>Test design 13: the inner routing has 4 outputs, the outer one reads 8 (<c>PLY0804</c>).</summary>
  [Test]
  public void Compose_WithMismatchedWidths_IsRefusedWithPly0804() {
    var refusal = Assert.Throws<PermutationModelException>(() => BitRoutingAlgebra.Compose(R("8:[7,6,5,4,3,2,1,0]"), R("8:[0,2,4,6]")))!;
    Assert.That(refusal.Message, Is.EqualTo("PLY0804: cannot compose: the inner mapping has 4 output bits, the outer mapping reads 8 input bits"));
  }

  /// <summary>Missing operands are refused.</summary>
  [Test]
  public void NullArguments_AreRefused() {
    var routing = R("1:[0]");
    Assert.Multiple(() => {
      Assert.That(() => BitRoutingAlgebra.Compose(null!, routing), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("outer"));
      Assert.That(() => BitRoutingAlgebra.Compose(routing, null!), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("inner"));
    });
  }

}
