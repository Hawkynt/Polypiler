using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Verify;

/// <summary>
/// The steps of <c>tests/features/infra/ver-images.feature</c>. Values are written as the scenarios
/// write them (<c>"é"</c>, <c>U+1F600</c>, <c>[1, 258]</c>, <c>{x: 1, y: 2}</c>, <c>the f64 1.5</c>)
/// and read under the image type of their row.
/// </summary>
[Binding, Scope(Feature = "Value images and typed call signatures")]
public sealed class ImageSteps {

  private ImageType? _type;
  private ImageValue? _value;
  private byte[]? _image;
  private ImageException? _failure;
  private CallSignature? _signature;
  private readonly List<ImageValue> _arguments = [];
  private ImageValue? _result;
  private readonly List<ImageValue> _afterCall = [];
  private VectorFile? _file;
  private string? _path;
  private CallOutcome? _outcome;

  private ImageType Type => this._type ?? throw new InvalidOperationException("no image type was given");

  private CallSignature Signature => this._signature ?? throw new InvalidOperationException("no signature was given");

  private static string Repository(string path) => Path.Combine(PrototypeFixture.RepositoryRoot, path);

  [Given("^the image type " + StepText.Quoted + " and the value (.+)$")]
  public void TypeAndValue(string type, string value) {
    this._type = ImageType.Parse(type);
    this._value = Value(this._type, value);
  }

  [When("^the value is encoded$")]
  public void Encode() {
    try {
      this._image = ImageCodec.Encode(this.Type, this._value!);
    } catch (ImageException exception) {
      this._failure = exception;
    }
  }

  [Then("^the image is " + StepText.Quoted + "$")]
  public void ImageIs(string hex) => Assert.That(Convert.ToHexStringLower(this._image!), Is.EqualTo(hex));

  [Then("^decoding " + StepText.Quoted + " as " + StepText.Quoted + " gives the value back and consumes all bytes$")]
  public void DecodesBack(string hex, string type) {
    var bytes = Convert.FromHexString(hex);
    Assert.That(ImageCodec.Decode(ImageType.Parse(type), bytes, out var consumed), Is.EqualTo(this._value));
    Assert.That(consumed, Is.EqualTo(bytes.Length));
  }

  [Then("^encoding fails with " + StepText.Quoted + "$")]
  public void EncodingFails(string text) {
    Assert.That(this._failure, Is.Not.Null, "the value was encoded");
    var diagnostic = this._failure!.ToDiagnostic();
    Assert.That($"{diagnostic.Code}: {diagnostic.Message}", Is.EqualTo(text));
  }

  [Given("^the signature " + StepText.Quoted + "$")]
  public void SignatureIs(string text) => this._signature = ParseSignature(text);

  [Given(@"^the arguments (\w+) = (\S+) and (\w+) = (\[[^\]]*\])$")]
  public void Arguments(string first, string firstValue, string second, string secondValue) {
    this._arguments.Add(Value(this.Parameter(first).Type, firstValue));
    this._arguments.Add(Value(this.Parameter(second).Type, secondValue));
  }

  [When(@"^the function returns (\S+), leaves (\w+) = (\[[^\]]*\]) and sets (\w+) = (\S+)$")]
  public void FunctionReturns(string result, string left, string leftValue, string set, string setValue) {
    this._result = Value(this.Signature.Result!, result);
    this._afterCall.Add(Value(this.Parameter(left).Type, leftValue));
    this._afterCall.Add(Value(this.Parameter(set).Type, setValue));
  }

  [Then("^the input image is " + StepText.Quoted + "$")]
  public void InputImage(string hex) => Assert.That(Convert.ToHexStringLower(CallImages.Input(this.Signature, this._arguments)), Is.EqualTo(hex));

  [Then("^the output image is " + StepText.Quoted + "$")]
  public void OutputImage(string hex) => Assert.That(Convert.ToHexStringLower(CallImages.Output(this.Signature, this._result, this._afterCall)), Is.EqualTo(hex));

  [Given("^the vector file " + StepText.Quoted + " for a JavaScript function$")]
  public void RunnerFile(string path) {
    this._file = VectorFile.Load(Repository(path));
    this._signature = this._file.Signature ?? throw new InvalidOperationException($"{path} has no signature");
  }

  /// <summary>
  /// A JavaScript driver: the input image decodes under the signature, the identity function returns
  /// a <c>number</c>, and the number becomes the result value - an integer where it is integral, the
  /// bits of a binary64 otherwise. Neither fits the declared <c>u32</c> when it is out of range.
  /// </summary>
  [When("^the driver receives the input image " + StepText.Quoted + @" and the function returns the number (\S+)$")]
  public void DriverRuns(string hex, string number) {
    var input = CallImages.Decode(this.Signature, CallImage.Input, Convert.FromHexString(hex));
    Assert.That(input.Select(value => value.Name), Is.EqualTo(new[] { "x" }));
    var native = double.Parse(number, CultureInfo.InvariantCulture);
    ImageValue result = double.IsInteger(native) ? new ImageValue.Integer(new BigInteger(native)) : ImageValue.FloatBits.Of(native);
    this._outcome = CallImages.Outcome(this.Signature, result, []);
  }

  [Then("^the outcome is the trap " + StepText.Quoted + "$")]
  public void OutcomeIsTrap(string trap) {
    Assert.That(this._outcome!.Trap, Is.EqualTo(TrapClass.Parse(trap)));
    Assert.That(this._file!.Vectors.Select(vector => vector.ExpectedTrap), Does.Contain(this._outcome.Trap), "the vector file expects that trap");
  }

  [Then("^no output image is produced$")]
  public void NoOutputImage() => Assert.That(this._outcome!.Image, Is.Null);

  [Given("^the vector file " + StepText.Quoted + "$")]
  public void VectorFileIs(string path) => this._path = Repository(path);

  [When("^it is loaded$")]
  public void Load() {
    try {
      VectorFile.Load(this._path!);
    } catch (ImageException exception) {
      this._failure = exception;
    }
  }

  [Then("^loading fails with " + StepText.Quoted + " and the message contains " + StepText.Quoted + "$")]
  public void LoadingFails(string code, string text) {
    Assert.That(this._failure, Is.Not.Null, "the file loaded");
    var diagnostic = this._failure!.ToDiagnostic();
    Assert.That(diagnostic.Code, Is.EqualTo(code));
    Assert.That(diagnostic.Message, Does.Contain(text));
  }

  private CallParameter Parameter(string name) => this.Signature.Parameters.Single(parameter => parameter.Name == name);

  /// <summary>Reads <c>(a: u32 in, buf: seq&lt;u8&gt; inout) -&gt; bool</c> through the JSON form the vector files use.</summary>
  private static CallSignature ParseSignature(string text) {
    var match = Regex.Match(text, @"^\((.*)\) -> (.+)$");
    var parameters = new JsonArray();
    foreach (var parameter in Split(match.Groups[1].Value)) {
      var parts = Regex.Match(parameter, @"^(\w+): (.+) (in|inout|out)$");
      parameters.Add(new JsonObject { ["name"] = parts.Groups[1].Value, ["type"] = parts.Groups[2].Value, ["mode"] = parts.Groups[3].Value });
    }

    return CallSignature.Parse(new JsonObject { ["parameters"] = parameters, ["result"] = match.Groups[2].Value });
  }

  /// <summary>Splits at the commas outside brackets.</summary>
  private static List<string> Split(string text) {
    var parts = new List<string>();
    var (depth, start) = (0, 0);
    for (var i = 0; i < text.Length; ++i)
      switch (text[i]) {
        case '<' or '[' or '{':
          ++depth;
          break;
        case '>' or ']' or '}':
          --depth;
          break;
        case ',' when depth == 0:
          parts.Add(text[start..i].Trim());
          start = i + 1;
          break;
      }

    if (text.Trim().Length > 0)
      parts.Add(text[start..].Trim());
    return parts;
  }

  private static ImageValue Value(ImageType type, string text) {
    switch (type.Kind) {
      case ImageKind.Integer or ImageKind.Char16 or ImageKind.Enum or ImageKind.BigInt:
        return new ImageValue.Integer(BigInteger.Parse(text, CultureInfo.InvariantCulture));
      case ImageKind.Boolean:
        return new ImageValue.Boolean(bool.Parse(text));
      case ImageKind.Float: {
        var number = double.Parse(text, CultureInfo.InvariantCulture);
        return type.Width == 4 ? ImageValue.FloatBits.Of((float)number) : ImageValue.FloatBits.Of(number);
      }
      case ImageKind.Utf8 or ImageKind.Utf16:
        return new ImageValue.Text(text.StartsWith("U+", StringComparison.Ordinal) ? char.ConvertFromUtf32(int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : text.Trim('"'));
      case ImageKind.Sequence:
        return new ImageValue.Sequence([.. Split(text[1..^1]).Select(item => Value(type.Element!, item))]);
      case ImageKind.Optional:
        return text == "absent" ? ImageValue.Optional.Absent : new ImageValue.Optional(Value(type.Element!, text));
      case ImageKind.Record: {
        var fields = Split(text[1..^1]).Select(field => field.Split(": ", 2)).ToDictionary(pair => pair[0], pair => pair[1]);
        return new ImageValue.Record([.. type.Fields.Select(field => Value(field.Type, fields[field.Name]))]);
      }
      case ImageKind.Union: {
        var match = Regex.Match(text, @"^the (\S+) (.+)$");
        var tag = type.Fields.Select(field => field.Type.ToString()).ToList().IndexOf(match.Groups[1].Value);
        return new ImageValue.Union(tag, Value(type.Fields[tag].Type, match.Groups[2].Value));
      }
      default:
        throw new NotSupportedException($"the scenarios write no value of {type}");
    }
  }

}
