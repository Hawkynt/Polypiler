using System;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Tests.Permutation.Semantics;

/// <summary>
/// <see cref="BitPermutation"/> and <see cref="BitRouting"/> of issue #267: creation, the widening
/// conversion and the checked narrowing by rule H1.
/// </summary>
[TestFixture]
public sealed class BitPermutationTests {

  /// <summary>Gherkin "A reversal narrows to a bit permutation".</summary>
  [Test]
  public void Reversal_IsAPermutation() {
    var mapping = BitMapping.Parse("8:[7,6,5,4,3,2,1,0]");
    var permutation = BitPermutation.From(mapping);
    Assert.Multiple(() => {
      Assert.That(permutation.Mapping, Is.SameAs(mapping));
      Assert.That((permutation.Width, permutation.InputWidth, permutation.OutputWidth), Is.EqualTo((8, 8, 8)));
      Assert.That(permutation.Layout, Is.EqualTo(new BitLayout(8, ByteOrder.NotApplicable)));
      Assert.That(permutation.ToString(), Is.EqualTo("8:[7..0]"));
      Assert.That(BitSemanticsVerifier.Check(permutation), Is.Empty);
    });
  }

  [Test]
  public void From_KeepsAGivenLayout() {
    var layout = new BitLayout(4, ByteOrder.BigEndian);
    Assert.That(BitPermutation.From(BitMapping.Identity(8), layout).Layout, Is.SameAs(layout));
  }

  /// <summary>Gherkin "A mapping that duplicates a bit is a routing and cannot be declared a permutation".</summary>
  [Test]
  public void From_RefusesADuplication() {
    var refusal = Assert.Throws<PermutationModelException>(() => BitPermutation.From(BitMapping.Parse("8:[0,?,0,?,0,?,?,?]")))!;
    Assert.Multiple(() => {
      Assert.That(refusal.Message, Is.EqualTo("PLY0805: mapping is declared BitPermutation but input bit 0 is used 3 times"));
      Assert.That(refusal.Diagnostic.ExitCode, Is.EqualTo(ExitCode.UnsupportedConstruct));
      Assert.That(refusal.Diagnostic.Rule, Is.EqualTo("permutation.hierarchy.class"));
    });
  }

  [Test]
  public void From_AndTryFrom_RefuseNull() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => BitPermutation.From(null!));
      Assert.Throws<ArgumentNullException>(() => BitPermutation.TryFrom(null!, new DiagnosticBag(), out _));
      Assert.Throws<ArgumentNullException>(() => BitPermutation.TryFrom(BitMapping.Identity(2), null!, out _));
    });
  }

  /// <summary>Widening is lossless: same mapping and lanes, the condition the mapping implies.</summary>
  [Test]
  public void Permutation_WidensToARouting() {
    var permutation = BitPermutation.From(BitMapping.SwapHalves(8), new BitLayout(4, ByteOrder.LittleEndian));
    BitRouting routing = permutation;
    Assert.Multiple(() => {
      Assert.That(routing.Mapping, Is.SameAs(permutation.Mapping));
      Assert.That(routing.Layout, Is.SameAs(permutation.Layout));
      Assert.That(routing.Condition, Is.EqualTo(new BitCondition(0, 0, 0xFF)));
      Assert.That(routing.TryAsPermutation(out var back, out var reason), Is.True);
      Assert.That(back!.Mapping, Is.SameAs(permutation.Mapping));
      Assert.That(back.Layout, Is.SameAs(permutation.Layout));
      Assert.That(reason, Is.Null);
      Assert.Throws<ArgumentNullException>(() => { BitRouting _ = (BitPermutation)null!; });
    });
  }

  /// <summary>Test design 2: each cause of non-bijectivity refuses the narrowing with its H1 reason.</summary>
  [TestCase("2:[0,0]", "input bit 0 is used 2 times")]
  [TestCase("4:[~0,1,2,3]", "output bit 0 is inverted")]
  [TestCase("2:[0,=1]", "output bit 1 is a constant")]
  [TestCase("2:[0,?]", "output bit 1 is a don't-care")]
  [TestCase("8:[0,2,4,6]", "widths differ (8 in, 4 out)")]
  public void Routing_NarrowsOnlyByH1(string text, string expected) {
    var routing = BitRouting.From(BitMapping.Parse(text));
    Assert.Multiple(() => {
      Assert.That(routing.TryAsPermutation(out var permutation, out var reason), Is.False);
      Assert.That(permutation, Is.Null);
      Assert.That(reason, Is.EqualTo(expected));
    });
  }

  /// <summary>A routing defaults to the condition and the lanes of its mapping and keeps what it is given.</summary>
  [Test]
  public void Routing_DefaultsAndKeepsItsCondition() {
    var mapping = BitMapping.Parse("8:[0,2,4,6]");
    var routing = BitRouting.From(mapping);
    var condition = new BitCondition(1, 2, 0xF);
    var layout = new BitLayout(2, ByteOrder.NotApplicable);
    var given = BitRouting.From(mapping, condition, layout);
    Assert.Multiple(() => {
      Assert.That((routing.InputWidth, routing.OutputWidth), Is.EqualTo((8, 4)));
      Assert.That(routing.Condition, Is.EqualTo(new BitCondition(BigInteger.Zero, BigInteger.Zero, new BigInteger(0xF))));
      Assert.That(routing.Layout, Is.EqualTo(new BitLayout(4, ByteOrder.NotApplicable)), "gcd(8, 4)");
      Assert.That(routing.ToString(), Is.EqualTo("8:[0,2,4,6]"));
      Assert.That(given.Condition, Is.SameAs(condition));
      Assert.That(given.Layout, Is.SameAs(layout));
      Assert.Throws<ArgumentNullException>(() => BitRouting.From(null!));
    });
  }

  /// <summary>A conditioned bijective routing narrows to a permutation, which holds on every input and so on the domain too.</summary>
  [Test]
  public void ConditionedRouting_NarrowsByH1Alone() {
    var routing = BitRouting.From(BitMapping.Reverse(4), new BitCondition(1, 2, 0xF));
    Assert.That(routing.TryAsPermutation(out var permutation, out _), Is.True);
    Assert.That(permutation!.Mapping, Is.EqualTo(BitMapping.Reverse(4)));
  }

}
