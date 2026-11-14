using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family of type descriptors, the folder <c>types</c> (PRD §4.1): one document per type at
/// <c>spec/types/&lt;family&gt;/&lt;name&gt;.yaml</c> with the ID <c>TYPES.&lt;name&gt;</c>, whose body
/// <c>spec/schemas/types.schema.json</c> describes. Semantic width, storage and signedness stay separate
/// properties (PRD §4.4), and types of the same size are kept apart by <c>distinctFrom</c> (PRD §5.2).
/// </summary>
public sealed class TypesSpecFamily : ISpecFamily {

  /// <summary>The properties each family requires beyond the ones every type has.</summary>
  private static readonly (string Property, string[] Families)[] _required = [
    ("lanes", ["vector"]),
    ("overflow", ["integer", "fixed"]),
    ("rounding", ["float", "fixed", "decimal", "posit", "mbf"]),
    ("nan", ["float", "posit", "mbf"]),
    ("subnormals", ["float", "mbf"]),
    ("encoding", ["encoded-integer"]),
  ];

  /// <summary>The properties only the families that require them may have.</summary>
  private static readonly HashSet<string> _exclusive = new(StringComparer.Ordinal) { "lanes", "encoding" };

  /// <summary>The families whose signedness may be <c>none</c>.</summary>
  private static readonly HashSet<string> _unsigned = new(StringComparer.Ordinal) { "boolean", "text", "custom" };

  /// <inheritdoc />
  public string Folder => "types";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/types.schema.json";

  /// <summary>
  /// The rules <c>layout</c>, <c>bits-fit-layout</c>, <c>example-width</c>, <c>distinct-symmetric</c>,
  /// <c>alignment-power-of-two</c> and <c>family-requirements</c> (<c>PLY0046</c>), and the references
  /// of <c>encoding.of</c> and <c>distinctFrom</c> (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the references are resolved in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    var body = (JsonObject)document.Body!;
    var family = (string)body["family"]!;
    var bits = Whole(body["bits"]!);
    var layout = (JsonObject)body["layout"]!;

    foreach (var problem in Layout(document, family))
      yield return problem;

    var sizeBytes = Whole(layout["sizeBytes"]!);
    if (sizeBytes * 8 < bits)
      yield return SpecDiagnostics.Rule(document, "/spec/bits", $"bits {bits} do not fit layout.sizeBytes {sizeBytes}", "bits-fit-layout");

    var values = (JsonArray)body["values"]!;
    for (var index = 0; index < values.Count; ++index) {
      var literal = (string)values[index]!["bits"]!;
      var needed = SignificantBits(literal);
      if (needed > bits)
        yield return SpecDiagnostics.Rule(document, $"/spec/values/{index}/bits", $"values[{index}].bits {literal} needs {needed} bits, the type has {bits}", "example-width");
    }

    var distinct = (JsonArray)body["distinctFrom"]!;
    for (var index = 0; index < distinct.Count; ++index) {
      var other = (string)distinct[index]!;
      var pointer = $"/spec/distinctFrom/{index}";
      if (tree.Resolve(document, pointer, other) is { } broken)
        yield return broken;
      else if (!ListsBack(tree.ById[other], document.Id!))
        yield return SpecDiagnostics.Rule(document, pointer, $"{document.Id} lists {other} as distinct, but {other} does not list {document.Id}", "distinct-symmetric");
    }

    var alignment = Whole(layout["alignment"]!);
    if (!BitOperations.IsPow2(alignment))
      yield return SpecDiagnostics.Rule(document, "/spec/layout/alignment", $"layout.alignment {alignment} is not a power of two", "alignment-power-of-two");

    foreach (var problem in Requirements(document, body, family))
      yield return problem;

    if (body["encoding"] is JsonObject encoding && tree.Resolve(document, "/spec/encoding/of", (string)encoding["of"]!) is { } dangling)
      yield return dangling;
  }

  /// <summary>Rule <c>layout</c>: the file is <c>types/&lt;family&gt;/&lt;name&gt;.yaml</c> and the ID is <c>TYPES.&lt;name&gt;</c>.</summary>
  private static IEnumerable<Diagnostic> Layout(SpecDocument document, string family) {
    var parts = document.Path.Split('/');
    if (parts.Length != 3 || parts[1] != family)
      yield return SpecDiagnostics.Rule(document, "/spec/family", $"spec/{document.Path} declares family '{family}', expected folder spec/types/{family}", "layout");

    var name = System.IO.Path.GetFileNameWithoutExtension(parts[^1]);
    var expected = "TYPES." + name;
    if (document.Id != expected)
      yield return SpecDiagnostics.Rule(document, "/id", $"id '{document.Id}' does not follow from the file name, expected '{expected}'", "layout");
  }

  /// <summary>Rule <c>family-requirements</c>: what the family requires is there, what it excludes is not, and <c>none</c> is a signedness only where the family allows it.</summary>
  private static IEnumerable<Diagnostic> Requirements(SpecDocument document, JsonObject body, string family) {
    foreach (var (property, families) in _required) {
      var required = families.Contains(family, StringComparer.Ordinal);
      var present = body.ContainsKey(property);
      if (required && !present)
        yield return SpecDiagnostics.Rule(document, "/spec/family", $"family '{family}' requires '{property}'", "family-requirements");
      else if (!required && present && _exclusive.Contains(property))
        yield return SpecDiagnostics.Rule(document, $"/spec/{property}", $"family '{family}' excludes '{property}'", "family-requirements");
    }

    if ((string)body["signedness"]! == "none" && !_unsigned.Contains(family))
      yield return SpecDiagnostics.Rule(document, "/spec/signedness", $"family '{family}' does not allow signedness 'none'", "family-requirements");
  }

  /// <summary>Whether the document lists the ID in its <c>distinctFrom</c>; a document whose body is broken reports that itself, so it counts as listing it.</summary>
  private static bool ListsBack(SpecDocument other, string id)
    => (other.Body as JsonObject)?["distinctFrom"] is not JsonArray list
      || list.Any(entry => entry is JsonValue value && value.TryGetValue<string>(out var text) && text == id);

  /// <summary>An integer of the body, which YAML may also have written as <c>4.0</c>; the schema has checked that it is whole and in range.</summary>
  private static long Whole(JsonNode node) => node.AsValue().TryGetValue<long>(out var number) ? number : (long)node.GetValue<double>();

  /// <summary>The number of bits a hexadecimal literal <c>0x…</c> needs, leading zeros not counted.</summary>
  private static long SignificantBits(string literal)
    => BigInteger.Parse("0" + literal[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture).GetBitLength();

}
