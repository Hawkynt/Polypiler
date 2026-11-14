using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>intrinsics</c> (PRD §4.1, §4.3): one document per intrinsic under
/// <c>spec/intrinsics/&lt;group in lower case&gt;/&lt;name in lower case&gt;.yaml</c>, its contract —
/// signature, legality, effects, exactness, reference algorithm and reference vectors — that a backend
/// lowers from and a recognizer rewrites to. The schema <c>spec/schemas/intrinsics.schema.json</c> states
/// the shape; <see cref="Check"/> adds the rules across fields and files.
/// </summary>
public sealed class IntrinsicsSpecFamily : ISpecFamily {

  private const string TypesPrefix = "TYPES.";

  private const string ArraySuffix = "[]";

  /// <summary>The groups whose intrinsics may be inexact; every other group is exact.</summary>
  private static readonly HashSet<string> _inexactGroups = new(StringComparer.Ordinal) { "FloatMath", "Trig", "Matrix", "Tensor" };

  /// <inheritdoc />
  public string Folder => "intrinsics";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/intrinsics.schema.json";

  /// <summary>
  /// The rules of the family, each <c>PLY0046</c> at the offending value: <c>layout</c> (the path is
  /// <c>intrinsics/&lt;group&gt;/&lt;name&gt;.yaml</c> in lower case and the ID <c>INTRINSICS.&lt;name&gt;</c>;
  /// when it fails, nothing else is checked), <c>type-bound</c> (every type is declared, every vector binds
  /// each type parameter to a type its constraint admits), <c>vector-arity</c>, <c>vector-width</c>,
  /// <c>unique-name</c> (names ignoring case, spellings exactly; reported at the later document in path
  /// order) and <c>integer-is-exact</c>. Every <c>TYPES.</c>, <c>EFFECTS.</c>, <c>IR.</c>,
  /// <c>INTRINSICS.</c> and <c>VERIFICATION.</c> ID must name a document (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the references and the uniqueness rule look into.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var body = (JsonObject)document.Body!;
    if (Layout(document, body) is { } layout)
      return [layout];

    var problems = new List<Diagnostic>();
    problems.AddRange(Unique(document, body, tree));
    var group = (string)body["group"]!;
    var mode = (string)body["exactness"]!["mode"]!;
    if (mode != "exact" && !_inexactGroups.Contains(group))
      problems.Add(SpecDiagnostics.Rule(document, "/spec/exactness/mode", $"group '{group}' must be exact, found mode '{mode}'", "integer-is-exact"));

    problems.AddRange(References(document, body, tree));
    var typeParameters = ((JsonArray)body["typeParameters"]!).ToDictionary(parameter => (string)parameter!["name"]!, parameter => (string)parameter!["constraint"]!, StringComparer.Ordinal);
    var parameters = Values(document, body, "parameters", typeParameters, tree, problems);
    var returns = Values(document, body, "returns", typeParameters, tree, problems);
    var vectors = (JsonArray)body["vectors"]!;
    for (var index = 0; index < vectors.Count; ++index)
      problems.AddRange(Vector(document, tree, index, (JsonObject)vectors[index]!, typeParameters, parameters, returns));
    return problems;
  }

  /// <summary>Rule <c>layout</c>: <c>intrinsics/&lt;group in lower case&gt;/&lt;name in lower case&gt;.yaml</c> with the ID <c>INTRINSICS.&lt;name&gt;</c>.</summary>
  private static Diagnostic? Layout(SpecDocument document, JsonObject body) {
    var name = (string)body["name"]!;
    var group = (string)body["group"]!;
    var expected = $"intrinsics/{group.ToLowerInvariant()}/{name.ToLowerInvariant()}.yaml";
    if (document.Path.Split('/') is not [_, var folder, _])
      return SpecDiagnostics.Rule(document, "", $"path spec/{document.Path} is outside the layout of spec/intrinsics/: <group>/<name>.yaml, expected spec/{expected}", "layout");
    if (folder != group.ToLowerInvariant())
      return SpecDiagnostics.Rule(document, "/spec/group", $"group '{group}' does not match folder '{folder}', expected spec/{expected}", "layout");
    if (document.Path != expected)
      return SpecDiagnostics.Rule(document, "/spec/name", $"name '{name}' does not match path spec/{document.Path}, expected spec/{expected}", "layout");
    return document.Id == "INTRINSICS." + name
      ? null
      : SpecDiagnostics.Rule(document, "/id", $"id '{document.Id}' does not match spec.name '{name}'", "layout");
  }

  /// <summary>Rule <c>unique-name</c>: no earlier intrinsic has the name, ignoring case, or the spelling.</summary>
  private static IEnumerable<Diagnostic> Unique(SpecDocument document, JsonObject body, SpecTree tree) {
    var earlier = tree.Documents.TakeWhile(other => !ReferenceEquals(other, document)).Where(other => other.Family == document.Family).ToList();
    var name = (string)body["name"]!;
    if (earlier.FirstOrDefault(other => Text(other, "name") is { } otherName && string.Equals(otherName, name, StringComparison.OrdinalIgnoreCase)) is { } holder)
      yield return SpecDiagnostics.Rule(document, "/spec/name", $"intrinsic '{name}' is already defined by {holder.Id}", "unique-name");
    if (body["spelling"] is { } spellingNode && (string)spellingNode! is var spelling
      && earlier.FirstOrDefault(other => Text(other, "spelling") == spelling) is { } speller)
      yield return SpecDiagnostics.Rule(document, "/spec/spelling", $"spelling '{spelling}' is already used by {speller.Id}", "unique-name");
  }

  /// <summary>A string property of another document's body, or <see langword="null"/> when it has none.</summary>
  private static string? Text(SpecDocument document, string property)
    => document.Body is JsonObject body && body[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

  /// <summary>The references of the body other than types: effects, the tolerance policy, the stage, the decomposition and the attribute types.</summary>
  private static IEnumerable<Diagnostic> References(SpecDocument document, JsonObject body, SpecTree tree) {
    var references = new List<(string Pointer, string Id)>();
    var effects = (JsonArray)body["effects"]!;
    for (var index = 0; index < effects.Count; ++index)
      if ((string)effects[index]! is var effect && effect != "pure")
        references.Add(($"/spec/effects/{index}", effect));
    if (body["exactness"]!["policy"] is { } policy)
      references.Add(("/spec/exactness/policy", (string)policy!));
    references.Add(("/spec/mustSurviveUntil", (string)body["mustSurviveUntil"]!));
    if (body["lowersTo"] is JsonArray lowersTo)
      for (var index = 0; index < lowersTo.Count; ++index)
        references.Add(($"/spec/lowersTo/{index}", (string)lowersTo[index]!));
    if (body["attributes"] is JsonArray attributes)
      for (var index = 0; index < attributes.Count; ++index)
        references.Add(($"/spec/attributes/{index}/type", (string)attributes[index]!["type"]!));
    return references.Select(reference => tree.Resolve(document, reference.Pointer, reference.Id)).OfType<Diagnostic>();
  }

  /// <summary>
  /// The types of the parameters or return values: a <c>TYPES.</c> ID must name a document
  /// (<c>PLY0043</c>), any other name must be a type parameter (<c>type-bound</c>). A trailing <c>[]</c>
  /// makes the value an array of that type.
  /// </summary>
  private static List<ValueType> Values(SpecDocument document, JsonObject body, string property, Dictionary<string, string> typeParameters, SpecTree tree, List<Diagnostic> problems) {
    var values = new List<ValueType>();
    var list = (JsonArray)body[property]!;
    for (var index = 0; index < list.Count; ++index) {
      var pointer = $"/spec/{property}/{index}/type";
      var text = (string)list[index]!["type"]!;
      var isArray = text.EndsWith(ArraySuffix, StringComparison.Ordinal);
      var type = isArray ? text[..^ArraySuffix.Length] : text;
      if (type.StartsWith(TypesPrefix, StringComparison.Ordinal)) {
        if (tree.Resolve(document, pointer, type) is { } broken)
          problems.Add(broken);
      } else if (!typeParameters.ContainsKey(type)) {
        problems.Add(SpecDiagnostics.Rule(document, pointer, $"{property}[{index}] has type '{text}', which is no type parameter of the intrinsic", "type-bound"));
      }
      values.Add(new(type, isArray));
    }
    return values;
  }

  /// <summary>The rules <c>type-bound</c>, <c>vector-arity</c> and <c>vector-width</c> for one vector, and its type references.</summary>
  private static IEnumerable<Diagnostic> Vector(SpecDocument document, SpecTree tree, int index, JsonObject vector, Dictionary<string, string> typeParameters, List<ValueType> parameters, List<ValueType> returns) {
    var name = $"vectors[{index}]";
    var pointer = $"/spec/vectors/{index}";
    var types = (JsonObject)vector["types"]!;
    var bound = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (parameter, value) in types) {
      var id = (string)value!;
      var at = $"{pointer}/types/{parameter}";
      if (!typeParameters.TryGetValue(parameter, out var constraint)) {
        yield return SpecDiagnostics.Rule(document, at, $"{name} binds '{parameter}', which is no type parameter of the intrinsic", "type-bound");
      } else if (tree.Resolve(document, at, id) is { } broken) {
        yield return broken;
      } else {
        if (Excludes(constraint, tree.ById[id]) is { } reason)
          yield return SpecDiagnostics.Rule(document, at, $"{name} binds '{parameter}' to {id} {reason}, which the constraint '{constraint}' excludes", "type-bound");
        bound[parameter] = id;
      }
    }

    foreach (var parameter in typeParameters.Keys.Where(parameter => !types.ContainsKey(parameter)))
      yield return SpecDiagnostics.Rule(document, pointer + "/types", $"{name} does not bind type parameter '{parameter}'", "type-bound");

    foreach (var (list, declared, noun) in new[] { ("inputs", parameters, "parameter"), ("outputs", returns, "return value") }) {
      var literals = (JsonArray)vector[list]!;
      if (literals.Count != declared.Count) {
        yield return SpecDiagnostics.Rule(document, $"{pointer}/{list}", $"{name} has {literals.Count} {list}, the intrinsic declares {declared.Count} {noun}{(declared.Count == 1 ? "" : "s")}", "vector-arity");
        continue;
      }

      for (var position = 0; position < literals.Count; ++position)
        foreach (var problem in Width(document, tree, $"{name}.{list}[{position}]", $"{pointer}/{list}/{position}", literals[position]!, declared[position], bound))
          yield return problem;
    }
  }

  /// <summary>Why a type document does not satisfy a constraint, or <see langword="null"/> when it does.</summary>
  private static string? Excludes(string constraint, SpecDocument type) {
    var family = Text(type, "family");
    var signedness = Text(type, "signedness");
    var admitted = constraint switch {
      "integer" => family == "integer",
      "unsigned" => family == "integer" && signedness == "unsigned",
      "signed" => family == "integer" && signedness == "signed",
      "float" => family == "float",
      _ => true
    };
    return admitted ? null : $"of family '{family}' and signedness '{signedness}'";
  }

  /// <summary>Rule <c>vector-width</c>: a literal, or each element of a list for an array type, fits the type bound to it.</summary>
  private static IEnumerable<Diagnostic> Width(SpecDocument document, SpecTree tree, string name, string pointer, JsonNode literal, ValueType declared, Dictionary<string, string> bound) {
    var id = declared.Type.StartsWith(TypesPrefix, StringComparison.Ordinal) ? declared.Type : bound.GetValueOrDefault(declared.Type);
    if (id is null || !tree.ById.TryGetValue(id, out var type) || type.Body is not JsonObject body
      || body["bits"] is not JsonValue bitsValue || !bitsValue.TryGetValue<long>(out var bits) || Text(type, "signedness") is not { } signedness)
      yield break;

    // A value of a type of the family text is the list of its code units; an array of them a list of such lists.
    if (Text(type, "family") == "text") {
      foreach (var problem in Units(document, name, pointer, literal, declared.IsArray, id, bits))
        yield return problem;

      yield break;
    }

    if (literal is JsonArray != declared.IsArray) {
      yield return declared.IsArray
        ? SpecDiagnostics.Rule(document, pointer, $"{name} {Shown(literal)} is not a list, the value is an array of {id}", "vector-width")
        : SpecDiagnostics.Rule(document, pointer, $"{name} is a list, the value is a single {id}", "vector-width");
      yield break;
    }

    var scalars = literal is JsonArray list
      ? list.Select((element, position) => (Name: $"{name}[{position}]", Pointer: $"{pointer}/{position}", Literal: element!))
      : [(name, pointer, literal)];
    foreach (var scalar in scalars)
      if (!Fits(scalar.Literal, bits, signedness))
        yield return SpecDiagnostics.Rule(document, scalar.Pointer, $"{scalar.Name} {Shown(scalar.Literal)} does not fit {id} ({bits} bits)", "vector-width");
  }

  /// <summary>
  /// Whether a literal fits a type: a hexadecimal string is a bit pattern below 2^bits; a decimal integer
  /// is a value of the type's range, two's complement for a signed type, from 0 otherwise.
  /// </summary>
  /// <summary>The code units of a string, or of every string of an array of them: each a list of unsigned scalars of the type's width.</summary>
  private static IEnumerable<Diagnostic> Units(SpecDocument document, string name, string pointer, JsonNode literal, bool isArray, string id, long bits) {
    var texts = isArray
      ? literal is JsonArray list ? list.Select((element, position) => (Name: $"{name}[{position}]", Pointer: $"{pointer}/{position}", Literal: element!)).ToList() : null
      : [(Name: name, Pointer: pointer, Literal: literal)];
    if (texts is null) {
      yield return SpecDiagnostics.Rule(document, pointer, $"{name} {Shown(literal)} is not a list, the value is an array of {id}", "vector-width");
      yield break;
    }

    foreach (var text in texts) {
      if (text.Literal is not JsonArray units || units.Any(unit => unit is JsonArray)) {
        yield return SpecDiagnostics.Rule(document, text.Pointer, $"{text.Name} {Shown(text.Literal)} is not the list of the code units of a {id}", "vector-width");
        continue;
      }

      for (var position = 0; position < units.Count; ++position)
        if (!Fits(units[position]!, bits, "unsigned"))
          yield return SpecDiagnostics.Rule(document, $"{text.Pointer}/{position}", $"{text.Name}[{position}] {Shown(units[position]!)} does not fit a code unit of {id} ({bits} bits)", "vector-width");
    }
  }

  private static bool Fits(JsonNode literal, long bits, string signedness) {
    var text = literal.ToJsonString();
    if (text.StartsWith('"'))
      return BigInteger.Parse("0" + ((string)literal!)[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) < BigInteger.One << (int)bits;

    var value = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    var (low, high) = signedness == "signed"
      ? (-(BigInteger.One << (int)(bits - 1)), (BigInteger.One << (int)(bits - 1)) - 1)
      : (BigInteger.Zero, (BigInteger.One << (int)bits) - 1);
    return value >= low && value <= high;
  }

  /// <summary>A literal as the document writes it.</summary>
  private static string Shown(JsonNode literal) => literal is JsonValue value && value.TryGetValue<string>(out var text) ? text : literal.ToJsonString();

  /// <summary>The type of a parameter or return value: a type parameter or <c>TYPES.</c> ID, and whether the value is an array of it.</summary>
  private sealed record ValueType(string Type, bool IsArray);

}
