using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>Where the expected values of a vector file come from: the <c>provenance</c> block that <c>polypiler-tooling integrity check</c> also holds to its rules.</summary>
/// <param name="Kind">How they were made: <c>published</c>, <c>reference-run</c> or <c>upstream-embedded</c>.</param>
/// <param name="Source">The document, the key of <c>tooling/toolchains.lock</c> or the pinned upstream path.</param>
/// <param name="Detail">The section, the exact command or the line.</param>
/// <param name="GeneratedBy">What wrote the values: a toolchain key, <c>hand</c>, …; never Polypiler.</param>
public sealed record VectorProvenance(string Kind, string Source, string Detail, string GeneratedBy) {

  /// <summary>The oracle kind the provenance stands for: values recorded from a reference run stand for that toolchain, all others are published.</summary>
  public OracleKind OracleKind => this.Kind == "reference-run" ? OracleKind.SourceToolchain : OracleKind.PublishedVectors;

}

/// <summary>
/// A <c>*.vectors.json</c> file, schema 1 (<c>spec/schemas/vectors.schema.json</c>): a fixture, the
/// provenance of its expected values, the exactness policy its outputs are compared under, and its
/// vectors in file order. Inputs and expected values are byte images, written as <c>hex</c> or as
/// <c>text</c> (its UTF-8 bytes); typed <c>args</c> and <c>value</c> forms are refused here.
/// </summary>
public sealed class VectorFile {

  private static readonly string[] _provenanceKinds = ["published", "reference-run", "upstream-embedded"];

  private VectorFile(string path, string fixture, VectorProvenance provenance, ExactnessPolicy exactness, IReadOnlyList<TestVector> vectors) {
    this.Path = path;
    this.Fixture = fixture;
    this.Provenance = provenance;
    this.Exactness = exactness;
    this.Vectors = vectors;
  }

  /// <summary>The file, as it was loaded.</summary>
  public string Path { get; }

  /// <summary>The fixture's ID.</summary>
  public string Fixture { get; }

  /// <summary>Where the expected values come from.</summary>
  public VectorProvenance Provenance { get; }

  /// <summary>How outputs are compared; <see cref="ExactnessPolicy.Exact"/> unless the file declares a tolerance.</summary>
  public ExactnessPolicy Exactness { get; }

  /// <summary>The vectors, in file order.</summary>
  public IReadOnlyList<TestVector> Vectors { get; }

  /// <summary>Loads a vector file.</summary>
  /// <param name="path">The file.</param>
  /// <returns>The file's content.</returns>
  /// <exception cref="FormatException">The file is not valid JSON or not of the declared shape; the message names the property.</exception>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public static VectorFile Load(string path) {
    ArgumentNullException.ThrowIfNull(path);
    return Parse(File.ReadAllText(path), path);
  }

  /// <summary>Reads the text of a vector file.</summary>
  /// <param name="json">The text.</param>
  /// <param name="path">The file the text came from, as the vector file and the messages name it.</param>
  /// <returns>The file's content.</returns>
  /// <exception cref="FormatException">The text is not valid JSON or not of the declared shape; the message names the property.</exception>
  public static VectorFile Parse(string json, string path) {
    ArgumentNullException.ThrowIfNull(json);
    ArgumentNullException.ThrowIfNull(path);
    JsonDocument document;
    try {
      document = JsonDocument.Parse(json);
    } catch (JsonException exception) {
      throw new FormatException($"{path}: not valid JSON: {exception.Message}", exception);
    }

    using (document) {
      var root = Object(document.RootElement, "$", path);
      Only(root, "$", path, "schema", "fixture", "provenance", "exactness", "vectors");
      if (Required(root, "schema", "$", path) is not { ValueKind: JsonValueKind.Number } version || !version.TryGetInt32(out var number) || number != 1)
        throw Error(path, "schema", "must be 1");

      var fixture = Text(root, "fixture", "$", path);
      var provenance = ReadProvenance(Required(root, "provenance", "$", path), path);
      var exactness = root.TryGetProperty("exactness", out var declared) ? ReadExactness(declared, path) : new ExactnessPolicy.Exact();
      var vectors = ReadVectors(Required(root, "vectors", "$", path), path);
      return new(path, fixture, provenance, exactness, vectors);
    }
  }

  private static VectorProvenance ReadProvenance(JsonElement element, string path) {
    Object(element, "provenance", path);
    Only(element, "provenance", path, "kind", "source", "detail", "generatedBy");
    var kind = Text(element, "kind", "provenance", path);
    if (!_provenanceKinds.Contains(kind))
      throw Error(path, "provenance.kind", $"'{kind}' is not one of {string.Join(", ", _provenanceKinds)}");

    return new(kind, Text(element, "source", "provenance", path), Text(element, "detail", "provenance", path), Text(element, "generatedBy", "provenance", path));
  }

  private static ExactnessPolicy ReadExactness(JsonElement element, string path) {
    Object(element, "exactness", path);
    var mode = Text(element, "mode", "exactness", path);
    var rule = element.TryGetProperty("specialValues", out var special) ? Rule(special, path) : SpecialValues.SignedZero;
    if (mode == "exact") {
      Only(element, "exactness", path, "mode", "specialValues");
      return new ExactnessPolicy.Exact { SpecialValues = rule };
    }

    if (mode is not ("ulp" or "relative" or "absolute"))
      throw Error(path, "exactness.mode", $"'{mode}' is not one of exact, ulp, relative, absolute");

    Only(element, "exactness", path, "mode", "bound", "format", "specialValues", "policy", "reason");
    var format = Text(element, "format", "exactness", path) switch {
      "f32" => FloatFormat.F32,
      "f64" => FloatFormat.F64,
      var other => throw Error(path, "exactness.format", $"'{other}' is not one of f32, f64")
    };
    var policy = element.TryGetProperty("policy", out _) ? Text(element, "policy", "exactness", path) : null;
    var reason = element.TryGetProperty("reason", out _) ? Text(element, "reason", "exactness", path) : null;
    if ((policy is null) == (reason is null))
      throw Error(path, policy is null ? "exactness.policy" : "exactness.reason", $"a '{mode}' tolerance carries exactly one of 'policy' and 'reason'");

    var bound = Required(element, "bound", "exactness", path);
    if (bound.ValueKind != JsonValueKind.Number || !bound.TryGetDouble(out var value) || !double.IsFinite(value) || value <= 0)
      throw Error(path, "exactness.bound", "must be a positive number");

    ExactnessPolicy result;
    if (mode == "ulp") {
      if (!bound.TryGetInt32(out var steps))
        throw Error(path, "exactness.bound", "must be a positive integer in ulp mode");

      result = new ExactnessPolicy.Ulp(steps, format, policy, reason);
    } else
      result = mode == "relative" ? new ExactnessPolicy.Relative(value, format, policy, reason) : new ExactnessPolicy.Absolute(value, format, policy, reason);

    return result with { SpecialValues = rule };
  }

  private static SpecialValues Rule(JsonElement element, string path) => (element.ValueKind == JsonValueKind.String ? element.GetString() : null) switch {
    "bitwise" => SpecialValues.Bitwise,
    "signed-zero" => SpecialValues.SignedZero,
    "class" => SpecialValues.Class,
    _ => throw Error(path, "exactness.specialValues", "must be one of bitwise, signed-zero, class")
  };

  private static List<TestVector> ReadVectors(JsonElement element, string path) {
    if (element.ValueKind != JsonValueKind.Array)
      throw Error(path, "vectors", "must be an array");

    var vectors = new List<TestVector>();
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var item in element.EnumerateArray()) {
      var at = $"vectors[{vectors.Count}]";
      Object(item, at, path);
      Only(item, at, path, "name", "input", "expected");
      var name = Text(item, "name", at, path);
      if (!names.Add(name))
        throw Error(path, $"{at}.name", $"'{name}' is not unique");

      var input = Bytes(Required(item, "input", at, path), $"{at}.input", path);
      var expected = item.TryGetProperty("expected", out var value) ? Bytes(value, $"{at}.expected", path) : null;
      vectors.Add(new(name, input, expected));
    }

    return vectors;
  }

  private static byte[] Bytes(JsonElement element, string at, string path) {
    Object(element, at, path);
    var properties = element.EnumerateObject().ToList();
    if (properties.Count != 1)
      throw Error(path, at, "must hold exactly one of 'hex' and 'text'");

    var (name, value) = (properties[0].Name, properties[0].Value);
    return name switch {
      "hex" => (value.ValueKind == JsonValueKind.String ? Hex.TryParse(value.GetString()!) : null)
        ?? throw Error(path, $"{at}.hex", "must be an even number of hexadecimal digits without separators"),
      "text" => value.ValueKind == JsonValueKind.String ? Encoding.UTF8.GetBytes(value.GetString()!) : throw Error(path, $"{at}.text", "must be a string"),
      "args" or "value" => throw Error(path, $"{at}.{name}", "is a typed value, not a byte image; this framework compares bytes"),
      _ => throw Error(path, $"{at}.{name}", "is not a property of the vector file format")
    };
  }

  private static JsonElement Object(JsonElement element, string at, string path)
    => element.ValueKind == JsonValueKind.Object ? element : throw Error(path, at, "must be an object");

  private static void Only(JsonElement element, string at, string path, params string[] allowed) {
    foreach (var property in element.EnumerateObject())
      if (!allowed.Contains(property.Name))
        throw Error(path, at == "$" ? property.Name : $"{at}.{property.Name}", "is not a property of the vector file format");
  }

  private static JsonElement Required(JsonElement element, string property, string at, string path)
    => element.TryGetProperty(property, out var value) ? value : throw Error(path, at == "$" ? property : $"{at}.{property}", "is missing");

  private static string Text(JsonElement element, string property, string at, string path) {
    var value = Required(element, property, at, path);
    var name = at == "$" ? property : $"{at}.{property}";
    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
      throw Error(path, name, "must be a non-empty string");

    return value.GetString()!;
  }

  private static FormatException Error(string path, string property, string problem) => new($"{path}: '{property}' {problem}");

}
