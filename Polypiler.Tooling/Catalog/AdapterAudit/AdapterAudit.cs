using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Polypiler.Core;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tooling.Catalog.AdapterAudit;

/// <summary>
/// <c>spec/adapters/&lt;adapter&gt;/licence-audit.yaml</c>: the licence facts of every upstream file an
/// adapter may compile, link or load, at one pinned commit of the corpus, and of the components it
/// needs that are not files of that commit. Its format is <c>spec/schemas/adapter-audit.schema.json</c>,
/// which travels with the tool; the rules a schema cannot state are <see cref="AdapterAuditValidator"/>'s.
/// </summary>
public sealed class AdapterAudit {

  /// <summary>The name under which the tooling assembly carries <c>spec/schemas/adapter-audit.schema.json</c>.</summary>
  public const string SchemaResource = "adapter-audit.schema.json";

  private static readonly Lazy<JsonSchema> _schema = new(() => {
    using var stream = typeof(AdapterAudit).Assembly.GetManifestResourceStream(SchemaResource)!;
    using var reader = new StreamReader(stream);
    return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
  });

  private static readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.Hierarchical };

  private AdapterAudit(string file, string repository, string commit, AuditClearance clearance, IReadOnlyList<AuditRecord> records, IReadOnlyList<AuditExternal> external) {
    this.File = file;
    this.Repository = repository;
    this.Commit = commit;
    this.Clearance = clearance;
    this.Records = records;
    this.External = external;
  }

  /// <summary>The audit file as it was named.</summary>
  public string File { get; }

  /// <summary>The audited repository as <c>owner/repo</c>.</summary>
  public string Repository { get; }

  /// <summary>The pinned commit the records describe.</summary>
  public string Commit { get; }

  /// <summary>The owner's clearance of the usable entries.</summary>
  public AuditClearance Clearance { get; }

  /// <summary>The records, in file order.</summary>
  public IReadOnlyList<AuditRecord> Records { get; }

  /// <summary>The components that are not files of the commit, in file order.</summary>
  public IReadOnlyList<AuditExternal> External { get; }

  /// <summary>The record of a path, or <see langword="null"/>; where two share one, the first.</summary>
  public AuditRecord? Record(string path) => this.Records.FirstOrDefault(record => record.Path == path);

  /// <summary>Reads and checks an audit file against its schema.</summary>
  /// <exception cref="AdapterAuditException">The file cannot be read, is not YAML or breaks the schema (<c>PLY0950</c>).</exception>
  public static AdapterAudit Load(string path) {
    string text;
    try {
      text = System.IO.File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new AdapterAuditException([AdapterAuditDiagnostics.Rule($"cannot read '{path}': {exception.Message}")]);
    }

    return Parse(text, path);
  }

  /// <summary>Checks the text of an audit against its schema.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="file">The name diagnostics give the file.</param>
  /// <exception cref="AdapterAuditException">The text is not YAML or breaks the schema (<c>PLY0950</c>), with every violation found.</exception>
  public static AdapterAudit Parse(string text, string file) {
    JsonNode? root;
    IReadOnlyDictionary<string, (int Line, int Column)> positions;
    try {
      root = YamlToJson.Read(text, out positions);
    } catch (YamlToJsonException exception) {
      throw new AdapterAuditException([AdapterAuditDiagnostics.Rule(exception.Message, file, exception.Line, exception.Column)]);
    }

    var problems = Errors(_schema.Value.Evaluate(JsonSerializer.SerializeToElement(root), _evaluation))
      .Distinct()
      .Select(error => {
        var (line, column) = positions.TryGetValue(error.Pointer, out var position) ? position : (0, 0);
        return AdapterAuditDiagnostics.Rule($"{(error.Pointer.Length == 0 ? "/" : error.Pointer)}: {error.Message}", file, line, column);
      })
      .ToList();
    if (problems.Count > 0)
      throw new AdapterAuditException(problems);

    var document = root!.AsObject();
    var clearance = document["clearance"]!.AsObject();
    return new(
      file,
      Text(document["repository"]),
      Text(document["commit"]),
      new(Text(clearance["status"]), clearance["adr"]?.GetValue<string>(), clearance["date"]?.GetValue<string>()),
      [.. document["records"]!.AsArray().Select(node => Record(node!.AsObject()))],
      [.. document["external"]!.AsArray().Select(node => Component(node!.AsObject()))]);
  }

  private static AuditRecord Record(JsonObject node) => new(
    Text(node["path"]),
    Text(node["kind"]) == "model",
    Text(node["sha256"]),
    node["bytes"]!.GetValue<long>(),
    Text(node["licence"]),
    node["origin"] is JsonObject origin ? new(Text(origin["project"]), Text(origin["url"]), Text(origin["licence"]), Text(origin["notice"])) : null,
    Texts(node["obligations"]),
    AuditDecisions.Parse(Text(node["decision"])),
    Texts(node["usedBy"]),
    Text(node["reviewer"]),
    Text(node["reviewedAt"]),
    node["reason"]?.GetValue<string>());

  private static AuditExternal Component(JsonObject node) => new(
    Text(node["name"]),
    Text(node["kind"]),
    Text(node["licence"]),
    Text(node["url"]),
    Text(node["notice"]),
    Texts(node["obligations"]),
    AuditDecisions.Parse(Text(node["decision"])),
    Text(node["reason"]),
    Text(node["reviewer"]),
    Text(node["reviewedAt"]));

  private static string Text(JsonNode? node) => node!.GetValue<string>();

  private static string[] Texts(JsonNode? node) => [.. node!.AsArray().Select(Text)];

  /// <summary>
  /// The errors of a node of the hierarchical output and of the nodes below it, as JSON pointer and
  /// message. The condition of an <c>if</c> is a test, not a requirement: what fails inside it is no error.
  /// </summary>
  private static IEnumerable<(string Pointer, string Message)> Errors(EvaluationResults node) {
    if (node.IsValid || node.EvaluationPath.ToString().EndsWith("/if", StringComparison.Ordinal))
      yield break;

    foreach (var error in node.Errors ?? new Dictionary<string, string>())
      yield return (node.InstanceLocation.ToString(), node.EvaluationPath.ToString().EndsWith("/additionalProperties", StringComparison.Ordinal)
        ? $"property '{Unescape(node.InstanceLocation.ToString())}' is not allowed"
        : error.Value);

    foreach (var child in node.Details ?? [])
      foreach (var error in Errors(child))
        yield return error;
  }

  /// <summary>The last segment of a JSON pointer, unescaped.</summary>
  private static string Unescape(string pointer)
    => pointer[(pointer.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

}
