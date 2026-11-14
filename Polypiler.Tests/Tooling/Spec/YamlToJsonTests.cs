using System;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="YamlToJson"/>: scalars typed by the YAML 1.2 core schema, a position for every JSON
/// pointer, and the constructs it refuses with their line and column.
/// </summary>
[TestFixture]
public sealed class YamlToJsonTests {

  private static JsonNode? Read(string text) => YamlToJson.Read(text, out _);

  private static string Json(string scalar) => Read($"v: {scalar}")!["v"]?.ToJsonString() ?? "null";

  [TestCase("~", "null")]
  [TestCase("null", "null")]
  [TestCase("Null", "null")]
  [TestCase("NULL", "null")]
  [TestCase("", "null")]
  [TestCase("true", "true")]
  [TestCase("True", "true")]
  [TestCase("TRUE", "true")]
  [TestCase("false", "false")]
  [TestCase("False", "false")]
  [TestCase("FALSE", "false")]
  [TestCase("42", "42")]
  [TestCase("-7", "-7")]
  [TestCase("+3", "3")]
  [TestCase("0o17", "15")]
  [TestCase("0x1F", "31")]
  [TestCase("1.5", "1.5")]
  [TestCase("-.5e1", "-5")]
  [TestCase("2.0", "2")]
  [TestCase("123456789012345678901234567890", "123456789012345678901234567890")]
  [TestCase("no", "\"no\"")]
  [TestCase("on", "\"on\"")]
  [TestCase("yes", "\"yes\"")]
  [TestCase("0b101", "\"0b101\"")]
  [TestCase("\"2\"", "\"2\"")]
  [TestCase("'true'", "\"true\"")]
  [TestCase("\"\"", "\"\"")]
  [TestCase("plain text", "\"plain text\"")]
  public void Scalar_IsTypedByTheCoreSchema(string scalar, string json) => Assert.That(Json(scalar), Is.EqualTo(json));

  [Test]
  public void Collections_BecomeObjectsAndArraysInOrder()
    => Assert.That(Read("b: [1, x]\na: {c: d}\n")!.ToJsonString(), Is.EqualTo("{\"b\":[1,\"x\"],\"a\":{\"c\":\"d\"}}"));

  [Test]
  public void QuotedMergeKey_IsAnOrdinaryKey() => Assert.That((long)Read("\"<<\": 1\n")!["<<"]!, Is.EqualTo(1));

  [Test]
  public void Positions_AreRecordedForEveryPointer() {
    YamlToJson.Read("id: x\nspec:\n  parts:\n    - name: gear\n  a/b~c: 1\n", out var positions);
    Assert.That(positions[""], Is.EqualTo((1, 1)));
    Assert.That(positions["/id"], Is.EqualTo((1, 5)));
    Assert.That(positions["/spec"], Is.EqualTo((3, 3)));
    Assert.That(positions["/spec/parts/0/name"], Is.EqualTo((4, 13)));
    Assert.That(positions["/spec/a~1b~0c"], Is.EqualTo((5, 10)));
  }

  [Test]
  public void EmptyText_HasNoDocument() {
    Assert.That(YamlToJson.Read("", out var positions), Is.Null);
    Assert.That(positions, Is.Empty);
  }

  [TestCase("~", "a~0")]
  [TestCase("/", "a~1")]
  public void Escape_FollowsRfc6901(string character, string escaped) => Assert.That(YamlToJson.Escape("a" + character), Is.EqualTo(escaped));

  /// <summary>Test design 15: malformed YAML, tab indentation, duplicate key, anchor and alias, each with line and column.</summary>
  [TestCase("a: \"x\n", "not valid YAML: ", 1, 4)]
  [TestCase("a: {b: 1\n", "not valid YAML: ", 2, 1)]
  [TestCase("a: 1\n\tb: 2\n", "not valid YAML: ", 1, 4)]
  [TestCase("a: 1\nb: 2\na: 3\n", "duplicate key 'a'", 3, 1)]
  [TestCase("a: &x 1\n", "anchors are not allowed", 1, 4)]
  [TestCase("a: 1\nb: *x\n", "aliases are not allowed", 2, 4)]
  [TestCase("a: !!str 1\n", "tag 'tag:yaml.org,2002:str' is not allowed", 1, 4)]
  [TestCase("base: {a: 1}\nother:\n  <<: {b: 2}\n", "merge keys are not allowed", 3, 3)]
  [TestCase("? [a]\n: 1\n", "a mapping key must be a plain or quoted scalar", 1, 3)]
  [TestCase("!t a: 1\n", "a mapping key must be a plain or quoted scalar", 1, 1)]
  [TestCase("&k a: 1\n", "a mapping key must be a plain or quoted scalar", 1, 1)]
  [TestCase("a: 1\n---\nb: 2\n", "a spec file holds one YAML document", 2, 1)]
  [TestCase("a: .inf\n", "'.inf' has no JSON value", 1, 4)]
  [TestCase("a: .NaN\n", "'.NaN' has no JSON value", 1, 4)]
  public void Refused_WithItsPosition(string text, string message, int line, int column) {
    var exception = Assert.Throws<YamlToJsonException>(() => Read(text))!;
    Assert.That(exception.Message, Does.StartWith(message));
    Assert.That((exception.Line, exception.Column), Is.EqualTo((line, column)));
  }

  [Test]
  public void Alias_OfAnAnchorInTheFile_IsRefusedAtTheAnchor()
    => Assert.That(Assert.Throws<YamlToJsonException>(() => Read("a: &x 1\nb: *x\n"))!.Message, Is.EqualTo("anchors are not allowed"));

  [Test]
  public void NullText_Throws() => Assert.Throws<ArgumentNullException>(() => YamlToJson.Read(null!, out _));

}
