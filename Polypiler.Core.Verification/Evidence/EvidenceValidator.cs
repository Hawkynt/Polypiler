using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Polypiler.Core.Verification.Evidence;

/// <summary>
/// Checks an evidence bundle: <c>evidence.json</c> must pass <c>spec/schemas/evidence.schema.json</c>
/// and hold every item its scenario's tags require, and a passed <c>@translation</c> scenario must not
/// record a failed vector. Every scenario needs its tool versions and CI link (its identity and outcome
/// the schema requires); one tagged <c>@translation</c> also a source, a generated file, the manifest,
/// a process and the comparison (reference and target results, diff, exactness policy); one tagged
/// <c>@optimization</c> the opt diagnostics. A file recorded with no hash, or recorded but absent from
/// the bundle, is missing whatever the tags. Each finding is one <c>PLY0060</c>.
/// </summary>
public static class EvidenceValidator {

  /// <summary>The tag of a scenario that translates code.</summary>
  public const string TranslationTag = "translation";

  /// <summary>The tag of a scenario that optimizes code.</summary>
  public const string OptimizationTag = "optimization";

  private const string SchemaResource = "Polypiler.Core.Verification.Evidence.evidence.schema.json";

  private static readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List };

  private static readonly JsonDocumentOptions _strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, AllowDuplicateProperties = false };

  private static readonly Lazy<JsonSchema> _schema = new(() => {
    using var reader = new StreamReader(typeof(EvidenceValidator).Assembly.GetManifestResourceStream(SchemaResource)!);
    return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
  });

  /// <summary>
  /// The items PRD §6.6 requires a scenario to attach, by the name the diagnostics give them, with the
  /// property of <c>evidence.json</c> that records each.
  /// </summary>
  public static IReadOnlyList<(string Item, string Property)> PrdItems { get; } = [
    ("source hash", "sources"),
    ("target code hash", "targetCode"),
    ("generated manifest", "manifest"),
    ("tool versions", "toolVersions"),
    ("fixture values", "fixtureValues"),
    ("stdout/stderr/exit code", "processes"),
    ("reference result", "reference"),
    ("target result", "target"),
    ("diff", "diff"),
    ("exactness policy", "exactness"),
    ("opt diagnostics", "optDiagnostics"),
    ("CI link", "ciLink")
  ];

  /// <summary>Checks a bundle.</summary>
  /// <param name="bundleDirectory">The bundle directory; the diagnostics name it as given.</param>
  /// <param name="sink">Receives one <c>PLY0060</c> per finding.</param>
  /// <returns>Whether the bundle is valid.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static bool Validate(string bundleDirectory, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(bundleDirectory);
    ArgumentNullException.ThrowIfNull(sink);
    var file = Path.Combine(bundleDirectory, EvidenceWriter.FileName);
    if (!File.Exists(file)) {
      Report(sink, bundleDirectory, $"is incomplete: missing {EvidenceWriter.FileName}");
      return false;
    }

    JsonObject root;
    try {
      var text = File.ReadAllText(file);
      using var document = JsonDocument.Parse(text, _strict);
      var results = _schema.Value.Evaluate(document.RootElement, _evaluation);
      if (!results.IsValid) {
        foreach (var (pointer, reason) in Errors(results))
          Report(sink, bundleDirectory, $"is invalid: {pointer}: {reason}");
        return false;
      }

      root = JsonNode.Parse(text)!.AsObject();
    } catch (JsonException exception) {
      Report(sink, bundleDirectory, $"is invalid: /: not JSON: {exception.Message}");
      return false;
    }

    var valid = true;
    var missing = Missing(bundleDirectory, root);
    if (missing.Count > 0) {
      Report(sink, bundleDirectory, $"is incomplete: missing {string.Join(", ", missing)}");
      valid = false;
    }

    var failed = FailedVectors(bundleDirectory, root);
    if (Tagged(root, TranslationTag) && root["outcome"]!.GetValue<string>() == "passed" && failed > 0) {
      Report(sink, bundleDirectory, $"contradicts its outcome: {failed.ToString(CultureInfo.InvariantCulture)} vector(s) failed");
      valid = false;
    }

    return valid;
  }

  /// <summary>The items the bundle lacks, in the order of <see cref="PrdItems"/>.</summary>
  private static List<string> Missing(string directory, JsonObject root) {
    var translation = Tagged(root, TranslationTag);
    var lacking = new HashSet<string>(StringComparer.Ordinal);
    if (Copies(directory, root["sources"]!.AsArray(), translation))
      lacking.Add("source hash");
    if (Copies(directory, root["targetCode"]!.AsArray(), translation))
      lacking.Add("target code hash");
    if (root["manifest"] is { } manifest ? !Present(directory, manifest) || manifest["state"] is null : translation)
      lacking.Add("generated manifest");
    if (root["toolVersions"]!.AsObject().Count == 0)
      lacking.Add("tool versions");
    var processes = root["processes"]!.AsArray();
    if (translation && processes.Count == 0 || processes.Any(process => !Exists(directory, process!["stdout"]) || !Exists(directory, process!["stderr"])))
      lacking.Add("stdout/stderr/exit code");
    if (translation && Empty(root["reference"]))
      lacking.Add("reference result");
    if (translation && Empty(root["target"]))
      lacking.Add("target result");
    if (root["diff"] is { } diff ? !Exists(directory, diff) : translation)
      lacking.Add("diff");
    if (translation && root["exactness"] is null)
      lacking.Add("exactness policy");
    if (root["optDiagnostics"] is { } opt ? !Exists(directory, opt) : Tagged(root, OptimizationTag))
      lacking.Add("opt diagnostics");
    if (string.IsNullOrEmpty(root["ciLink"]?.GetValue<string>()))
      lacking.Add("CI link");

    var names = PrdItems.Select(item => item.Item).Where(lacking.Contains).ToList();
    names.AddRange(root["files"]!.AsArray().Where(entry => !Present(directory, entry!)).Select(entry => $"file '{entry!["path"]!.GetValue<string>()}'"));
    return names;
  }

  /// <summary>Whether a list of copied files lacks one: it is empty where one is required, or an entry has no copy.</summary>
  private static bool Copies(string directory, JsonArray copies, bool required)
    => required && copies.Count == 0 || copies.Any(entry => !Present(directory, entry!));

  /// <summary>Whether a copied file has its hash and its copy in the bundle.</summary>
  private static bool Present(string directory, JsonNode entry) => entry["sha256"] is not null && Exists(directory, entry["path"]);

  private static bool Exists(string directory, JsonNode? path) => File.Exists(Path.Combine(directory, path!.GetValue<string>()));

  private static bool Empty(JsonNode? results) => results is null || results.AsObject().Count == 0;

  private static bool Tagged(JsonObject root, string tag) => root["tags"]!.AsArray().Any(item => item!.GetValue<string>() == tag);

  /// <summary>The number of lines of the diff, one per failed vector; 0 without a diff.</summary>
  private static int FailedVectors(string directory, JsonObject root)
    => root["diff"] is { } diff && Exists(directory, diff)
      ? File.ReadAllLines(Path.Combine(directory, diff.GetValue<string>())).Count(line => line.Length > 0)
      : 0;

  /// <summary>Every schema error as a JSON pointer and a reason, each once; the schema has no <c>oneOf</c> or <c>if</c> whose failed branches would be errors.</summary>
  private static IEnumerable<(string Pointer, string Reason)> Errors(EvaluationResults results)
    => results.Details!
      .Where(node => !node.IsValid && node.Errors is not null)
      .SelectMany(node => node.Errors!.Select(error => (Pointer: node.InstanceLocation.ToString() is { Length: > 0 } location ? location : "/", Reason: error.Value)))
      .Distinct();

  private static void Report(IDiagnosticSink sink, string directory, string reason)
    => sink.Report(EvidenceDiagnostics.Invalid.At(string.Format(CultureInfo.InvariantCulture, EvidenceDiagnostics.Invalid.MessageFormat, directory, reason), Path.Combine(directory, EvidenceWriter.FileName)));

}
