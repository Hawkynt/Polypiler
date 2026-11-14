using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Tests.Permutation.Model;

/// <summary>The two JSON forms of issue #268: BitPerm's mapping state and the <c>inputWidth</c>/<c>outputs</c> document of <c>spec/permutations/</c>.</summary>
[TestFixture]
public sealed class BitMappingDocumentTests {

  private static MemoryStream Json(string text) => new(Encoding.UTF8.GetBytes(text));

  private static Diagnostic StateRefusal(string json, string? name = null)
    => Assert.Throws<PermutationModelException>(() => BitPermState.Read(Json(json), name))!.Diagnostic;

  private static Diagnostic DocumentRefusal(string json)
    => Assert.Throws<PermutationModelException>(() => BitMappingDocument.Read(Json(json)))!.Diagnostic;

  [Test]
  public void BitPermState_ReadsEveryEntryType() {
    var mapping = BitPermState.Read(Json("""
      {"name": "x", "width": 5, "mapping": [
        {"type": "src", "val": 1, "inv": false}, {"type": "src", "val": 1, "inv": true}, {"type": "src", "val": 4},
        {"type": "const", "val": 0, "inv": false}, {"type": "const", "val": 1}, {"type": "empty", "val": 0, "inv": false}, {"type": "empty"}]}
      """));
    // Seven entries over a 5-bit input: the output count is the number of entries.
    Assert.That(mapping.ToString(), Is.EqualTo("5:[1,~1,4,=0,=1,?,?]"));
  }

  /// <summary>Test design 11: malformed fixture documents.</summary>
  [TestCase("""{"name": "n", "width": 8, "mapping": [{"type": "src"}]}""", "mapping document 'n': output bit 0 has no integer 'val'")]
  [TestCase("""{"name": "n", "width": 8, "mapping": [{"type": "src", "val": 1.5}]}""", "mapping document 'n': output bit 0 has no integer 'val'")]
  [TestCase("""{"name": "n", "width": 8, "mapping": [{"type": "src", "val": "1"}]}""", "mapping document 'n': output bit 0 has no integer 'val'")]
  [TestCase("""{"name": "n", "width": 8, "mapping": [{"type": "src", "val": 1}, {"type": "collision", "sources": [1, 2]}]}""", "mapping document 'n': output bit 1 has unknown type 'collision'")]
  [TestCase("""{"name": "n", "width": 8, "mapping": [{"type": "const", "val": 2}]}""", "mapping document 'n': output bit 0: constant 2 is neither 0 nor 1")]
  [TestCase("""{"name": "n", "width": 8, "mapping": [{"type": "src", "val": 1, "inv": 1}]}""", "mapping document 'n': output bit 0: 'inv' is not true or false")]
  [TestCase("""{"name": "n", "width": 8, "mapping": [{"val": 1}]}""", "mapping document 'n': output bit 0 has no type")]
  [TestCase("""{"name": "n", "width": 8, "mapping": [{"type": 3}]}""", "mapping document 'n': output bit 0 has no type")]
  [TestCase("""{"name": "n", "width": 8, "mapping": [3]}""", "mapping document 'n': output bit 0 has no type")]
  [TestCase("""{"name": "n", "mapping": []}""", "mapping document 'n': missing width")]
  [TestCase("""{"name": "n", "width": "8", "mapping": []}""", "mapping document 'n': width is not an integer")]
  [TestCase("""{"name": "n", "width": 8.5, "mapping": []}""", "mapping document 'n': width is not an integer")]
  [TestCase("""{"name": "n", "width": 8}""", "mapping document 'n': missing mapping")]
  [TestCase("""{"name": "n", "width": 8, "mapping": {}}""", "mapping document 'n': missing mapping")]
  [TestCase("""{"width": 8}""", "mapping document 'stream': missing mapping")]
  [TestCase("""{"name": 7, "width": 8}""", "mapping document 'stream': missing mapping")]
  [TestCase("""[1]""", "mapping document 'stream': expected an object")]
  public void BitPermState_Malformed_IsPLY0802(string json, string message) {
    var diagnostic = StateRefusal(json);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo(("PLY0802", message)));
  }

  [Test]
  public void BitPermState_NotJson_IsPLY0802WithTheGivenName() {
    var diagnostic = StateRefusal("{", "broken.json");
    Assert.That(diagnostic.Code, Is.EqualTo("PLY0802"));
    Assert.That(diagnostic.Message, Does.StartWith("mapping document 'broken.json': "));
    Assert.That(StateRefusal("""{"name": "n", "width": 8}""", "given").Message, Is.EqualTo("mapping document 'given': missing mapping"));
    Assert.That(StateRefusal("nope").Message, Does.StartWith("mapping document 'stream': "));
  }

  [TestCase("""{"width": 0, "mapping": []}""", "PLY0800", "mapping width 0 is outside 1..4096")]
  [TestCase("""{"width": 99999999999, "mapping": []}""", "PLY0800", "mapping width 99999999999 is outside 1..4096")]
  [TestCase("""{"width": 8, "mapping": []}""", "PLY0800", "mapping width 0 is outside 1..4096")]
  [TestCase("""{"width": 8, "mapping": [{"type": "src", "val": 8}]}""", "PLY0801", "output bit 0 reads input bit 8, input width is 8")]
  [TestCase("""{"width": 8, "mapping": [{"type": "src", "val": -1, "inv": true}]}""", "PLY0801", "output bit 0 reads input bit -1, input width is 8")]
  public void BitPermState_InvalidMapping_IsRefused(string json, string code, string message) {
    var diagnostic = StateRefusal(json);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo((code, message)));
  }

  [Test]
  public void Document_ReadsEveryEntryForm_AndIgnoresTheRestOfASpecBody() {
    var mapping = BitMappingDocument.Read(Json("""
      {"kind": "mapping", "inputWidth": 4, "outputs": [3, {"not": 2}, {"const": 0}, {"const": 1}, {"dontCare": true}], "bijective": false, "vectors": []}
      """));
    Assert.That(mapping.ToString(), Is.EqualTo("4:[3,~2,=0,=1,?]"));
  }

  [Test]
  public void Document_WriteIsTheSchemaForm() {
    using var stream = new MemoryStream();
    BitMappingDocument.Write(BitMapping.Parse("4:[3,~2,=0,=1,?]"), stream);
    Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Is.EqualTo("""{"inputWidth":4,"outputs":[3,{"not":2},{"const":0},{"const":1},{"dontCare":true}]}"""));
  }

  [TestCase("""{"inputWidth": 4, "outputs": ["3"]}""", "output bit 0: expected an input bit, {\"not\": i}, {\"const\": 0|1} or {\"dontCare\": true}, got \"3\"")]
  [TestCase("""{"inputWidth": 4, "outputs": [{}]}""", """output bit 0: expected an input bit, {"not": i}, {"const": 0|1} or {"dontCare": true}, got {}""")]
  [TestCase("""{"inputWidth": 4, "outputs": [{"not": 1, "const": 0}]}""", """output bit 0: expected an input bit, {"not": i}, {"const": 0|1} or {"dontCare": true}, got {"not": 1, "const": 0}""")]
  [TestCase("""{"inputWidth": 4, "outputs": [{"const": 2}]}""", """output bit 0: expected an input bit, {"not": i}, {"const": 0|1} or {"dontCare": true}, got {"const": 2}""")]
  [TestCase("""{"inputWidth": 4, "outputs": [{"const": "0"}]}""", """output bit 0: expected an input bit, {"not": i}, {"const": 0|1} or {"dontCare": true}, got {"const": "0"}""")]
  [TestCase("""{"inputWidth": 4, "outputs": [{"const": 0.5}]}""", """output bit 0: expected an input bit, {"not": i}, {"const": 0|1} or {"dontCare": true}, got {"const": 0.5}""")]
  [TestCase("""{"inputWidth": 4, "outputs": [{"dontCare": false}]}""", """output bit 0: expected an input bit, {"not": i}, {"const": 0|1} or {"dontCare": true}, got {"dontCare": false}""")]
  [TestCase("""{"inputWidth": 4, "outputs": [{"not": true}]}""", """output bit 0: expected an input bit, {"not": i}, {"const": 0|1} or {"dontCare": true}, got {"not": true}""")]
  [TestCase("""{"inputWidth": 4, "outputs": [{"xor": 1}]}""", """output bit 0: expected an input bit, {"not": i}, {"const": 0|1} or {"dontCare": true}, got {"xor": 1}""")]
  [TestCase("""{"inputWidth": 4, "outputs": [1.5]}""", "output bit 0: 1.5 is not an input bit")]
  [TestCase("""{"inputWidth": 4, "outputs": [0, {"not": 1e30}]}""", "output bit 1: 1e30 is not an input bit")]
  [TestCase("""{"inputWidth": 4}""", "missing outputs")]
  [TestCase("""{"inputWidth": 4, "outputs": 3}""", "missing outputs")]
  [TestCase("""{"outputs": [0]}""", "missing inputWidth")]
  [TestCase("""{"inputWidth": true, "outputs": [0]}""", "inputWidth is not an integer")]
  [TestCase("""7""", "expected an object")]
  public void Document_Malformed_IsPLY0802(string json, string reason) {
    var diagnostic = DocumentRefusal(json);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo(("PLY0802", $"mapping document 'stream': {reason}")));
  }

  [TestCase("""{"inputWidth": 4097, "outputs": [0]}""", "PLY0800", "mapping width 4097 is outside 1..4096")]
  [TestCase("""{"inputWidth": 4, "outputs": [4]}""", "PLY0801", "output bit 0 reads input bit 4, input width is 4")]
  [TestCase("""{"inputWidth": 4, "outputs": [0, {"not": 99999999999}]}""", "PLY0801", "output bit 1 reads input bit 99999999999, input width is 4")]
  [TestCase("""{"inputWidth": 4, "outputs": [-1]}""", "PLY0801", "output bit 0 reads input bit -1, input width is 4")]
  public void Document_InvalidMapping_IsRefused(string json, string code, string message) {
    var diagnostic = DocumentRefusal(json);
    Assert.That((diagnostic.Code, diagnostic.Message), Is.EqualTo((code, message)));
  }

  [Test]
  public void Document_NamesItself() {
    var refusal = Assert.Throws<PermutationModelException>(() => BitMappingDocument.Read(Json("{"), "m.json"))!;
    Assert.That(refusal.Diagnostic.Message, Does.StartWith("mapping document 'm.json': "));
  }

  [Test]
  public void Arguments_MustBeGiven() {
    Assert.Throws<ArgumentNullException>(() => BitPermState.Read(null!));
    Assert.Throws<ArgumentNullException>(() => BitMappingDocument.Read(null!));
    Assert.Throws<ArgumentNullException>(() => BitMappingDocument.Write(null!, new MemoryStream()));
    Assert.Throws<ArgumentNullException>(() => BitMappingDocument.Write(BitMapping.Identity(1), null!));
  }

}
