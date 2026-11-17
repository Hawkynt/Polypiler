using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Options;

namespace Polypiler.Tests.Options;

/// <summary>The fact grammar of <c>--assume</c> (issue #179, test design 3-9, 12-15, 26 and the round-trip measurement).</summary>
[TestFixture]
public sealed class AssumptionParserTests {

  private static Assumption Parsed(string text) {
    Assert.That(AssumptionParser.TryParse(text, out var result, out var error), Is.True, error);
    Assert.That(error, Is.Null);
    return result!;
  }

  private static string Refused(string text) {
    Assert.That(AssumptionParser.TryParse(text, out var result, out var error), Is.False);
    Assert.That(result, Is.Null);
    return error!;
  }

  /// <summary>Test design 3: each kind of fact.</summary>
  [Test]
  public void EachKind_IsRecognized() {
    Assert.That(Parsed("a in [0, 9]"), Is.EqualTo(new Assumption(AssumptionKind.Range, "a", 0, 9, null, null, "a in [0, 9]")));
    Assert.That(Parsed("a != null"), Is.EqualTo(new Assumption(AssumptionKind.NonNull, "a", null, null, null, null, "a != null")));
    Assert.That(Parsed("a is u8"), Is.EqualTo(new Assumption(AssumptionKind.Type, "a", null, null, "u8", null, "a is u8")));
    Assert.That(Parsed("a noalias b"), Is.EqualTo(new Assumption(AssumptionKind.NoAlias, "a", null, null, null, "b", "a noalias b")));
  }

  /// <summary>Every literal of the type production.</summary>
  [TestCase("u8"), TestCase("u16"), TestCase("u32"), TestCase("u64"), TestCase("i8"), TestCase("i16")]
  [TestCase("i32"), TestCase("i64"), TestCase("f32"), TestCase("f64"), TestCase("bool")]
  public void EveryType_IsAccepted(string type) => Assert.That(Parsed($"x is {type}").TypeName, Is.EqualTo(type));

  /// <summary>Test design 4: the spellings of an integer.</summary>
  [TestCase("10", 10), TestCase("0xA", 10), TestCase("0xa", 10), TestCase("-10", -10), TestCase("-0xA", -10), TestCase("007", 7), TestCase("0xFF", 255)]
  public void IntegerSpellings_HaveTheirValue(string spelling, int value) {
    var fact = Parsed($"a in [{spelling}, 1000]");
    Assert.That(fact.Low, Is.EqualTo(new BigInteger(value)));
    Assert.That(fact.Text, Is.EqualTo($"a in [{value}, 1000]"));
  }

  /// <summary>Test design 5: whitespace is optional between tokens and normalized.</summary>
  [TestCase("a in[0,9]"), TestCase("a  in  [ 0 , 9 ]"), TestCase(" a in [0, 9] "), TestCase("a\tin\t[0,\t9]")]
  public void Whitespace_IsNormalized(string text) => Assert.That(Parsed(text).Text, Is.EqualTo("a in [0, 9]"));

  [TestCase("a!=null", "a != null"), TestCase("a  is  u8", "a is u8"), TestCase("a   noalias   b.c", "a noalias b.c")]
  public void WhitespaceOfTheOtherKinds_IsNormalized(string text, string normal) => Assert.That(Parsed(text).Text, Is.EqualTo(normal));

  /// <summary>Test design 6: paths of any depth.</summary>
  [TestCase("n"), TestCase("Sum.n"), TestCase("Sum.data.length"), TestCase("_x.y_1")]
  public void Paths_OfAnyDepth(string path) => Assert.That(Parsed($"{path} != null").Subject, Is.EqualTo(path));

  /// <summary>Test design 7 and 8: a range of one value, and one whose bounds are inverted by one.</summary>
  [Test]
  public void RangeBounds_OnAndJustOutside() {
    Assert.That(Parsed("a in [5, 5]").High, Is.EqualTo(new BigInteger(5)));
    Assert.That(Refused("a in [6, 5]"), Is.EqualTo("lower bound 6 exceeds upper bound 5"));
  }

  /// <summary>Test design 9: bounds beyond 64 bits stay exact.</summary>
  [Test]
  public void BoundsBeyond64Bits_AreExact() {
    var fact = Parsed("a in [0, 0x10000000000000000]");
    Assert.That(fact.High, Is.EqualTo(BigInteger.Pow(2, 64)));
    Assert.That(fact.Text, Is.EqualTo("a in [0, 18446744073709551616]"));
  }

  /// <summary>Test design 12-15 and every other reason a text is no fact.</summary>
  [TestCase("", "empty assumption")]
  [TestCase("   ", "empty assumption")]
  [TestCase("Sum.n 5", "expected 'in', '!=', 'is' or 'noalias' after the path")]
  [TestCase("Sum.n", "expected 'in', '!=', 'is' or 'noalias' after the path")]
  [TestCase("a inx [0, 1]", "expected 'in', '!=', 'is' or 'noalias' after the path")]
  [TestCase("a in [x, 3]", "'x' is not an integer")]
  [TestCase("a in [0, 3x]", "'3x' is not an integer")]
  [TestCase("a in [0x, 3]", "'0x' is not an integer")]
  [TestCase("a in [0X1, 3]", "'0X1' is not an integer")]
  [TestCase("a in [0xG, 3]", "'0xG' is not an integer")]
  [TestCase("a in [-, 3]", "'-' is not an integer")]
  [TestCase("a in [, 3]", "expected an integer")]
  [TestCase("a in 0, 3]", "expected '[' after 'in'")]
  [TestCase("a in [0 3]", "expected ',' between the bounds")]
  [TestCase("a in [0, 3", "expected ']' after the upper bound")]
  [TestCase("a is u7", "unknown type 'u7'")]
  [TestCase("a is U8", "unknown type 'U8'")]
  [TestCase("a is", "expected a type after 'is'")]
  [TestCase("a != nil", "expected 'null' after '!='")]
  [TestCase("a noalias", "expected a path after 'noalias'")]
  [TestCase("a noalias b.", "expected a path after 'noalias'")]
  [TestCase("1a != null", "expected a path at '1a != null'")]
  [TestCase("a. != null", "expected a path at 'a. != null'")]
  [TestCase("a != null x", "unexpected 'x' after the assumption")]
  [TestCase("a in [0, 1]]", "unexpected ']' after the assumption")]
  public void Malformed_IsRefusedWithItsReason(string text, string reason) => Assert.That(Refused(text), Is.EqualTo(reason));

  /// <summary>Identifiers are case-sensitive: the keywords are lower case only.</summary>
  [Test]
  public void Keywords_AreCaseSensitive() => Assert.That(Refused("a IN [0, 1]"), Is.EqualTo("expected 'in', '!=', 'is' or 'noalias' after the path"));

  /// <summary>Test design 26: a comma inside a fact belongs to it.</summary>
  [Test]
  public void ACommaInsideAFact_BelongsToIt() => Assert.That(Parsed("a in [0, 9]").Kind, Is.EqualTo(AssumptionKind.Range));

  [Test]
  public void Null_IsRejected() => Assert.That(() => AssumptionParser.TryParse(null!, out _, out _), Throws.ArgumentNullException);

  /// <summary>
  /// The round-trip measurement: 10 000 generated facts, seed 7; each parses, its text parses to the
  /// same fact, and every bound agrees with <see cref="BigInteger.Parse(string, NumberStyles, IFormatProvider)"/>.
  /// </summary>
  [Test]
  public void GeneratedFacts_RoundTrip() {
    var random = new Random(7);
    var differences = 0;
    for (var index = 0; index < 10_000; ++index) {
      var (text, low) = Generate(random);
      Assert.That(AssumptionParser.TryParse(text, out var fact, out var error), Is.True, $"{text}: {error}");
      Assert.That(AssumptionParser.TryParse(fact!.Text, out var again, out _), Is.True, fact.Text);
      if (again != fact || (low is { } expected && fact.Low != expected))
        ++differences;
    }

    Assert.That(differences, Is.Zero);
  }

  private static (string Text, BigInteger? Low) Generate(Random random) {
    var path = string.Join('.', new string[random.Next(1, 4)].Select(_ => "v" + random.Next(100).ToString(CultureInfo.InvariantCulture)));
    string Space() => random.Next(3) switch { 0 => "", 1 => " ", _ => "  " };
    switch (random.Next(4)) {
      case 0:
        var digits = string.Concat(new char[random.Next(1, 30)].Select(_ => (char)('0' + random.Next(10))));
        var negative = random.Next(2) == 0;
        var low = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) * (negative ? -1 : 1);
        var hex = random.Next(2) == 0;
        var spelled = hex ? (negative ? "-" : "") + "0x" + BigInteger.Abs(low).ToString("X", CultureInfo.InvariantCulture) : (negative ? "-" : "") + digits;
        var high = BigInteger.Abs(low) + random.Next(1000);
        return ($"{path} {Space()}in{Space()}[{Space()}{spelled}{Space()},{Space()}{high}{Space()}]", low);
      case 1:
        return ($"{path}{Space()}!={Space()}null", null);
      case 2:
        return ($"{path} is{Space()} {AssumptionParser.Types[random.Next(AssumptionParser.Types.Count)]}", null);
      default:
        return ($"{path} noalias {path}x", null);
    }
  }

}
