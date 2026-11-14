using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Polypiler.Core;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>languages</c>: a source language in <c>spec/languages/&lt;identifier&gt;/language.yaml</c>
/// (ID <c>LANGUAGES.&lt;identifier&gt;</c>) and its profiles in
/// <c>spec/languages/&lt;identifier&gt;/profiles/&lt;profile&gt;.yaml</c> (ID
/// <c>LANGUAGES.&lt;identifier&gt;.&lt;profile&gt;</c>). The language holds the <c>--from</c> identifier,
/// the backlog code and the diagnostic block; a profile holds the version, the evaluation, overflow,
/// division and shift rules, the toolchain and the construct table of the frontend.
/// </summary>
public sealed class LanguagesSpecFamily : ISpecFamily {

  private const string LanguageFile = "language.yaml";

  private const string ProfileFolder = "profiles";

  private const string Extension = ".yaml";

  /// <summary>The width of the frontend range of a block: <c>x00</c> to <c>x49</c>.</summary>
  private const int FrontendCodes = 50;

  private static readonly ConditionalWeakTable<SpecTree, Toolchains> _toolchains = [];

  /// <inheritdoc />
  public string Folder => "languages";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/languages.schema.json";

  /// <summary>
  /// The rules, in this order: <c>layout</c> (and nothing else when it fails), then for a language
  /// <c>unique-identifier</c>, for a profile <c>profile-has-language</c> or the reference to the
  /// language (<c>PLY0043</c>), <c>toolchain-known</c>, and per construct <c>unique-construct</c>,
  /// <c>diagnosed-needs-code</c> and <c>diagnostic-in-block</c>.
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the other languages and the toolchain lock are found in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var body = (JsonObject)document.Body!;
    var kind = Text(body["kind"])!;
    var layout = Layout(document, kind, body).ToList();
    if (layout.Count > 0)
      return layout;

    var folder = document.Path.Split('/')[1];
    return kind == "language" ? UniqueIdentifier(document, body, tree) : Profile(document, folder, body, tree);
  }

  /// <summary>Rule <c>layout</c>: the path is one of the two forms, the ID and the kind follow from it, and a language's identifier is its folder.</summary>
  private static IEnumerable<Diagnostic> Layout(SpecDocument document, string kind, JsonObject body) {
    var parts = document.Path.Split('/');
    var id = document.Meta!.Id;
    string expectedId, expectedKind;
    if (parts is [_, var language, LanguageFile]) {
      expectedId = $"LANGUAGES.{language}";
      expectedKind = "language";
    } else if (parts is [_, var owner, ProfileFolder, var file]) {
      expectedId = $"LANGUAGES.{owner}.{file[..^Extension.Length]}";
      expectedKind = "profile";
    } else {
      yield return SpecDiagnostics.Rule(document, "/id", $"path '{document.Path}' is neither languages/<identifier>/{LanguageFile} nor languages/<identifier>/{ProfileFolder}/<profile>{Extension}", "layout");
      yield break;
    }

    var folder = parts[1];
    if (id != expectedId)
      yield return SpecDiagnostics.Rule(document, "/id", $"id '{id}' does not match folder '{folder}', expected '{expectedId}'", "layout");
    if (kind != expectedKind)
      yield return SpecDiagnostics.Rule(document, "/spec/kind", $"{parts[^1]} holds kind '{kind}', expected '{expectedKind}'", "layout");
    else if (kind == "language" && Text(body["identifier"]) != folder)
      yield return SpecDiagnostics.Rule(document, "/spec/identifier", $"identifier '{Text(body["identifier"])}' does not match folder '{folder}'", "layout");
  }

  /// <summary>
  /// Rule <c>unique-identifier</c>: an earlier language in path order with the same backlog code or
  /// diagnostic block. The identifier needs no check of its own: <c>layout</c> makes it the folder name.
  /// </summary>
  private static IEnumerable<Diagnostic> UniqueIdentifier(SpecDocument document, JsonObject body, SpecTree tree) {
    var earlier = tree.Documents
      .Where(other => other.Family == document.Family && string.CompareOrdinal(other.Path, document.Path) < 0)
      .Select(other => (Document: other, Body: other.Body as JsonObject))
      .Where(other => other.Body is not null && Text(other.Body["kind"]) == "language")
      .ToList();
    foreach (var key in new[] { "backlogCode", "diagnosticBlock" }) {
      var value = Scalar(body[key])!;
      if (earlier.FirstOrDefault(other => Scalar(other.Body![key]) == value) is { Document: { } first })
        yield return SpecDiagnostics.Rule(document, $"/spec/{key}", $"{key} {(key == "diagnosticBlock" ? value : $"'{value}'")} is already used by {first.Id}", "unique-identifier");
    }
  }

  /// <summary>The rules of a profile.</summary>
  private static IEnumerable<Diagnostic> Profile(SpecDocument document, string folder, JsonObject body, SpecTree tree) {
    var language = Text(body["language"])!;
    var ownId = $"LANGUAGES.{folder}";
    long? block = null;
    if (language != ownId)
      yield return SpecDiagnostics.Rule(document, "/spec/language", $"profile in folder '{folder}' names language '{language}'", "profile-has-language");
    else if (tree.Resolve(document, "/spec/language", language) is { } broken)
      yield return broken;
    else if (tree.ById[language].Body is JsonObject owner && owner["diagnosticBlock"] is JsonValue value && value.TryGetValue<long>(out var first))
      block = first;

    var toolchain = Text(body["toolchain"])!;
    var known = _toolchains.GetValue(tree, Toolchains.Load);
    if (known.Problem is { } problem)
      yield return SpecDiagnostics.Rule(document, "/spec/toolchain", $"toolchain '{toolchain}' cannot be checked: {problem}", "toolchain-known");
    else if (!known.Keys.Contains(toolchain))
      yield return SpecDiagnostics.Rule(document, "/spec/toolchain", $"toolchain '{toolchain}' is not a key of {ToolchainLock.RepositoryPath}", "toolchain-known");

    var constructs = (JsonArray)body["constructs"]!;
    var names = new HashSet<string>(StringComparer.Ordinal);
    for (var index = 0; index < constructs.Count; ++index) {
      var construct = (JsonObject)constructs[index]!;
      var name = Text(construct["construct"])!;
      var pointer = $"/spec/constructs/{index}";
      if (!names.Add(name))
        yield return SpecDiagnostics.Rule(document, $"{pointer}/construct", $"construct '{name}' is listed twice", "unique-construct");

      var diagnostic = Text(construct["diagnostic"]);
      var diagnosed = Text(construct["state"]) == "diagnosed";
      if (diagnosed && diagnostic is null)
        yield return SpecDiagnostics.Rule(document, pointer, $"construct '{name}' is diagnosed but names no diagnostic", "diagnosed-needs-code");
      else if (!diagnosed && diagnostic is not null)
        yield return SpecDiagnostics.Rule(document, $"{pointer}/diagnostic", $"construct '{name}' is supported but names diagnostic {diagnostic}", "diagnosed-needs-code");
      else if (diagnostic is not null && block is { } start && int.Parse(diagnostic.AsSpan(3), CultureInfo.InvariantCulture) is var code && (code < start || code >= start + FrontendCodes))
        yield return SpecDiagnostics.Rule(document, $"{pointer}/diagnostic", $"diagnostic {diagnostic} is outside the frontend range {Code(start)}-{Code(start + FrontendCodes - 1)} of {folder}", "diagnostic-in-block");
    }
  }

  private static string Code(long number) => $"PLY{number.ToString("D4", CultureInfo.InvariantCulture)}";

  private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

  /// <summary>A string or an integer as text, so that values of either kind compare.</summary>
  private static string? Scalar(JsonNode? node) => node is JsonValue value
    ? value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : Text(value)
    : null;

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
