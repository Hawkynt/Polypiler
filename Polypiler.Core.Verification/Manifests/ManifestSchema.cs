using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Polypiler.Core.Verification.Manifests;

/// <summary>
/// <c>spec/schemas/manifest.schema.json</c> with the sub-schemas of its sections. A section owned by
/// another area is checked against <c>manifest.&lt;section&gt;.schema.json</c> beside the main schema
/// once its owner has written that file; until then it is any non-empty list (<c>benchmarks</c>,
/// <c>assumptions</c>) or any non-empty object (the others). The schema is evaluated by JsonSchema.Net,
/// which knows nothing of the C# model, so it is an independent check of what the writer produces.
/// </summary>
public sealed class ManifestSchema {

  /// <summary>The file name of the main schema.</summary>
  public const string MainFile = "manifest.schema.json";

  private const string ResourcePrefix = "Polypiler.Core.Verification.Manifests.";

  private static readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List };

  private static readonly Lazy<ManifestSchema> _default = new(() => {
    var assembly = typeof(ManifestSchema).Assembly;
    string Text(string resource) {
      using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
      return reader.ReadToEnd();
    }

    var files = assembly.GetManifestResourceNames()
      .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
      .ToDictionary(name => name[ResourcePrefix.Length..], Text, StringComparer.Ordinal);
    return FromFiles(files);
  });

  private readonly JsonSchema _schema;

  private ManifestSchema(JsonSchema schema, IReadOnlyList<string> sections) {
    this._schema = schema;
    this.SectionsWithSchema = sections;
  }

  /// <summary>The sections other areas own, in the order the manifest writes them.</summary>
  public static IReadOnlyList<string> Sections { get; } = ["benchmarks", "assumptions", "resources", "objectModel", "profile", "budget"];

  /// <summary>The schema the assembly was built with: the main schema and every section sub-schema that existed then.</summary>
  public static ManifestSchema Default => _default.Value;

  /// <summary>The sections checked against a sub-schema of their own; the others are checked only for their type.</summary>
  public IReadOnlyList<string> SectionsWithSchema { get; }

  /// <summary>Reads the main schema and the section sub-schemas from a directory, such as <c>spec/schemas</c>.</summary>
  /// <param name="directory">The directory that holds <see cref="MainFile"/>.</param>
  /// <returns>The schema.</returns>
  /// <exception cref="IOException">A file cannot be read.</exception>
  /// <exception cref="JsonException">A file is not JSON.</exception>
  public static ManifestSchema Load(string directory) {
    ArgumentNullException.ThrowIfNull(directory);
    var files = new Dictionary<string, string>(StringComparer.Ordinal) { [MainFile] = File.ReadAllText(Path.Combine(directory, MainFile)) };
    foreach (var section in Sections) {
      var path = Path.Combine(directory, SectionFile(section));
      if (File.Exists(path))
        files[SectionFile(section)] = File.ReadAllText(path);
    }

    return FromFiles(files);
  }

  /// <summary>The file name of a section's sub-schema.</summary>
  /// <param name="section">The section, for example <c>assumptions</c>.</param>
  /// <returns><c>manifest.&lt;section&gt;.schema.json</c>.</returns>
  public static string SectionFile(string section) => $"manifest.{section}.schema.json";

  /// <summary>
  /// Builds the schema from file texts by name: each section definition <c>$defs/section.&lt;name&gt;</c>
  /// of the main schema becomes the conjunction of its type check and the section's own sub-schema.
  /// </summary>
  private static ManifestSchema FromFiles(IReadOnlyDictionary<string, string> files) {
    var main = JsonNode.Parse(files[MainFile])!.AsObject();
    var definitions = main["$defs"]!.AsObject();
    var owned = new List<string>();
    foreach (var section in Sections) {
      if (!files.TryGetValue(SectionFile(section), out var text))
        continue;

      var type = definitions[$"section.{section}"]!.DeepClone();
      definitions[$"section.{section}"] = new JsonObject { ["allOf"] = new JsonArray(type, JsonNode.Parse(text)) };
      owned.Add(section);
    }

    return new(JsonSchema.FromText(main.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() }), owned);
  }

  /// <summary>
  /// Every error of the schema on a manifest, as a JSON pointer and a reason. What fails inside the
  /// condition of an <c>if</c> is a test, not an error; a property that is not allowed is named.
  /// </summary>
  /// <param name="manifest">The manifest.</param>
  /// <returns>The errors, distinct, in the order of the evaluation; empty for a valid manifest.</returns>
  public IReadOnlyList<(string Pointer, string Reason)> Evaluate(JsonElement manifest) {
    var results = this._schema.Evaluate(manifest, _evaluation);
    if (results.IsValid)
      return [];

    // The list output holds every node, the root included, in Details.
    return [.. results.Details!
      .Where(node => !node.IsValid && node.Errors is not null && !node.EvaluationPath.ToString().Split('/').Contains("if"))
      .SelectMany(node => node.Errors!.Select(error => (Pointer: Pointer(node.InstanceLocation.ToString()), Reason: Reason(node, error.Value))))
      .Distinct()];
  }

  private static string Pointer(string location) => location.Length == 0 ? "/" : location;

  /// <summary>The reason of an error; for a property the schema forbids, which the false schema reports, the property's name.</summary>
  private static string Reason(EvaluationResults node, string message) {
    var path = node.EvaluationPath.ToString();
    if (!path.EndsWith("/additionalProperties", StringComparison.Ordinal) && !path.EndsWith("/worstVector", StringComparison.Ordinal))
      return message;

    var location = node.InstanceLocation.ToString();
    var name = location[(location.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    return $"property '{name}' is not allowed";
  }

}
