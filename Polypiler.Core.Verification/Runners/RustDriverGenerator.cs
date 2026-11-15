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
public sealed record RustExport(string Path, CallSignature Signature);

/// <summary>
/// Generates <c>src/bin/polypiler_driver.rs</c>, the program that calls the exports of a crate on input
/// images. It reads one call per line, <c>&lt;export&gt; &lt;input image in hex&gt;</c>, decodes the
/// arguments from the image (the little-endian encoding of <see cref="ImageCodec"/>), declares each as
/// the Rust type of its image type and calls the function inside <c>std::panic::catch_unwind</c> with
/// a panic hook that prints nothing. It writes one line per call: the output image in hex, or
/// <c>!</c> and the first line of the panic message. Since each argument is declared with the type the
/// signature names, a signature that does not fit the declaration does not compile, with the error in
/// the driver. A line it cannot read ends it with exit code 2.
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
  /// <exception cref="ArgumentException">A signature has a parameter that is not <c>in</c>, or an image type that is no scalar.</exception>
  public static string Generate(string crate, IReadOnlyList<RustExport> exports) {
    ArgumentNullException.ThrowIfNull(crate);
    ArgumentNullException.ThrowIfNull(exports);
    var text = new StringBuilder();
    text.Append("use std::io::{self, BufRead, Write};\n\n");
    text.Append("fn hex(bytes: &[u8]) -> String {\n    bytes.iter().map(|b| format!(\"{:02x}\", b)).collect()\n}\n\n");
    text.Append("fn unhex(text: &str) -> Vec<u8> {\n    (0..text.len() / 2).map(|i| u8::from_str_radix(&text[2 * i..2 * i + 2], 16).unwrap_or_else(|_| std::process::exit(2))).collect()\n}\n\n");
    text.Append("fn call(export: usize, input: &[u8]) -> String {\n    match export {\n");
    for (var index = 0; index < exports.Count; ++index) {
      var export = exports[index];
      var signature = export.Signature;
      var width = signature.Parameters.Sum(parameter => Width(parameter));
      text.Append(CultureInfo.InvariantCulture, $"        {index} => {{\n");
      text.Append(CultureInfo.InvariantCulture, $"            if input.len() != {width} {{\n                std::process::exit(2);\n            }}\n");
      var offset = 0;
      var arguments = new List<string>();
      foreach (var parameter in signature.Parameters) {
        var size = Width(parameter);
        var name = "a" + arguments.Count.ToString(CultureInfo.InvariantCulture);
        text.Append(CultureInfo.InvariantCulture, $"            let {name}: {RustType(parameter.Type)} = {Decode(parameter.Type, offset, size)};\n");
        arguments.Add(name);
        offset += size;
      }

      text.Append(CultureInfo.InvariantCulture, $"            let result = {crate}::{export.Path}({string.Join(", ", arguments)});\n");
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
