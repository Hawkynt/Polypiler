using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Polypiler.Core;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>targets</c>: one target profile in
/// <c>spec/targets/&lt;language identifier&gt;/&lt;profile&gt;/target.yaml</c> (ID
/// <c>TARGETS.&lt;language identifier&gt;.&lt;profile&gt;</c>) with its physical types, ABI, pointer and
/// union model, alignment, SIMD features, limits, runtime dependencies and packaging (PRD §4.1, §4.7).
/// The <c>profile</c> is the value <c>--target-profile</c> selects the document with.
/// </summary>
public sealed class TargetsSpecFamily : ISpecFamily {

  private const string TargetFile = "target.yaml";

  private const string LanguagePrefix = "LANGUAGES.";

  private static readonly ConditionalWeakTable<SpecTree, Toolchains> _toolchains = [];

  /// <inheritdoc />
  public string Folder => "targets";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/targets.schema.json";

  /// <summary>
  /// The rules, in this order: <c>layout</c> of the path and the ID (and nothing else when it fails),
  /// <c>layout</c> of <c>language</c> and <c>profile</c> against the folders, the reference to the
  /// language (<c>PLY0043</c>), <c>pointer-consistency</c>, <c>limit-order</c>, <c>toolchain-known</c>,
  /// per physical type <c>unique-type</c>, its reference (<c>PLY0043</c>) and <c>type-fits-target</c>,
  /// and last <c>unique-target</c>.
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the languages, the types, the other targets and the toolchain lock are found in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var body = (JsonObject)document.Body!;
    var parts = document.Path.Split('/');
    if (parts is not [_, var language, var profile, TargetFile])
      return [SpecDiagnostics.Rule(document, "/id", $"path '{document.Path}' is not targets/<language identifier>/<profile>/{TargetFile}", "layout")];

    var id = document.Meta!.Id;
    var expected = $"TARGETS.{language}.{profile}";
    if (id != expected) {
      var folder = id.StartsWith($"TARGETS.{language}.", StringComparison.Ordinal) ? profile : language;
      return [SpecDiagnostics.Rule(document, "/id", $"id '{id}' does not match folder '{folder}', expected '{expected}'", "layout")];
    }

    return Rules(document, language, profile, body, tree);
  }

  private static IEnumerable<Diagnostic> Rules(SpecDocument document, string folder, string profileFolder, JsonObject body, SpecTree tree) {
    var language = Text(body["language"])!;
    if (language != LanguagePrefix + folder)
      yield return SpecDiagnostics.Rule(document, "/spec/language", $"language '{language}' does not match folder '{folder}'", "layout");
    else if (tree.Resolve(document, "/spec/language", language) is { } broken)
      yield return broken;

    var profile = Text(body["profile"])!;
    if (profile != profileFolder)
      yield return SpecDiagnostics.Rule(document, "/spec/profile", $"profile '{profile}' does not match folder '{profileFolder}'", "layout");

    var pointers = Text(body["pointers"])!;
    var pointerBits = Number(body["abi"]!["pointerBits"]);
    if (pointers == "native" && pointerBits == 0)
      yield return SpecDiagnostics.Rule(document, "/spec/abi/pointerBits", "pointers 'native' requires abi.pointerBits above 0, found 0", "pointer-consistency");
    else if (pointers != "native" && pointerBits != 0)
      yield return SpecDiagnostics.Rule(document, "/spec/abi/pointerBits", $"pointers '{pointers}' requires abi.pointerBits 0, found {pointerBits}", "pointer-consistency");

    var maxIntegerBits = Number(body["limits"]!["maxIntegerBits"]);
    var exactIntegerBits = Number(body["limits"]!["exactIntegerBits"]);
    if (exactIntegerBits > maxIntegerBits)
      yield return SpecDiagnostics.Rule(document, "/spec/limits/exactIntegerBits", $"limits.exactIntegerBits {exactIntegerBits} exceeds limits.maxIntegerBits {maxIntegerBits}", "limit-order");

    var toolchain = Text(body["toolchain"])!;
    var known = _toolchains.GetValue(tree, Toolchains.Load);
    if (known.Problem is { } problem)
      yield return SpecDiagnostics.Rule(document, "/spec/toolchain", $"toolchain '{toolchain}' cannot be checked: {problem}", "toolchain-known");
    else if (!known.Keys.Contains(toolchain))
      yield return SpecDiagnostics.Rule(document, "/spec/toolchain", $"toolchain '{toolchain}' is not a key of {ToolchainLock.RepositoryPath}", "toolchain-known");

    var types = (JsonArray)body["physicalTypes"]!;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var index = 0; index < types.Count; ++index) {
      var entry = (JsonObject)types[index]!;
      var type = Text(entry["type"])!;
      var pointer = $"/spec/physicalTypes/{index}";
      if (!seen.Add(type))
        yield return SpecDiagnostics.Rule(document, $"{pointer}/type", $"type '{type}' is listed twice", "unique-type");
      else if (tree.Resolve(document, $"{pointer}/type", type) is { } dangling)
        yield return dangling;
      else if (!(bool)entry["helper"]! && tree.ById[type].Body is JsonObject descriptor && Text(descriptor["family"]) == "integer" && descriptor["bits"] is JsonValue width && width.TryGetValue<long>(out var bits) && bits > maxIntegerBits)
        yield return SpecDiagnostics.Rule(document, $"{pointer}/helper", $"{type} ({bits} bits) is mapped without a helper but limits.maxIntegerBits is {maxIntegerBits}", "type-fits-target");
    }

    var earlier = tree.Documents
      .Where(other => other.Family == document.Family && string.CompareOrdinal(other.Path, document.Path) < 0)
      .FirstOrDefault(other => other.Body is JsonObject them && Text(them["language"]) == language && Text(them["profile"]) == profile);
    if (earlier is not null)
      yield return SpecDiagnostics.Rule(document, "/spec/profile", $"target {Identifier(language)}/{profile} is already defined by {earlier.Id}", "unique-target");
  }

  /// <summary>The identifier of a language ID; the schema makes every one start with <c>LANGUAGES.</c>.</summary>
  private static string Identifier(string language) => language[LanguagePrefix.Length..];

  private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

  private static long Number(JsonNode? node) => ((JsonValue)node!).GetValue<long>();

  /// <summary>The keys of <c>tooling/toolchains.lock</c> below the repository root of a tree, read once per tree.</summary>
  private sealed record Toolchains(IReadOnlySet<string> Keys, string? Problem) {

    public static Toolchains Load(SpecTree tree) {
      var path = Path.Combine(tree.RepositoryRoot, ToolchainLock.RepositoryPath);
      try {
        return new(ToolchainLock.Load(path).Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal), null);
      } catch (ToolchainLockException exception) {
        return new(new HashSet<string>(), $"{ToolchainLock.RepositoryPath} is not a valid toolchain lock ({exception.Diagnostics[0].Message})");
      }
    }

  }

}
