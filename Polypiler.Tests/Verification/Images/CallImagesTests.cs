using System;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Tests.Verification.Images;

/// <summary><see cref="CallImages"/>: input and output images of a call, traps, and complete decoding (test design 2, 4, 16).</summary>
[TestFixture]
public sealed class CallImagesTests {

  private static readonly CallSignature _scenario = CallSignature.Parse(JsonNode.Parse(CallSignatureTests.Scenario));

  private static ImageValue.Integer Int(int value) => new(value);

  private static ImageValue.Sequence Bytes(params int[] values) => new([.. Array.ConvertAll(values, value => (ImageValue)Int(value))]);

  private static CallSignature Signature(string json) => CallSignature.Parse(JsonNode.Parse(json));

  /// <summary>Test design 2: the scenario's call with an in, an inout and an out parameter and a result.</summary>
  [Test]
  public void TheScenarioCall_HasItsImages() {
    Assert.That(Convert.ToHexStringLower(CallImages.Input(_scenario, [Int(7), Bytes(1, 2)])), Is.EqualTo("07000000020000000102"));
    Assert.That(Convert.ToHexStringLower(CallImages.Output(_scenario, new ImageValue.Boolean(true), [Bytes(9, 2), Int(513)])), Is.EqualTo("010200000009020102"));
  }

  [Test]
  public void TheScenarioImages_DecodeByParameter() {
    Assert.That(CallImages.Decode(_scenario, CallImage.Input, Convert.FromHexString("07000000020000000102")),
      Is.EqualTo(new[] { new NamedValue("a", Int(7)), new NamedValue("buf", Bytes(1, 2)) }));
    Assert.That(CallImages.Decode(_scenario, CallImage.Output, Convert.FromHexString("010200000009020102")),
      Is.EqualTo(new[] { new NamedValue("result", new ImageValue.Boolean(true)), new NamedValue("buf", Bytes(9, 2)), new NamedValue("r", Int(513)) }));
  }

  /// <summary>Test design 4: no parameters give an empty input image, a void result an output image of only the after-call values.</summary>
  [Test]
  public void EmptyPartsGiveEmptyImages() {
    var nothing = Signature("""{ "parameters": [], "result": "void" }""");
    Assert.That((CallImages.Input(nothing, []), CallImages.Output(nothing, null, [])), Is.EqualTo((Array.Empty<byte>(), Array.Empty<byte>())));
    var fill = Signature("""{ "parameters": [ { "name": "out", "type": "u8", "mode": "out" } ], "result": "void" }""");
    Assert.That((CallImages.Input(fill, []), CallImages.Output(fill, null, [Int(5)])), Is.EqualTo((Array.Empty<byte>(), new byte[] { 5 })));
  }

  [Test]
  public void WrongCounts_AreArgumentErrors() {
    Assert.That(Assert.Throws<ArgumentException>(() => CallImages.Input(_scenario, [Int(7)]))!.Message, Does.StartWith("2 value(s) expected, 1 given"));
    Assert.That(Assert.Throws<ArgumentException>(() => CallImages.Output(_scenario, new ImageValue.Boolean(true), [Int(1)]))!.Message, Does.StartWith("3 value(s) expected, 2 given"));
    Assert.That(Assert.Throws<ArgumentException>(() => CallImages.Output(_scenario, null, [Bytes(), Int(1)]))!.Message, Does.StartWith("the signature returns bool"));
    var nothing = Signature("""{ "parameters": [], "result": "void" }""");
    Assert.That(Assert.Throws<ArgumentException>(() => CallImages.Output(nothing, Int(1), []))!.Message, Does.StartWith("the signature returns void"));
  }

  [Test]
  public void AnArgumentThatDoesNotFit_IsRefused()
    => Assert.That(Assert.Throws<ImageException>(() => CallImages.Input(_scenario, [Int(-1), Bytes()]))!.Message, Is.EqualTo("value -1 does not fit u32"));

  /// <summary>The scenario of a width-less native value: a result that does not fit its type is the trap <c>result-type</c>, with no output image.</summary>
  [Test]
  public void AResultThatDoesNotFit_IsTheTrapResultType() {
    var identity = Signature("""{ "parameters": [ { "name": "x", "type": "u32" } ], "result": "u32" }""");
    Assert.That(CallImages.Outcome(identity, new ImageValue.Integer(4294967296), []), Is.EqualTo(new CallOutcome(null, new TrapClass(TrapKind.ResultType))));
    var returned = CallImages.Outcome(identity, Int(7), []);
    Assert.That((Convert.ToHexStringLower(returned.Image!), returned.Trap), Is.EqualTo(("07000000", (TrapClass?)null)));
  }

  [Test]
  public void ATrappedCall_HasNoOutputImage() {
    var trap = TrapClass.Parse("divide-by-zero");
    Assert.That(CallImages.Outcome(_scenario, new ImageValue.Trap(trap), []), Is.EqualTo(new CallOutcome(null, trap)));
  }

  /// <summary>Test design 16: bytes left over and truncated images name the parameter.</summary>
  [TestCase(CallImage.Input, "0700000000000000aabb", "input image has 2 byte(s) left over after 'buf'")]
  [TestCase(CallImage.Input, "0700", "image of 'a' is truncated at byte 2")]
  [TestCase(CallImage.Input, "070000000200000001", "image of 'buf' is truncated at byte 9")]
  [TestCase(CallImage.Output, "02", "image of 'result': byte 02 is not a valid bool")]
  [TestCase(CallImage.Output, "01000000000000ff", "output image has 1 byte(s) left over after 'r'")]
  public void IncompleteImages_AreRefused(CallImage image, string hex, string problem)
    => Assert.That(Assert.Throws<ImageException>(() => CallImages.Decode(_scenario, image, Convert.FromHexString(hex)))!.Message, Is.EqualTo(problem));

  [Test]
  public void LeftoverBytesWithoutParameters_NameNone()
    => Assert.That(Assert.Throws<ImageException>(() => CallImages.Decode(Signature("""{ "parameters": [], "result": "void" }"""), CallImage.Input, [1]))!.Message,
      Is.EqualTo("input image has 1 byte(s) left over"));

  [Test]
  public void Describe_ShowsDecodedValues() {
    Assert.That(CallImages.Describe(_scenario, Convert.FromHexString("010200000009020102")), Is.EqualTo("result=true, buf=[9, 2], r=513"));
    Assert.That(CallImages.Describe(_scenario, Convert.FromHexString("07000000020000000102"), CallImage.Input), Is.EqualTo("a=7, buf=[1, 2]"));
    Assert.That(CallImages.Describe(Signature("""{ "parameters": [], "result": "void" }"""), []), Is.EqualTo("(nothing)"));
    Assert.That(CallImages.Describe(_scenario, [2]), Is.EqualTo("malformed (image of 'result': byte 02 is not a valid bool)"));
  }

  [Test]
  public void Null_IsRefused() {
    Assert.Throws<ArgumentNullException>(() => CallImages.Input(null!, []));
    Assert.Throws<ArgumentNullException>(() => CallImages.Input(_scenario, null!));
    Assert.Throws<ArgumentNullException>(() => CallImages.Output(null!, null, []));
    Assert.Throws<ArgumentNullException>(() => CallImages.Output(_scenario, null, null!));
    Assert.Throws<ArgumentNullException>(() => CallImages.Decode(null!, CallImage.Input, []));
  }

}
