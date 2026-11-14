using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Polypiler.Core;

namespace Polypiler.Permutation.Model;

/// <summary>
/// The form a mapping takes in <c>spec/permutations/</c> (the <c>mapping</c> kind of
/// <c>spec/schemas/permutations.schema.json</c>), as JSON: <c>{"inputWidth": w, "outputs": [...]}</c>,
/// least significant output first. An output is an integer (that input bit), <c>{"not": i}</c> (input
/// bit <c>i</c> inverted), <c>{"const": 0}</c> or <c>{"const": 1}</c>, or <c>{"dontCare": true}</c>.
/// Reading ignores the other fields of a specification body (<c>kind</c>, <c>bijective</c>,
/// <c>vectors</c>).
/// </summary>
public static class BitMappingDocument {

  /// <summary>Reads a mapping.</summary>
  /// <param name="stream">The JSON document.</param>
  /// <param name="name">The name diagnostics give the document; <c>stream</c> by default.</param>
  /// <returns>The mapping.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The document is malformed (<c>PLY0802</c>) or describes no valid mapping (<c>PLY0800</c>, <c>PLY0801</c>).</exception>
  public static BitMapping Read(Stream stream, string? name = null) {
    ArgumentNullException.ThrowIfNull(stream);
    name ??= "stream";
    using var document = MappingJson.Load(stream, name);
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
      throw MappingJson.Malformed(name, "expected an object");

    var width = MappingJson.Width(root, "inputWidth", name);
    if (!root.TryGetProperty("outputs", out var entries) || entries.ValueKind != JsonValueKind.Array)
      throw MappingJson.Malformed(name, "missing outputs");

    var outputs = new List<BitSource>();
    foreach (var entry in entries.EnumerateArray())
      outputs.Add(Entry(entry, outputs.Count, width, name));
    return BitMapping.Create(width, outputs);
  }

  private static BitSource Entry(JsonElement entry, int bit, int width, string name) {
    if (entry.ValueKind == JsonValueKind.Number)
      return BitSource.From(Index(entry, bit, width, name));

    var unknown = MappingJson.Malformed(name, $"output bit {bit}: expected an input bit, {{\"not\": i}}, {{\"const\": 0|1}} or {{\"dontCare\": true}}, got {entry.GetRawText()}");
    if (entry.ValueKind != JsonValueKind.Object)
      throw unknown;

    using var properties = entry.EnumerateObject();
    if (!properties.MoveNext())
      throw unknown;

    var property = properties.Current;
    if (properties.MoveNext())
      throw unknown;

    var value = property.Value;
    return property.Name switch {
      "not" when value.ValueKind == JsonValueKind.Number => BitSource.Not(Index(value, bit, width, name)),
      "const" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var constant) && constant is 0 or 1 => constant == 0 ? BitSource.Zero : BitSource.One,
      "dontCare" when value.ValueKind == JsonValueKind.True => BitSource.DontCare,
      _ => throw unknown
    };
  }

  /// <summary>An input bit; an integer beyond <see cref="int"/> lies outside every input (<c>PLY0801</c>), a fraction is no input bit (<c>PLY0802</c>).</summary>
  private static int Index(JsonElement value, int bit, int width, string name) {
    if (value.TryGetInt32(out var index))
      return index;
    if (value.TryGetInt64(out _))
      throw new PermutationModelException(Diagnostic.Create(PermutationDiagnostics.SourceOutOfRange, null, bit, value.GetRawText(), width));

    throw MappingJson.Malformed(name, $"output bit {bit}: {value.GetRawText()} is not an input bit");
  }

  /// <summary>Writes a mapping as one line of JSON that <see cref="Read"/> reads back to an equal mapping.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <param name="stream">Receives the UTF-8 JSON.</param>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static void Write(BitMapping mapping, Stream stream) {
    ArgumentNullException.ThrowIfNull(mapping);
    ArgumentNullException.ThrowIfNull(stream);
    using var writer = new Utf8JsonWriter(stream);
    writer.WriteStartObject();
    writer.WriteNumber("inputWidth", mapping.InputWidth);
    writer.WriteStartArray("outputs");
    foreach (var entry in mapping.Outputs) {
      switch (entry.Kind) {
        case BitSourceKind.Source:
          writer.WriteNumberValue(entry.Index);
          continue;
        case BitSourceKind.InvertedSource:
          writer.WriteStartObject();
          writer.WriteNumber("not", entry.Index);
          break;
        case BitSourceKind.Zero:
        case BitSourceKind.One:
          writer.WriteStartObject();
          writer.WriteNumber("const", entry.Kind == BitSourceKind.One ? 1 : 0);
          break;
        default:
          writer.WriteStartObject();
          writer.WriteBoolean("dontCare", true);
          break;
      }
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
  }

}
