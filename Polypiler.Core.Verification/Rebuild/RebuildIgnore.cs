using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Core.Verification.Rebuild;

/// <summary>A file the rebuild check leaves out of the listing.</summary>
/// <param name="Glob">The pattern, matched against the whole relative path: <c>*</c> and <c>?</c> stay within one segment, <c>**</c> crosses segments.</param>
/// <param name="Reason">Why the file may differ between runs.</param>
public sealed record IgnoredFile(string Glob, string Reason);

/// <summary>A member of the output manifest the rebuild check removes before it compares.</summary>
/// <param name="Pointer">The member, as an RFC 6901 JSON pointer.</param>
/// <param name="Reason">Why the member may differ between runs.</param>
public sealed record IgnoredPointer(string Pointer, string Reason);

/// <summary>
/// <c>tooling/ci/rebuild-ignore.yaml</c>: the only place where content that depends on the run may be
/// exempted from the rebuild check, each entry with its reason. An ignored file is left out of the
/// listing; an ignored pointer is removed from the manifest before it is re-serialized canonically
/// (keys sorted ordinally, no insignificant whitespace) and hashed.
/// </summary>
public sealed class RebuildIgnore {

  /// <summary>Where the file lives, relative to the repository root.</summary>
  public const string RepositoryPath = "tooling/ci/rebuild-ignore.yaml";

  private readonly Regex[] _globs;

  /// <summary>Creates the list.</summary>
  /// <param name="files">The files left out.</param>
  /// <param name="manifestPointers">The manifest members removed.</param>
  /// <exception cref="ArgumentException">A glob is empty, a pointer is not an RFC 6901 pointer below the root, or a reason is empty.</exception>
  public RebuildIgnore(IReadOnlyList<IgnoredFile> files, IReadOnlyList<IgnoredPointer> manifestPointers) {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(manifestPointers);
    foreach (var problem in files.Select(Problem).Concat(manifestPointers.Select(Problem)).OfType<string>())
      throw new ArgumentException(problem);

    this.Files = [.. files];
    this.ManifestPointers = [.. manifestPointers];
    this._globs = [.. files.Select(file => Glob(file.Glob))];
  }

  /// <summary>The list that exempts nothing.</summary>
  public static RebuildIgnore Empty { get; } = new([], []);

  /// <summary>The files left out.</summary>
  public IReadOnlyList<IgnoredFile> Files { get; }

  /// <summary>The manifest members removed.</summary>
  public IReadOnlyList<IgnoredPointer> ManifestPointers { get; }

  /// <summary>Whether a file is left out of the listing.</summary>
  /// <param name="path">The path relative to the tree's root, with <c>/</c> between segments.</param>
  public bool IsIgnored(string path) => this._globs.Any(glob => glob.IsMatch(path));

  /// <summary>
  /// The canonical form of a manifest: the JSON without the ignored members, keys sorted ordinally,
  /// no insignificant whitespace, in UTF-8.
  /// </summary>
  /// <param name="manifest">The manifest's bytes.</param>
  /// <returns>The canonical bytes; <see langword="null"/> when the bytes are not JSON, which are then compared as they are.</returns>
  public ReadOnlyMemory<byte>? Canonicalize(ReadOnlyMemory<byte> manifest) {
    try {
      var root = JsonNode.Parse(manifest.Span);
      foreach (var ignored in this.ManifestPointers)
        Remove(root, ignored.Pointer);

      using var buffer = new MemoryStream();
      using (var writer = new Utf8JsonWriter(buffer))
        Write(writer, root);
      return buffer.ToArray();
    } catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException) {
      // Not JSON, or JSON that System.Text.Json refuses as a tree (a property given twice): bytes it is.
      return null;
    }
  }

  /// <summary>Reads the list from a file.</summary>
  /// <param name="path">The file.</param>
  /// <returns>The list.</returns>
  /// <exception cref="RebuildIgnoreException">The file cannot be read, is not schema 1, or has an entry without its reason.</exception>
  public static RebuildIgnore Load(string path) {
    string text;
    try {
      text = File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new RebuildIgnoreException($"'{path}' cannot be read: {exception.Message}", 0, exception);
    }

    return Parse(text);
  }

  /// <summary>Reads the list from its text.</summary>
  /// <param name="text">The YAML.</param>
  /// <returns>The list.</returns>
  /// <exception cref="RebuildIgnoreException">The text is not schema 1, or has an entry without its reason.</exception>
  public static RebuildIgnore Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      throw new RebuildIgnoreException(exception.Message, (int)exception.Start.Line, exception);
    }

    if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
      throw new RebuildIgnoreException("expected a mapping with 'schema: 1'", 1);

    var schema = Member(root, "schema");
    if (schema is not YamlScalarNode { Value: "1" })
      throw new RebuildIgnoreException("expected 'schema: 1'", Line(schema ?? root));

    foreach (var key in root.Children.Keys)
      if (key is not YamlScalarNode { Value: "schema" or "files" or "manifestPointers" })
        throw new RebuildIgnoreException($"unknown key '{key}'", Line(key));

    var files = new List<IgnoredFile>();
    foreach (var (glob, reason, line) in Entries(root, "files", "glob")) {
      var file = new IgnoredFile(glob, reason);
      files.Add(Problem(file) is { } problem ? throw new RebuildIgnoreException(problem, line) : file);
    }

    var pointers = new List<IgnoredPointer>();
    foreach (var (value, reason, line) in Entries(root, "manifestPointers", "pointer")) {
      var pointer = new IgnoredPointer(value, reason);
      pointers.Add(Problem(pointer) is { } problem ? throw new RebuildIgnoreException(problem, line) : pointer);
    }

    return new(files, pointers);
  }

  private static YamlNode? Member(YamlMappingNode mapping, string key)
    => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

  private static int Line(YamlNode node) => (int)node.Start.Line;

  /// <summary>The entries of a list: each a mapping of exactly the value key and <c>reason</c>, both text.</summary>
  private static IEnumerable<(string Value, string Reason, int Line)> Entries(YamlMappingNode root, string list, string valueKey) {
    switch (Member(root, list)) {
      case null:
        yield break;
      case YamlSequenceNode sequence:
        foreach (var item in sequence) {
          if (item is not YamlMappingNode entry)
            throw new RebuildIgnoreException($"an entry of '{list}' must be a mapping with '{valueKey}' and 'reason'", Line(item));
          foreach (var key in entry.Children.Keys)
            if (key is not YamlScalarNode || (key.ToString() != valueKey && key.ToString() != "reason"))
              throw new RebuildIgnoreException($"unknown key '{key}' in an entry of '{list}'", Line(key));

          yield return (Text(entry, valueKey) ?? "", Text(entry, "reason") ?? "", Line(entry));
        }

        break;
      case var other:
        throw new RebuildIgnoreException($"'{list}' must be a list", Line(other));
    }
  }

  private static string? Text(YamlMappingNode entry, string key) => Member(entry, key) is YamlScalarNode { Value: { } value } ? value.Trim() : null;

  private static string? Problem(IgnoredFile file)
    => string.IsNullOrWhiteSpace(file.Glob) ? "an entry of 'files' has no 'glob'"
      : string.IsNullOrWhiteSpace(file.Reason) ? $"the file glob '{file.Glob}' has no 'reason'"
      : null;

  private static string? Problem(IgnoredPointer pointer)
    => string.IsNullOrWhiteSpace(pointer.Pointer) ? "an entry of 'manifestPointers' has no 'pointer'"
      : !IsPointer(pointer.Pointer) ? $"'{pointer.Pointer}' is not a JSON pointer below the root (RFC 6901: '/' before every token, '~' only as '~0' or '~1')"
      : string.IsNullOrWhiteSpace(pointer.Reason) ? $"the manifest pointer '{pointer.Pointer}' has no 'reason'"
      : null;

  /// <summary>Whether a text is an RFC 6901 pointer that names something below the root.</summary>
  public static bool IsPointer(string text) => text.StartsWith('/') && !Regex.IsMatch(text, "~(?![01])");

  /// <summary>The reference tokens of a pointer, unescaped as RFC 6901 §4 says: <c>~1</c> first, then <c>~0</c>.</summary>
  private static string[] Tokens(string pointer) => [.. pointer[1..].Split('/').Select(token => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];

  /// <summary>Removes the member a pointer names; a pointer that names nothing removes nothing.</summary>
  private static void Remove(JsonNode? root, string pointer) {
    var tokens = Tokens(pointer);
    var parent = root;
    foreach (var token in tokens[..^1])
      parent = Child(parent, token);

    switch (parent) {
      case JsonObject members:
        members.Remove(tokens[^1]);
        break;
      case JsonArray items when Index(tokens[^1], items.Count) is { } index:
        items.RemoveAt(index);
        break;
    }
  }

  private static JsonNode? Child(JsonNode? node, string token) => node switch {
    JsonObject members => members.TryGetPropertyValue(token, out var value) ? value : null,
    JsonArray items => Index(token, items.Count) is { } index ? items[index] : null,
    _ => null
  };

  /// <summary>An array index as RFC 6901 writes it: <c>0</c> or digits without a leading zero, within the array.</summary>
  private static int? Index(string token, int count)
    => Regex.IsMatch(token, "^(0|[1-9][0-9]{0,8})$") && int.Parse(token, System.Globalization.CultureInfo.InvariantCulture) is var index && index < count ? index : null;

  private static void Write(Utf8JsonWriter writer, JsonNode? node) {
    switch (node) {
      case JsonObject members:
        writer.WriteStartObject();
        foreach (var (name, value) in members.OrderBy(member => member.Key, StringComparer.Ordinal)) {
          writer.WritePropertyName(name);
          Write(writer, value);
        }

        writer.WriteEndObject();
        break;
      case JsonArray items:
        writer.WriteStartArray();
        foreach (var item in items)
          Write(writer, item);
        writer.WriteEndArray();
        break;
      case null:
        writer.WriteNullValue();
        break;
      default:
        node.WriteTo(writer);
        break;
    }
  }

  /// <summary>A glob as a regular expression over the whole path.</summary>
  private static Regex Glob(string glob) {
    var pattern = new StringBuilder("^");
    for (var index = 0; index < glob.Length; ++index) {
      var character = glob[index];
      if (character == '*' && index + 1 < glob.Length && glob[index + 1] == '*') {
        ++index;
        if (index + 1 < glob.Length && glob[index + 1] == '/') {
          ++index;
          pattern.Append("(?:.*/)?");
        } else {
          pattern.Append(".*");
        }
      } else {
        pattern.Append(character switch {
          '*' => "[^/]*",
          '?' => "[^/]",
          _ => Regex.Escape(character.ToString())
        });
      }
    }

    return new(pattern.Append('$').ToString(), RegexOptions.CultureInvariant);
  }

}

/// <summary>Thrown for a <c>rebuild-ignore.yaml</c> that is not schema 1 or has an entry without its reason; reported as <c>PLY0026</c>.</summary>
public sealed class RebuildIgnoreException : Exception {

  /// <summary>Creates the exception.</summary>
  /// <param name="message">What is wrong.</param>
  /// <param name="line">The line, 1-based; 0 when the problem has none.</param>
  /// <param name="inner">What caused it.</param>
  public RebuildIgnoreException(string message, int line, Exception? inner = null) : base(message, inner) => this.Line = line;

  /// <summary>The line, 1-based; 0 when the problem has none.</summary>
  public int Line { get; }

}
