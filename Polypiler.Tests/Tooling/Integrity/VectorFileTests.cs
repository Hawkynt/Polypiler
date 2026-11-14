using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Integrity;

namespace Polypiler.Tests.Tooling.Integrity;

/// <summary>
/// <see cref="VectorFile"/>: the vector file format with its mandatory provenance, read into immutable
/// records; test design 3 and 6 to 9, 15, and every malformed file.
/// </summary>
[TestFixture]
public sealed class VectorFileTests {

  private static string Problems(string text) {
    var exception = Assert.Throws<IntegrityException>(() => VectorFile.Parse(text, "v.vectors.json"))!;
    Assert.That(exception.Diagnostics.Select(diagnostic => diagnostic.Code), Is.All.EqualTo(IntegrityDiagnostics.ProvenanceMissing));
    return string.Join("\n", exception.Diagnostics.Select(diagnostic => diagnostic.Message));
  }

  private static string Kinds => GoldenTree.Fixture("kinds.json");

  [Test]
  public void RepositoryFile_IsTheZlibReference() {
    var file = VectorFile.Load(Path.Combine(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot, GoldenTree.Adler32));
    Assert.That(file.Fixture, Is.EqualTo("cipher.checksum.adler32"));
    Assert.That(file.Provenance, Is.EqualTo(new Provenance(ProvenanceKind.ReferenceRun, "python", file.Provenance.Detail, "python")));
    Assert.That(file.Vectors.Select(vector => (vector.Input.Text, Convert.ToHexString([.. vector.Expected.Bytes]))),
      Is.EqualTo(new[] { ("", "00000001"), ("a", "00620062"), ("abc", "024D0127") }));
  }

  [TestCase("published.json", ProvenanceKind.Published, "published")]
  [TestCase("upstream-embedded.json", ProvenanceKind.UpstreamEmbedded, "upstream-embedded")]
  [TestCase("kinds.json", ProvenanceKind.ReferenceRun, "reference-run")]
  public void EveryKind_Loads(string fixture, ProvenanceKind kind, string name) {
    Assert.That(VectorFile.Parse(GoldenTree.Fixture(fixture), fixture).Provenance.Kind, Is.EqualTo(kind));
    Assert.That(Provenance.KindName(kind), Is.EqualTo(name));
  }

  [Test]
  public void ValueKinds_LoadEach_HexCaseInsensitiveAsBytes() {
    var file = VectorFile.Parse(Kinds, "kinds");
    Assert.That(file.Name, Is.EqualTo("kinds"));
    var hex = file.Find("hex")!;
    Assert.That((hex.Input.Kind, hex.Expected.Kind), Is.EqualTo((VectorValueKind.Hex, VectorValueKind.Hex)));
    Assert.That(hex.Expected.Bytes, Is.EqualTo(new byte[] { 0x02, 0x4D, 0x01, 0x27 }));
    Assert.That(hex.Expected.Json, Is.EqualTo("\"024d0127\""));
    var text = file.Find("text")!;
    Assert.That((text.Input.Kind, text.Expected.Kind, text.Expected.Text, text.Expected.Bytes.Length), Is.EqualTo((VectorValueKind.Text, VectorValueKind.Text, "024D0127", 0)));
    var value = file.Find("value")!;
    Assert.That((value.Input.Kind, value.Input.Json, value.Expected.Kind, value.Expected.Json, value.Expected.Text),
      Is.EqualTo((VectorValueKind.Args, "[[97, 98, 99]]", VectorValueKind.Value, "38600999", (string?)null)));
    Assert.That(file.Find("nothing"), Is.Null);
  }

  [Test]
  public void ExactlyOneVector_IsAccepted_AndEmptyHexIsZeroBytes() {
    var file = VectorFile.Parse(GoldenTree.Fixture("published.json"), "p");
    Assert.That(file.Vectors, Has.Length.EqualTo(1));
    Assert.That(file.Vectors[0].Input.Bytes, Is.Empty);
  }

  /// <summary>The exactness block belongs to the verification framework, which checks its content; the gate only accepts it.</summary>
  [Test]
  public void AnExactnessObject_IsAccepted()
    => Assert.That(VectorFile.Parse(Kinds.Replace("\"schema\": 1", "\"schema\": 1, \"exactness\": { \"mode\": \"exact\" }", StringComparison.Ordinal), "k").Vectors, Is.Not.Empty);

  [Test]
  public void EmptyVectorList_IsRefused() => Assert.That(Problems(Kinds[..Kinds.IndexOf("\"vectors\"", StringComparison.Ordinal)] + "\"vectors\": []\n}"), Does.EndWith("no vectors"));

  [TestCase("\"024d0127\"", "\"024\"", "vector 'hex' expected hex '024' must be an even number of hexadecimal digits")]
  [TestCase("\"024d0127\"", "\"0x24\"", "vector 'hex' expected hex '0x24' must be an even number of hexadecimal digits")]
  [TestCase("\"provenance\"", "\"origin\"", "no provenance")]
  [TestCase("\"kind\": \"reference-run\"", "\"kind\": \"guess\"", "provenance kind 'guess' is unknown; it is 'published', 'reference-run' or 'upstream-embedded'")]
  [TestCase("\"kind\": \"reference-run\",", "", "provenance has no 'kind'")]
  [TestCase("\"source\": \"python\"", "\"source\": \" \"", "provenance 'source' must be a non-empty string")]
  [TestCase("\"source\": \"python\"", "\"source\": \"python\", \"by\": 1", "provenance has the unknown property 'by'")]
  [TestCase("\"schema\": 1", "\"schema\": 2", "'schema' must be 1")]
  [TestCase("\"schema\": 1", "\"schema\": 1, \"extra\": 0", "unknown property 'extra'")]
  [TestCase("\"schema\": 1", "\"schema\": 1, \"exactness\": \"exact\"", "'exactness' must be an object")]
  [TestCase("\"fixture\": \"cipher.checksum.adler32\"", "\"fixture\": \"\"", "'fixture' must name a corpus fixture")]
  [TestCase("\"fixture\": \"cipher.checksum.adler32\"", "\"fixture\": 7", "'fixture' must name a corpus fixture")]
  [TestCase("{ \"name\": \"text\"", "{ \"name\": \"hex\"", "vector 'hex' is listed twice")]
  [TestCase("{ \"name\": \"text\"", "{ \"name\": \"\"", "vector 2 must have a non-empty 'name'")]
  [TestCase("{ \"name\": \"text\"", "{ \"name\": 2", "vector 2 must have a non-empty 'name'")]
  [TestCase("{ \"name\": \"text\",", "{ \"label\": \"text\",", "vector 2 must be an object of exactly 'name', 'input' and 'expected' or 'expectedTrap'")]
  [TestCase("{ \"text\": \"abc\" }, \"expected\"", "{ \"text\": \"abc\", \"hex\": \"\" }, \"expected\"", "vector 'text' input must be an object of exactly one of 'hex', 'text' or 'args'")]
  [TestCase("{ \"text\": \"abc\" }, \"expected\"", "{ \"value\": 1 }, \"expected\"", "vector 'text' input must be an object of exactly one of 'hex', 'text' or 'args'")]
  [TestCase("{ \"text\": \"abc\" }, \"expected\"", "[], \"expected\"", "vector 'text' input must be an object of exactly one of 'hex', 'text' or 'args'")]
  [TestCase("{ \"value\": 38600999 }", "{ \"args\": [] }", "vector 'value' expected must be an object of exactly one of 'hex', 'text' or 'value'")]
  [TestCase("{ \"args\": [[97, 98, 99]] }", "{ \"args\": 1 }", "vector 'value' input must be an object of exactly one of 'hex', 'text' or 'args'")]
  [TestCase("{ \"hex\": \"616263\" }", "{ \"hex\": 616263 }", "vector 'hex' input must be an object of exactly one of 'hex', 'text' or 'args'")]
  [TestCase("{ \"text\": \"024D0127\" }", "{ \"text\": 1 }", "vector 'text' expected must be an object of exactly one of 'hex', 'text' or 'value'")]
  [TestCase("{ \"hex\": \"616263\" }", "{ \"nope\": 1 }", "vector 'hex' input must be an object of exactly one of 'hex', 'text' or 'args'")]
  [TestCase("{ \"text\": \"abc\" }, \"expected\"", "{ \"text\": 1 }, \"expected\"", "vector 'text' input must be an object of exactly one of 'hex', 'text' or 'args'")]
  [TestCase("{ \"hex\": \"024d0127\" }", "{ \"hex\": 1 }", "vector 'hex' expected must be an object of exactly one of 'hex', 'text' or 'value'")]
  [TestCase("{ \"hex\": \"024d0127\" }", "{ \"nope\": 1 }", "vector 'hex' expected must be an object of exactly one of 'hex', 'text' or 'value'")]
  [TestCase("\"source\": \"python\"", "\"source\": 1", "provenance 'source' must be a non-empty string")]
  public void Malformed_IsRefused(string original, string replacement, string reason) {
    Assert.That(Kinds, Does.Contain(original));
    Assert.That(Problems(Kinds.Replace(original, replacement, StringComparison.Ordinal)), Does.Contain(reason));
  }

  [TestCase("\"provenance\": {", "\"provenance\": 1, \"was\": {", "'provenance' must be an object")]
  [TestCase("\"vectors\": [", "\"vectors\": 1, \"was\": [", "'vectors' must be an array")]
  [TestCase("\"vectors\": [", "\"vectors\": [1,", "vector 1 must be an object of exactly 'name', 'input' and 'expected' or 'expectedTrap'")]
  public void WrongShape_IsRefused(string original, string replacement, string reason)
    => Assert.That(Problems(Kinds.Replace(original, replacement, StringComparison.Ordinal)), Does.Contain(reason));

  /// <summary>A runner's file names the function and its signature, and a vector may expect a trap; the verification framework checks both in depth.</summary>
  [Test]
  public void ARunnerFile_WithATrapVector_Loads() {
    var text = Kinds.Replace("\"schema\": 1", "\"schema\": 1, \"export\": \"f\", \"signature\": { \"parameters\": [], \"result\": \"u8\" }", StringComparison.Ordinal)
      .Replace("\"expected\": { \"hex\": \"024d0127\" }", "\"expectedTrap\": \"divide-by-zero\"", StringComparison.Ordinal);
    var trap = VectorFile.Parse(text, "runner").Find("hex")!.Expected;
    Assert.That((trap.Kind, trap.Text, trap.Json, trap.Bytes.Length), Is.EqualTo((VectorValueKind.Trap, "divide-by-zero", "\"divide-by-zero\"", 0)));
  }

  [TestCase("1")]
  [TestCase("\"\"")]
  public void AnExpectedTrapThatIsNoName_IsRefused(string trap)
    => Assert.That(Problems(Kinds.Replace("\"expected\": { \"hex\": \"024d0127\" }", $"\"expectedTrap\": {trap}", StringComparison.Ordinal)),
      Does.Contain("vector 'hex' expectedTrap must be a non-empty string"));

  [Test]
  public void ExpectedAndExpectedTrapTogether_AreRefused()
    => Assert.That(Problems(Kinds.Replace("\"expected\": { \"hex\": \"024d0127\" }", "\"expected\": { \"hex\": \"024d0127\" }, \"expectedTrap\": \"bounds\"", StringComparison.Ordinal)),
      Does.Contain("must be an object of exactly 'name', 'input' and 'expected' or 'expectedTrap'"));

  [TestCase("[]", "a vector file must be a JSON object")]
  [TestCase("{", "not JSON: ")]
  public void NotAnObject_IsRefused(string text, string reason) => Assert.That(Problems(text), Does.StartWith("vectors 'v.vectors.json' lack independent provenance: " + reason));

  [Test]
  public void EveryProblem_IsReportedAtOnce() {
    var exception = Assert.Throws<IntegrityException>(() => VectorFile.Parse("{\"schema\":2,\"vectors\":[]}", "v"))!;
    Assert.That(exception.Diagnostics.Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      "vectors 'v' lack independent provenance: 'schema' must be 1",
      "vectors 'v' lack independent provenance: 'fixture' must name a corpus fixture",
      "vectors 'v' lack independent provenance: no provenance",
      "vectors 'v' lack independent provenance: no vectors"
    }));
    Assert.That(exception.Message, Does.Contain("no vectors"));
  }

  [Test]
  public void Load_OfAFolderOrOfInvalidUtf8_IsRefused() {
    var folder = Scratch.Create("polypiler-vector-file-");
    try {
      Assert.That(Assert.Throws<IntegrityException>(() => VectorFile.Load(folder, "f"))!.Diagnostics[0].Message, Does.StartWith("vectors 'f' lack independent provenance: cannot be read: "));
      var file = Scratch.Write(folder, "x.vectors.json", [0x7B, 0xFF, 0x7D]);
      Assert.That(Assert.Throws<IntegrityException>(() => VectorFile.Load(file))!.Diagnostics[0].Message, Does.Contain("cannot be read: "));
    } finally {
      Scratch.Delete(folder);
    }
  }

}
