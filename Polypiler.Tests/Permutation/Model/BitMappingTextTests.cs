using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Tests.Permutation.Model;

/// <summary>The compact text notation of issue #268: reading, ranges, writing and every refusal.</summary>
[TestFixture]
public sealed class BitMappingTextTests {

  private static Diagnostic Refusal(string text) {
    var diagnostics = new DiagnosticBag();
    Assert.That(BitMapping.TryParse(text, diagnostics, out var mapping), Is.False);
    Assert.That(mapping, Is.Null);
    Assert.That(diagnostics.Items, Has.Count.EqualTo(1));
    var thrown = Assert.Throws<PermutationModelException>(() => BitMapping.Parse(text))!;
    Assert.That(thrown.Diagnostic, Is.EqualTo(diagnostics.Items[0]));
    return thrown.Diagnostic;
  }

  [TestCase("8:[7..0]", "8:[7,6,5,4,3,2,1,0]")]
  [TestCase("8:[0..3,7..4]", "8:[0,1,2,3,7,6,5,4]")]
  [TestCase("4:[2..2]", "4:[2]")]
  [TestCase(" 8 : [ 7 , ~6 , =0 , =1 , ? ] ", "8:[7,~6,=0,=1,?]")]
  [TestCase("8:[1..3,=1,~0]", "8:[1,2,3,=1,~0]")]
  public void Ranges_AndBlanks_ReadAsTheirExpansion(string text, string expanded)
    => Assert.That(BitMapping.Parse(text), Is.EqualTo(BitMapping.Parse(expanded)));

  /// <summary>Ranges are written only for runs of four or more plain sources, in either direction.</summary>
  [TestCase("8:[0,1,2]", "8:[0,1,2]")]
  [TestCase("8:[0,1,2,3]", "8:[0..3]")]
  [TestCase("8:[3,2,1,0,=1,?,~5,5]", "8:[3..0,=1,?,~5,5]")]
  [TestCase("8:[0,1,2,3,4,3,2,1]", "8:[0..4,3,2,1]")]
  [TestCase("8:[0,1,2,3,2,1,0,1]", "8:[0..3,2,1,0,1]")]
  [TestCase("8:[0,1,2,3,3,2,1,0]", "8:[0..3,3..0]")]
  [TestCase("8:[0,2,4,6]", "8:[0,2,4,6]")]
  [TestCase("8:[0,0,0,0,0]", "8:[0,0,0,0,0]")]
  [TestCase("8:[~0,~1,~2,~3]", "8:[~0,~1,~2,~3]")]
  [TestCase("8:[0,1,?,2,3,4,5]", "8:[0,1,?,2..5]")]
  [TestCase("8:[7]", "8:[7]")]
  public void Format_UsesRangesForRunsOfFour(string text, string written) {
    var mapping = BitMapping.Parse(text);
    Assert.That(mapping.ToString(), Is.EqualTo(written));
    Assert.That(BitMapping.Parse(written), Is.EqualTo(mapping));
  }

  /// <summary>Test design 10: malformed text, each refusal naming what is wrong.</summary>
  [TestCase("8:[7,,6]", "mapping text '8:[7,,6]': entry 2 is empty")]
  [TestCase("[7,6]", "mapping text '[7,6]': missing width")]
  [TestCase(":[7,6]", "mapping text ':[7,6]': missing width")]
  [TestCase("8:[a]", "mapping text '8:[a]': unknown entry 'a'")]
  [TestCase("8:[=2]", "mapping text '8:[=2]': unknown entry '=2': a constant is =0 or =1")]
  [TestCase("x:[0]", "mapping text 'x:[0]': width 'x' is not a number")]
  [TestCase("8:[0", "mapping text '8:[0': expected '<inputWidth>:[<entry>,...]'")]
  [TestCase("8:[0]]", "mapping text '8:[0]]': expected '<inputWidth>:[<entry>,...]'")]
  [TestCase("8:[~]", "mapping text '8:[~]': unknown entry '~'")]
  [TestCase("8:[~a]", "mapping text '8:[~a]': unknown entry '~a'")]
  [TestCase("8:[1...3]", "mapping text '8:[1...3]': unknown entry '1...3'")]
  [TestCase("8:[-1]", "mapping text '8:[-1]': unknown entry '-1'")]
  [TestCase("8:[0,]", "mapping text '8:[0,]': entry 2 is empty")]
  public void MalformedText_IsPLY0802(string text, string message) {
    var diagnostic = Refusal(text);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo(("PLY0802", message)));
  }

  [TestCase("8:[]", "mapping width 0 is outside 1..4096")]
  [TestCase("0:[0]", "mapping width 0 is outside 1..4096")]
  [TestCase("4097:[0]", "mapping width 4097 is outside 1..4096")]
  [TestCase("99999999999:[0]", "mapping width 99999999999 is outside 1..4096")]
  [TestCase("8:[0..4095,0]", "mapping width 4097 is outside 1..4096")]
  [TestCase("8:[0,0..4095]", "mapping width 4097 is outside 1..4096")]
  [TestCase("8:[2147483647..0]", "mapping width 2147483648 is outside 1..4096")]
  public void WidthOutOfRange_IsPLY0800(string text, string message) {
    var diagnostic = Refusal(text);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo(("PLY0800", message)));
  }

  [TestCase("8:[7,6,5,4,3,2,1,8]", "output bit 7 reads input bit 8, input width is 8")]
  [TestCase("8:[0,~99999999999]", "output bit 1 reads input bit 99999999999, input width is 8")]
  [TestCase("8:[99999999999]", "output bit 0 reads input bit 99999999999, input width is 8")]
  [TestCase("8:[0,99999999999..0]", "output bit 1 reads input bit 99999999999, input width is 8")]
  [TestCase("8:[0..99999999999]", "output bit 0 reads input bit 99999999999, input width is 8")]
  [TestCase("8:[0..8]", "output bit 8 reads input bit 8, input width is 8")]
  public void SourceOutOfRange_IsPLY0801(string text, string message) {
    var diagnostic = Refusal(text);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo(("PLY0801", message)));
  }

  /// <summary>Gherkin "A source index outside the input is refused", as rendered.</summary>
  [Test]
  public void SourceOutOfRange_RendersAsTheScenarioSays()
    => Assert.That(Assert.Throws<PermutationModelException>(() => BitMapping.Parse("8:[7,6,5,4,3,2,1,8]"))!.Message,
      Is.EqualTo("PLY0801: output bit 7 reads input bit 8, input width is 8"));

  [Test]
  public void Arguments_MustBeGiven() {
    Assert.Throws<ArgumentNullException>(() => BitMapping.Parse(null!));
    Assert.Throws<ArgumentNullException>(() => BitMapping.TryParse(null!, new DiagnosticBag(), out _));
    Assert.Throws<ArgumentNullException>(() => BitMapping.TryParse("1:[0]", null!, out _));
    Assert.Throws<ArgumentNullException>(() => BitMappingText.Format(null!));
  }

  [Test]
  public void TryParse_AcceptsAMapping() {
    var diagnostics = new DiagnosticBag();
    Assert.That(BitMapping.TryParse("3:[2..0]", diagnostics, out var mapping), Is.True);
    Assert.That(mapping, Is.EqualTo(BitMapping.Reverse(3)));
    Assert.That(diagnostics.Items, Is.Empty);
  }

  [Test]
  public void TheWidestText_RoundTrips() {
    var mapping = BitMapping.Parse("4096:[4095..0]");
    Assert.That(mapping, Is.EqualTo(BitMapping.Reverse(4096)));
    Assert.That(mapping.ToString(), Is.EqualTo("4096:[4095..0]"));
    Assert.That(BitMapping.Parse(string.Concat("1:[", string.Join(',', Enumerable.Repeat("?", 4096)), "]")).OutputWidth, Is.EqualTo(4096));
  }

}
