using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Core;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tooling.Ci;

/// <summary>A test category the fast-tier filter excludes, and the step that runs it instead.</summary>
/// <param name="Name">The NUnit category.</param>
/// <param name="Job">The job of the workflow.</param>
/// <param name="Step">The step of that job, by id or name.</param>
/// <param name="Pending">The backlog ID of the issue that brings the category's first test; a pending category may have none.</param>
public sealed record CiCategory(string Name, string Job, string Step, string? Pending);

/// <summary>
/// <c>tooling/ci/categories.yaml</c>: <c>schema: 1</c> and <c>categories</c>, a list of
/// <c>{name, job, step, pending?}</c> with every name once.
/// </summary>
public static class CiCategories {

  /// <summary>The file, relative to the repository root.</summary>
  public const string RepositoryPath = "tooling/ci/categories.yaml";

  private static readonly HashSet<string> _keys = new(StringComparer.Ordinal) { "name", "job", "step", "pending" };

  /// <summary>Reads the category file of a repository.</summary>
  /// <param name="root">The repository root.</param>
  /// <param name="problems">Receives a <c>PLY0075</c> for every entry that is not valid.</param>
  /// <returns>The valid categories, in the order of the file.</returns>
  /// <exception cref="CiEnvironmentException">The file does not exist (<c>PLY0004</c>).</exception>
  public static IReadOnlyList<CiCategory> Load(string root, List<Diagnostic> problems) {
    ArgumentNullException.ThrowIfNull(root);
    var path = Path.Combine(root, RepositoryPath);
    if (!File.Exists(path))
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(RepositoryPath, "the file does not exist"));

    return Parse(File.ReadAllText(path), RepositoryPath, problems);
  }

  /// <summary>Reads the text of a category file.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="file">The name messages give the file.</param>
  /// <param name="problems">Receives a <c>PLY0075</c> <c>&lt;file&gt;: &lt;reason&gt;</c> for every problem.</param>
  /// <returns>The valid categories, in the order of the file.</returns>
  public static IReadOnlyList<CiCategory> Parse(string text, string file, List<Diagnostic> problems) {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(problems);
    JsonNode? document;
    try {
      document = YamlToJson.Read(text, out _);
    } catch (YamlToJsonException exception) {
      problems.Add(CiDiagnostics.InconsistentWith($"{file}: {exception.Message}"));
      return [];
    }

    if (document is not JsonObject root || root["schema"]?.ToJsonString() != "1" || root["categories"] is not JsonArray entries) {
      problems.Add(CiDiagnostics.InconsistentWith($"{file}: expected 'schema: 1' and a list 'categories'"));
      return [];
    }

    var categories = new List<CiCategory>();
    for (var index = 0; index < entries.Count; ++index) {
      if (Entry(entries[index]) is not { } category) {
        problems.Add(CiDiagnostics.InconsistentWith($"{file}: /categories/{index}: expected 'name', 'job' and 'step' as text and an optional 'pending', nothing else"));
        continue;
      }

      if (categories.Any(known => known.Name == category.Name)) {
        problems.Add(CiDiagnostics.InconsistentWith($"{file}: category '{category.Name}' is listed twice"));
        continue;
      }

      categories.Add(category);
    }

    return categories;
  }

  private static CiCategory? Entry(JsonNode? node) {
    if (node is not JsonObject entry || entry.Any(property => !_keys.Contains(property.Key)))
      return null;

    var pending = entry["pending"];
    return Text(entry["name"]) is { } name && Text(entry["job"]) is { } job && Text(entry["step"]) is { } step && (pending is null || Text(pending) is not null)
      ? new(name, job, step, pending is null ? null : Text(pending))
      : null;
  }

  private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

}
