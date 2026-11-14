using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Tests.Verification.Images.Support;

namespace Polypiler.Tests.Verification.Images;

/// <summary>
/// The vector file of a language runner: <c>export</c>, <c>signature</c> and <c>expectedTrap</c>, every
/// image checked against the signature on load (test design 16, 18, 19).
/// </summary>
[TestFixture]
public sealed class RunnerVectorFileTests {

  private const string Head = """
    "schema": 1, "fixture": "f",
    "provenance": { "kind": "published", "source": "s", "detail": "d", "generatedBy": "hand" },
    """;

  private const string Divide = """
    "export": "divide",
    "signature": { "parameters": [ { "name": "a", "type": "i32" }, { "name": "b", "type": "i32" } ], "result": "i32" },
    """;

  private static VectorFile Parse(string call, string vectors) => VectorFile.Parse($$"""{ {{Head}} {{call}} "vectors": {{vectors}} }""", "t.vectors.json");

  private static string Problem(string call, string vectors) => Assert.Throws<ImageException>(() => Parse(call, vectors))!.Message;

  [Test]
  public void ARunnerFile_Loads() {
    var file = Parse(Divide, """
      [ { "name": "7 / 2", "input": { "hex": "0700000002000000" }, "expected": { "hex": "03000000" } },
        { "name": "1 / 0", "input": { "hex": "0100000000000000" }, "expectedTrap": "divide-by-zero" },
        { "name": "recorded", "input": { "hex": "0100000001000000" } } ]
      """);
    Assert.That((file.Export, file.Signature!.ToString()), Is.EqualTo(("divide", "(a: i32 in, b: i32 in) -> i32")));
    Assert.That((file.Vectors[0].ExpectedTrap, file.Vectors[1].ExpectedTrap, file.Vectors[1].Expected), Is.EqualTo(((TrapClass?)null, (TrapClass?)new TrapClass(TrapKind.DivideByZero), (byte[]?)null)));
    Assert.That((file.Vectors[2].Expected, file.Vectors[2].ExpectedTrap), Is.EqualTo(((byte[]?)null, (TrapClass?)null)), "test design 18: an input-only vector is a recording");
  }

  [Test]
  public void AByteStreamFile_HasNoSignature() {
    var file = Parse("", """[ { "name": "x", "input": { "hex": "01" }, "expected": { "hex": "0102" } } ]""");
    Assert.That((file.Export, file.Signature), Is.EqualTo(((string?)null, (CallSignature?)null)));
  }

  /// <summary>The invalid fixtures of the feature file, and the further ones of the test design.</summary>
  [TestCase("leftover.json", "vector 'five bytes': input image has 1 byte(s) left over after 'x'")]
  [TestCase("truncated.json", "vector 'two bytes': image of 'x' is truncated at byte 2")]
  [TestCase("bad-bool.json", "vector 'two': image of 'result': byte 02 is not a valid bool")]
  [TestCase("bad-tag.json", "vector 'tag three': image of 'result': tag 3 exceeds the 3 alternatives of union<null|i64|f64>")]
  [TestCase("fat-bigint.json", "vector '127 with a sign byte': image of 'n': bigint is not minimal")]
  [TestCase("unknown-type.json", "signature: unknown image type 'int'")]
  [TestCase("hex-and-trap.json", "vector 'both': both expected and expectedTrap are present")]
  [TestCase("missing-signature.json", "signature is required")]
  [TestCase("bad-utf8.json", "vector 'ff': image of 's': the utf8 bytes at 4 are not well-formed UTF-8")]
  public void AnInvalidFixture_IsRefusedWithPLY0064(string file, string problem) {
    var path = Path.Combine(ImageJson.InvalidFolder, file);
    var exception = Assert.Throws<ImageException>(() => VectorFile.Load(path))!;
    Assert.That(exception.Message, Is.EqualTo($"{path}: {problem}"));
    Assert.That((exception.ToDiagnostic().Code, exception.ToDiagnostic().Message), Is.EqualTo(("PLY0064", exception.Message)));
  }

  [Test]
  public void ASignatureWithoutExport_IsRefused()
    => Assert.That(Problem("""
      "signature": { "parameters": [], "result": "void" },
      """, """[ { "name": "x", "input": { "hex": "" } } ]"""), Is.EqualTo("t.vectors.json: export is required"));

  [Test]
  public void AnEmptyExport_IsRefused()
    => Assert.That(Assert.Throws<FormatException>(() => Parse("""
      "export": "", "signature": { "parameters": [], "result": "void" },
      """, "[]"))!.Message, Is.EqualTo("t.vectors.json: 'export' must be a non-empty string"));

  [TestCase("1", "vector 'x': expectedTrap must be a string")]
  [TestCase("\"boom\"", "vector 'x': 'boom' is not a trap class")]
  public void AMalformedTrap_IsRefused(string trap, string problem)
    => Assert.That(Problem(Divide, $$"""[ { "name": "x", "input": { "hex": "0100000000000000" }, "expectedTrap": {{trap}} } ]"""), Is.EqualTo($"t.vectors.json: {problem}"));

  [Test]
  public void AnExpectedOutputThatIsNoOutputImage_IsRefused()
    => Assert.That(Problem(Divide, """[ { "name": "x", "input": { "hex": "0100000001000000" }, "expected": { "hex": "01" } } ]"""),
      Is.EqualTo("t.vectors.json: vector 'x': image of 'result' is truncated at byte 1"));

  [Test]
  public void AnInvalidSignature_IsRefused()
    => Assert.That(Problem("""
      "export": "f", "signature": { "parameters": [ { "name": "a", "type": "void", "mode": "out" } ], "result": "void" },
      """, "[]"), Is.EqualTo("t.vectors.json: signature: parameter 'a' has mode 'out' and type 'void'"));

}
