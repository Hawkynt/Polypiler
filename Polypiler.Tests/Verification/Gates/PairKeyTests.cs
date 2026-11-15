using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Gates;

namespace Polypiler.Tests.Verification.Gates;

/// <summary>The pair and the name of its gate record.</summary>
[TestFixture]
public sealed class PairKeyTests {

  [Test]
  public void TheFileName_JoinsThePartsWithTwoHyphens()
    => Assert.That(GateFixtures.CSharpToJavaScript.FileName, Is.EqualTo("csharp--javascript--net10--node-es2022.json"));

  /// <summary>Test design 17: a profile with a single hyphen, and one with dots, read back to the same pair.</summary>
  [TestCase("csharp", "javascript", "net10", "node-es2022")]
  [TestCase("javascript", "csharp", "es2022", "netstandard2.0")]
  [TestCase("c", "c++", "c11", "x86_64-linux-gnu")]
  public void AFileName_ReadsBackToTheSamePair(string from, string to, string source, string target) {
    var pair = new PairKey(from, to, source, target);
    Assert.That(PairKey.TryParseFileName(pair.FileName, out var read), Is.True);
    Assert.That(read, Is.EqualTo(pair));
  }

  /// <summary>Test design 18: a part that would make the name ambiguous, or is no part at all.</summary>
  [TestCase("a--b")]
  [TestCase("-a")]
  [TestCase("a-")]
  [TestCase("")]
  [TestCase("Net10")]
  [TestCase("a b")]
  [TestCase("a/b")]
  [TestCase("a\n")]
  public void AnAmbiguousPart_IsRefused(string part) {
    Assert.That(() => new PairKey(part, "javascript", "net10", "node"), Throws.ArgumentException.With.Message.Contains("From"));
    Assert.That(() => new PairKey("csharp", part, "net10", "node"), Throws.ArgumentException.With.Message.Contains("To"));
    Assert.That(() => new PairKey("csharp", "javascript", part, "node"), Throws.ArgumentException.With.Message.Contains("SourceProfile"));
    Assert.That(() => new PairKey("csharp", "javascript", "net10", part), Throws.ArgumentException.With.Message.Contains("TargetProfile"));
    Assert.That(PairKey.IsPart(part), Is.False);
  }

  [Test]
  public void Null_IsNoPart() {
    Assert.That(PairKey.IsPart(null), Is.False);
    Assert.That(() => new PairKey(null!, "a", "b", "c"), Throws.ArgumentException);
  }

  [TestCase(null)]
  [TestCase("csharp--javascript--net10--node.yaml")]
  [TestCase("csharp--javascript--net10.json")]
  [TestCase("csharp--javascript--net10--node--x.json")]
  [TestCase("csharp--javascript--net10---node.json")]
  [TestCase("csharp--javascript--net10--Node.json")]
  [TestCase("missing-evidence.json")]
  public void OtherNames_AreNoGateRecords(string? name) {
    Assert.That(PairKey.TryParseFileName(name, out var pair), Is.False);
    Assert.That(pair, Is.Null);
  }

  [Test]
  public void ThePair_DescribesItself()
    => Assert.That(GateFixtures.CSharpToJavaScript.ToString(), Is.EqualTo("csharp (net10) to javascript (node-es2022)"));

  [Test]
  public void Pairs_AreEqualByValue() {
    var pair = new PairKey("a", "b", "c", "d");
    var same = new PairKey("a", "b", "c", "d");
    Assert.That(pair, Is.EqualTo(same));
    Assert.That(pair, Is.Not.EqualTo(new PairKey("a", "b", "c", "e")));
    Assert.That(pair.GetHashCode(), Is.EqualTo(same.GetHashCode()));
  }

}
