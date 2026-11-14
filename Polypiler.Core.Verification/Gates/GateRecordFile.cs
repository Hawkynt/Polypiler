using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Core.Verification.Gates;

/// <summary>One evidence entry as the file holds it, the state still as text.</summary>
internal sealed record RawEvidence(string State, string Kind, string Path, string Sha256);

/// <summary>One fixture as the file holds it.</summary>
internal sealed record RawFixture(string Fixture, IReadOnlyList<RawEvidence> Evidence);

/// <summary>A gate record as the file holds it, after the schema accepted it.</summary>
internal sealed record RawRecord(PairKey Pair, string Commit, IReadOnlyList<RawFixture> Fixtures);

/// <summary>
/// The file format of a gate record: <c>spec/schemas/gate-record.schema.json</c>, written canonically
/// as the manifest is (<see cref="CanonicalJson"/>), with the fixtures sorted by ID and each fixture's
/// evidence in the order of the states.
/// </summary>
internal static class GateRecordFile {

  /// <summary>The version the file carries.</summary>
  public const int SchemaVersion = 1;

  private const string Resource = "Polypiler.Core.Verification.Gates.gate-record.schema.json";

  private static readonly JsonDocumentOptions _strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, AllowDuplicateProperties = false };

  private static readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List };

  private static readonly UTF8Encoding _utf8 = new(false);

  private static readonly Lazy<JsonSchema> _schema = new(() => {
    using var reader = new StreamReader(typeof(GateRecordFile).Assembly.GetManifestResourceStream(Resource)!);
    return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
  });

  /// <summary>Reads a record's text: JSON, then the schema.</summary>
  /// <param name="text">The text.</param>
  /// <param name="errors">What is wrong with it; empty when the result is not <see langword="null"/>.</param>
  /// <returns>The record, or <see langword="null"/>.</returns>
  public static RawRecord? Parse(string text, out IReadOnlyList<string> errors) {
    JsonNode root;
    try {
      using var document = JsonDocument.Parse(text, _strict);
      var results = _schema.Value.Evaluate(document.RootElement, _evaluation);
      if (!results.IsValid) {
        errors = [.. results.Details!
          .Where(node => !node.IsValid && node.Errors is not null)
          .SelectMany(node => node.Errors!.Select(error => $"{Pointer(node.InstanceLocation.ToString())}: {Reason(node, error.Value)}"))
          .Distinct()];
        return null;
      }

      root = JsonNode.Parse(text, documentOptions: _strict)!;
    } catch (JsonException exception) {
      errors = [$"not JSON: {exception.Message}"];
      return null;
    }

    errors = [];
    var pair = root["pair"]!;
    return new(
      new(Text(pair["from"]), Text(pair["to"]), Text(pair["sourceProfile"]), Text(pair["targetProfile"])),
      Text(root["commit"]),
      [.. root["fixtures"]!.AsArray().Select(fixture => new RawFixture(
        Text(fixture!["fixture"]),
        [.. fixture["evidence"]!.AsArray().Select(evidence => new RawEvidence(Text(evidence!["state"]), Text(evidence["kind"]), Text(evidence["path"]), Text(evidence["sha256"])))]))]);
  }

  /// <summary>The canonical text of a record, ending in a line break.</summary>
  /// <param name="pair">The pair.</param>
  /// <param name="commit">The commit the evidence was produced at.</param>
  /// <param name="fixtures">The fixtures, in any order.</param>
  /// <returns>The text.</returns>
  public static string Serialize(PairKey pair, string commit, IEnumerable<FixtureGate> fixtures) {
    var root = new JsonObject {
      ["schemaVersion"] = SchemaVersion,
      ["pair"] = new JsonObject {
        ["from"] = pair.From,
        ["to"] = pair.To,
        ["sourceProfile"] = pair.SourceProfile,
        ["targetProfile"] = pair.TargetProfile
      },
      ["commit"] = commit,
      ["fixtures"] = new JsonArray([.. fixtures
        .OrderBy(fixture => fixture.FixtureId, StringComparer.Ordinal)
        .Select(fixture => (JsonNode)new JsonObject {
          ["fixture"] = fixture.FixtureId,
          ["evidence"] = new JsonArray([.. fixture.Evidence
            .OrderBy(evidence => evidence.State)
            .Select(evidence => (JsonNode)new JsonObject {
              ["state"] = GateStates.Name(evidence.State),
              ["kind"] = evidence.Kind,
              ["path"] = evidence.Path,
              ["sha256"] = evidence.Sha256
            })])
        })])
    };
    return CanonicalJson.Serialize(root) + "\n";
  }

  /// <summary>Writes a record through a temporary file that is renamed over it.</summary>
  /// <param name="path">The record.</param>
  /// <param name="text">Its text.</param>
  public static void Write(string path, string text) {
    var temporary = path + $".{Guid.NewGuid():N}.tmp";
    try {
      File.WriteAllBytes(temporary, _utf8.GetBytes(text));
      File.Move(temporary, path, true);
    } finally {
      if (File.Exists(temporary))
        File.Delete(temporary);
    }
  }

  /// <summary>Reads a file as UTF-8.</summary>
  /// <param name="path">The file.</param>
  /// <returns>Its text.</returns>
  public static string Read(string path) => File.ReadAllText(path, _utf8);

  private static string Text(JsonNode? node) => node!.GetValue<string>();

  private static string Pointer(string location) => location.Length == 0 ? "/" : location;

  /// <summary>The reason of an error; for a property the schema forbids, which the false schema reports, the property's name.</summary>
  private static string Reason(EvaluationResults node, string message) {
    if (!node.EvaluationPath.ToString().EndsWith("/additionalProperties", StringComparison.Ordinal))
      return message;

    var location = node.InstanceLocation.ToString();
    return $"property '{location[(location.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)}' is not allowed";
  }

}
