using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Polypiler.Tooling.Corpus;

/// <summary>One imported file as the lock records it.</summary>
/// <param name="Repository">The repository as <c>owner/repo</c>.</param>
/// <param name="Commit">The pinned commit the file was read from.</param>
/// <param name="Path">The file, relative to the repository root.</param>
/// <param name="Sha256">The SHA-256 of the file's bytes, 64 lower-case hexadecimal digits.</param>
/// <param name="Bytes">The file's length.</param>
public sealed record CorpusLockEntry(string Repository, string Commit, string Path, string Sha256, long Bytes) {

  /// <summary>The entry for a file's bytes.</summary>
  public static CorpusLockEntry For(CorpusRepository repository, string path, byte[] content)
    => new(repository.Name, repository.Commit, path, Hash(content), content.LongLength);

  /// <summary>The SHA-256 of bytes as 64 lower-case hexadecimal digits.</summary>
  public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

  /// <summary>The file as diagnostics name it: <c>owner/repo:path</c>.</summary>
  public string Name => $"{this.Repository}:{this.Path}";

  /// <summary>The file below <c>corpus/upstream</c>: <c>owner/repo@commit/path</c>.</summary>
  public string Location => $"{this.Repository}@{this.Commit}/{this.Path}";

}

/// <summary>
/// <c>tooling/corpus/corpus.lock.json</c>: the SHA-256 and length of every imported file. It is
/// generated, never edited: entries sorted by repository, then path (ordinal), two-space indent, LF
/// line ends and a trailing line break, so the same files always give the same bytes.
/// </summary>
public sealed class CorpusLock {

  /// <summary>The lock's path below the repository root.</summary>
  public const string RepositoryPath = "tooling/corpus/corpus.lock.json";

  private static readonly Regex _commit = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
  private static readonly Regex _sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
  private static readonly string[] _fields = ["repository", "commit", "path", "sha256", "bytes"];

  /// <summary>Creates a lock of entries, which it sorts.</summary>
  public CorpusLock(IEnumerable<CorpusLockEntry> entries)
    => this.Entries = [.. entries.OrderBy(entry => entry.Repository, StringComparer.Ordinal).ThenBy(entry => entry.Path, StringComparer.Ordinal)];

  /// <summary>The entries, sorted by repository, then path.</summary>
  public IReadOnlyList<CorpusLockEntry> Entries { get; }

  /// <summary>The entry of a file of a repository at a commit, or <see langword="null"/>.</summary>
  public CorpusLockEntry? Find(string repository, string commit, string path)
    => this.Entries.FirstOrDefault(entry => entry.Repository == repository && entry.Commit == commit && entry.Path == path);

  /// <summary>Reads a lock file; a file that does not exist is an empty lock.</summary>
  /// <exception cref="CorpusException">The file cannot be read or is not a valid lock (<c>PLY0026</c>).</exception>
  public static CorpusLock Load(string path) {
    if (!File.Exists(path))
      return new([]);

    string text;
    try {
      text = File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new CorpusException(CorpusDiagnostics.Manifest($"cannot read '{path}': {exception.Message}"));
    }

    return Parse(text, path);
  }

  /// <summary>Checks the text of a lock.</summary>
  /// <exception cref="CorpusException">The text is not a valid lock (<c>PLY0026</c>).</exception>
  public static CorpusLock Parse(string text, string file) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(text);
    } catch (JsonException exception) {
      throw Invalid(file, $"not JSON: {exception.Message}");
    }

    using (document) {
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object || Names(root) is not ["schema", "files"])
        throw Invalid(file, "the lock must be an object of exactly 'schema' and 'files'");
      if (root.GetProperty("schema") is not { ValueKind: JsonValueKind.Number } schema || schema.GetRawText() != "1")
        throw Invalid(file, $"unsupported lock schema {root.GetProperty("schema").GetRawText()}");
      if (root.GetProperty("files") is not { ValueKind: JsonValueKind.Array } files)
        throw Invalid(file, "'files' of the lock must be an array");

      var entries = new List<CorpusLockEntry>();
      foreach (var item in files.EnumerateArray()) {
        var entry = Entry(item) ?? throw Invalid(file, $"lock entry {entries.Count + 1} is malformed: {item.GetRawText()}");
        if (entries.Any(other => other.Location == entry.Location))
          throw Invalid(file, $"lock entry '{entry.Location}' is listed twice");
        entries.Add(entry);
      }

      return new(entries);
    }
  }

  private static CorpusLockEntry? Entry(JsonElement item) {
    if (item.ValueKind != JsonValueKind.Object || !Names(item).SequenceEqual(_fields))
      return null;

    var texts = _fields[..4].Select(name => item.GetProperty(name)).ToArray();
    if (texts.Any(value => value.ValueKind != JsonValueKind.String))
      return null;

    var (repository, commit, path, sha256) = (texts[0].GetString()!, texts[1].GetString()!, texts[2].GetString()!, texts[3].GetString()!);
    if (!item.GetProperty("bytes").TryGetInt64(out var bytes) || bytes < 0)
      return null;
    if (!_commit.IsMatch(commit) || !_sha256.IsMatch(sha256) || CorpusManifest.PathProblem(path) is not null || repository.Count(character => character == '/') != 1)
      return null;
    return new(repository, commit, path, sha256, bytes);
  }

  private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];

  private static CorpusException Invalid(string file, string reason) => new(CorpusDiagnostics.Manifest(reason, file));

  /// <summary>The lock as its canonical text.</summary>
  public string ToJson() {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = "\n" })) {
      writer.WriteStartObject();
      writer.WriteNumber("schema", 1);
      writer.WriteStartArray("files");
      foreach (var entry in this.Entries) {
        writer.WriteStartObject();
        writer.WriteString("repository", entry.Repository);
        writer.WriteString("commit", entry.Commit);
        writer.WriteString("path", entry.Path);
        writer.WriteString("sha256", entry.Sha256);
        writer.WriteNumber("bytes", entry.Bytes);
        writer.WriteEndObject();
      }

      writer.WriteEndArray();
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
  }

}
