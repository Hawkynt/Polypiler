using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tooling.Integrity;

/// <summary>
/// A <c>golden.provenance.json</c> file: it makes every file of its folder and below golden, except
/// those its <c>exclude</c> patterns match, and says where they came from. It is how expectations in a
/// format of their own - the floating-point reference vectors, the outcomes recorded from node - are
/// held to the same lock and the same provenance rules as a <c>*.vectors.json</c> file:
/// <c>{"schema":1,"provenance":{...},"exclude":["&lt;glob&gt;", ...]}</c>, <c>exclude</c> optional.
/// </summary>
public sealed class ProvenanceMarker {

  /// <summary>The name of every marker file.</summary>
  public const string FileName = "golden.provenance.json";

  private static readonly string[] _fields = ["schema", "provenance", "exclude"];

  private ProvenanceMarker(string path, Provenance? provenance, ImmutableArray<GlobPattern> exclude, ImmutableArray<string> problems) {
    this.Path = path;
    this.Folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    this.Provenance = provenance;
    this.Exclude = exclude;
    this.Problems = problems;
  }

  /// <summary>The marker's path below the repository root.</summary>
  public string Path { get; }

  /// <summary>The folder the marker covers, below the repository root.</summary>
  public string Folder { get; }

  /// <summary>Where the files came from; <see langword="null"/> when the marker is invalid.</summary>
  public Provenance? Provenance { get; }

  /// <summary>The patterns, relative to <see cref="Folder"/>, of files the marker leaves out.</summary>
  public ImmutableArray<GlobPattern> Exclude { get; }

  /// <summary>What is wrong with the marker; empty when it is valid.</summary>
  public ImmutableArray<string> Problems { get; }

  /// <summary>Whether a path below the repository root is in the marker's folder and not excluded.</summary>
  public bool Covers(string path) {
    if (!path.StartsWith(this.Folder + "/", StringComparison.Ordinal))
      return false;
    var relative = path[(this.Folder.Length + 1)..];
    return !this.Exclude.Any(pattern => pattern.IsMatch(relative));
  }

  /// <summary>Opens a marker read-only and reads it; what is wrong with it ends up in <see cref="Problems"/>.</summary>
  /// <param name="file">The file on disk.</param>
  /// <param name="path">The marker's path below the repository root.</param>
  public static ProvenanceMarker Load(string file, string path) {
    try {
      using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
      using var reader = new StreamReader(stream);
      return Parse(reader.ReadToEnd(), path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return new(path, null, [], [$"cannot be read: {exception.Message}"]);
    }
  }

  /// <summary>Reads the text of a marker.</summary>
  /// <param name="text">The JSON text.</param>
  /// <param name="path">The marker's path below the repository root.</param>
  public static ProvenanceMarker Parse(string text, string path) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(text);
    } catch (JsonException exception) {
      return new(path, null, [], [$"not JSON: {exception.Message}"]);
    }

    using (document) {
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
        return new(path, null, [], ["a provenance marker must be a JSON object"]);

      var problems = new List<string>();
      foreach (var property in root.EnumerateObject().Where(property => !_fields.Contains(property.Name)))
        problems.Add($"unknown property '{property.Name}'");
      if (!root.TryGetProperty("schema", out var schema) || schema.GetRawText() != "1")
        problems.Add("'schema' must be 1");

      var provenance = Provenance.Read(root.TryGetProperty("provenance", out var block) ? block : null, problems);
      var exclude = ImmutableArray.CreateBuilder<GlobPattern>();
      if (root.TryGetProperty("exclude", out var list))
        if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Length == 0))
          problems.Add("'exclude' must be an array of non-empty patterns");
        else
          exclude.AddRange(list.EnumerateArray().Select(item => GlobPattern.Parse(item.GetString()!)));

      return new(path, problems.Count == 0 ? provenance : null, exclude.ToImmutable(), [.. problems]);
    }
  }

}
