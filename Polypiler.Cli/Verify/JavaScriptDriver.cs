using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Cli.Verify;

/// <summary>What one call of an exported function gave, as an output image to compare.</summary>
/// <param name="Image">The output image; empty when the call trapped.</param>
/// <param name="Trap">The trap the call ended in, or <see langword="null"/>.</param>
public sealed record DriverOutcome(byte[] Image, TrapClass? Trap);

/// <summary>
/// The program that calls an exported function of a JavaScript module once per vector, and the two
/// ends of its protocol. The vectors' input images are decoded here, under the vector file's call
/// signature, and handed to the driver as typed JSON arguments on standard input, one array per
/// vector; the driver prints one JSON line per call with the raw JavaScript result, which is read back
/// here into the output image of the signature. A result that does not fit the declared type is the
/// trap <c>result-type</c>; a call that throws is the trap <c>other:&lt;constructor name&gt;</c>.
/// Parameters are <c>in</c> parameters of an integer, <c>bool</c>, float, <c>bytes</c>, <c>utf8</c>
/// or <c>utf16</c> type; integers of up to 32 bits are numbers, of 64 bits <c>BigInt</c>s, and
/// <c>bytes</c> a <c>Uint8Array</c>.
/// </summary>
public static class JavaScriptDriver {

  /// <summary>The driver's file name in the run's directory.</summary>
  public const string FileName = "polypiler-verify-driver.mjs";

  /// <summary>The driver: <c>node polypiler-verify-driver.mjs &lt;module&gt; &lt;export&gt;</c>.</summary>
  public const string Source = """
    // Calls one export of a module once per argument list read from standard input and prints one
    // JSON line per call: {"t":"f","v":<float64 bits>} for a number, "b" a BigInt, "z" a boolean,
    // "s" a string, "y" bytes, "u" undefined, "o" anything else, "x" a thrown value.
    import { readFileSync } from 'node:fs';
    import { resolve } from 'node:path';
    import { pathToFileURL } from 'node:url';

    const [entry, name] = process.argv.slice(2);
    const loaded = await import(pathToFileURL(resolve(entry)).href);
    const target = loaded[name] ?? loaded.default?.[name];
    if (typeof target !== 'function') {
      process.stderr.write(`'${entry}' exports no function '${name}'\n`);
      process.exit(3);
    }

    const bits = new BigUint64Array(1);
    const number = new Float64Array(bits.buffer);
    const hex = (bytes) => Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
    const argument = ({ t, v }) => {
      switch (t) {
        case 'n': return Number(v);
        case 'b': return BigInt(v);
        case 'f': bits[0] = BigInt('0x' + v); return number[0];
        case 'y': return Uint8Array.from(v.match(/../g) ?? [], (pair) => parseInt(pair, 16));
        default: return v;
      }
    };
    const isBytes = (value) => value instanceof Uint8Array || (Array.isArray(value) && value.every((item) => Number.isInteger(item) && item >= 0 && item < 256));
    const result = (value) => {
      switch (typeof value) {
        case 'undefined': return { t: 'u' };
        case 'number': number[0] = value; return { t: 'f', v: bits[0].toString(16).padStart(16, '0') };
        case 'bigint': return { t: 'b', v: value.toString() };
        case 'boolean': return { t: 'z', v: value };
        case 'string': return { t: 's', v: value };
        default: return isBytes(value) ? { t: 'y', v: hex(value) } : { t: 'o', v: typeof value };
      }
    };

    const lines = [];
    for (const args of JSON.parse(readFileSync(0, 'utf8'))) {
      try {
        lines.push(JSON.stringify(result(target(...args.map(argument)))));
      } catch (error) {
        lines.push(JSON.stringify({ t: 'x', v: String(error?.constructor?.name ?? typeof error) }));
      }
    }

    process.stdout.write(lines.join('\n') + '\n');
    """;

  /// <summary>Why the driver cannot make the calls of a signature, or <see langword="null"/> when it can.</summary>
  /// <param name="signature">The signature.</param>
  public static string? Unsupported(CallSignature signature) {
    ArgumentNullException.ThrowIfNull(signature);
    if (signature.Parameters.FirstOrDefault(parameter => parameter.Mode != ParameterMode.In) is { } written)
      return $"the parameter '{written.Name}' is '{(written.Mode == ParameterMode.Out ? "out" : "inout")}'; only 'in' parameters can be passed to a JavaScript function";

    var types = signature.Parameters.Select(parameter => parameter.Type);
    if (signature.Result is { } result)
      types = types.Append(result);

    return types.FirstOrDefault(type => !IsPlain(type)) is { } other
      ? $"the type '{other}' cannot be passed to or returned from a JavaScript function; integers, bool, f32, f64, bytes, utf8 and utf16 can"
      : null;
  }

  /// <summary>Standard input of the driver: one JSON array of typed arguments per vector.</summary>
  /// <param name="signature">The signature, which <see cref="Unsupported"/> accepts.</param>
  /// <param name="vectors">The vectors, whose inputs are input images of the signature.</param>
  /// <returns>The JSON text.</returns>
  /// <exception cref="ImageException">An input is not an input image of the signature.</exception>
  public static string Arguments(CallSignature signature, IReadOnlyList<TestVector> vectors) {
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentNullException.ThrowIfNull(vectors);
    var calls = new JsonArray();
    foreach (var vector in vectors) {
      var values = CallImages.Decode(signature, CallImage.Input, vector.Input);
      var call = new JsonArray();
      for (var index = 0; index < values.Count; ++index)
        call.Add(Argument(signature.Parameters[index].Type, values[index].Value));

      calls.Add(call);
    }

    return calls.ToJsonString();
  }

  private static JsonObject Argument(ImageType type, ImageValue value) => (type.Kind, value) switch {
    (ImageKind.Integer, ImageValue.Integer integer) => Tagged(type.Width <= 4 ? "n" : "b", integer.Value.ToString(CultureInfo.InvariantCulture)),
    (ImageKind.Float, ImageValue.FloatBits pattern) => Tagged("f", (type.Width == 4 ? BitConverter.DoubleToUInt64Bits(BitConverter.UInt32BitsToSingle((uint)pattern.Bits)) : pattern.Bits).ToString("x16", CultureInfo.InvariantCulture)),
    (_, ImageValue.Boolean boolean) => new JsonObject { ["t"] = "z", ["v"] = boolean.Value },
    (_, ImageValue.Bytes bytes) => Tagged("y", Hex.Format(bytes.Value)),
    (_, ImageValue.Text text) => Tagged("s", text.Value),
    _ => throw new ImageException($"a value of '{type}' cannot be passed to a JavaScript function")
  };

  private static JsonObject Tagged(string tag, string value) => new() { ["t"] = tag, ["v"] = value };

  /// <summary>Reads the driver's standard output: one outcome per vector.</summary>
  /// <param name="signature">The signature.</param>
  /// <param name="output">The driver's standard output.</param>
  /// <param name="count">The number of vectors.</param>
  /// <returns>The outcomes, or <see langword="null"/> when the output is not one JSON line per vector.</returns>
  public static IReadOnlyList<DriverOutcome>? Outcomes(CallSignature signature, string output, int count) {
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentNullException.ThrowIfNull(output);
    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (lines.Length != count)
      return null;

    var outcomes = new List<DriverOutcome>();
    foreach (var line in lines) {
      JsonObject reply;
      try {
        reply = JsonNode.Parse(line) as JsonObject ?? throw new FormatException();
      } catch (Exception exception) when (exception is JsonException or FormatException) {
        return null;
      }

      outcomes.Add(Outcome(signature, reply));
    }

    return outcomes;
  }

  /// <summary>The outcome of one call from the driver's line for it.</summary>
  /// <param name="signature">The signature.</param>
  /// <param name="reply">The parsed line.</param>
  public static DriverOutcome Outcome(CallSignature signature, JsonObject reply) {
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentNullException.ThrowIfNull(reply);
    var tag = Text(reply, "t");
    if (tag == "x") {
      var name = Text(reply, "v");
      return new([], new(TrapKind.Other, name.Length > 0 && !name.Any(char.IsWhiteSpace) ? name : "Error"));
    }

    if (signature.Result is not { } type)
      return tag == "u" ? new(CallImages.Output(signature, null, []), null) : ResultType;

    var value = Value(type, tag, reply["v"]);
    if (value is null)
      return ResultType;

    var outcome = CallImages.Outcome(signature, value, []);
    return new(outcome.Image ?? [], outcome.Trap);
  }

  private static readonly DriverOutcome ResultType = new([], new(TrapKind.ResultType));

  private static ImageValue? Value(ImageType type, string tag, JsonNode? raw) {
    var text = raw is JsonValue scalar && scalar.TryGetValue<string>(out var s) ? s : null;
    return (type.Kind, tag) switch {
      (ImageKind.Integer, "f") => Integral(Number(text)),
      (ImageKind.Integer, "b") => BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big) ? new ImageValue.Integer(big) : null,
      (ImageKind.Float, "f") => Float(type, Number(text)),
      (ImageKind.Boolean, "z") => raw is JsonValue flag && flag.TryGetValue<bool>(out var b) ? new ImageValue.Boolean(b) : null,
      (ImageKind.Bytes, "y") => text is not null && Hex.TryParse(text) is { } bytes ? new ImageValue.Bytes(bytes) : null,
      (ImageKind.Utf8 or ImageKind.Utf16, "s") => text is not null ? new ImageValue.Text(text) : null,
      _ => null
    };
  }

  private static double? Number(string? bits)
    => bits is not null && ulong.TryParse(bits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var pattern) ? BitConverter.UInt64BitsToDouble(pattern) : null;

  private static ImageValue? Integral(double? number)
    => number is { } value && double.IsFinite(value) && Math.Floor(value) == value ? new ImageValue.Integer(new BigInteger(value)) : null;

  private static ImageValue? Float(ImageType type, double? number) {
    if (number is not { } value)
      return null;
    if (type.Width == 8)
      return ImageValue.FloatBits.Of(value);

    var single = (float)value;
    return double.IsNaN(value) || (double)single == value ? ImageValue.FloatBits.Of(single) : null;
  }

  private static string Text(JsonObject reply, string name)
    => reply[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

  /// <summary>
  /// An output image as a FAIL line shows it: <c>trap &lt;class&gt;</c> for a call that trapped; each
  /// value of the signature's output image, an integer as <c>0x</c> and its upper-case digits at the
  /// width of its type (<c>0x024D0127</c>) and any other value as the image codec writes it; without a
  /// signature <c>0x</c> and the bytes; <c>(nothing)</c> for an empty image.
  /// </summary>
  /// <param name="signature">The signature of the vector file, or <see langword="null"/> for a program's byte stream.</param>
  /// <param name="image">The output image, which decodes under the signature.</param>
  /// <param name="trap">The trap the call ended in, or <see langword="null"/>.</param>
  /// <exception cref="ImageException">The image is not an output image of the signature.</exception>
  public static string Display(CallSignature? signature, byte[] image, TrapClass? trap) {
    ArgumentNullException.ThrowIfNull(image);
    if (trap is not null)
      return $"trap {trap}";
    if (signature is null)
      return image.Length == 0 ? "(nothing)" : "0x" + Convert.ToHexString(image);

    var values = CallImages.Decode(signature, CallImage.Output, image);
    return values.Count == 0 ? "(nothing)" : string.Join(", ", values.Select(value => Shown(signature.Result!, value.Value)));
  }

  private static string Shown(ImageType type, ImageValue value) {
    if (type.Kind != ImageKind.Integer || value is not ImageValue.Integer integer)
      return value.ToString()!;

    var unsigned = integer.Value.Sign < 0 ? integer.Value + (BigInteger.One << (8 * type.Width)) : integer.Value;
    return "0x" + unsigned.ToString("X", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(2 * type.Width, '0');
  }

  private static bool IsPlain(ImageType type) => type.Kind is ImageKind.Integer or ImageKind.Boolean or ImageKind.Float or ImageKind.Bytes or ImageKind.Utf8 or ImageKind.Utf16;

}
