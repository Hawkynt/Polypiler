using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Polypiler.Core.Verification.Images;

/// <summary>Which image of a call.</summary>
public enum CallImage {

  /// <summary>The input image: the <c>in</c> and <c>inout</c> arguments.</summary>
  Input,

  /// <summary>The output image: the result, then the <c>inout</c> and <c>out</c> values after the call.</summary>
  Output

}

/// <summary>A decoded value of a call image with the parameter it belongs to; <c>result</c> names the result.</summary>
/// <param name="Name">The parameter, or <c>result</c>.</param>
/// <param name="Value">The value.</param>
public sealed record NamedValue(string Name, ImageValue Value) {

  /// <inheritdoc />
  public override string ToString() => $"{this.Name}={this.Value}";

}

/// <summary>What a call ended in: an output image, or a trap that has none.</summary>
/// <param name="Image">The output image; <see langword="null"/> when the call trapped.</param>
/// <param name="Trap">The trap; <see langword="null"/> when the call returned.</param>
public sealed record CallOutcome(byte[]? Image, TrapClass? Trap);

/// <summary>
/// The input and output images of a call under its signature. The input image is the images of the
/// <c>in</c> and <c>inout</c> arguments in parameter order; the output image is the result image
/// (nothing for <c>void</c>) followed by the images, after the call, of the <c>inout</c> and
/// <c>out</c> parameters in parameter order.
/// </summary>
public static class CallImages {

  /// <summary>The name of the result in decoded output images and in messages.</summary>
  public const string ResultName = "result";

  /// <summary>The input image of a call.</summary>
  /// <param name="signature">The signature.</param>
  /// <param name="arguments">The values of the <c>in</c> and <c>inout</c> parameters, in parameter order.</param>
  /// <returns>The image.</returns>
  /// <exception cref="ArgumentException">The number of arguments differs from the number of input parameters.</exception>
  /// <exception cref="ImageException">An argument does not fit its type.</exception>
  public static byte[] Input(CallSignature signature, IReadOnlyList<ImageValue> arguments) {
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentNullException.ThrowIfNull(arguments);
    return Concatenate([.. signature.Inputs.Select(parameter => parameter.Type)], arguments, nameof(arguments));
  }

  /// <summary>The output image of a call that returned.</summary>
  /// <param name="signature">The signature.</param>
  /// <param name="result">The result; <see langword="null"/> for a <c>void</c> signature.</param>
  /// <param name="afterCall">The values after the call of the <c>inout</c> and <c>out</c> parameters, in parameter order.</param>
  /// <returns>The image.</returns>
  /// <exception cref="ArgumentException">The result is given for <c>void</c> or missing otherwise, or the number of values differs from the number of output parameters.</exception>
  /// <exception cref="ImageException">A value does not fit its type.</exception>
  public static byte[] Output(CallSignature signature, ImageValue? result, IReadOnlyList<ImageValue> afterCall) {
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentNullException.ThrowIfNull(afterCall);
    if ((signature.Result is null) != (result is null))
      throw new ArgumentException(result is null ? $"the signature returns {signature.Result}" : "the signature returns void", nameof(result));

    var types = signature.Outputs.Select(parameter => parameter.Type).ToList();
    var values = afterCall.ToList();
    if (signature.Result is { } type) {
      types.Insert(0, type);
      values.Insert(0, result!);
    }

    return Concatenate(types, values, nameof(afterCall));
  }

  /// <summary>
  /// The outcome of a call as a driver reports it: the output image, or a trap - the trap the
  /// <paramref name="result"/> is, or <c>result-type</c> when a native value does not fit its
  /// declared image type (a JavaScript <c>number</c> 4294967296 for <c>u32</c>).
  /// </summary>
  /// <param name="signature">The signature.</param>
  /// <param name="result">The result, a <see cref="ImageValue.Trap"/> for a call that trapped; <see langword="null"/> for <c>void</c>.</param>
  /// <param name="afterCall">The values after the call of the <c>inout</c> and <c>out</c> parameters; ignored when the call trapped.</param>
  /// <returns>The outcome.</returns>
  public static CallOutcome Outcome(CallSignature signature, ImageValue? result, IReadOnlyList<ImageValue> afterCall) {
    if (result is ImageValue.Trap trap)
      return new(null, trap.Class);

    try {
      return new(Output(signature, result, afterCall), null);
    } catch (ImageException) {
      return new(null, new(TrapKind.ResultType));
    }
  }

  /// <summary>Decodes an input or output image completely.</summary>
  /// <param name="signature">The signature.</param>
  /// <param name="image">Which image the bytes are.</param>
  /// <param name="bytes">The bytes.</param>
  /// <returns>The values, named by their parameters (the result as <c>result</c>), in image order.</returns>
  /// <exception cref="ImageException">
  /// The bytes are not that image: <c>image of '&lt;parameter&gt;' is truncated at byte &lt;offset&gt;</c>,
  /// <c>input image has &lt;n&gt; byte(s) left over after '&lt;parameter&gt;'</c>, or a malformed value.
  /// </exception>
  public static IReadOnlyList<NamedValue> Decode(CallSignature signature, CallImage image, ReadOnlySpan<byte> bytes) {
    ArgumentNullException.ThrowIfNull(signature);
    var slots = Slots(signature, image);
    var values = new List<NamedValue>();
    var position = 0;
    foreach (var (name, type) in slots)
      values.Add(new(name, ImageCodec.Read(type, bytes, ref position, $"image of '{name}'")));

    if (position != bytes.Length) {
      var after = slots.Count == 0 ? "" : $" after '{slots[^1].Name}'";
      throw new ImageException($"{(image == CallImage.Input ? "input" : "output")} image has {bytes.Length - position} byte(s) left over{after}");
    }

    return values;
  }

  /// <summary>An image as its decoded values, for mismatch messages: <c>a=7, buf=[1, 2]</c>; a malformed image as <c>malformed (&lt;reason&gt;)</c>.</summary>
  /// <param name="signature">The signature.</param>
  /// <param name="bytes">The bytes.</param>
  /// <param name="image">Which image the bytes are; the output image unless stated.</param>
  public static string Describe(CallSignature signature, ReadOnlySpan<byte> bytes, CallImage image = CallImage.Output) {
    try {
      var values = Decode(signature, image, bytes);
      return values.Count == 0 ? "(nothing)" : string.Join(", ", values);
    } catch (ImageException exception) {
      return $"malformed ({exception.Message})";
    }
  }

  private static List<(string Name, ImageType Type)> Slots(CallSignature signature, CallImage image) {
    if (image == CallImage.Input)
      return [.. signature.Inputs.Select(parameter => (parameter.Name, parameter.Type))];

    var slots = signature.Outputs.Select(parameter => (parameter.Name, parameter.Type)).ToList();
    if (signature.Result is { } result)
      slots.Insert(0, (ResultName, result));
    return slots;
  }

  private static byte[] Concatenate(IReadOnlyList<ImageType> types, IReadOnlyList<ImageValue> values, string argument) {
    if (types.Count != values.Count)
      throw new ArgumentException($"{types.Count} value(s) expected, {values.Count} given", argument);

    var output = new MemoryStream();
    for (var i = 0; i < types.Count; ++i)
      output.Write(ImageCodec.Encode(types[i], values[i]));

    return output.ToArray();
  }

}
