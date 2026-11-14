using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Polypiler.Tooling.Integrity;

/// <summary>One golden file as the golden lock records it.</summary>
/// <param name="Path">The file below the repository root, with <c>/</c> separators.</param>
/// <param name="Sha256">The SHA-256 of the file's bytes, 64 lower-case hexadecimal digits.</param>
/// <param name="Bytes">The file's length.</param>
public sealed record GoldenLockEntry(string Path, string Sha256, long Bytes) {

  /// <summary>The entry for a file's bytes.</summary>
  public static GoldenLockEntry For(string path, byte[] content) => new(path, Convert.ToHexStringLower(SHA256.HashData(content)), content.LongLength);

}

/// <summary>
/// <c>tests/fixtures/golden.lock.json</c>: the SHA-256 and length of every golden file. Written only by
/// <c>polypiler-tooling integrity seal</c>: entries sorted by path (ordinal), two-space indent, LF line
/// ends and a trailing line break, so the same files always give the same bytes.
/// </summary>
public sealed class GoldenLock {

  /// <summary>The lock's path below the repository root.</summary>
  public const string RepositoryPath = "tests/fixtures/golden.lock.json";

  /// <summary>The change log a pull request that changes a golden file adds its line to.</summary>
  public const string ChangeLogPath = "tests/fixtures/golden.changes.md";

  private static readonly Regex _sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
  private static readonly string[] _fields = ["path", "sha256", "bytes"];

  /// <summary>Creates a lock of entries, which it sorts.</summary>
  public GoldenLock(IEnumerable<GoldenLockEntry> entries) => this.Entries = [.. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)];

  /// <summary>The entries, sorted by path.</summary>
  public IReadOnlyList<GoldenLockEntry> Entries { get; }

  /// <summary>The entry of a path, or <see langword="null"/>.</summary>
  public GoldenLockEntry? Find(string path) => this.Entries.FirstOrDefault(entry => entry.Path == path);

  /// <summary>The lock with the entries of some paths replaced: a path given an entry gets it, a path given <see langword="null"/> loses its entry; every other entry stays.</summary>
  public GoldenLock With(IReadOnlyDictionary<string, GoldenLockEntry?> changes)
    => new(this.Entries.Where(entry => !changes.ContainsKey(entry.Path)).Concat(changes.Values.OfType<GoldenLockEntry>()));

  /// <summary>Reads a lock file; a file that does not exist is an empty lock.</summary>
  /// <exception cref="IntegrityException">The file cannot be read or is not a valid lock (<c>PLY0027</c>).</exception>
  public static GoldenLock Load(string file) {
    string text;
    try {
      text = File.ReadAllText(file);
    } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) {
      return new([]);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw Invalid($"cannot be read: {exception.Message}");
    }

    return Parse(text);
  }

  /// <summary>Checks the text of a lock.</summary>
  /// <exception cref="IntegrityException">The text is not a valid lock (<c>PLY0027</c>).</exception>
  public static GoldenLock Parse(string text) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(text);
    } catch (JsonException exception) {
      throw Invalid($"not JSON: {exception.Message}");
    }

    using (document) {
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(property => property.Name).ToArray() is not ["schema", "files"])
        throw Invalid("the lock must be an object of exactly 'schema' and 'files'");
      if (root.GetProperty("schema").GetRawText() != "1")
        throw Invalid($"unsupported lock schema {root.GetProperty("schema").GetRawText()}");
      if (root.GetProperty("files") is not { ValueKind: JsonValueKind.Array } files)
        throw Invalid("'files' of the lock must be an array");

      var entries = new List<GoldenLockEntry>();
      foreach (var item in files.EnumerateArray()) {
        var entry = Entry(item) ?? throw Invalid($"lock entry {entries.Count + 1} is malformed: {item.GetRawText()}");
        if (entries.Any(other => other.Path == entry.Path))
          throw Invalid($"lock entry '{entry.Path}' is listed twice");
        entries.Add(entry);
      }

      return new(entries);
    }
  }

  private static GoldenLockEntry? Entry(JsonElement item) {
    if (item.ValueKind != JsonValueKind.Object || !item.EnumerateObject().Select(property => property.Name).SequenceEqual(_fields))
      return null;
    if (item.GetProperty("path") is not { ValueKind: JsonValueKind.String } path || path.GetString()!.Length == 0)
      return null;
    if (item.GetProperty("sha256") is not { ValueKind: JsonValueKind.String } sha256 || !_sha256.IsMatch(sha256.GetString()!))
      return null;
    if (item.GetProperty("bytes") is not { ValueKind: JsonValueKind.Number } bytes || !bytes.TryGetInt64(out var length) || length < 0)
      return null;
    return new(path.GetString()!, sha256.GetString()!, length);
  }

  private static IntegrityException Invalid(string reason) => new([IntegrityDiagnostics.Modified(RepositoryPath, $"not a valid golden lock: {reason}")]);

  /// <summary>The lock as its canonical text.</summary>
  public string ToJson() {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = "\n" })) {
      writer.WriteStartObject();
      writer.WriteNumber("schema", 1);
      writer.WriteStartArray("files");
      foreach (var entry in this.Entries) {
        writer.WriteStartObject();
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

/// <summary>
/// Which files of a repository are golden: below <c>tests/fixtures</c> and <c>tests/vectors</c>, every
/// <c>*.vectors.json</c>, every file in a folder named <c>golden</c>, every
/// <see cref="ProvenanceMarker"/> and every file a marker covers. The golden lock and the change log are
/// never golden themselves, and <c>.git</c> folders are not looked into.
/// </summary>
public sealed class GoldenCoverage {

  /// <summary>The folders, below the repository root, that hold golden files.</summary>
  public static IReadOnlyList<string> Roots { get; } = ["tests/fixtures", "tests/vectors"];

  private GoldenCoverage(IReadOnlyList<ProvenanceMarker> markers, IReadOnlyList<string> files) {
    this.Markers = markers;
    this.Files = files;
  }

  /// <summary>The markers, sorted by path.</summary>
  public IReadOnlyList<ProvenanceMarker> Markers { get; }

  /// <summary>The golden files on disk, below the repository root, sorted by path (ordinal).</summary>
  public IReadOnlyList<string> Files { get; }

  /// <summary>Finds the golden files below a repository root.</summary>
  public static GoldenCoverage Of(string root) {
    var all = new List<string>();
    foreach (var folder in Roots) {
      var full = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
      if (Directory.Exists(full))
        all.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
          .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
          .Where(file => !file.Split('/').Contains(".git")));
    }

    all.Sort(StringComparer.Ordinal);
    var markers = all.Where(file => file.EndsWith("/" + ProvenanceMarker.FileName, StringComparison.Ordinal))
      .Select(file => ProvenanceMarker.Load(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)), file))
      .ToList();
    var coverage = new GoldenCoverage(markers, []);
    return new(markers, [.. all.Where(coverage.Covers)]);
  }

  /// <summary>Whether a path below the repository root is golden, whether or not the file exists.</summary>
  public bool Covers(string path) {
    if (path is GoldenLock.RepositoryPath or GoldenLock.ChangeLogPath || !Roots.Any(root => path.StartsWith(root + "/", StringComparison.Ordinal)))
      return false;

    var segments = path.Split('/');
    return segments[^1].EndsWith(VectorFile.Extension, StringComparison.Ordinal)
      || segments[^1] == ProvenanceMarker.FileName
      || segments[..^1].Contains("golden")
      || this.MarkerOf(path) is not null;
  }

  /// <summary>The innermost marker that covers a path, or <see langword="null"/>.</summary>
  public ProvenanceMarker? MarkerOf(string path)
    => this.Markers.Where(marker => marker.Covers(path)).MaxBy(marker => marker.Folder.Length);

}
