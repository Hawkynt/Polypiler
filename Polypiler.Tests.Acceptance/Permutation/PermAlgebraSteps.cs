using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Algebra;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Permutation;

/// <summary>
/// The steps of <c>tests/features/permutation/perm-algebra.feature</c>: composition, inversion,
/// distribution and the normal form, run in process on the typed values, with
/// <see cref="MappingEvaluator"/> as the oracle of every evaluated value.
/// </summary>
[Binding, Scope(Feature = "Canonical algebra of bit mappings")]
public sealed class PermAlgebraSteps {

  private BitMapping? _mapping;
  private BitPermutation? _p1;
  private BitPermutation? _p2;
  private BitPermutation? _result;
  private bool _answer;
  private Diagnostic? _refusal;
  private MappingShape? _shape;

  private BitMapping Mapping => this._mapping ?? throw new AssertionException("no mapping was given");

  private BitPermutation P1 => this._p1 ?? throw new AssertionException("no permutation was given");

  private BitPermutation Result => this._result ?? throw new AssertionException("nothing was computed");

  private static BitPermutation Permutation(string text) => BitPermutation.From(BitMapping.Parse(text));

  private static ulong Hex(string text) => ulong.Parse(text[2..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>Two permutations, the first applied first.</summary>
  [Given("^P1 = " + StepText.Quoted + @" \(rotate left by 3\) and P2 = " + StepText.Quoted + @" \(reverse\)$")]
  public void TwoPermutations(string p1, string p2) => (this._p1, this._p2) = (Permutation(p1), Permutation(p2));

  /// <summary>One permutation.</summary>
  [Given("^P = " + StepText.Quoted + "$")]
  public void OnePermutation(string p) => this._p1 = Permutation(p);

  /// <summary>An untyped mapping.</summary>
  [Given("^the mapping " + StepText.Quoted + "$")]
  public void MappingText(string text) => this._mapping = BitMapping.Parse(text);

  /// <summary>Composes P2 after P1.</summary>
  [When("^I compose outer P2 with inner P1$")]
  public void Compose() => this._result = BitPermutationAlgebra.Compose(this._p2!, this.P1);

  /// <summary>Inverts P.</summary>
  [When("^I invert it$")]
  public void Invert() => this._result = BitPermutationAlgebra.Inverse(this.P1);

  /// <summary>Inverts the untyped mapping through the only entry point that accepts one.</summary>
  [When("^TryInverse is called$")]
  public void TryInverse() {
    this._answer = BitPermutationAlgebra.TryInverse(this.Mapping, out var inverse, out this._refusal);
    this._result = inverse;
  }

  /// <summary>Asks whether XOR distributes.</summary>
  [When("^Distribution.OverXor is asked$")]
  public void OverXor() => this._answer = Distribution.OverXor(this.Mapping);

  /// <summary>Names the mapping.</summary>
  [When("^it is described$")]
  public void Describe() => this._shape = MappingShape.Describe(this.Mapping);

  /// <summary>Checks the composed permutation.</summary>
  [Then("^the result is " + StepText.Quoted + "$")]
  public void ResultIs(string expected) => Assert.That(this.Result.Mapping, Is.EqualTo(BitMapping.Parse(expected)));

  /// <summary>Checks the composition against the evaluator applied twice.</summary>
  [Then("^evaluating it on (0x[0-9A-F]+) gives (0x[0-9A-F]+), the same as reversing the rotation of (0x[0-9A-F]+)$")]
  public void EvaluatesAsComposition(string input, string output, string again) {
    Assert.That(MappingEvaluator.Evaluate(this.Result.Mapping, Hex(input)), Is.EqualTo(Hex(output)));
    Assert.That(MappingEvaluator.Evaluate(this._p2!.Mapping, MappingEvaluator.Evaluate(this.P1.Mapping, Hex(again))), Is.EqualTo(Hex(output)));
  }

  /// <summary>Checks the inverse.</summary>
  [Then("^the inverse is " + StepText.Quoted + "$")]
  public void InverseIs(string expected) => Assert.That(this.Result.Mapping, Is.EqualTo(BitMapping.Parse(expected)));

  /// <summary>Checks both compositions with the inverse.</summary>
  [Then("^composing P with its inverse in either order gives the identity$")]
  public void CancelsBothWays() {
    var identity = BitMapping.Identity(this.P1.Width);
    Assert.That(BitPermutationAlgebra.Compose(this.P1, this.Result).Mapping, Is.EqualTo(identity));
    Assert.That(BitPermutationAlgebra.Compose(this.Result, this.P1).Mapping, Is.EqualTo(identity));
  }

  /// <summary>Checks the order.</summary>
  [Then(@"^the order of P is (\d+)$")]
  public void OrderIs(int order) => Assert.That(BitPermutationAlgebra.Order(this.P1), Is.EqualTo(new BigInteger(order)));

  /// <summary>Checks the refusal of an inverse.</summary>
  [Then("^it returns false with " + StepText.Quoted + "$")]
  public void RefusedWith(string expected) {
    Assert.That(this._answer, Is.False);
    Assert.That(this._result, Is.Null);
    Assert.That($"{this._refusal!.Code}: {this._refusal.Message}", Is.EqualTo(expected));
  }

  /// <summary>Checks a negative answer.</summary>
  [Then("^it is false$")]
  public void IsFalse() => Assert.That(this._answer, Is.False);

  /// <summary>Checks the XOR counterexample and recomputes both sides with the evaluator.</summary>
  [Then(@"^the counterexample a = (\d+), b = (\d+) gives (\d+) for the routed XOR and (\d+) for the XOR of the routed values$")]
  public void XorCounterexample(int a, int b, int routed, int combined) {
    var example = Distribution.Counterexample(this.Mapping, BitwiseOperation.Xor);
    Assert.That(example, Is.EqualTo(new DistributionCounterexample(BitwiseOperation.Xor, a, b, routed, combined)));
    Assert.That(MappingEvaluator.Evaluate(this.Mapping, new BigInteger(a ^ b)), Is.EqualTo(new BigInteger(routed)));
    Assert.That(MappingEvaluator.Evaluate(this.Mapping, new BigInteger(a)) ^ MappingEvaluator.Evaluate(this.Mapping, new BigInteger(b)), Is.EqualTo(new BigInteger(combined)));
  }

  /// <summary>Checks the shape in its report notation.</summary>
  [Then("^the shape is " + StepText.Quoted + "$")]
  public void ShapeIs(string expected) => Assert.That(this._shape?.ToString(), Is.EqualTo(expected));

}
