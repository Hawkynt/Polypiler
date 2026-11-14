using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>effects</c>: one document per effect class, stating whether an operation with that
/// effect may be reordered, duplicated or eliminated, what it reads and writes, and whether it traps.
/// The schema <c>spec/schemas/effects.schema.json</c> states the shape; this class the rules that look
/// across fields and files, each reported as <c>PLY0046</c>. The conservative case is the only legal
/// default, and the volatile classes cannot be relaxed.
/// </summary>
public sealed class EffectsSpecFamily : ISpecFamily {

  private const string Prefix = "EFFECTS.";

  /// <summary>The three permissions with their restrictive values.</summary>
  private static readonly (string Key, string Value)[] _restrictive = [("ordering", "strict"), ("duplication", "forbidden"), ("elimination", "forbidden")];

  /// <summary>The classes whose operations keep program order and count: ordering <c>strict</c>, duplication and elimination <c>forbidden</c>.</summary>
  public static IReadOnlySet<string> VolatileClasses { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "volatile", "mmio", "device-io", "code-write", "unknown-call"
  };

  /// <inheritdoc />
  public string Folder => "effects";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/effects.schema.json";

  /// <summary>
  /// The rules, in this order: <c>layout</c> (the file lies directly in <c>effects/</c>, its ID is
  /// <c>EFFECTS.&lt;file name&gt;</c> and its name the file name), <c>single-default</c> (exactly one
  /// document of the family is <c>defaultForUnknown</c>), <c>default-is-conservative</c>,
  /// <c>volatile-is-strict</c>, <c>write-implies-order</c> and <c>unique-name</c>.
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree, for the rules across the family's documents.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var body = document.Body!;
    var name = Text(body, "name")!;
    var effectClass = Text(body, "class")!;
    var ordering = Text(body, "ordering")!;
    var reads = Regions(body, "reads");
    var writes = Regions(body, "writes");
    var isDefault = IsDefault(body);
    var family = tree.Documents.Where(other => other.Family == this.Folder).ToList();

    foreach (var problem in Layout(document, name))
      yield return problem;

    var defaults = family.Where(other => IsDefault(other.Body)).ToList();
    if (defaults.Count == 0 && family[0] == document)
      yield return Rule(document, "defaultForUnknown", "no effect has defaultForUnknown: true", "single-default");
    var established = defaults.FirstOrDefault(other => Text(other.Body, "class") == "unknown-call") ?? defaults.FirstOrDefault();
    if (isDefault && established != document)
      yield return Rule(document, "defaultForUnknown", $"{document.Id} is a second default effect; {established!.Id} already is", "single-default");

    if (isDefault) {
      foreach (var (key, required) in _restrictive)
        if (Text(body, key) is var found && found != required)
          yield return Rule(document, key, $"the default effect must have {key} '{required}', found '{found}'", "default-is-conservative");
      if (!reads.Contains("unknown"))
        yield return Rule(document, "reads", "the default effect must read 'unknown'", "default-is-conservative");
      if (!writes.Contains("unknown"))
        yield return Rule(document, "writes", "the default effect must write 'unknown'", "default-is-conservative");
    }

    if (VolatileClasses.Contains(effectClass))
      foreach (var (key, required) in _restrictive)
        if (Text(body, key) is var found && found != required)
          yield return Rule(document, key, $"class '{effectClass}' requires {key} '{required}', found '{found}'", "volatile-is-strict");

    if (writes.Count > 0 && ordering == "free")
      yield return Rule(document, "ordering", $"ordering 'free' is not allowed for an effect that writes {string.Join(", ", writes)}", "write-implies-order");

    var first = family.First(other => Text(other.Body, "name") == name);
    if (first != document)
      yield return Rule(document, "name", $"effect '{name}' is already defined by {first.Id}", "unique-name");
  }

  /// <summary>The rule <c>layout</c>: <c>effects/&lt;name&gt;.yaml</c> with the ID <c>EFFECTS.&lt;name&gt;</c> and the body's name <c>&lt;name&gt;</c>.</summary>
  private static IEnumerable<Diagnostic> Layout(SpecDocument document, string name) {
    var parts = document.Path.Split('/');
    var file = parts[^1];
    var stem = file[..^".yaml".Length];
    if (parts.Length > 2)
      yield return SpecDiagnostics.Rule(document, "/id", $"file '{document.Path}' is not directly in the family folder 'effects'", "layout");
    if (document.Id != Prefix + stem)
      yield return SpecDiagnostics.Rule(document, "/id", $"id '{document.Id}' does not match file name '{file}'", "layout");
    if (name != stem)
      yield return Rule(document, "name", $"name '{name}' does not match file name '{file}'", "layout");
  }

  private static Diagnostic Rule(SpecDocument document, string key, string reason, string rule) => SpecDiagnostics.Rule(document, "/spec/" + key, reason, rule);

  /// <summary>A string property of a body, or <see langword="null"/> when the body has none.</summary>
  private static string? Text(JsonNode? body, string key) => body is JsonObject value && value[key] is JsonValue text && text.TryGetValue<string>(out var result) ? result : null;

  /// <summary>Whether a body declares itself the default effect.</summary>
  private static bool IsDefault(JsonNode? body) => body is JsonObject value && value["defaultForUnknown"] is JsonValue flag && flag.TryGetValue<bool>(out var result) && result;

  /// <summary>The region kinds of a list property of a body that conforms to the schema.</summary>
  private static List<string> Regions(JsonNode body, string key) => [.. ((JsonArray)body[key]!).Select(region => (string)region!)];

}
