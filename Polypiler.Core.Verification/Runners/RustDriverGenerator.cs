using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Runners;

/// <summary>An exported function a driver calls: its path below the crate root and its signature.</summary>
/// <param name="Path">The path, such as <c>numbers::gcd</c>; the function must be <c>pub</c> along it.</param>
/// <param name="Signature">The images it takes and gives; the runner never derives it from the source.</param>
public sealed record RustExport(string Path, CallSignature Signature) {

  /// <summary>
  /// Whether the scalars go in and come out through <c>From</c>: <c>a0.into()</c> and
  /// <c>let result: T = f(…).into();</c>, for an export whose scalars are newtypes over the integers of
  /// the images, such as the enums of a crate.
  /// </summary>
  public bool Converted { get; init; }

  /// <summary>
  /// Whether the export returns a <c>Result</c>, as every function a trap can leave does under the
  /// Rust backend's trap policy <c>result</c>: an <c>Err</c> is the outcome <c>?</c> and the error's
  /// <c>Display</c>, the value of an <c>Ok</c> the result.
  /// </summary>
  public bool Fallible { get; init; }

}

/// <summary>
/// Generates <c>src/bin/polypiler_driver.rs</c>, the program that calls the exports of a crate on input
/// images. It reads one call per line, <c>&lt;export&gt; &lt;input image in hex&gt;</c>, decodes the
/// arguments from the image (the little-endian encoding of <see cref="ImageCodec"/>), declares each as
/// the Rust type of its image type and calls the function inside <c>std::panic::catch_unwind</c> with
/// a panic hook that prints nothing. It writes one line per call: the output image in hex, or
/// <c>!</c> and the first line of the panic message. Since each argument is declared with the type the
/// signature names, a signature that does not fit the declaration does not compile, with the error in
/// the driver. A line it cannot read ends it with exit code 2. An export that returns a <c>Result</c>
/// (<see cref="RustExport.Fallible"/>) writes the value of an <c>Ok</c> as any other result, and
/// <c>?</c> with what its error displays for an <c>Err</c>. A <c>seq</c> of scalars is a vector,
/// passed borrowed as <c>&amp;v</c>, or as <c>&amp;mut v</c> where it is <c>inout</c>, whose contents
/// after the call follow the result in the output image; a <c>seq</c> result is read as a slice. An
/// <c>opt&lt;T&gt;</c> is an <c>Option</c>: of a scalar <c>Option&lt;T&gt;</c>, of a <c>seq</c>
/// <c>Option&lt;Vec&lt;T&gt;&gt;</c> passed as <c>a.as_deref()</c>, which the function only reads; its
/// image is the presence byte and the value.
/// </summary>
public static class RustDriverGenerator {

  /// <summary>The name of the driver's binary target.</summary>
  public const string Binary = "polypiler_driver";

  /// <summary>The path of the driver below the package.</summary>
  public const string FilePath = "src/bin/" + Binary + ".rs";

  /// <summary>The source of the driver.</summary>
  /// <param name="crate">The name of the library crate, the package name with <c>-</c> as <c>_</c>.</param>
  /// <param name="exports">The functions, numbered in order.</param>
  /// <returns>The source.</returns>
  /// <exception cref="ArgumentException">A signature has an <c>out</c> parameter, an <c>inout</c> one that is no <c>seq</c>, or an image type that is neither a scalar nor a <c>seq</c> of scalars.</exception>
  public static string Generate(string crate, IReadOnlyList<RustExport> exports) {
    ArgumentNullException.ThrowIfNull(crate);
    ArgumentNullException.ThrowIfNull(exports);
    var text = new StringBuilder();
    text.Append("use std::io::{self, BufRead, Write};\n\n");
    text.Append("fn hex(bytes: &[u8]) -> String {\n    bytes.iter().map(|b| format!(\"{:02x}\", b)).collect()\n}\n\n");
    text.Append("fn unhex(text: &str) -> Vec<u8> {\n    (0..text.len() / 2).map(|i| u8::from_str_radix(&text[2 * i..2 * i + 2], 16).unwrap_or_else(|_| std::process::exit(2))).collect()\n}\n\n");
    if (exports.Any(export => Sequences(export.Signature)))
      text.Append("fn take<'a>(input: &'a [u8], at: &mut usize, n: usize) -> &'a [u8] {\n    let bytes = input.get(*at..*at + n).unwrap_or_else(|| std::process::exit(2));\n    *at += n;\n    bytes\n}\n\n");

    text.Append("fn call(export: usize, input: &[u8]) -> String {\n    match export {\n");
    for (var index = 0; index < exports.Count; ++index) {
      var export = exports[index];
      var signature = export.Signature;
      if (Sequences(signature)) {
        text.Append(CultureInfo.InvariantCulture, $"        {index} => {{\n");
        Sequenced(text, crate, export);
        text.Append("        }\n");
        continue;
      }

      var width = signature.Parameters.Sum(parameter => Width(parameter));
      text.Append(CultureInfo.InvariantCulture, $"        {index} => {{\n");
      text.Append(CultureInfo.InvariantCulture, $"            if input.len() != {width} {{\n                std::process::exit(2);\n            }}\n");
      var offset = 0;
      var arguments = new List<string>();
      foreach (var parameter in signature.Parameters) {
        var size = Width(parameter);
        var name = "a" + arguments.Count.ToString(CultureInfo.InvariantCulture);
        text.Append(CultureInfo.InvariantCulture, $"            let {name}: {RustType(parameter.Type)} = {Decode(parameter.Type, offset, size)};\n");
        arguments.Add(export.Converted ? name + ".into()" : name);
        offset += size;
      }

      var call = $"{crate}::{export.Path}({string.Join(", ", arguments)})";
      var into = export.Converted && signature.Result is not null ? ".into()" : "";
      text.Append(CultureInfo.InvariantCulture, $"            let result{Converted(export, signature.Result)} = {(export.Fallible ? Unwrapped(call, into, "            ") : call + into)};\n");
      text.Append("            ").Append(Encode(signature.Result)).Append('\n');
      text.Append("        }\n");
    }

    text.Append("        _ => std::process::exit(2),\n    }\n}\n\n");
    text.Append("fn main() {\n");
    text.Append("    std::panic::set_hook(Box::new(|_| {}));\n");
    text.Append("    let stdin = io::stdin();\n    let stdout = io::stdout();\n    let mut out = stdout.lock();\n");
    text.Append("    for line in stdin.lock().lines() {\n");
    text.Append("        let line = line.unwrap_or_else(|_| std::process::exit(2));\n");
    text.Append("        let mut parts = line.splitn(2, ' ');\n");
    text.Append("        let export: usize = parts.next().unwrap_or(\"\").parse().unwrap_or_else(|_| std::process::exit(2));\n");
    text.Append("        let input = unhex(parts.next().unwrap_or(\"\"));\n");
    text.Append("        match std::panic::catch_unwind(|| call(export, &input)) {\n");
    text.Append("            Ok(image) => writeln!(out, \"{}\", image).unwrap(),\n");
    text.Append("            Err(payload) => {\n");
    text.Append("                let message = payload.downcast_ref::<&str>().map(|s| s.to_string()).or_else(|| payload.downcast_ref::<String>().cloned()).unwrap_or_default();\n");
    text.Append("                writeln!(out, \"!{}\", message.lines().next().unwrap_or(\"\")).unwrap();\n");
    text.Append("            }\n        }\n        out.flush().unwrap();\n    }\n}\n");
    return text.ToString();
  }

  /// <summary>Whether a signature passes or gives a <c>seq</c>, whose width is known only from its image.</summary>
  private static bool Sequences(CallSignature signature)
    => signature.Parameters.Any(parameter => Counted(parameter.Type) is not null || parameter.Type.Kind == ImageKind.Optional)
      || signature.Result is { } result && (Counted(result) is not null || result.Kind == ImageKind.Optional);

  /// <summary>
  /// The <c>seq</c> an image type is laid out as, or <see langword="null"/> for a scalar: a <c>seq</c>
  /// itself, a <c>utf16</c> text as the <c>seq&lt;u16&gt;</c> of its code units, which is how the Rust
  /// code holds a string.
  /// </summary>
  private static ImageType? Counted(ImageType type) => type.Kind switch {
    ImageKind.Sequence => type,
    ImageKind.Utf16 => ImageType.Seq(ImageType.U16),
    _ => null
  };

  /// <summary>
  /// The arm of an export with a <c>seq</c>: every argument read from a cursor into the input image,
  /// the image read to its end, the call, then the result and the <c>inout</c> sequences written one
  /// after the other.
  /// </summary>
  private static void Sequenced(StringBuilder text, string crate, RustExport export) {
    const string Pad = "            ";
    text.Append(Pad).Append("let mut at = 0;\n");
    var arguments = new List<string>();
    var outputs = new List<(string Name, ImageType Type)>();
    foreach (var parameter in export.Signature.Parameters) {
      var name = "a" + arguments.Count.ToString(CultureInfo.InvariantCulture);
      if (parameter.Type.Kind == ImageKind.Optional) {
        if (parameter.Mode == ParameterMode.Out)
          throw new ArgumentException($"The parameter '{parameter.Name}' has mode {parameter.Mode}; the Rust driver passes 'in' and 'inout' parameters.", nameof(export));

        var inner = parameter.Type.Element!;
        var sequence = Counted(inner);
        var value = sequence is null ? Taken(inner) : $"{{\n{Pad}        let count = u32::from_le_bytes(take(input, &mut at, 4).try_into().unwrap());\n{Pad}        (0..count).map(|_| {Taken(sequence.Element!)}).collect()\n{Pad}    }}";
        var type = sequence is null ? RustType(inner) : $"Vec<{RustType(sequence.Element!)}>";
        text.Append(CultureInfo.InvariantCulture, $"{Pad}let {name}: Option<{type}> = if take(input, &mut at, 1)[0] != 0 {{\n{Pad}    Some({value})\n{Pad}}} else {{\n{Pad}    None\n{Pad}}};\n");
        arguments.Add(sequence is null ? name : name + ".as_deref()");
        if (parameter.Mode == ParameterMode.InOut)
          outputs.Add((name, parameter.Type));
        continue;
      }

      if (Counted(parameter.Type) is not { } counted) {
        Width(parameter);
        text.Append(CultureInfo.InvariantCulture, $"{Pad}let {name}: {RustType(parameter.Type)} = {Taken(parameter.Type)};\n");
        arguments.Add(export.Converted ? name + ".into()" : name);
        continue;
      }

      if (parameter.Mode == ParameterMode.Out)
        throw new ArgumentException($"The parameter '{parameter.Name}' has mode {parameter.Mode}; the Rust driver passes 'in' and 'inout' parameters.", nameof(export));

      var element = counted.Element!;
      var mutable = parameter.Mode == ParameterMode.InOut;
      text.Append(CultureInfo.InvariantCulture, $"{Pad}let count = u32::from_le_bytes(take(input, &mut at, 4).try_into().unwrap());\n");
      text.Append(CultureInfo.InvariantCulture, $"{Pad}let {(mutable ? "mut " : "")}{name}: Vec<{RustType(element)}> = (0..count).map(|_| {Taken(element)}).collect();\n");
      arguments.Add((mutable ? "&mut " : "&") + name);
      if (mutable)
        outputs.Add((name, counted));
    }

    text.Append(Pad).Append("if at != input.len() {\n").Append(Pad).Append("    std::process::exit(2);\n").Append(Pad).Append("}\n");
    var call = $"{crate}::{export.Path}({string.Join(", ", arguments)})";
    text.Append(CultureInfo.InvariantCulture, $"{Pad}let result = {(export.Fallible ? Unwrapped(call, "", Pad) : call)};\n");
    text.Append(Pad).Append("let mut image: Vec<u8> = Vec::new();\n");
    if (export.Signature.Result is { } result)
      Written(text, Pad, "result", result.Kind == ImageKind.Optional ? result : Counted(result) ?? result);
    else
      text.Append(Pad).Append("let () = result;\n");

    foreach (var (name, type) in outputs)
      Written(text, Pad, name, type);

    text.Append(Pad).Append("hex(&image)\n");
  }

  /// <summary>The value of a call that returns a <c>Result</c>: the <c>Ok</c>'s, an <c>Err</c> ending the arm with <c>?</c> and its <c>Display</c>.</summary>
  private static string Unwrapped(string call, string into, string pad)
    => $"match {call} {{\n{pad}    Ok(value) => value{into},\n{pad}    Err(error) => return format!(\"?{{}}\", error),\n{pad}}}";

  /// <summary>The type a converted scalar result is declared with, <c>: T</c>, or nothing.</summary>
  private static string Converted(RustExport export, ImageType? result)
    => export.Converted && result is { Kind: ImageKind.Integer or ImageKind.Float or ImageKind.Boolean } ? ": " + RustType(result) : "";

  /// <summary>A scalar read at the cursor, which moves past it.</summary>
  private static string Taken(ImageType type) {
    var size = Scalar(type);
    var bytes = $"take(input, &mut at, {size.ToString(CultureInfo.InvariantCulture)})";
    return type.Kind switch {
      ImageKind.Boolean => $"{bytes}[0] != 0",
      ImageKind.Float => $"{RustType(type)}::from_bits(u{(size * 8).ToString(CultureInfo.InvariantCulture)}::from_le_bytes({bytes}.try_into().unwrap()))",
      _ => $"{RustType(type)}::from_le_bytes({bytes}.try_into().unwrap())"
    };
  }

  /// <summary>A value appended to the output image: a scalar's bytes, a sequence's count and then its elements.</summary>
  private static void Written(StringBuilder text, string pad, string value, ImageType type) {
    if (type.Kind == ImageKind.Optional) {
      // The presence byte, then the value: an optional sequence is an Option of a vector or of a slice, read as a slice.
      var inner = Counted(type.Element!) ?? type.Element!;
      var bound = "present" + pad.Length.ToString(CultureInfo.InvariantCulture);
      text.Append(pad).Append(CultureInfo.InvariantCulture, $"match &{value} {{\n");
      text.Append(pad).Append(CultureInfo.InvariantCulture, $"    Some({bound}) => {{\n");
      text.Append(pad).Append("        image.push(1);\n");
      Written(text, pad + "        ", inner.Kind == ImageKind.Sequence ? bound : "(*" + bound + ")", inner);
      text.Append(pad).Append("    }\n");
      text.Append(pad).Append("    None => image.push(0),\n");
      text.Append(pad).Append("}\n");
      return;
    }

    if (type.Kind != ImageKind.Sequence) {
      text.Append(pad).Append("image.extend_from_slice(&").Append(Bytes(type, value)).Append(");\n");
      return;
    }

    var element = type.Element!;
    text.Append(pad).Append(CultureInfo.InvariantCulture, $"let values: &[{RustType(element)}] = &{value};\n");
    text.Append(pad).Append("image.extend_from_slice(&(values.len() as u32).to_le_bytes());\n");
    text.Append(pad).Append("for &value in values {\n");
    text.Append(pad).Append("    image.extend_from_slice(&").Append(Bytes(element, "value")).Append(");\n");
    text.Append(pad).Append("}\n");
  }

  /// <summary>The little-endian bytes of a scalar.</summary>
  private static string Bytes(ImageType type, string value) => type.Kind switch {
    ImageKind.Boolean => $"[u8::from({value})]",
    ImageKind.Float => $"{value}.to_bits().to_le_bytes()",
    _ => $"{value}.to_le_bytes()"
  };

  /// <summary>The bytes an <c>in</c> parameter takes in the input image.</summary>
  private static int Width(CallParameter parameter) {
    if (parameter.Mode != ParameterMode.In)
      throw new ArgumentException($"The parameter '{parameter.Name}' has mode {parameter.Mode}; the Rust driver passes 'in' parameters of scalar types.", nameof(parameter));

    return Scalar(parameter.Type);
  }

  /// <summary>The width of a scalar image type in bytes.</summary>
  private static int Scalar(ImageType type) => type.Kind is ImageKind.Integer or ImageKind.Float or ImageKind.Boolean
    ? type.Width
    : throw new ArgumentException($"The image type '{type}' has no pairing in the Rust driver, which passes scalars.", nameof(type));

  /// <summary>The Rust type of a scalar image type.</summary>
  public static string RustType(ImageType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type.Kind switch {
      ImageKind.Boolean => "bool",
      ImageKind.Float => type.Width == 4 ? "f32" : "f64",
      ImageKind.Integer => (type.Signed ? "i" : "u") + (Scalar(type) * 8).ToString(CultureInfo.InvariantCulture),
      _ => throw new ArgumentException($"The image type '{type}' has no pairing in the Rust driver, which passes scalars.", nameof(type))
    };
  }

  private static string Decode(ImageType type, int offset, int size) {
    var range = $"input[{offset}..{offset + size}].try_into().unwrap()";
    return type.Kind switch {
      ImageKind.Boolean => $"input[{offset}] != 0",
      ImageKind.Float => $"{RustType(type)}::from_bits(u{size * 8}::from_le_bytes({range}))",
      _ => $"{RustType(type)}::from_le_bytes({range})"
    };
  }

  private static string Encode(ImageType? result) => result switch {
    null => "let () = result;\n            String::new()",
    { Kind: ImageKind.Boolean } => "hex(&[u8::from(result)])",
    { Kind: ImageKind.Float } => "hex(&result.to_bits().to_le_bytes())",
    _ => $"hex(&{RustType(result)}::to_le_bytes(result))"
  };

}
