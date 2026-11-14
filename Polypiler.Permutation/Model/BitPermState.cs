using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Polypiler.Core;

namespace Polypiler.Permutation.Model;

/// <summary>
/// Reads BitPerm's mapping state, in the form <c>tooling/bitperm/export.mjs</c> exports it to
/// <c>tests/fixtures/bitperm/mappings</c>: <c>{"width": w, "mapping": [{"type", "val", "inv"}, …]}</c>,
/// entry <c>i</c> being output bit <c>i</c>. <c>src</c> is an input bit, inverted when <c>inv</c> is
/// <see langword="true"/>; <c>const</c> is <see cref="BitSource.Zero"/> or <see cref="BitSource.One"/>
/// by <c>val</c>; <c>empty</c>, the page's unconnected bit, is <see cref="BitSource.DontCare"/>.
/// The page's other entry type, <c>collision</c>, is display state and is refused.
/// </summary>
public static class BitPermState {

  /// <summary>Reads one mapping.</summary>
  /// <param name="stream">The JSON document.</param>
  /// <param name="name">The name diagnostics give the document; by default its <c>name</c> field, else <c>stream</c>.</param>
  /// <returns>The mapping, whose input and output width are the page's <c>width</c> and the number of entries.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The document is malformed (<c>PLY0802</c>) or describes no valid mapping (<c>PLY0800</c>, <c>PLY0801</c>).</exception>
  public static BitMapping Read(Stream stream, string? name = null) {
    ArgumentNullException.ThrowIfNull(stream);
    using var document = MappingJson.Load(stream, name);
    var root = document.RootElement;
    name ??= root.ValueKind == JsonValueKind.Object && root.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString()! : "stream";
    if (root.ValueKind != JsonValueKind.Object)
      throw MappingJson.Malformed(name, "expected an object");

    var width = MappingJson.Width(root, "width", name);
    if (!root.TryGetProperty("mapping", out var mapping) || mapping.ValueKind != JsonValueKind.Array)
      throw MappingJson.Malformed(name, "missing mapping");

    var outputs = new List<BitSource>();
    foreach (var entry in mapping.EnumerateArray())
      outputs.Add(Entry(entry, outputs.Count, name));
    return BitMapping.Create(width, outputs);
  }

  private static BitSource Entry(JsonElement entry, int bit, string name) {
    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
      throw MappingJson.Malformed(name, $"output bit {bit} has no type");

    switch (type.GetString()) {
      case "src":
        var index = Value(entry, bit, name);
        var inverted = entry.TryGetProperty("inv", out var inv) && inv.ValueKind switch {
          JsonValueKind.True => true,
          JsonValueKind.False => false,
          _ => throw MappingJson.Malformed(name, $"output bit {bit}: 'inv' is not true or false")
        };
        return inverted ? BitSource.Not(index) : BitSource.From(index);
      case "const":
        return Value(entry, bit, name) switch {
          0 => BitSource.Zero,
          1 => BitSource.One,
          var other => throw MappingJson.Malformed(name, $"output bit {bit}: constant {other} is neither 0 nor 1")
        };
      case "empty":
        return BitSource.DontCare;
      default:
        throw MappingJson.Malformed(name, $"output bit {bit} has unknown type '{type.GetString()}'");
    }
  }

  private static int Value(JsonElement entry, int bit, string name)
    => entry.TryGetProperty("val", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
      ? number
      : throw MappingJson.Malformed(name, $"output bit {bit} has no integer 'val'");

}

/// <summary>What the two JSON forms of a mapping share: loading, the width and the diagnostic of a malformed document.</summary>
internal static class MappingJson {

  internal static JsonDocument Load(Stream stream, string? name) {
    try {
      return JsonDocument.Parse(stream);
    } catch (JsonException exception) {
      throw Malformed(name ?? "stream", exception.Message);
    }
  }

  internal static int Width(JsonElement root, string property, string name) {
    if (!root.TryGetProperty(property, out var width))
      throw Malformed(name, $"missing {property}");
    if (width.ValueKind != JsonValueKind.Number || !width.TryGetInt64(out var value))
      throw Malformed(name, $"{property} is not an integer");
    if (!BitMapping.IsWidth(value))
      throw new PermutationModelException(Diagnostic.Create(PermutationDiagnostics.WidthOutOfRange, null, value));

    return (int)value;
  }

  internal static PermutationModelException Malformed(string name, string reason)
    => new(Diagnostic.Create(PermutationDiagnostics.Malformed, null, "document", name, reason));

}
