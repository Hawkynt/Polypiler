using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;
using static Polypiler.Permutation.Semantics.BoolExpr;

namespace Polypiler.Tests.Permutation.Semantics;

/// <summary>
/// The verifier rules H1–H6 of issue #267, a negative test for each (test design 9 to 12) and the
/// values that pass all of them.
/// </summary>
[TestFixture]
public sealed class BitSemanticsVerifierTests {

  private static string[] Messages(IBitSemantics semantics) => [.. BitSemanticsVerifier.Check(semantics).Select(item => $"{item.Code}: {item.Message}")];

  [Test]
  public void SoundValues_PassEveryRule() {
    Assert.Multiple(() => {
      Assert.That(Messages(BitPermutation.From(BitMapping.Identity(16))), Is.Empty);
      Assert.That(Messages(BitRouting.From(BitMapping.Parse("4:[0,?,=1,~3]"))), Is.Empty);
      Assert.That(Messages(BitBooleanTransform.FromGf2Matrix(4, [3, 6, 12, 9])), Is.Empty);
      Assert.Throws<ArgumentNullException>(() => BitSemanticsVerifier.Check(null!));
    });
  }

  /// <summary>H1 negative: <see cref="BitSemanticsVerifier.H1"/> is the rule both <see cref="BitPermutation.From"/> and the check run.</summary>
  [Test]
  public void H1_RefusesAMappingThatIsNoBijection() {
    Assert.That(BitSemanticsVerifier.H1(BitMapping.Parse("4:[0,1,2,2]")).Select(item => item.Message),
      Is.EqualTo(new[] { "mapping is declared BitPermutation but input bit 2 is used 2 times" }));
    Assert.That(BitSemanticsVerifier.H1(BitMapping.Identity(4)), Is.Empty);
  }

  /// <summary>Test design 9 (H2): known zero and known one on the same bit.</summary>
  [Test]
  public void H2_ReportsAContradictoryCondition() {
    var routing = BitRouting.From(BitMapping.Identity(8), new BitCondition(0x01, 0x01, 0xFF));
    Assert.That(Messages(routing), Is.EqualTo(new[] { "PLY0805: condition is contradictory on input bits 0x1" }));
  }

  [TestCase(0x100, 0, 0xFF, "condition mask exceeds width 8")]
  [TestCase(0, 0x100, 0xFF, "condition mask exceeds width 8")]
  [TestCase(0, 0, 0x1FF, "condition mask exceeds width 8")]
  [TestCase(0, -2, 0xFF, "condition mask exceeds width 8")]
  [TestCase(0, 0x80, 0xFF, null)]
  public void H2_ReportsAMaskWiderThanItsWidth(long zero, long one, long required, string? expected) {
    var routing = BitRouting.From(BitMapping.Identity(8), new BitCondition(zero, one, required));
    Assert.That(Messages(routing), Is.EqualTo(expected is null ? Array.Empty<string>() : ["PLY0805: " + expected]));
  }

  /// <summary>H2 on a non-square transform names the width of the side whose mask is too wide.</summary>
  [Test]
  public void H2_NamesTheInputAndTheOutputWidth() {
    var transform = BitBooleanTransform.Create(4, [new Input(0), new Input(1)], new BitCondition(-1, -1, 0x7), new BitLayout(2, ByteOrder.NotApplicable));
    Assert.That(Messages(transform), Is.EqualTo(new[] {
      "PLY0805: condition mask exceeds width 4",
      "PLY0805: condition mask exceeds width 2",
      "PLY0805: condition is contradictory on input bits -0x1"
    }));
  }

  /// <summary>Test design 10 (H3) and its converse.</summary>
  [Test]
  public void H3_HoldsTheConditionToTheDontCares() {
    var mapping = BitMapping.Parse("4:[0,?,2,3]");
    Assert.Multiple(() => {
      Assert.That(Messages(BitRouting.From(mapping, new BitCondition(0, 0, 0xF))),
        Is.EqualTo(new[] { "PLY0805: output bit 1 is don't-care in the mapping but required by the condition" }));
      Assert.That(Messages(BitRouting.From(mapping, new BitCondition(0, 0, 0x9))),
        Is.EqualTo(new[] { "PLY0805: output bit 2 is fixed in the mapping but not required by the condition" }));
      Assert.That(Messages(BitRouting.From(mapping, new BitCondition(0, 0, 0xD))), Is.Empty);
    });
  }

  /// <summary>Test design 11 (H4): input 4 of a 4-bit transform; each output names its smallest missing input.</summary>
  [Test]
  public void H4_ReportsAnInputPastTheWidth() {
    var transform = BitBooleanTransform.Create(4, [new Input(4), new Input(3), new And(new Input(7), new Input(5))]);
    Assert.That(Messages(transform), Is.EqualTo(new[] {
      "PLY0805: output bit 0 reads input 4, input width is 4",
      "PLY0805: output bit 2 reads input 5, input width is 4"
    }));
  }

  /// <summary>Test design 7 (H5): lane 12 on width 32, and both sides of a non-square value.</summary>
  [Test]
  public void H5_ReportsALaneSizeThatDoesNotDivide() {
    Assert.Multiple(() => {
      Assert.That(Messages(BitPermutation.From(BitMapping.Identity(32), new BitLayout(12, ByteOrder.NotApplicable))),
        Is.EqualTo(new[] { "PLY0805: lane size 12 does not divide width 32" }));
      Assert.That(Messages(BitPermutation.From(BitMapping.Identity(32), new BitLayout(8, ByteOrder.LittleEndian))), Is.Empty);
      Assert.That(Messages(BitRouting.From(BitMapping.Parse("8:[0,2,4,6,1,3]"), null, new BitLayout(4, ByteOrder.NotApplicable))),
        Is.EqualTo(new[] { "PLY0805: lane size 4 does not divide width 6" }));
      Assert.That(Messages(BitRouting.From(BitMapping.Parse("6:[0,2,4,1]"), null, new BitLayout(4, ByteOrder.NotApplicable))),
        Is.EqualTo(new[] { "PLY0805: lane size 4 does not divide width 6" }));
    });
  }

  /// <summary>Test design 12 (H6): a true Boolean transform does not narrow.</summary>
  [Test]
  public void H6_RefusesToNarrowATrueBooleanTransform() {
    var transform = BitBooleanTransform.Create(2, [new Xor(new Input(0), new Input(1)), new Input(1)]);
    Assert.Multiple(() => {
      Assert.That(transform.TryAsRouting(out var routing, out var reason), Is.False);
      Assert.That(routing, Is.Null);
      Assert.That(reason, Is.EqualTo("output bit 0 depends on 2 input bits"));
      Assert.That(Messages(transform), Is.Empty, "the transform itself is sound");
    });
  }

  [Test]
  public void Diagnostics_CarryTheirExitCodeAndRule() {
    var found = BitSemanticsVerifier.Check(BitRouting.From(BitMapping.Identity(2), new BitCondition(1, 1, 3)));
    Assert.That(found.Select(item => (item.ExitCode, item.Rule, item.Severity)),
      Is.EqualTo(new[] { ((ExitCode?)ExitCode.UnsupportedConstruct, (string?)"permutation.hierarchy.class", DiagnosticSeverity.Error) }));
  }

}
