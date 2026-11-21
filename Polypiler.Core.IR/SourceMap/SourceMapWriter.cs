using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Polypiler.Core.IR;

/// <summary>
/// Writes the source map of a function, a <c>*.map.json</c> of the shape
/// <c>spec/ir/sourcemap.schema.json</c> gives. It follows the field names of the Source Map
/// specification (ECMA-426) where the concepts coincide - <c>version</c>, <c>sources</c> - and differs
/// where they do not: the hashes of the sources stand where ECMA-426 has their content, and the
/// mappings are a list of instructions with their origins rather than a VLQ string. It never holds an
/// absolute path or any text of a source.
/// </summary>
public static class SourceMapWriter {

  /// <summary>The version of the layout.</summary>
  public const int Version = 1;

  /// <summary>Writes the map of a function.</summary>
  /// <param name="module">The module the function belongs to, whose <see cref="IrModule.Sources"/> the origins name.</param>
  /// <param name="function">The function.</param>
  /// <returns>
  /// The JSON, indented, with LF line ends: the version, the name of the function, the sources with
  /// their hashes and dialects, and one mapping per instruction (<see cref="IrOrigins.Instructions"/>)
  /// with its number, its kind and its origins in order.
  /// </returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static string Write(IrModule module, IrFunction function) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(function);
    using var buffer = new MemoryStream();
    using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" })) {
      json.WriteStartObject();
      json.WriteNumber("version", Version);
      json.WriteString("function", function.Name);
      json.WriteStartArray("sources");
      foreach (var source in module.Sources)
        json.WriteStringValue(source.Reference);
      json.WriteEndArray();
      json.WriteStartArray("sourcesSha256");
      foreach (var source in module.Sources)
        json.WriteStringValue(source.Sha256);
      json.WriteEndArray();
      json.WriteStartArray("dialects");
      foreach (var source in module.Sources)
        json.WriteStringValue(source.Dialect);
      json.WriteEndArray();
      json.WriteStartArray("mappings");
      foreach (var (number, statement) in IrOrigins.Instructions(function)) {
        json.WriteStartObject();
        json.WriteNumber("instruction", number);
        json.WriteString("kind", OriginRule.Kind(statement));
        json.WriteStartArray("origins");
        foreach (var origin in statement.Origins)
          Origin(json, origin);
        json.WriteEndArray();
        json.WriteEndObject();
      }

      json.WriteEndArray();
      json.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
  }

  private static void Origin(Utf8JsonWriter json, Origin origin) {
    json.WriteStartObject();
    switch (origin) {
      case SourceSpan span:
        json.WriteNumber("source", span.Artifact);
        json.WriteNumber("startLine", span.StartLine);
        json.WriteNumber("startColumn", span.StartColumn);
        json.WriteNumber("endLine", span.EndLine);
        json.WriteNumber("endColumn", span.EndColumn);
        break;
      case AddressSpan address:
        json.WriteNumber("source", address.Artifact);
        json.WriteNumber("offset", address.Offset);
        json.WriteNumber("length", address.Length);
        break;
      default:
        json.WriteString("synthetic", ((Synthetic)origin).PassId);
        break;
    }

    json.WriteEndObject();
  }

}
