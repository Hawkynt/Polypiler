using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;
using static Polypiler.Permutation.Semantics.BoolExpr;

namespace Polypiler.Tests.Permutation.Semantics;

/// <summary>
/// <see cref="BitBooleanTransform"/> of issue #267: creation, the widening from a routing, GF(2)
/// matrices (test design 3), the narrowing of rule H6 (test design 12), the truth table and its limit
/// (test design 6) and the internal error of a folding that disagrees with its truth table.
/// </summary>
[TestFixture]
public sealed class BitBooleanTransformTests {

  /// <summary>Gherkin "A Boolean expression that folds to a single input is a routing".</summary>
  [Test]
  public void FoldableTransform_NarrowsToARouting() {
    var transform = BitBooleanTransform.Create(4, [new Xor(new Input(3), new Const(true)), new And(new Input(0), new Input(0)), new Const(false), new Input(2)]);
    Assert.Multiple(() => {
      Assert.That(transform.TryAsRouting(out var routing, out var reason), Is.True);
      Assert.That(reason, Is.Null);
      Assert.That(routing!.Mapping, Is.EqualTo(BitMapping.Parse("4:[~3,0,=0,2]")));
      Assert.That(routing.Condition, Is.SameAs(transform.Condition));
      Assert.That(routing.Layout, Is.SameAs(transform.Layout));
      Assert.That(BitSemanticsClassifier.Classify(transform), Is.EqualTo(BitSemanticClass.Routing));
      Assert.That(BitSemanticsClassifier.Explain(transform), Is.EqualTo("output bit 0 is inverted"));
    });
  }

  /// <summary>Test design 3: the identity GF(2) matrix is a permutation, a row of two inputs and an And are Boolean transforms.</summary>
  [Test]
  public void BooleanClasses_FollowTheFolding() {
    var identity = BitBooleanTransform.FromGf2Matrix(4, [1, 2, 4, 8]);
    var twoInputs = BitBooleanTransform.FromGf2Matrix(4, [3, 2, 4, 8]);
    var and = BitBooleanTransform.Create(2, [new And(new Input(0), new Input(1)), new Input(1)]);
    Assert.Multiple(() => {
      Assert.That(identity.Outputs, Is.EqualTo(new BoolExpr[] { new Input(0), new Input(1), new Input(2), new Input(3) }));
      Assert.That(BitSemanticsClassifier.Classify(identity), Is.EqualTo(BitSemanticClass.Permutation));
      Assert.That(BitSemanticsClassifier.Explain(identity), Is.EqualTo("each of the 4 input bits is read exactly once"));
      Assert.That(twoInputs.Outputs[0], Is.EqualTo(new Xor(new Input(0), new Input(1))));
      Assert.That(BitSemanticsClassifier.Classify(twoInputs), Is.EqualTo(BitSemanticClass.BooleanTransform));
      Assert.That(BitSemanticsClassifier.Explain(twoInputs), Is.EqualTo("output bit 0 depends on 2 input bits"));
      Assert.That(BitSemanticsClassifier.Classify(and), Is.EqualTo(BitSemanticClass.BooleanTransform));
    });
  }

  [Test]
  public void Gf2Matrix_ChainsTheRowAndZeroIsAConstant() {
    var transform = BitBooleanTransform.FromGf2Matrix(3, [0b111, 0]);
    Assert.Multiple(() => {
      Assert.That(transform.Outputs, Is.EqualTo(new BoolExpr[] { new Xor(new Xor(new Input(0), new Input(1)), new Input(2)), new Const(false) }));
      Assert.That((transform.InputWidth, transform.OutputWidth), Is.EqualTo((3, 2)));
      Assert.That(transform.ToString(), Is.EqualTo("3:[Xor(Xor(Input 0, Input 1), Input 2), Const 0]"));
      Assert.Throws<ArgumentOutOfRangeException>(() => BitBooleanTransform.FromGf2Matrix(3, [1, -1]));
      Assert.Throws<ArgumentNullException>(() => BitBooleanTransform.FromGf2Matrix(3, null!));
      Assert.That(Assert.Throws<PermutationModelException>(() => BitBooleanTransform.FromGf2Matrix(3, []))!.Message, Is.EqualTo("PLY0800: mapping width 0 is outside 1..4096"));
    });
  }

  [Test]
  public void Create_ValidatesAndDefaults() {
    var transform = BitBooleanTransform.Create(8, [new Input(0), new Input(1)]);
    Assert.Multiple(() => {
      Assert.That(transform.Condition, Is.EqualTo(BitCondition.Unconstrained(8, 2)));
      Assert.That(transform.Layout, Is.EqualTo(new BitLayout(2, ByteOrder.NotApplicable)));
      Assert.Throws<ArgumentNullException>(() => BitBooleanTransform.Create(2, null!));
      Assert.Throws<ArgumentNullException>(() => BitBooleanTransform.Create(2, [new Input(0), null!]));
      Assert.That(Assert.Throws<PermutationModelException>(() => BitBooleanTransform.Create(4097, [new Input(0)]))!.Message, Is.EqualTo("PLY0800: mapping width 4097 is outside 1..4096"));
    });
  }

  /// <summary>Widening is lossless: the routing comes back unchanged, don't-cares, constants and inversions included.</summary>
  [TestCase("4:[~3,?,=0,=1]")]
  [TestCase("8:[0,0,~1,=1,?,7,6,5]")]
  [TestCase("8:[0,2,4,6]")]
  public void Routing_WidensAndNarrowsBackUnchanged(string text) {
    var routing = BitRouting.From(BitMapping.Parse(text), null, new BitLayout(1, ByteOrder.BigEndian));
    var transform = BitBooleanTransform.FromRouting(routing);
    Assert.Multiple(() => {
      Assert.That(transform.Condition, Is.SameAs(routing.Condition));
      Assert.That(transform.Layout, Is.SameAs(routing.Layout));
      Assert.That(transform.TryAsRouting(out var back, out _), Is.True);
      Assert.That(back!.Mapping, Is.EqualTo(routing.Mapping));
      Assert.That(back.Condition, Is.EqualTo(routing.Condition));
    });
  }

  [Test]
  public void FromRouting_MapsEachKind() {
    var transform = BitBooleanTransform.FromRouting(BitRouting.From(BitMapping.Parse("2:[1,~0,=1,=0,?]")));
    Assert.Multiple(() => {
      Assert.That(transform.Outputs, Is.EqualTo(new BoolExpr[] { new Input(1), new Not(new Input(0)), new Const(true), new Const(false), new Const(false) }));
      Assert.That(transform.Condition.Requires(4), Is.False, "the don't-care stays one");
      Assert.Throws<ArgumentNullException>(() => BitBooleanTransform.FromRouting(null!));
    });
  }

  /// <summary>An output the condition does not require is a don't-care whatever its expression.</summary>
  [Test]
  public void UnrequiredOutputs_BecomeDontCares() {
    var transform = BitBooleanTransform.Create(2, [new Xor(new Input(0), new Input(1)), new Input(1)], new BitCondition(0, 0, 0b10));
    Assert.That(transform.TryAsRouting(out var routing, out _), Is.True);
    Assert.That(routing!.Mapping, Is.EqualTo(BitMapping.Parse("2:[?,1]")));
  }

  /// <summary>H6 refusals: a folded output with one remaining input that is not a literal, and an input past the width.</summary>
  [Test]
  public void TryAsRouting_NamesWhyItRefuses() {
    Assert.Multiple(() => {
      Assert.That(BitBooleanTransform.Create(2, [new Input(0), new And(new Input(1), new Not(new Input(1)))]).TryAsRouting(out _, out var single), Is.False);
      Assert.That(single, Is.EqualTo("output bit 1 depends on 1 input bit"), "the listed rules do not fold And(x, Not x)");
      Assert.That(BitBooleanTransform.Create(4, [new Not(new Input(4))]).TryAsRouting(out _, out var outside), Is.False);
      Assert.That(outside, Is.EqualTo("output bit 0 reads input 4, input width is 4"));
      Assert.That(BitSemanticsClassifier.Explain(BitBooleanTransform.Create(4, [new Input(9)])), Is.EqualTo("output bit 0 reads input 9, input width is 4"));
    });
  }

  /// <summary>Test design 6: up to 16 inputs the table is built and agrees with the oracle; above it is PLY0807.</summary>
  [TestCase(1)]
  [TestCase(5)]
  [TestCase(6)]
  [TestCase(7)]
  [TestCase(16)]
  public void TruthTable_AgreesWithTheOracle(int width) {
    var outputs = new BoolExpr[] {
      new Input(width - 1),
      new Xor(new Input(0), new Input(width / 2)),
      new Or(new And(new Input(0), new Not(new Input(width - 1))), new Const(false)),
      new Input(width + 3)
    };
    var transform = BitBooleanTransform.Create(width, outputs);
    var diagnostics = new DiagnosticBag();
    Assert.That(transform.TryGetTruthTable(diagnostics, out var table), Is.True);
    Assert.That(diagnostics.Items, Is.Empty);
    Assert.That(table, Has.Length.EqualTo(1 << width));
    var stride = width > 10 ? 97 : 1;
    for (long x = 0; x < table!.Length; x += stride)
      if (table[x] != BoolOracle.Outputs(transform, x))
        Assert.Fail($"input {x}: table {table[x]}, oracle {BoolOracle.Outputs(transform, x)}");
    Assert.That(table[^1], Is.EqualTo(BoolOracle.Outputs(transform, table.Length - 1)));
  }

  [Test]
  public void TruthTable_Of17Inputs_IsPLY0807() {
    var transform = BitBooleanTransform.Create(17, [new Input(16)]);
    var diagnostics = new DiagnosticBag();
    Assert.Multiple(() => {
      Assert.That(transform.TryGetTruthTable(diagnostics, out var table), Is.False);
      Assert.That(table, Is.Null);
      Assert.That(diagnostics.Items.Select(item => $"{item.Code}: {item.Message}"), Is.EqualTo(new[] { "PLY0807: truth table of 17 inputs exceeds the limit of 16" }));
      Assert.Throws<ArgumentNullException>(() => transform.TryGetTruthTable(null!, out _));
    });
  }

  /// <summary>Test design 6: at 16 inputs the folding is cross-checked, at 17 it is structural only; both narrow.</summary>
  [TestCase(16)]
  [TestCase(17)]
  public void Narrowing_AtTheCrossCheckLimit(int width) {
    var transform = BitBooleanTransform.Create(width, [new Xor(new Input(width - 1), new Const(true)), new Or(new Input(0), new Input(0))]);
    Assert.That(transform.TryAsRouting(out var routing, out _), Is.True);
    Assert.That(routing!.Mapping.Outputs, Is.EqualTo(new[] { BitSource.Not(width - 1), BitSource.From(0) }));
  }

  /// <summary>A folding that disagreed with the truth table would be an internal error, never a silent choice.</summary>
  [Test]
  public void CrossCheck_RefusesAWrongFolding() {
    Assert.DoesNotThrow(() => TruthTable.CrossCheck(new Xor(new Input(2), new Const(true)), new Not(new Input(2)), 3, 0));
    var refusal = Assert.Throws<PermutationModelException>(() => TruthTable.CrossCheck(new And(new Input(0), new Input(1)), new Input(0), 2, 5))!;
    Assert.That(refusal.Message, Is.EqualTo("PLY0001: internal compiler error: output bit 5: And(Input 0, Input 1) folds to Input 0, which disagrees with its truth table over 2 inputs"));
  }

  /// <summary>Measurement "structural vs truth-table disagreement": 10 000 seeded random transforms, at most 16 inputs, seed 805.</summary>
  [Test, Category("Slow")]
  public void RandomTransforms_NeverDisagreeWithTheOracle() {
    var random = new Random(805);
    var (routings, refused, incomplete) = (0, 0, 0);
    for (var round = 0; round < 10_000; ++round) {
      var width = round % 10 == 0 ? random.Next(9, 17) : random.Next(1, 9);
      var expression = RandomExpression(random, width, random.Next(0, 5));
      var transform = BitBooleanTransform.Create(width, [expression]);
      if (transform.TryAsRouting(out var routing, out _)) {
        ++routings;
        var entry = routing.Mapping.Outputs[0];
        BoolExpr literal = entry.Kind switch {
          BitSourceKind.Source => new Input(entry.Index),
          BitSourceKind.InvertedSource => new Not(new Input(entry.Index)),
          _ => new Const(entry.Kind == BitSourceKind.One)
        };
        if (!BoolOracle.Equivalent(expression, literal, width))
          Assert.Fail($"seed 805, round {round}: {expression} narrowed to {entry} over {width} inputs");
      } else {
        ++refused;
        if (width <= 10 && BoolOracle.EssentialInputs(expression, width) <= 1)
          ++incomplete;
      }
      if (width <= 10 && transform.TryGetTruthTable(new DiagnosticBag(), out var table))
        for (long x = 0; x < table.Length; ++x)
          if (table[x] != BoolOracle.Outputs(transform, x))
            Assert.Fail($"seed 805, round {round}: truth table of {expression} at {x}");
    }
    TestContext.Out.WriteLine($"10000 transforms: {routings} narrowed, {refused} refused ({incomplete} of them single-input or constant functions the listed rules do not fold); 0 disagreements");
    Assert.That(routings + refused, Is.EqualTo(10_000));
  }

  private static BoolExpr RandomExpression(Random random, int width, int depth) {
    if (depth == 0 || random.Next(4) == 0)
      return random.Next(5) == 0 ? new Const(random.Next(2) == 1) : new Input(random.Next(width));

    var left = RandomExpression(random, width, depth - 1);
    var right = random.Next(4) == 0 ? left : RandomExpression(random, width, depth - 1);
    return random.Next(4) switch {
      0 => new Not(left),
      1 => new And(left, right),
      2 => new Or(left, right),
      _ => new Xor(left, right)
    };
  }

}
