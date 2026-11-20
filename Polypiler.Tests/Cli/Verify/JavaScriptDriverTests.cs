using System;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Cli.Verify;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Tests.Cli.Verify;

/// <summary>The two ends of the driver protocol: typed arguments from input images, output images from the driver's lines.</summary>
[TestFixture]
public sealed class JavaScriptDriverTests {

  private static CallSignature Signature(string json) => CallSignature.Parse(JsonNode.Parse(json));

  private static CallSignature Returning(string result) => Signature($$"""{ "parameters": [], "result": {{result}} }""");

  private static DriverOutcome Outcome(CallSignature signature, string line) => JavaScriptDriver.Outcome(signature, JsonNode.Parse(line)!.AsObject());

  private static string Bits(double value) => BitConverter.DoubleToUInt64Bits(value).ToString("x16");

  [TestCase("""{ "parameters": [ { "name": "x", "type": "u32", "mode": "in" } ], "result": "u32" }""", null)]
  [TestCase("""{ "parameters": [], "result": "void" }""", null)]
  [TestCase("""{ "parameters": [ { "name": "x", "type": "u32", "mode": "inout" } ], "result": "void" }""", "the parameter 'x' is 'inout'; only 'in' parameters can be passed to a JavaScript function")]
  [TestCase("""{ "parameters": [ { "name": "x", "type": "u32", "mode": "in" } ], "result": "opt<u32>" }""", "the type 'opt<u32>' cannot be passed to or returned from a JavaScript function; integers, bool, f32, f64, bytes, utf8 and utf16 can")]
  public void Unsupported_NamesWhatTheDriverCannotPass(string signature, string? reason)
    => Assert.That(JavaScriptDriver.Unsupported(Signature(signature)), Is.EqualTo(reason));

  [Test]
  public void Arguments_AreTypedByTheSignature() {
    var signature = Signature("""
      { "parameters": [ { "name": "a", "type": "i32", "mode": "in" }, { "name": "b", "type": "u64", "mode": "in" }, { "name": "c", "type": "f32", "mode": "in" },
        { "name": "d", "type": "f64", "mode": "in" }, { "name": "e", "type": "bool", "mode": "in" }, { "name": "f", "type": "bytes", "mode": "in" },
        { "name": "g", "type": "utf8", "mode": "in" } ], "result": "void" }
      """);
    var input = CallImages.Input(signature, [
      new ImageValue.Integer(-2), new ImageValue.Integer(ulong.MaxValue), ImageValue.FloatBits.Of(1.5f), ImageValue.FloatBits.Of(-0.0),
      new ImageValue.Boolean(true), new ImageValue.Bytes([1, 255]), new ImageValue.Text("é")
    ]);

    var json = JsonNode.Parse(JavaScriptDriver.Arguments(signature, [new TestVector("v", input, null)]))!.AsArray();

    Assert.That(json.Single()!.ToJsonString(), Is.EqualTo(
      $$"""[{"t":"n","v":"-2"},{"t":"b","v":"18446744073709551615"},{"t":"f","v":"{{Bits(1.5)}}"},{"t":"f","v":"{{Bits(-0.0)}}"},{"t":"z","v":true},{"t":"y","v":"01ff"},{"t":"s","v":"\u00E9"}]"""));
  }

  [Test]
  public void Arguments_OfATypeTheDriverCannotPass_Throw() {
    var signature = Signature("""{ "parameters": [ { "name": "x", "type": "seq<u8>", "mode": "in" } ], "result": "void" }""");
    var input = CallImages.Input(signature, [new ImageValue.Sequence([new ImageValue.Integer(1)])]);

    Assert.That(() => JavaScriptDriver.Arguments(signature, [new TestVector("v", input, null)]), Throws.InstanceOf<ImageException>());
  }

  [TestCase("\"u32\"", "{\"t\":\"f\",\"v\":\"\"}")]
  [TestCase("\"u32\"", "{\"t\":\"o\",\"v\":\"object\"}")]
  [TestCase("\"u32\"", "{\"t\":\"z\",\"v\":true}")]
  [TestCase("\"u32\"", "{\"t\":\"b\",\"v\":\"x\"}")]
  [TestCase("\"u8\"", "{\"t\":\"b\",\"v\":\"300\"}")]
  [TestCase("\"bool\"", "{\"t\":\"z\",\"v\":\"yes\"}")]
  [TestCase("\"bytes\"", "{\"t\":\"y\",\"v\":\"0\"}")]
  [TestCase("\"bytes\"", "{\"t\":\"y\",\"v\":7}")]
  [TestCase("\"utf8\"", "{\"t\":\"s\",\"v\":7}")]
  [TestCase("\"f64\"", "{\"t\":\"f\",\"v\":\"zz\"}")]
  [TestCase("\"u32\"", "{\"t\":\"u\"}")]
  [TestCase("\"void\"", "{\"t\":\"f\",\"v\":\"0000000000000000\"}")]
  [TestCase("\"u32\"", "{}")]
  public void AResultThatDoesNotFit_IsTheTrapResultType(string result, string line)
    => Assert.That(Outcome(Returning(result), line), Is.EqualTo(new DriverOutcome([], new(TrapKind.ResultType))).Using<DriverOutcome>(Same));

  private static bool Same(DriverOutcome left, DriverOutcome right) => left.Image.SequenceEqual(right.Image) && Equals(left.Trap, right.Trap);

  [TestCase(0.5)]
  [TestCase(double.PositiveInfinity)]
  [TestCase(4294967296.0)]
  public void ANumberThatIsNoU32_IsTheTrapResultType(double value)
    => Assert.That(Outcome(Returning("\"u32\""), $$"""{"t":"f","v":"{{Bits(value)}}"}""").Trap, Is.EqualTo(new TrapClass(TrapKind.ResultType)));

  [TestCase("\"u32\"", "{\"t\":\"f\",\"v\":\"41e0000000000000\"}", "00000080")]
  [TestCase("\"i64\"", "{\"t\":\"b\",\"v\":\"-1\"}", "ffffffffffffffff")]
  [TestCase("\"f64\"", "{\"t\":\"f\",\"v\":\"3ff8000000000000\"}", "000000000000f83f")]
  [TestCase("\"f32\"", "{\"t\":\"f\",\"v\":\"3ff8000000000000\"}", "0000c03f")]
  [TestCase("\"f32\"", "{\"t\":\"f\",\"v\":\"7ff8000000000000\"}", "0000c07f")]
  [TestCase("\"bool\"", "{\"t\":\"z\",\"v\":true}", "01")]
  [TestCase("\"bytes\"", "{\"t\":\"y\",\"v\":\"01ff\"}", "0200000001ff")]
  [TestCase("\"utf8\"", "{\"t\":\"s\",\"v\":\"a\"}", "0100000061")]
  [TestCase("\"utf16\"", "{\"t\":\"s\",\"v\":\"a\"}", "010000006100")]
  [TestCase("\"void\"", "{\"t\":\"u\"}", "")]
  public void AResultThatFits_IsItsOutputImage(string result, string line, string image) {
    var outcome = Outcome(Returning(result), line);

    Assert.That((Hex.Format(outcome.Image), outcome.Trap), Is.EqualTo((image, (TrapClass?)null)));
  }

  [Test]
  public void AnF64ThatIsNoF32_IsTheTrapResultType()
    => Assert.That(Outcome(Returning("\"f32\""), $$"""{"t":"f","v":"{{Bits(0.1)}}"}""").Trap, Is.EqualTo(new TrapClass(TrapKind.ResultType)));

  [TestCase("RangeError", "other:RangeError")]
  [TestCase("my error", "other:Error")]
  [TestCase("", "other:Error")]
  public void AThrownValue_IsATrapOfItsName(string name, string trap)
    => Assert.That(Outcome(Returning("\"u32\""), $$"""{"t":"x","v":"{{name}}"}""").Trap!.ToString(), Is.EqualTo(trap));

  [Test]
  public void Outcomes_ReadOneLinePerVector() {
    var outcomes = JavaScriptDriver.Outcomes(Returning("\"bool\""), "{\"t\":\"z\",\"v\":true}\r\n\n{\"t\":\"z\",\"v\":false}\n", 2);

    Assert.That(outcomes!.Select(outcome => Hex.Format(outcome.Image)), Is.EqualTo(new[] { "01", "00" }));
  }

  [TestCase("{\"t\":\"z\",\"v\":true}\n", 2)]
  [TestCase("not json\n", 1)]
  [TestCase("[1]\n", 1)]
  [TestCase("", 1)]
  public void Outcomes_OfAnOutputThatIsNoLinePerVector_AreNone(string output, int count)
    => Assert.That(JavaScriptDriver.Outcomes(Returning("\"bool\""), output, count), Is.Null);

  [TestCase("\"u32\"", "27014d02", null, "0x024D0127")]
  [TestCase("\"i16\"", "feff", null, "0xFFFE")]
  [TestCase("\"u8\"", "00", null, "0x00")]
  [TestCase("\"bool\"", "01", null, "true")]
  [TestCase("\"void\"", "", null, "(nothing)")]
  [TestCase("\"u32\"", "", "bounds", "trap bounds")]
  public void Display_ShowsTheValuesOfAnImage(string result, string image, string? trap, string shown)
    => Assert.That(JavaScriptDriver.Display(Returning(result), Convert.FromHexString(image), trap is null ? null : TrapClass.Parse(trap)), Is.EqualTo(shown));

  [TestCase("", "(nothing)")]
  [TestCase("0aff", "0x0AFF")]
  public void Display_WithoutASignature_ShowsTheBytes(string image, string shown)
    => Assert.That(JavaScriptDriver.Display(null, Convert.FromHexString(image), null), Is.EqualTo(shown));

  [Test]
  public void TheArguments_AreChecked() {
    var signature = Returning("\"void\"");
    Assert.Multiple(() => {
      Assert.That(() => JavaScriptDriver.Unsupported(null!), Throws.ArgumentNullException);
      Assert.That(() => JavaScriptDriver.Arguments(null!, []), Throws.ArgumentNullException);
      Assert.That(() => JavaScriptDriver.Arguments(signature, null!), Throws.ArgumentNullException);
      Assert.That(() => JavaScriptDriver.Outcomes(null!, "", 0), Throws.ArgumentNullException);
      Assert.That(() => JavaScriptDriver.Outcomes(signature, null!, 0), Throws.ArgumentNullException);
      Assert.That(() => JavaScriptDriver.Outcome(null!, new JsonObject()), Throws.ArgumentNullException);
      Assert.That(() => JavaScriptDriver.Outcome(signature, null!), Throws.ArgumentNullException);
      Assert.That(() => JavaScriptDriver.Display(signature, null!, null), Throws.ArgumentNullException);
    });
  }

}
