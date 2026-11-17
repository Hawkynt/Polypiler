using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Polypiler.Core.Verification.Gates;

namespace Polypiler.Core.Verification.Capabilities;

/// <summary>
/// The capability report of PRD §23.6: one <see cref="CapabilityPair"/> per combination of a source
/// profile and a target profile, same-language pairs included, each at the state
/// <see cref="GateStore.StateOf"/> gives it. Nothing but a valid gate record raises a pair above
/// <see cref="GateState.Planned"/>. The report is written as <c>docs/capabilities.json</c>
/// (<c>spec/schemas/capabilities.schema.json</c>) and <c>docs/capabilities.md</c>; the same inputs give
/// the same bytes.
/// </summary>
public sealed partial class CapabilityReport {

  /// <summary>The version the JSON file carries.</summary>
  public const int SchemaVersion = 1;

  /// <summary>The name of the JSON file.</summary>
  public const string JsonFile = "capabilities.json";

  /// <summary>The name of the Markdown file.</summary>
  public const string MarkdownFile = "capabilities.md";

  /// <summary>The commit a report names when nothing says which it is: forty zeros.</summary>
  public static readonly string UnknownCommit = new('0', 40);

  private const string Resource = "Polypiler.Core.Verification.Capabilities.capabilities.schema.json";

  private static readonly JsonDocumentOptions _strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, AllowDuplicateProperties = false };

  private static readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List };

  private static readonly Lazy<JsonSchema> _schema = new(() => {
    using var reader = new StreamReader(typeof(CapabilityReport).Assembly.GetManifestResourceStream(Resource)!);
    return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
  });

  private readonly Dictionary<(string, string, string, string), CapabilityPair> _byKey;

  /// <summary>A report of pairs, which it sorts by <c>from</c>, <c>sourceProfile</c>, <c>to</c> and <c>targetProfile</c>, ordinally.</summary>
  /// <param name="commit">The commit the report was generated at: 40 lower-case hexadecimal digits.</param>
  /// <param name="gateRecords">The number of valid gate records the report was computed from.</param>
  /// <param name="pairs">The pairs, each combination at most once.</param>
  /// <exception cref="ArgumentException">The commit is no 40-digit hash, the count is negative, or a pair is given twice.</exception>
  public CapabilityReport(string commit, int gateRecords, IEnumerable<CapabilityPair> pairs) {
    ArgumentNullException.ThrowIfNull(commit);
    ArgumentNullException.ThrowIfNull(pairs);
    if (!Commit40().IsMatch(commit))
      throw new ArgumentException($"'{commit}' is no commit of 40 lower-case hexadecimal digits", nameof(commit));
    ArgumentOutOfRangeException.ThrowIfNegative(gateRecords);

    this.Commit = commit;
    this.GateRecords = gateRecords;
    this.Pairs = [.. pairs
      .OrderBy(pair => pair.From, StringComparer.Ordinal)
      .ThenBy(pair => pair.SourceProfile, StringComparer.Ordinal)
      .ThenBy(pair => pair.To, StringComparer.Ordinal)
      .ThenBy(pair => pair.TargetProfile, StringComparer.Ordinal)];
    this._byKey = [];
    foreach (var pair in this.Pairs)
      if (!this._byKey.TryAdd(pair.Key, pair))
        throw new ArgumentException($"the pair {pair.Name} is given twice", nameof(pairs));
  }

  /// <summary>The commit the report was generated at.</summary>
  public string Commit { get; }

  /// <summary>The number of valid gate records the report was computed from.</summary>
  public int GateRecords { get; }

  /// <summary>The pairs, sorted.</summary>
  public IReadOnlyList<CapabilityPair> Pairs { get; }

  /// <summary>
  /// Computes the report: every source profile with every target profile, each pair at the state of
  /// its valid gate record and with the source profile's limitations. A valid record whose pair has
  /// no source or target profile is no row but a <c>PLY0061</c>.
  /// </summary>
  /// <param name="sources">The source profiles of <c>spec/languages</c>.</param>
  /// <param name="targets">The target profiles of <c>spec/targets</c>.</param>
  /// <param name="gates">The valid gate records.</param>
  /// <param name="gateDirectory">The gate directory as the report names it, for example <c>artifacts/gates</c>.</param>
  /// <param name="commit">The commit the report is generated at.</param>
  /// <param name="sink">Receives a <c>PLY0061</c> for every record of a pair without spec documents.</param>
  /// <returns>The report.</returns>
  /// <exception cref="ArgumentException">The commit is no 40-digit hash, or the gate directory is empty.</exception>
  public static CapabilityReport Build(IEnumerable<CapabilitySource> sources, IEnumerable<CapabilityTarget> targets, GateStore gates, string gateDirectory, string commit, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(targets);
    ArgumentNullException.ThrowIfNull(gates);
    ArgumentException.ThrowIfNullOrEmpty(gateDirectory);
    ArgumentNullException.ThrowIfNull(sink);
    var directory = gateDirectory.Replace('\\', '/').TrimEnd('/');
    var targetList = targets.DistinctBy(target => (target.Language, target.Profile)).ToList();
    var pairs = new List<CapabilityPair>();
    var used = new HashSet<PairKey>();
    foreach (var source in sources.DistinctBy(source => (source.Language, source.Profile)))
      foreach (var target in targetList) {
        var key = KeyOf(source.Language, source.Profile, target.Language, target.Profile);
        var state = key is null ? GateState.Planned : gates.StateOf(key);
        if (key is not null && gates.Pairs.Contains(key))
          used.Add(key);

        pairs.Add(state == GateState.Planned
          ? new(source.Language, source.Profile, target.Language, target.Profile, state, 0, null, source.Limitations)
          : new(source.Language, source.Profile, target.Language, target.Profile, state, gates.FixturesOf(key!).Count, directory + "/" + key!.FileName, source.Limitations));
      }

    foreach (var orphan in gates.Pairs.Where(pair => !used.Contains(pair)))
      sink.Report(Diagnostic.Create(CapabilityDiagnostics.Unsupported, null, $"gate record '{directory}/{orphan.FileName}' names a pair with no language or target document"));

    return new(commit, used.Count, pairs);
  }

  /// <summary>The state of a pair; <see cref="GateState.Planned"/> for a pair the report does not hold.</summary>
  /// <param name="from">The source language.</param>
  /// <param name="sourceProfile">The source profile.</param>
  /// <param name="to">The target language.</param>
  /// <param name="targetProfile">The target profile.</param>
  /// <returns>The state.</returns>
  public GateState StateOf(string from, string sourceProfile, string to, string targetProfile)
    => this.Find(from, sourceProfile, to, targetProfile)?.State ?? GateState.Planned;

  /// <summary>A pair of the report.</summary>
  /// <param name="from">The source language.</param>
  /// <param name="sourceProfile">The source profile.</param>
  /// <param name="to">The target language.</param>
  /// <param name="targetProfile">The target profile.</param>
  /// <returns>The pair, or <see langword="null"/> when the report does not hold it.</returns>
  public CapabilityPair? Find(string from, string sourceProfile, string to, string targetProfile)
    => this._byKey.GetValueOrDefault((from, sourceProfile, to, targetProfile));

  /// <summary>Writes <c>docs/capabilities.json</c>: two-space indentation, LF, one final line break, no timestamp.</summary>
  /// <param name="writer">Receives the text.</param>
  public void WriteJson(TextWriter writer) {
    ArgumentNullException.ThrowIfNull(writer);
    writer.Write(CapabilityWriter.Json(this));
  }

  /// <summary>Writes <c>docs/capabilities.md</c>: one table per source language and the legend of the states.</summary>
  /// <param name="writer">Receives the text.</param>
  public void WriteMarkdown(TextWriter writer) {
    ArgumentNullException.ThrowIfNull(writer);
    writer.Write(CapabilityWriter.Markdown(this));
  }

  /// <summary>Reads a report as <see cref="WriteJson"/> writes it.</summary>
  /// <param name="stream">The UTF-8 bytes of the file.</param>
  /// <returns>The report.</returns>
  /// <exception cref="InvalidDataException">The bytes are no JSON or break the schema; the message names every problem.</exception>
  public static CapabilityReport Load(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var reader = new StreamReader(stream, new UTF8Encoding(false));
    return Parse(reader.ReadToEnd(), out var errors) ?? throw new InvalidDataException($"no capability report: {string.Join("; ", errors)}");
  }

  /// <summary>Reads a report's text: JSON, then the schema, then the rules the schema cannot state.</summary>
  /// <param name="text">The text.</param>
  /// <param name="errors">What is wrong, each as <c>&lt;JSON pointer&gt;: &lt;reason&gt;</c> or <c>not JSON: &lt;reason&gt;</c>; empty when the result is not <see langword="null"/>.</param>
  /// <returns>The report, or <see langword="null"/>.</returns>
  public static CapabilityReport? Parse(string text, out IReadOnlyList<string> errors) {
    ArgumentNullException.ThrowIfNull(text);
    JsonNode root;
    try {
      using var document = JsonDocument.Parse(text, _strict);
      var results = _schema.Value.Evaluate(document.RootElement, _evaluation);
      if (!results.IsValid) {
        errors = [.. results.Details!
          .Where(node => !node.IsValid && node.Errors is not null)
          .SelectMany(node => node.Errors!.Select(error => $"{Pointer(node.InstanceLocation.ToString())}: {error.Value}"))
          .Distinct()];
        return null;
      }

      root = JsonNode.Parse(text, documentOptions: _strict)!;
    } catch (JsonException exception) {
      errors = [$"not JSON: {exception.Message}"];
      return null;
    }

    var pairs = root["pairs"]!.AsArray().Select(node => {
      GateStates.TryParse(Text(node!["state"]), out var state);
      return new CapabilityPair(Text(node["from"]), Text(node["sourceProfile"]), Text(node["to"]), Text(node["targetProfile"]), state,
        node["fixtures"]!.GetValue<int>(), node["evidence"]?.GetValue<string>(),
        [.. node["limitations"]!.AsArray().Select(limitation => new CapabilityLimitation(Text(limitation!["construct"]), Text(limitation["diagnostic"])))]);
    }).ToList();

    var seen = new HashSet<(string, string, string, string)>();
    var duplicates = pairs.Select((pair, index) => (pair, index)).Where(entry => !seen.Add(entry.pair.Key))
      .Select(entry => string.Create(CultureInfo.InvariantCulture, $"/pairs/{entry.index}: the pair {entry.pair.Name} is listed twice")).ToList();
    errors = duplicates;
    if (duplicates.Count > 0)
      return null;

    var generated = root["generatedFrom"]!;
    return new(Text(generated["commit"]), generated["gateRecords"]!.GetValue<int>(), pairs);
  }

  /// <summary>The pair key of four parts, or <see langword="null"/> when one is no part of a gate record's name and no record can exist.</summary>
  private static PairKey? KeyOf(string from, string sourceProfile, string to, string targetProfile)
    => PairKey.IsPart(from) && PairKey.IsPart(sourceProfile) && PairKey.IsPart(to) && PairKey.IsPart(targetProfile)
      ? new(from, to, sourceProfile, targetProfile)
      : null;

  private static string Text(JsonNode? node) => node!.GetValue<string>();

  private static string Pointer(string location) => location.Length == 0 ? "/" : location;

  [GeneratedRegex("^[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)]
  private static partial Regex Commit40();

}
