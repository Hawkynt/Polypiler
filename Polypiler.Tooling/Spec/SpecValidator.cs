using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec;

/// <summary>What one run of <see cref="SpecValidator"/> found.</summary>
/// <param name="Documents">The number of documents validated: the files of the selected families, readable or not.</param>
/// <param name="Diagnostics">Every problem, sorted by path, line, column and code; problems without a file come first.</param>
public sealed record SpecValidation(int Documents, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// The shared validator of every spec family. Per document it runs, in this order: YAML well-formed,
/// family known, metadata schema, metadata rules, family schema, family rules. A document that fails a
/// step goes no further, so one defect yields one diagnostic and never a cascade of follow-ups.
/// </summary>
public sealed class SpecValidator {

  /// <summary>The name under which the tooling assembly carries <c>spec/schemas/meta.schema.json</c>.</summary>
  public const string MetaSchemaResource = "meta.schema.json";

  private static readonly Lazy<JsonSchema> _metaSchema = new(() => {
    using var stream = typeof(SpecValidator).Assembly.GetManifestResourceStream(MetaSchemaResource)!;
    using var reader = new StreamReader(stream);
    return Build(reader.ReadToEnd());
  });

  private static readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List };

  private readonly Dictionary<string, ISpecFamily> _families = new(StringComparer.Ordinal);

  /// <summary>Creates a validator that knows the given families.</summary>
  /// <param name="families">The families; <see cref="SpecFamilies.All"/> in the tool.</param>
  /// <exception cref="ArgumentException">Two families claim the same folder.</exception>
  public SpecValidator(IEnumerable<ISpecFamily> families) {
    ArgumentNullException.ThrowIfNull(families);
    foreach (var family in families)
      if (!this._families.TryAdd(family.Folder, family))
        throw new ArgumentException($"two families claim the folder '{family.Folder}'", nameof(families));
  }

  /// <summary>Validates the documents under a spec root.</summary>
  /// <param name="root">The spec root; the repository root is found as <see cref="SpecTree.Load(string)"/> finds it.</param>
  /// <param name="family">The only family to validate, or <see langword="null"/> for all; the whole tree is read either way, so references into other families resolve.</param>
  /// <returns>The problems, sorted; a root that does not exist gives one <c>PLY0047</c>.</returns>
  public IReadOnlyList<Diagnostic> Validate(string root, string? family = null) {
    ArgumentNullException.ThrowIfNull(root);
    return Directory.Exists(root)
      ? this.Run(SpecTree.Load(root), family).Diagnostics
      : [Diagnostic.Create(SpecDiagnostics.Unreadable, null, $"spec root '{root}' does not exist")];
  }

  /// <summary>Validates the documents of a tree.</summary>
  /// <param name="tree">The tree.</param>
  /// <param name="family">The only family to validate, or <see langword="null"/> for all.</param>
  /// <returns>The number of documents validated and the problems, sorted.</returns>
  public SpecValidation Run(SpecTree tree, string? family = null) {
    ArgumentNullException.ThrowIfNull(tree);
    if (family is not null && !this._families.ContainsKey(family))
      return new(0, [Diagnostic.Create(SpecDiagnostics.Unreadable, null, $"unknown spec family '{family}'")]);

    var unreadable = tree.Unreadable.Where(problem => family is null || problem.File!.StartsWith(family + "/", StringComparison.Ordinal)).ToList();
    var diagnostics = new List<Diagnostic>(unreadable);
    var documents = tree.Documents.Where(document => family is null || document.Family == family).ToList();
    var run = new RunState(tree, diagnostics);
    foreach (var document in documents)
      diagnostics.AddRange(this.Check(document, run));

    return new(documents.Count + unreadable.Count, [.. diagnostics
        .OrderBy(problem => problem.File ?? "", StringComparer.Ordinal)
        .ThenBy(problem => problem.Line)
        .ThenBy(problem => problem.Column)
        .ThenBy(problem => problem.Code, StringComparer.Ordinal)
        .ThenBy(problem => problem.Message, StringComparer.Ordinal)]);
  }

  private IEnumerable<Diagnostic> Check(SpecDocument document, RunState run) {
    if (!this._families.TryGetValue(document.Family, out var family))
      return [SpecDiagnostics.At(SpecDiagnostics.Unreadable, document, "", $"unknown spec family '{document.Family}'")];

    var problems = SchemaProblems(_metaSchema.Value, document, document.Root, "");
    if (problems.Count > 0)
      return problems;

    problems = MetadataProblems(document, run.Tree);
    if (problems.Count > 0)
      return problems;

    if (run.Schema(family) is not { } schema)
      return [];

    problems = SchemaProblems(schema, document, document.Body, "/spec");
    return problems.Count > 0 ? problems : family.Check(document, run.Tree);
  }

  /// <summary>
  /// Every error of a schema on a value, as <c>PLY0040: &lt;pointer&gt;: &lt;message&gt;</c> at the value.
  /// The condition of an <c>if</c> is a test, not a requirement: what fails inside it is no error.
  /// </summary>
  private static List<Diagnostic> SchemaProblems(JsonSchema schema, SpecDocument document, JsonNode? value, string prefix) {
    var results = schema.Evaluate(JsonSerializer.SerializeToElement(value), _evaluation);
    if (results.IsValid)
      return [];

    return [.. results.Details!
      .Where(detail => !$"{detail.EvaluationPath}/".Contains("/if/", StringComparison.Ordinal))
      .SelectMany(detail => (detail.Errors ?? new Dictionary<string, string>()).Select(error => (
        Pointer: prefix + detail.InstanceLocation,
        Message: detail.EvaluationPath.ToString().EndsWith("/additionalProperties", StringComparison.Ordinal)
          ? $"property '{Unescape(detail.InstanceLocation.ToString())}' is not allowed"
          : error.Value)))
      .Distinct()
      .Select(error => SpecDiagnostics.At(SpecDiagnostics.SchemaViolation, document, error.Pointer, error.Pointer.Length == 0 ? "/" : error.Pointer, error.Message))];
  }

  /// <summary>The last segment of a JSON pointer, unescaped.</summary>
  private static string Unescape(string pointer)
    => pointer[(pointer.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

  /// <summary>The rules on the metadata block that its schema cannot state.</summary>
  private static List<Diagnostic> MetadataProblems(SpecDocument document, SpecTree tree) {
    var meta = document.Meta!;
    var problems = new List<Diagnostic>();
    void Report(DiagnosticDescriptor descriptor, string pointer, string message) => problems.Add(SpecDiagnostics.At(descriptor, document, pointer, message));

    var prefix = document.Family.ToUpperInvariant();
    if (!meta.Id.StartsWith(prefix + ".", StringComparison.Ordinal))
      Report(SpecDiagnostics.MetadataRule, "/id", $"id '{meta.Id}' does not start with family prefix '{prefix}'");

    var normative = meta.Status == "normative";
    if (normative && meta.Tests.Count == 0)
      Report(SpecDiagnostics.MetadataRule, "/tests", "normative document needs at least one test");
    if (normative && meta.ReviewedRevision < meta.Version)
      Report(SpecDiagnostics.MetadataRule, "/reviewedRevision", $"normative document must be reviewed at its current version (reviewedRevision {meta.ReviewedRevision}, version {meta.Version})");

    var deprecated = meta.Status == "deprecated";
    if (deprecated && meta.DeprecatedIn is null)
      Report(SpecDiagnostics.MetadataRule, "/status", "deprecatedIn is required when status is deprecated");
    if (!deprecated && meta.DeprecatedIn is not null)
      Report(SpecDiagnostics.MetadataRule, "/deprecatedIn", "deprecatedIn is only allowed when status is deprecated");
    if (meta.Version == 1 && meta.Compatibility != "none")
      Report(SpecDiagnostics.MetadataRule, "/compatibility", "compatibility must be none at version 1");

    if (meta.DeprecatedIn is { } deprecatedIn && string.CompareOrdinal(deprecatedIn, meta.IntroducedIn) < 0)
      Report(SpecDiagnostics.Lifecycle, "/deprecatedIn", $"deprecatedIn {deprecatedIn} is earlier than introducedIn {meta.IntroducedIn}");
    if (meta.ReviewedRevision > meta.Version)
      Report(SpecDiagnostics.Lifecycle, "/reviewedRevision", $"reviewedRevision {meta.ReviewedRevision} exceeds version {meta.Version}");

    for (var index = 0; index < meta.Tests.Count; ++index) {
      var test = meta.Tests[index];
      var full = Path.GetFullPath(Path.Combine(tree.RepositoryRoot, test));
      var relative = Path.GetRelativePath(tree.RepositoryRoot, full);
      if ((relative + Path.DirectorySeparatorChar).StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        Report(SpecDiagnostics.BrokenReference, $"/tests/{index}", $"test path '{test}' is outside the repository");
      else if (!Path.Exists(full))
        Report(SpecDiagnostics.BrokenReference, $"/tests/{index}", $"test path '{test}' does not exist");
    }

    if (tree.ById[meta.Id] is var first && first != document)
      problems.Add(SpecDiagnostics.At(SpecDiagnostics.DuplicateId, document, "/id", meta.Id, first.Path));
    return problems;
  }

  private static JsonSchema Build(string text) => JsonSchema.FromText(text, new BuildOptions { SchemaRegistry = new SchemaRegistry() });

  /// <summary>The state of one run: the tree, and each family's schema read once.</summary>
  private sealed class RunState(SpecTree tree, List<Diagnostic> diagnostics) {

    private readonly Dictionary<string, JsonSchema?> _schemas = new(StringComparer.Ordinal);

    public SpecTree Tree => tree;

    /// <summary>The family's body schema; the first time it cannot be read, one <c>PLY0047</c> naming it, and <see langword="null"/> for every document of the family.</summary>
    public JsonSchema? Schema(ISpecFamily family) {
      if (this._schemas.TryGetValue(family.Folder, out var known))
        return known;

      JsonSchema? schema = null;
      try {
        schema = Build(File.ReadAllText(Path.Combine(tree.RepositoryRoot, family.SchemaPath)));
      } catch (Exception exception) when (exception is IOException or JsonException or JsonSchemaException) {
        diagnostics.Add(Diagnostic.Create(SpecDiagnostics.Unreadable, null, $"cannot read schema '{family.SchemaPath}' of family '{family.Folder}': {exception.Message}"));
      }

      this._schemas[family.Folder] = schema;
      return schema;
    }

  }

}
