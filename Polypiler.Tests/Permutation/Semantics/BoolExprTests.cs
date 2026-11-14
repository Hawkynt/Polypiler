using System;
using NUnit.Framework;
using Polypiler.Permutation.Semantics;
using static Polypiler.Permutation.Semantics.BoolExpr;

namespace Polypiler.Tests.Permutation.Semantics;

/// <summary>
/// <see cref="BoolExpr"/> of issue #267: the text form, validation, the inputs an expression mentions
/// and every folding rule — each held to the oracle, so a rule that changed the function would fail.
/// </summary>
[TestFixture]
public sealed class BoolExprTests {

  private static readonly BoolExpr _a = new Input(0);
  private static readonly BoolExpr _b = new Input(1);
  private static readonly BoolExpr _ab = new And(_a, _b);
  private static readonly BoolExpr _zero = new Const(false);
  private static readonly BoolExpr _one = new Const(true);

  [Test]
  public void ToString_IsTheNotationOfTheIssue() {
    Assert.Multiple(() => {
      Assert.That(new Xor(new Input(3), new Const(true)).ToString(), Is.EqualTo("Xor(Input 3, Const 1)"));
      Assert.That(new Or(new Not(_a), new And(_b, _zero)).ToString(), Is.EqualTo("Or(Not(Input 0), And(Input 1, Const 0))"));
    });
  }

  [Test]
  public void Construction_RefusesNegativeInputsAndMissingOperands() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentOutOfRangeException>(() => _ = new Input(-1));
      Assert.Throws<ArgumentNullException>(() => _ = new Not(null!));
      Assert.Throws<ArgumentNullException>(() => _ = new And(null!, _a));
      Assert.Throws<ArgumentNullException>(() => _ = new And(_a, null!));
      Assert.Throws<ArgumentNullException>(() => _ = new Or(null!, _a));
      Assert.Throws<ArgumentNullException>(() => _ = new Or(_a, null!));
      Assert.Throws<ArgumentNullException>(() => _ = new Xor(null!, _a));
      Assert.Throws<ArgumentNullException>(() => _ = new Xor(_a, null!));
    });
  }

  [Test]
  public void Equality_IsStructural() {
    Assert.Multiple(() => {
      Assert.That(new And(new Input(0), new Input(1)), Is.EqualTo(_ab));
      Assert.That(new And(new Input(1), new Input(0)), Is.Not.EqualTo(_ab), "no commutativity is assumed");
      Assert.That(new Or(_a, _b), Is.Not.EqualTo(_ab));
    });
  }

  [Test]
  public void Inputs_AreTheMentionedBitsInOrder() {
    var expression = new Xor(new Or(new Input(5), new Not(new Input(2))), new And(new Const(true), new Input(5)));
    Assert.That(expression.Inputs(), Is.EqualTo(new[] { 2, 5 }));
    Assert.That(new Const(false).Inputs(), Is.Empty);
  }

  /// <summary>Every listed rule, in both operand positions, and the cases where nothing folds.</summary>
  public static TestCaseData[] Rules() => [
    new(new Not(_zero), _one),
    new(new Not(_one), _zero),
    new(new Not(new Not(_ab)), _ab),
    new(new Not(_a), new Not(_a)),
    new(new And(_zero, _a), _zero),
    new(new And(_a, _zero), _zero),
    new(new And(_one, _a), _a),
    new(new And(_a, _one), _a),
    new(new And(_ab, new And(_a, _b)), _ab),
    new(_ab, _ab),
    new(new Or(_one, _a), _one),
    new(new Or(_a, _one), _one),
    new(new Or(_zero, _a), _a),
    new(new Or(_a, _zero), _a),
    new(new Or(_a, _a), _a),
    new(new Or(_a, _b), new Or(_a, _b)),
    new(new Xor(_zero, _a), _a),
    new(new Xor(_a, _zero), _a),
    new(new Xor(_one, _a), new Not(_a)),
    new(new Xor(_a, _one), new Not(_a)),
    new(new Xor(new Not(_a), _one), _a),
    new(new Xor(_ab, _ab), _zero),
    new(new Xor(_a, _b), new Xor(_a, _b)),
    new(new Xor(new Input(3), new Const(true)), new Not(new Input(3))),
    new(new And(_a, new Not(_a)), new And(_a, new Not(_a))),
    new(_one, _one),
  ];

  [TestCaseSource(nameof(Rules))]
  public void Fold_AppliesExactlyTheListedRules(BoolExpr expression, BoolExpr expected) {
    var folded = expression.Fold();
    Assert.Multiple(() => {
      Assert.That(folded, Is.EqualTo(expected));
      Assert.That(BoolOracle.Equivalent(expression, folded, 2), Is.True, "folding keeps the function");
    });
  }

}
