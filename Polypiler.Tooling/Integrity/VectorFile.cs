using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Polypiler.Tooling.Integrity;

/// <summary>Where the expected values of a golden file came from.</summary>
public enum ProvenanceKind {

  /// <summary>Copied from a standard or a paper; the source names it, the detail the section.</summary>
  Published,

  /// <summary>Produced by running an independent implementation; the source is a key of <c>tooling/toolchains.lock</c>, the detail the command.</summary>
  ReferenceRun,

  /// <summary>Copied from a pinned upstream file; the source is <c>owner/repo@&lt;commit&gt;:&lt;path&gt;</c>, present in the corpus lock.</summary>
  UpstreamEmbedded

}

/// <summary>The <c>provenance</c> block of a vector file or of a <c>golden.provenance.json</c> marker.</summary>
/// <param name="Kind">How the values were obtained.</param>
/// <param name="Source">The document, toolchain key or pinned upstream path they came from.</param>
/// <param name="Detail">The section, command or line.</param>
/// <param name="GeneratedBy">The tool that wrote the values, or <c>hand</c>.</param>
public sealed record Provenance(ProvenanceKind Kind, string Source, string Detail, string GeneratedBy) {

  private static readonly string[] _fields = ["kind", "source", "detail", "generatedBy"];

  /// <summary>The name of a kind as files spell it.</summary>
  public static string KindName(ProvenanceKind kind) => kind switch {
    ProvenanceKind.Published => "published",
    ProvenanceKind.ReferenceRun => "reference-run",
    _ => "upstream-embedded"
  };

  /// <summary>Reads the block, adding a reason to <paramref name="problems"/> for everything wrong with it.</summary>
  /// <returns>The provenance, or <see langword="null"/> when it cannot be read.</returns>
  internal static Provenance? Read(JsonElement? element, List<string> problems) {
    if (element is not { } block) {
      problems.Add("no provenance");
      return null;
    }

    if (block.ValueKind != JsonValueKind.Object) {
      problems.Add("'provenance' must be an object");
      return null;
    }

    foreach (var property in block.EnumerateObject().Where(property => !_fields.Contains(property.Name)))
      problems.Add($"provenance has the unknown property '{property.Name}'");

    var before = problems.Count;
    var values = new string[_fields.Length];
    for (var index = 0; index < _fields.Length; ++index)
      if (!block.TryGetProperty(_fields[index], out var value))
        problems.Add($"provenance has no '{_fields[index]}'");
      else if (value.ValueKind != JsonValueKind.String || value.GetString()!.Trim().Length == 0)
        problems.Add($"provenance '{_fields[index]}' must be a non-empty string");
      else
        values[index] = value.GetString()!;

    ProvenanceKind? kind = values[0] switch {
      null => null,
      "published" => ProvenanceKind.Published,
      "reference-run" => ProvenanceKind.ReferenceRun,
      "upstream-embedded" => ProvenanceKind.UpstreamEmbedded,
      var other => Unknown(other, problems)
    };
    return problems.Count > before ? null : new(kind!.Value, values[1], values[2], values[3]);
  }

  private static ProvenanceKind? Unknown(string kind, List<string> problems) {
    problems.Add($"provenance kind '{kind}' is unknown; it is 'published', 'reference-run' or 'upstream-embedded'");
    return null;
  }

}

/// <summary>How an input or an expected value of a vector is written.</summary>
public enum VectorValueKind {

  /// <summary>Bytes as hexadecimal digits, either case.</summary>
  Hex,

  /// <summary>A string.</summary>
  Text,

  /// <summary>An array of arguments; inputs only.</summary>
  Args,

  /// <summary>Any JSON value; expected values only.</summary>
  Value

}

/// <summary>An input or an expected value of a vector.</summary>
/// <param name="Kind">How it is written.</param>
/// <param name="Bytes">The bytes of a <see cref="VectorValueKind.Hex"/> value, empty for the others.</param>
/// <param name="Text">The string of a <see cref="VectorValueKind.Text"/> value, <see langword="null"/> for the others.</param>
/// <param name="Json">The value as JSON text, exactly as the file has it.</param>
public sealed record VectorValue(VectorValueKind Kind, ImmutableArray<byte> Bytes, string? Text, string Json);

/// <summary>One vector: a named input and the value expected for it.</summary>
/// <param name="Name">The name, unique within the file.</param>
/// <param name="Input">The input.</param>
/// <param name="Expected">The expected value.</param>
public sealed record Vector(string Name, VectorValue Input, VectorValue Expected);

/// <summary>
/// A <c>*.vectors.json</c> file: a corpus fixture, where its expected values came from, and the
/// vectors. Loading opens the file read-only and gives immutable records; there is no way to write one
/// back, because expected values are never adjusted to fit generated code (PRD §11.2).
/// </summary>
public sealed class VectorFile {

  /// <summary>The ending of every vector file's name.</summary>
  public const string Extension = ".vectors.json";

  private static readonly string[] _fields = ["schema", "fixture", "provenance", "vectors"];
  private static readonly string[] _vectorFields = ["expected", "input", "name"];

  private VectorFile(string name, string fixture, Provenance provenance, ImmutableArray<Vector> vectors) {
    this.Name = name;
    this.Fixture = fixture;
    this.Provenance = provenance;
    this.Vectors = vectors;
  }

  /// <summary>The file as diagnostics name it.</summary>
  public string Name { get; }

  /// <summary>The corpus fixture ID the vectors belong to.</summary>
  public string Fixture { get; }

  /// <summary>Where the expected values came from.</summary>
  public Provenance Provenance { get; }

  /// <summary>The vectors, in the order of the file.</summary>
  public ImmutableArray<Vector> Vectors { get; }

  /// <summary>The vector of a name, or <see langword="null"/>.</summary>
  public Vector? Find(string name) => this.Vectors.FirstOrDefault(vector => vector.Name == name);

  /// <summary>Opens a vector file read-only and reads it.</summary>
  /// <param name="path">The file.</param>
  /// <param name="name">The name diagnostics give the file; the path when omitted.</param>
  /// <exception cref="IntegrityException">The file cannot be read or is not a valid vector file (<c>PLY0028</c>).</exception>
  public static VectorFile Load(string path, string? name = null) {
    name ??= path;
    string text;
    try {
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
      text = reader.ReadToEnd();
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException) {
      throw new IntegrityException([IntegrityDiagnostics.Provenance(name, $"cannot be read: {exception.Message}")]);
    }

    return Parse(text, name);
  }

  /// <summary>Reads the text of a vector file.</summary>
  /// <exception cref="IntegrityException">The text is not a valid vector file (<c>PLY0028</c>), with one diagnostic per problem.</exception>
  public static VectorFile Parse(string text, string name) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(text);
    } catch (JsonException exception) {
      throw Invalid(name, [$"not JSON: {exception.Message}"]);
    }

    using (document) {
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
        throw Invalid(name, ["a vector file must be a JSON object"]);

      var problems = new List<string>();
      foreach (var property in root.EnumerateObject().Where(property => !_fields.Contains(property.Name)))
        problems.Add($"unknown property '{property.Name}'");
      if (!root.TryGetProperty("schema", out var schema) || schema.GetRawText() != "1")
        problems.Add("'schema' must be 1");

      var fixture = root.TryGetProperty("fixture", out var id) && id.ValueKind == JsonValueKind.String && id.GetString()!.Length > 0 ? id.GetString()! : null;
      if (fixture is null)
        problems.Add("'fixture' must name a corpus fixture");

      var provenance = Provenance.Read(root.TryGetProperty("provenance", out var block) ? block : null, problems);
      var vectors = ReadVectors(root, problems);
      if (problems.Count > 0)
        throw Invalid(name, problems);
      return new(name, fixture!, provenance!, vectors);
    }
  }

  private static ImmutableArray<Vector> ReadVectors(JsonElement root, List<string> problems) {
    if (!root.TryGetProperty("vectors", out var list) || list.ValueKind != JsonValueKind.Array) {
      problems.Add("'vectors' must be an array");
      return [];
    }

    if (list.GetArrayLength() == 0) {
      problems.Add("no vectors");
      return [];
    }

    var vectors = ImmutableArray.CreateBuilder<Vector>();
    var names = new HashSet<string>(StringComparer.Ordinal);
    var index = 0;
    foreach (var item in list.EnumerateArray()) {
      ++index;
      if (item.ValueKind != JsonValueKind.Object || !item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(_vectorFields)) {
        problems.Add($"vector {index} must be an object of exactly 'name', 'input' and 'expected'");
        continue;
      }

      var name = item.GetProperty("name");
      if (name.ValueKind != JsonValueKind.String || name.GetString()!.Length == 0) {
        problems.Add($"vector {index} must have a non-empty 'name'");
        continue;
      }

      var label = name.GetString()!;
      if (!names.Add(label))
        problems.Add($"vector '{label}' is listed twice");
      var input = ReadValue(item.GetProperty("input"), $"vector '{label}' input", VectorValueKind.Args, problems);
      var expected = ReadValue(item.GetProperty("expected"), $"vector '{label}' expected", VectorValueKind.Value, problems);
      if (input is not null && expected is not null)
        vectors.Add(new(label, input, expected));
    }

    return vectors.ToImmutable();
  }

  private static VectorValue? ReadValue(JsonElement element, string what, VectorValueKind third, List<string> problems) {
    var thirdName = third == VectorValueKind.Args ? "args" : "value";
    if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 1) {
      problems.Add($"{what} must be an object of exactly one of 'hex', 'text' or '{thirdName}'");
      return null;
    }

    var property = element.EnumerateObject().Single();
    var value = property.Value;
    var json = value.GetRawText();
    if (property.Name == "hex" && value.ValueKind == JsonValueKind.String) {
      var digits = value.GetString()!;
      if (digits.Length % 2 == 0 && digits.All(Uri.IsHexDigit))
        return new(VectorValueKind.Hex, [.. Convert.FromHexString(digits)], null, json);

      problems.Add($"{what} hex '{digits}' must be an even number of hexadecimal digits");
      return null;
    }

    if (property.Name == "text" && value.ValueKind == JsonValueKind.String)
      return new(VectorValueKind.Text, [], value.GetString(), json);
    if (property.Name == thirdName && (third == VectorValueKind.Value || value.ValueKind == JsonValueKind.Array))
      return new(third, [], null, json);

    problems.Add($"{what} must be an object of exactly one of 'hex', 'text' or '{thirdName}'");
    return null;
  }

  private static IntegrityException Invalid(string name, IEnumerable<string> problems)
    => new([.. problems.Select(problem => IntegrityDiagnostics.Provenance(name, problem))]);

}
