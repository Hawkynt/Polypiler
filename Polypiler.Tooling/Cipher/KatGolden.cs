using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Polypiler.Tooling.Cipher;

/// <summary>
/// A value as <c>tooling/cipher/record-kats.mjs</c> records it: its kind - <c>bytes</c> (hexadecimal),
/// <c>typed</c> (a typed array that holds no bytes), <c>array</c>, <c>number</c>, <c>bigint</c>,
/// <c>string</c>, <c>bool</c>, <c>null</c>, <c>undefined</c>, <c>object</c> or <c>function</c> - and
/// the JSON that holds it.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Json">The recorded value, <c>{"t": kind, "v": …}</c>.</param>
public sealed record KatValue(string Kind, JsonElement Json) {

  /// <summary>Whether a translation can be given the value: anything but an object or a function, which have no form outside node.</summary>
  public bool IsData => this.Kind switch {
    "object" or "function" => false,
    "array" => this.Json.GetProperty("v").EnumerateArray().All(item => Of(item).IsData),
    _ => true
  };

  /// <summary>The bytes of a value of the kind <c>bytes</c>; <see langword="null"/> for another kind.</summary>
  public byte[]? Bytes => this.Kind == "bytes" ? Convert.FromHexString(this.Json.GetProperty("v").GetString()!) : null;

  /// <summary>Reads a recorded value.</summary>
  /// <exception cref="FormatException">The JSON is no recorded value.</exception>
  public static KatValue Of(JsonElement json)
    => json.ValueKind == JsonValueKind.Object && json.TryGetProperty("t", out var kind) && kind.ValueKind == JsonValueKind.String
      ? new(kind.GetString()!, json.Clone())
      : throw new FormatException($"a recorded value is an object with a kind 't', not {json.GetRawText()}");

}

/// <summary>What the corpus's test engine does to an instance before a vector is fed: set a property, or call a method.</summary>
/// <param name="Name">The property or the method.</param>
/// <param name="IsCall">Whether it is a call.</param>
/// <param name="Values">The value set, or the arguments of the call.</param>
public sealed record KatStep(string Name, bool IsCall, IReadOnlyList<KatValue> Values);

/// <summary>What node gave for a vector.</summary>
/// <param name="Outcome"><c>passed</c> (the expected bytes, or a result where none is expected), <c>failed</c> (other bytes) or <c>threw</c>.</param>
/// <param name="Output">The result, or <see langword="null"/> when node threw.</param>
/// <param name="Error">Why node threw, or <see langword="null"/>.</param>
/// <param name="Deterministic">Whether two runs on two instances gave the same.</param>
public sealed record KatNode(string Outcome, KatValue? Output, string? Error, bool Deterministic);

/// <summary>One known-answer test of an algorithm.</summary>
/// <param name="Text">Its description.</param>
/// <param name="Inverse">Whether it runs the inverse: an instance of <c>CreateInstance(true)</c>.</param>
/// <param name="Steps">What configures the instance, in order.</param>
/// <param name="Input">What is fed.</param>
/// <param name="Expected">The expected result, or <see langword="null"/> when the vector names none.</param>
/// <param name="Node">What node gave.</param>
public sealed record KatVector(string Text, bool Inverse, IReadOnlyList<KatStep> Steps, KatValue Input, KatValue? Expected, KatNode Node) {

  /// <summary>
  /// Why a translation cannot be checked on the vector, or <see langword="null"/> when it can: the
  /// original must pass it in node, give every value as data and, without an expected result, give
  /// the same result every time - which is then the answer the translation must give.
  /// </summary>
  public string? Unchecked {
    get {
      if (this.Node.Outcome != "passed")
        return $"node {this.Node.Outcome}";
      if (!this.Input.IsData || this.Steps.Any(step => step.Values.Any(value => !value.IsData)))
        return "a value that is an object";
      if (this.Answer is not { Kind: "bytes" })
        return "a result that is no bytes";
      return this.Expected is null && !this.Node.Deterministic ? "a result that differs from run to run" : null;
    }
  }

  /// <summary>The bytes a translation must give: the expected ones, or what node gave where none are expected.</summary>
  public KatValue? Answer => this.Expected ?? this.Node.Output;

}

/// <summary>An algorithm a file registers, with its vectors.</summary>
/// <param name="Name">The name it is registered under.</param>
/// <param name="Vectors">Its vectors, in order.</param>
public sealed record KatAlgorithm(string Name, IReadOnlyList<KatVector> Vectors);

/// <summary>
/// The golden file of one algorithm file: what node recorded of every algorithm the file registers,
/// or why the file did not load.
/// </summary>
/// <param name="File">The file, below the Cipher folder: <c>algorithms/checksum/adler.js</c>.</param>
/// <param name="Algorithms">The algorithms it registers, in order.</param>
/// <param name="Error">Why node could not load the file, or <see langword="null"/>.</param>
public sealed record KatFile(string File, IReadOnlyList<KatAlgorithm> Algorithms, string? Error) {

  /// <summary>The schema of the golden files this reader reads.</summary>
  public const int Schema = 1;

  /// <summary>The file below the <c>algorithms</c> folder: <c>checksum/adler.js</c>.</summary>
  public string Relative => this.File.StartsWith("algorithms/", StringComparison.Ordinal) ? this.File["algorithms/".Length..] : this.File;

  /// <summary>Every vector of every algorithm, with its algorithm.</summary>
  public IEnumerable<(KatAlgorithm Algorithm, KatVector Vector)> Vectors => this.Algorithms.SelectMany(algorithm => algorithm.Vectors.Select(vector => (algorithm, vector)));

  /// <summary>Reads a golden file.</summary>
  /// <exception cref="FormatException">The text is no golden file of <see cref="Schema"/>.</exception>
  public static KatFile Parse(string text) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(text);
    } catch (JsonException exception) {
      throw new FormatException($"not JSON: {exception.Message}", exception);
    }

    using (document) {
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != Schema)
        throw new FormatException($"not a golden file of schema {Schema}");

      try {
        return new(
          root.GetProperty("file").GetString()!,
          [.. root.GetProperty("algorithms").EnumerateArray().Select(Algorithm)],
          root.TryGetProperty("error", out var error) ? error.GetString() : null);
      } catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException) {
        throw new FormatException($"a golden file lacks a field or has one of the wrong kind: {exception.Message}", exception);
      }
    }
  }

  /// <summary>Reads the golden file of an algorithm file below a folder of golden files.</summary>
  /// <param name="folder">The folder, <c>tests/fixtures/corpus/cipher</c>.</param>
  /// <param name="relative">The algorithm file below <c>algorithms/</c>: <c>checksum/adler.js</c>.</param>
  /// <exception cref="FormatException">The golden file is not one.</exception>
  /// <exception cref="IOException">There is no golden file, or it cannot be read.</exception>
  public static KatFile Load(string folder, string relative) {
    var path = PathOf(folder, relative);
    if (System.IO.File.Exists(path))
      return Parse(System.IO.File.ReadAllText(path));

    // A golden file of more than a mebibyte is compressed.
    using var file = System.IO.File.OpenRead(path + ".gz");
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip);
    return Parse(reader.ReadToEnd());
  }

  /// <summary>The golden file of an algorithm file below a folder of golden files, without the <c>.gz</c> of a compressed one.</summary>
  public static string PathOf(string folder, string relative)
    => Path.Combine(folder, Path.ChangeExtension(relative, null).Replace('/', Path.DirectorySeparatorChar) + ".kat.json");

  private static KatAlgorithm Algorithm(JsonElement json)
    => new(json.GetProperty("name").GetString()!, [.. json.GetProperty("vectors").EnumerateArray().Select(Vector)]);

  private static KatVector Vector(JsonElement json) {
    var node = json.GetProperty("node");
    return new(
      json.GetProperty("text").GetString()!,
      json.GetProperty("inverse").GetBoolean(),
      [.. json.GetProperty("steps").EnumerateArray().Select(Step)],
      KatValue.Of(json.GetProperty("input")),
      json.TryGetProperty("expected", out var expected) ? KatValue.Of(expected) : null,
      new KatNode(
        node.GetProperty("outcome").GetString()!,
        node.TryGetProperty("output", out var output) ? KatValue.Of(output) : null,
        node.TryGetProperty("error", out var error) ? error.GetString() : null,
        node.GetProperty("deterministic").GetBoolean()));
  }

  private static KatStep Step(JsonElement json)
    => json.TryGetProperty("call", out var call)
      ? new(call.GetString()!, true, [.. json.GetProperty("args").EnumerateArray().Select(KatValue.Of)])
      : new(json.GetProperty("set").GetString()!, false, [KatValue.Of(json.GetProperty("value"))]);

}
