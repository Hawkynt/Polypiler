using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Cli;

/// <summary>
/// The program <c>translate --verify</c> runs the generated JavaScript with: it imports the module,
/// calls the export once per vector with the arguments the vector's input image decodes to, and
/// writes one line per call, <c>ok &lt;value&gt;</c> or <c>trap &lt;name&gt;</c>. The lines are read
/// back into output images, so that the oracle framework compares bytes as it does for every language.
/// The driver takes the scalar signatures whose values JavaScript holds in a <c>number</c> or a
/// <c>boolean</c>: <c>bool</c> and the integers of up to 32 bits, with a result.
/// </summary>
public static class JavaScriptDriver {

  /// <summary>The file name of the driver beside the generated module.</summary>
  public const string FileName = "polypiler.verify.mjs";

  /// <summary>Why a signature cannot be driven, or <see langword="null"/> when it can.</summary>
  /// <param name="signature">The signature of the vector file.</param>
  /// <returns>The reason.</returns>
  public static string? Unsupported(CallSignature signature) {
    ArgumentNullException.ThrowIfNull(signature);
    if (signature.Parameters.FirstOrDefault(parameter => parameter.Mode != ParameterMode.In) is { } output)
      return $"the parameter '{output.Name}' is {(output.Mode == ParameterMode.Out ? "out" : "inout")}; the JavaScript driver passes values in only";

    if (signature.Result is not { } result)
      return "the signature has no result; the JavaScript driver compares results, and a trap would leave the same empty image";

    var types = signature.Parameters.Select(parameter => parameter.Type).Append(result);
    return types.FirstOrDefault(type => !IsScalar(type)) is { } unsupported
      ? $"the image type '{unsupported}' is not one the JavaScript driver passes; it takes bool and the integers of up to 32 bits"
      : null;
  }

  private static bool IsScalar(ImageType type) => type.Kind == ImageKind.Boolean || type.Kind == ImageKind.Integer && type.Width <= 4;

  /// <summary>The text of the driver.</summary>
  /// <param name="module">The generated module's file name, beside the driver.</param>
  /// <param name="export">The function to call.</param>
  /// <param name="signature">The signature the input images follow; <see cref="Unsupported"/> returned <see langword="null"/> for it.</param>
  /// <param name="vectors">The vectors, in file order.</param>
  /// <returns>An ECMAScript module.</returns>
  public static string Script(string module, string export, CallSignature signature, IReadOnlyList<TestVector> vectors) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(export);
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentNullException.ThrowIfNull(vectors);
    var calls = vectors.Select(vector => $"[{string.Join(", ", CallImages.Decode(signature, CallImage.Input, vector.Input).Select(value => Literal(value.Value)))}]");
    return new StringBuilder()
      .Append("// Calls ").Append(export).Append(" of ").Append(module).Append(" once per vector of polypiler translate --verify.\n")
      .Append("const calls = [").Append(string.Join(", ", calls)).Append("];\n")
      .Append("let fn = null;\n")
      .Append("let failure = null;\n")
      .Append("try {\n")
      .Append("  const m = await import(").Append(Quoted("./" + module)).Append(");\n")
      .Append("  fn = m[").Append(Quoted(export)).Append("] ?? (m.default ? m.default[").Append(Quoted(export)).Append("] : undefined);\n")
      .Append("} catch (e) {\n")
      .Append("  failure = e;\n")
      .Append("}\n")
      .Append("const lines = [];\n")
      .Append("for (const args of calls) {\n")
      .Append("  try {\n")
      .Append("    if (failure !== null) throw failure;\n")
      .Append("    lines.push(\"ok \" + String(fn(...args)));\n")
      .Append("  } catch (e) {\n")
      .Append("    lines.push(\"trap \" + String(e && e.name ? e.name : e).replace(/\\s+/g, \" \"));\n")
      .Append("  }\n")
      .Append("}\n")
      .Append("process.stdout.write(lines.map(line => line + \"\\n\").join(\"\"));\n")
      .ToString();
  }

  /// <summary>
  /// The output image of every call from the driver's standard output, in vector order: the image of
  /// the value an <c>ok</c> line names, and an empty image for a trap, for a value that does not fit
  /// the result type and for a line that is missing.
  /// </summary>
  /// <param name="signature">The signature.</param>
  /// <param name="stdout">The driver's standard output.</param>
  /// <param name="count">The number of vectors.</param>
  /// <returns>The images.</returns>
  public static IReadOnlyList<byte[]> Outputs(CallSignature signature, string stdout, int count) {
    ArgumentNullException.ThrowIfNull(signature);
    ArgumentNullException.ThrowIfNull(stdout);
    var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    return [.. Enumerable.Range(0, count).Select(index => index < lines.Length ? Output(signature, lines[index].TrimEnd('\r')) : [])];
  }

  private static byte[] Output(CallSignature signature, string line) {
    if (!line.StartsWith("ok ", StringComparison.Ordinal))
      return [];

    var text = line[3..];
    ImageValue value = signature.Result!.Kind == ImageKind.Boolean
      ? text switch { "true" => new ImageValue.Boolean(true), "false" => new ImageValue.Boolean(false), _ => new ImageValue.Trap(new(TrapKind.ResultType)) }
      : BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? new ImageValue.Integer(number) : new ImageValue.Trap(new(TrapKind.ResultType));
    return CallImages.Outcome(signature, value, []).Image ?? [];
  }

  private static string Literal(ImageValue value) => value switch {
    ImageValue.Boolean boolean => boolean.Value ? "true" : "false",
    _ => ((ImageValue.Integer)value).Value.ToString(CultureInfo.InvariantCulture)
  };

  private static string Quoted(string text) => System.Text.Json.JsonSerializer.Serialize(text);

}
