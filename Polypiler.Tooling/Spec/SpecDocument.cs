using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec;

/// <summary>
/// One file under <c>spec/&lt;family&gt;/</c>, read by <see cref="YamlToJson"/>: the whole document as
/// JSON, its metadata block and its family body, and the source position of every value.
/// </summary>
public sealed class SpecDocument {

  private readonly IReadOnlyDictionary<string, (int Line, int Column)> _positions;

  private SpecDocument(string path, JsonNode? root, IReadOnlyDictionary<string, (int Line, int Column)> positions) {
    this.Path = path;
    this.Family = path[..path.IndexOf('/', StringComparison.Ordinal)];
    this.Root = root;
    this._positions = positions;
    this.Meta = SpecMeta.From(root);
  }

  /// <summary>The path relative to the spec root, with <c>/</c> between the parts: <c>ir/operations/shl.yaml</c>.</summary>
  public string Path { get; }

  /// <summary>The family folder, the first part of <see cref="Path"/>.</summary>
  public string Family { get; }

  /// <summary>The whole document; <see langword="null"/> for an empty file.</summary>
  public JsonNode? Root { get; }

  /// <summary>
  /// The metadata block, or <see langword="null"/> while a key is missing or of the wrong type. A
  /// document a family is given always has one.
  /// </summary>
  public SpecMeta? Meta { get; }

  /// <summary>The family body, the value of <c>spec</c>; <see langword="null"/> when there is none.</summary>
  public JsonNode? Body => (this.Root as JsonObject)?["spec"];

  /// <summary>The value of <c>id</c> when it is a string, else <see langword="null"/>; set even where <see cref="Meta"/> is not.</summary>
  public string? Id => (this.Root as JsonObject)?["id"] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;

  /// <summary>Reads a document.</summary>
  /// <param name="path">The path relative to the spec root, <c>&lt;family&gt;/…</c>, with <c>/</c> between the parts.</param>
  /// <param name="text">The YAML text.</param>
  /// <returns>The document.</returns>
  /// <exception cref="ArgumentException"><paramref name="path"/> names no family folder.</exception>
  /// <exception cref="YamlToJsonException">The text is not a valid spec YAML document.</exception>
  public static SpecDocument Parse(string path, string text) {
    ArgumentNullException.ThrowIfNull(path);
    if (path.IndexOf('/', StringComparison.Ordinal) < 1)
      throw new ArgumentException($"'{path}' is not inside a family folder", nameof(path));

    var root = YamlToJson.Read(text, out var positions);
    return new(path, root, positions);
  }

  /// <summary>
  /// Where the value at a JSON pointer starts; a pointer to a value the document lacks gives the
  /// position of its nearest ancestor that exists, and an empty document gives line 1, column 1.
  /// </summary>
  /// <param name="pointer">The JSON pointer, <c>""</c> for the whole document, <c>/spec/parts/0</c> for a value of the body.</param>
  /// <returns>The 1-based line and column.</returns>
  public (int Line, int Column) PositionOf(string pointer) {
    ArgumentNullException.ThrowIfNull(pointer);
    for (var candidate = pointer; ; candidate = candidate[..int.Max(candidate.LastIndexOf('/'), 0)]) {
      if (this._positions.TryGetValue(candidate, out var position))
        return position;
      if (candidate.Length == 0)
        return (1, 1);
    }
  }

  /// <summary>The source range of the value at a JSON pointer, as <see cref="PositionOf"/> finds it.</summary>
  /// <param name="pointer">The JSON pointer.</param>
  /// <returns>The range in <see cref="Path"/>.</returns>
  public SourceRange RangeOf(string pointer) {
    var (line, column) = this.PositionOf(pointer);
    return new(this.Path, line, column, line, column);
  }

}
