using System;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Tests.Verification.Manifests;

/// <summary>
/// The canonical JSON writer. The expected texts of numbers and strings are what node 24's
/// <c>JSON.stringify</c> printed for the same values, the ECMAScript serialization RFC 8785 adopts.
/// </summary>
[TestFixture]
public sealed class CanonicalJsonTests {

  [TestCase(1e21, "1e+21")]
  [TestCase(1e20, "100000000000000000000")]
  [TestCase(1e-7, "1e-7")]
  [TestCase(1e-6, "0.000001")]
  [TestCase(123456789012345680000d, "123456789012345680000")]
  [TestCase(5e-324, "5e-324")]
  [TestCase(1.7976931348623157e308, "1.7976931348623157e+308")]
  [TestCase(-0.5, "-0.5")]
  [TestCase(0.30000000000000004, "0.30000000000000004")]
  [TestCase(-1e-12, "-1e-12")]
  [TestCase(3d, "3")]
  [TestCase(1.5e300, "1.5e+300")]
  [TestCase(4.35, "4.35")]
  [TestCase(100d, "100")]
  [TestCase(-0d, "0")]
  public void Number_IsWrittenAsECMAScriptWritesIt(double value, string text)
    => Assert.That(CanonicalJson.Number(value), Is.EqualTo(text));

  [TestCase(double.NaN)]
  [TestCase(double.PositiveInfinity)]
  [TestCase(double.NegativeInfinity)]
  public void ANumberThatIsNotFinite_HasNoJsonForm(double value)
    => Assert.That(() => CanonicalJson.Number(value), Throws.ArgumentException);

  [Test]
  public void AString_EscapesOnlyWhatRfc8785Escapes()
    => Assert.That(CanonicalJson.Serialize(JsonValue.Create("a\"\\\b\f\n\r\t\u0001\u001f\u007fé€\ud800x")),
      Is.EqualTo("\"a\\\"\\\\\\b\\f\\n\\r\\t\\u0001\\u001f\u007fé€\\ud800x\""));

  [Test]
  public void ASurrogatePair_IsKept_ALoneLowSurrogateEscaped()
    => Assert.That(CanonicalJson.Serialize(JsonValue.Create("\ude00😀|\ude00|\ud83d")), Is.EqualTo("\"\\ude00😀|\\ude00|\\ud83d\""));

  [Test]
  public void ATree_IsIndentedByTwo_WithEmptyContainersOnOneLine() {
    var tree = new JsonObject {
      ["a"] = 1,
      ["b"] = new JsonArray(true, false, null, 2.5, (long)-3),
      ["c"] = new JsonObject(),
      ["d"] = new JsonArray(),
      ["e"] = new JsonObject { ["f"] = "g" }
    };
    Assert.That(CanonicalJson.Serialize(tree), Is.EqualTo(
      "{\n  \"a\": 1,\n  \"b\": [\n    true,\n    false,\n    null,\n    2.5,\n    -3\n  ],\n  \"c\": {},\n  \"d\": [],\n  \"e\": {\n    \"f\": \"g\"\n  }\n}"));
  }

  [Test]
  public void NumbersOfEveryHoldingType_AreWrittenTheSame() {
    var parsed = JsonNode.Parse("[3, 3.0, 1E-12, 25e-1]")!;
    Assert.That(CanonicalJson.Serialize(new JsonArray(JsonValue.Create(3), JsonValue.Create(3.0f), JsonValue.Create(3m), parsed.DeepClone())),
      Is.EqualTo("[\n  3,\n  3,\n  3,\n  [\n    3,\n    3,\n    1e-12,\n    2.5\n  ]\n]"));
  }

  [Test]
  public void ANullInATree_IsNull()
    => Assert.That(CanonicalJson.Serialize(JsonNode.Parse("[null]")), Is.EqualTo("[\n  null\n]"));

  [Test]
  public void TheRoot_MayBeNull()
    => Assert.That(CanonicalJson.Serialize(null), Is.EqualTo("null"));

}
