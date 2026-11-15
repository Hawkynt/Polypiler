using System;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Tests.Verification.Images;

/// <summary><see cref="CallSignature"/>: the JSON form, its modes and its refusals (test design 4, 17).</summary>
[TestFixture]
public sealed class CallSignatureTests {

  /// <summary>The signature of the call scenario: <c>(a: u32 in, buf: seq&lt;u8&gt; inout, r: u16 out) -&gt; bool</c>.</summary>
  internal const string Scenario = """
    { "parameters": [ { "name": "a", "type": "u32", "mode": "in" }, { "name": "buf", "type": "seq<u8>", "mode": "inout" }, { "name": "r", "type": "u16", "mode": "out" } ], "result": "bool" }
    """;

  private static CallSignature Parse(string json) => CallSignature.Parse(JsonNode.Parse(json));

  private static string Problem(string json) => Assert.Throws<ImageException>(() => Parse(json))!.Message;

  [Test]
  public void TheScenarioSignature_Parses() {
    var signature = Parse(Scenario);
    Assert.That(signature.Parameters, Is.EqualTo(new[] {
      new CallParameter("a", ImageType.U32, ParameterMode.In),
      new CallParameter("buf", ImageType.Seq(ImageType.U8), ParameterMode.InOut),
      new CallParameter("r", ImageType.U16, ParameterMode.Out)
    }));
    Assert.That(signature.Result, Is.EqualTo(ImageType.Bool));
    Assert.That(signature.Inputs.Select(parameter => parameter.Name), Is.EqualTo(new[] { "a", "buf" }));
    Assert.That(signature.Outputs.Select(parameter => parameter.Name), Is.EqualTo(new[] { "buf", "r" }));
    Assert.That(signature.ToString(), Is.EqualTo("(a: u32 in, buf: seq<u8> inout, r: u16 out) -> bool"));
  }

  /// <summary>Test design 4: the mode defaults to <c>in</c>; a void result and no parameters are allowed.</summary>
  [Test]
  public void DefaultsAndEmptyParts_Parse() {
    var signature = Parse("""{ "parameters": [ { "name": "_x", "type": "i64" } ], "result": "void" }""");
    Assert.That((signature.Parameters[0].Mode, signature.Result, signature.ToString()), Is.EqualTo((ParameterMode.In, (ImageType?)null, "(_x: i64 in) -> void")));
    var empty = Parse("""{ "parameters": [], "result": "u8" }""");
    Assert.That((empty.Parameters.Count, empty.ToString()), Is.EqualTo((0, "() -> u8")));
  }

  [TestCase("[]", "signature: must be an object")]
  [TestCase("""{ "parameters": [], "result": "u8", "export": "f" }""", "signature: 'export' is not a property of a signature")]
  [TestCase("""{ "result": "u8" }""", "signature: 'parameters' must be an array")]
  [TestCase("""{ "parameters": [1], "result": "u8" }""", "signature: 'parameters[0]' must be an object")]
  [TestCase("""{ "parameters": [ { "name": "a", "type": "u8", "width": 8 } ], "result": "u8" }""", "signature: 'parameters[0].width' is not a property of a signature")]
  [TestCase("""{ "parameters": [ { "type": "u8" } ], "result": "u8" }""", "signature: 'parameters[0].name' must be a non-empty string")]
  [TestCase("""{ "parameters": [ { "name": 1, "type": "u8" } ], "result": "u8" }""", "signature: 'parameters[0].name' must be a non-empty string")]
  [TestCase("""{ "parameters": [ { "name": "", "type": "u8" } ], "result": "u8" }""", "signature: 'parameters[0].name' must be a non-empty string")]
  [TestCase("""{ "parameters": [ { "name": "1a", "type": "u8" } ], "result": "u8" }""", "signature: 'parameters[0].name' '1a' is not a name")]
  [TestCase("""{ "parameters": [ { "name": "a-b", "type": "u8" } ], "result": "u8" }""", "signature: 'parameters[0].name' 'a-b' is not a name")]
  [TestCase("""{ "parameters": [ { "name": "a", "type": "u8", "mode": "ref" } ], "result": "u8" }""", "signature: parameter 'a' has the unknown mode 'ref'")]
  [TestCase("""{ "parameters": [ { "name": "a", "type": "void", "mode": "out" } ], "result": "u8" }""", "signature: parameter 'a' has mode 'out' and type 'void'")]
  [TestCase("""{ "parameters": [ { "name": "a", "type": "void", "mode": "inout" } ], "result": "u8" }""", "signature: parameter 'a' has mode 'inout' and type 'void'")]
  [TestCase("""{ "parameters": [ { "name": "a", "type": "int" } ], "result": "u8" }""", "signature: unknown image type 'int'")]
  [TestCase("""{ "parameters": [ { "name": "a" } ], "result": "u8" }""", "signature: 'parameters[0].type' must be a non-empty string")]
  [TestCase("""{ "parameters": [ { "name": "a", "type": "u8" }, { "name": "a", "type": "u16" } ], "result": "u8" }""", "signature: parameter 'a' is declared twice")]
  [TestCase("""{ "parameters": [] }""", "signature: 'result' must be a non-empty string")]
  [TestCase("""{ "parameters": [], "result": "seq<" }""", "signature: 'seq<' is not an image type: an image type expected at position 4")]
  public void Malformed_IsRefused(string json, string problem) => Assert.That(Problem(json), Is.EqualTo(problem));

  [Test]
  public void NoJson_IsRefused() => Assert.That(Assert.Throws<ImageException>(() => CallSignature.Parse(null))!.Message, Is.EqualTo("signature: must be an object"));

  [Test]
  public void TheConstructor_RefusesNullTypes() {
    Assert.That(Assert.Throws<ImageException>(() => _ = new CallSignature([new("a", ImageType.Null, ParameterMode.In)], null))!.Message,
      Is.EqualTo("signature: parameter 'a' has the type 'null', which only a union holds"));
    Assert.That(Assert.Throws<ImageException>(() => _ = new CallSignature([], ImageType.Null))!.Message,
      Is.EqualTo("signature: the result has the type 'null', which only a union holds"));
    Assert.Throws<ArgumentNullException>(() => _ = new CallSignature(null!, null));
    Assert.Throws<ArgumentNullException>(() => _ = new CallSignature([null!], null));
  }

}
