using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Runners;

/// <summary>
/// The call signature of a top-level Kotlin function, derived from its declaration as #426 states it:
/// <c>Byte Short Int Long</c> are <c>i8 i16 i32 i64</c>, the unsigned types <c>u8</c> to <c>u64</c>,
/// <c>Boolean</c> <c>bool</c>, <c>Char</c> <c>char16</c>, <c>Float</c> and <c>Double</c>
/// <c>f32</c> and <c>f64</c>, a primitive array a <c>seq</c> of its element, <c>ByteArray</c>
/// <c>bytes</c>, <c>String</c> <c>utf16</c>, <c>T?</c> <c>opt&lt;T&gt;</c>. Every parameter is
/// <c>in</c>; a vector file makes an array <c>inout</c>.
/// </summary>
public static partial class KotlinSignatures {

  /// <summary>The image type of a Kotlin type, or <see langword="null"/> for one the table has no row of.</summary>
  /// <param name="type">The Kotlin type as declared, such as <c>Int</c>, <c>IntArray</c> or <c>Long?</c>.</param>
  /// <returns>The image type.</returns>
  public static ImageType? ImageOf(string type) {
    ArgumentNullException.ThrowIfNull(type);
    type = type.Trim();
    if (type.EndsWith('?'))
      return ImageOf(type[..^1]) is { } inner ? ImageType.Opt(inner) : null;

    return type switch {
      "Byte" => ImageType.I8,
      "Short" => ImageType.I16,
      "Int" => ImageType.I32,
      "Long" => ImageType.I64,
      "UByte" => ImageType.U8,
      "UShort" => ImageType.U16,
      "UInt" => ImageType.U32,
      "ULong" => ImageType.U64,
      "Boolean" => ImageType.Bool,
      "Char" => ImageType.Char16,
      "Float" => ImageType.F32,
      "Double" => ImageType.F64,
      "String" => ImageType.Utf16,
      "ByteArray" => ImageType.Bytes,
      "ShortArray" => ImageType.Seq(ImageType.I16),
      "IntArray" => ImageType.Seq(ImageType.I32),
      "LongArray" => ImageType.Seq(ImageType.I64),
      "FloatArray" => ImageType.Seq(ImageType.F32),
      "DoubleArray" => ImageType.Seq(ImageType.F64),
      "BooleanArray" => ImageType.Seq(ImageType.Bool),
      "CharArray" => ImageType.Seq(ImageType.Char16),
      _ => null
    };
  }

  /// <summary>
  /// The signature of a top-level function of a source, from its declaration
  /// <c>fun name(a: T, b: U): R</c>; a function without a result type has none, and one with an
  /// expression body and no declared type has none either - the runner needs the type written.
  /// </summary>
  /// <param name="source">The Kotlin source.</param>
  /// <param name="export">The function's name.</param>
  /// <returns>The signature.</returns>
  /// <exception cref="ArgumentException">The source declares no such function, or a type has no image type.</exception>
  public static CallSignature Derive(string source, string export) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(export);
    var match = Declaration().Matches(source).FirstOrDefault(candidate => candidate.Groups["name"].Value == export)
      ?? throw new ArgumentException($"the unit defines no top-level function '{export}'", nameof(export));

    var parameters = new List<CallParameter>();
    var list = match.Groups["parameters"].Value.Trim();
    foreach (var parameter in list.Length == 0 ? [] : list.Split(',')) {
      var colon = parameter.IndexOf(':', StringComparison.Ordinal);
      if (colon < 0)
        throw new ArgumentException($"the parameter '{parameter.Trim()}' of '{export}' has no type", nameof(source));

      var name = parameter[..colon].Trim();
      var type = parameter[(colon + 1)..].Split('=')[0].Trim();
      parameters.Add(new(name, ImageOf(type) ?? throw new ArgumentException($"the parameter '{name}' of '{export}' has the type '{type}', which has no image type", nameof(source)), ParameterMode.In));
    }

    ImageType? result = null;
    if (match.Groups["result"].Success) {
      var type = match.Groups["result"].Value.Trim();
      result = type == "Unit" ? null : ImageOf(type) ?? throw new ArgumentException($"the result of '{export}' has the type '{type}', which has no image type", nameof(source));
    }

    return new CallSignature(parameters, result);
  }

  /// <summary>Whether a signature of a vector file is the derived one, but for the modes of its arrays, which only the vector file states.</summary>
  /// <param name="declared">The signature of the vector file.</param>
  /// <param name="derived">The signature of the declaration.</param>
  /// <returns>Whether they agree.</returns>
  public static bool Agree(CallSignature declared, CallSignature derived) {
    ArgumentNullException.ThrowIfNull(declared);
    ArgumentNullException.ThrowIfNull(derived);
    return Equals(declared.Result, derived.Result) && declared.Parameters.Count == derived.Parameters.Count
      && declared.Parameters.Zip(derived.Parameters).All(pair => pair.First.Type.Equals(pair.Second.Type)
        && (pair.First.Mode == pair.Second.Mode || pair.First.Type.Kind is ImageKind.Sequence or ImageKind.Bytes && pair.First.Mode == ParameterMode.InOut));
  }

  /// <summary>The package a source declares, or <see langword="null"/> for the default package.</summary>
  /// <param name="source">The Kotlin source.</param>
  /// <returns>The package.</returns>
  public static string? PackageOf(string source) {
    ArgumentNullException.ThrowIfNull(source);
    return Package().Match(source) is { Success: true } match ? match.Groups["package"].Value : null;
  }

  [GeneratedRegex(@"^\s*(?:(?:public|internal|inline)\s+)*fun\s+(?<name>\w+)\s*\((?<parameters>[^)]*)\)\s*(?::\s*(?<result>[\w?<>]+))?", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex Declaration();

  [GeneratedRegex(@"^\s*package\s+(?<package>[\w.]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex Package();

}
