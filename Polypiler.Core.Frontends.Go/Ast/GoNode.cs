using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Polypiler.Core.Frontends.Go.Ast;

/// <summary>What the helper printed for a source: the tree, or why there is none.</summary>
/// <param name="File">The <c>File</c> node, or <see langword="null"/> when the source was refused.</param>
/// <param name="Go">The version of Go that checked it, such as <c>go1.27.4</c>; empty when unknown.</param>
/// <param name="Error">The refusal: the parser's or type checker's message, its kind and position.</param>
internal sealed record ParsedSource(GoNode? File, string Go, (string Kind, string Message, int Line, int Column)? Error);

/// <summary>Reads the JSON of the helper's wire format into the node model.</summary>
internal static class DumpReader {

  /// <summary>The version of the wire format this reader understands.</summary>
  public const int Format = 1;

  /// <summary>Reads one printed dump.</summary>
  /// <param name="json">The text the helper wrote.</param>
  /// <returns>The tree, or the error the dump reports, or a mismatch of the format.</returns>
  public static ParsedSource Read(string json) {
    JsonDocument document;
    try {
      document = JsonDocument.Parse(json);
    } catch (JsonException exception) {
      return new(null, "", ("helper", $"the Go helper wrote no tree: {exception.Message}", 1, 1));
    }

    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || format.GetInt32() != Format)
      return new(null, "", ("helper", $"the Go helper speaks another wire format than {Format}", 1, 1));

    var go = root.TryGetProperty("go", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString()! : "";
    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object) {
      var node = new GoNode(error);
      return new(null, go, (node.Text("kind") ?? "type", node.Text("msg") ?? "", (int)node.Number("line"), (int)node.Number("col")));
    }

    return root.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object
      ? new(new GoNode(file), go, null)
      : new(null, go, ("helper", "the Go helper wrote neither a tree nor an error", 1, 1));
  }

}

/// <summary>
/// A node of the bound tree the helper printed: the <c>go/ast</c> type of the node, its range, its
/// fields, and what <c>go/types</c> knows of it - the type, the mode, the exact value of a constant,
/// the object an identifier names - read from the JSON as they are asked for. The tree is never
/// changed, and a field read twice gives the same node objects, so that a node can be a key.
/// </summary>
internal sealed class GoNode {

  private readonly JsonElement _element;
  private readonly Dictionary<string, object?> _children = [];

  /// <summary>Wraps an object of the wire format.</summary>
  /// <param name="element">The JSON object; it carries the type under <c>_</c>.</param>
  public GoNode(JsonElement element) => this._element = element;

  /// <summary>The <c>go/ast</c> type of the node, for example <c>BinaryExpr</c>.</summary>
  public string Kind => this.Text("_") ?? "";

  /// <summary>The 1-based line the node starts on.</summary>
  public int Line => this.Position("pos").Line;

  /// <summary>The 1-based column, in bytes, the node starts at.</summary>
  public int Column => this.Position("pos").Column;

  /// <summary>Whether the node is of a type.</summary>
  public bool Is(string kind) => this.Kind == kind;

  /// <summary>The type <c>go/types</c> gives an expression, or the type of the object an identifier names; empty where none.</summary>
  public string Type => this.Text("type") ?? "";

  /// <summary>The underlying type of <see cref="Type"/>.</summary>
  public string Underlying => this.Text("underlying") ?? this.Type;

  /// <summary>The mode of an expression: <c>type</c>, <c>builtin</c>, <c>value</c>, <c>constant</c>, <c>void</c>, <c>novalue</c>; empty for no expression.</summary>
  public string Mode => this.Text("mode") ?? "";

  /// <summary>The exact value of a constant expression, as <c>constant.Value.ExactString</c> writes it, or <see langword="null"/>.</summary>
  public string? Constant => this.Text("const");

  /// <summary>The kind of the constant: <c>int</c>, <c>float</c>, <c>bool</c>, <c>string</c>, <c>complex</c>.</summary>
  public string? ConstantKind => this.Text("constKind");

  /// <summary>The kind of the object an identifier names: <c>var</c>, <c>const</c>, <c>func</c>, <c>type</c>, <c>builtin</c>, <c>pkg</c>, <c>nil</c>, <c>label</c>.</summary>
  public string? Object => this.Text("obj");

  /// <summary>The position of the declaration of the object an identifier names, as a key; empty where none.</summary>
  public string Declaration => this.Position("decl") is var (line, column) && line > 0 ? $"{line}:{column}" : "";

  /// <summary>The import path of the package an identifier names, or of the package its object belongs to.</summary>
  public string? Package => this.Text("pkg") ?? this.Text("from");

  /// <summary>A field that holds one node, or <see langword="null"/> where it is missing or <c>nil</c>.</summary>
  public GoNode? Node(string field) {
    if (!this._children.TryGetValue("node:" + field, out var child))
      this._children["node:" + field] = child = this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Object ? new GoNode(value) : null;

    return (GoNode?)child;
  }

  /// <summary>A field that holds a list of nodes; empty where it is missing.</summary>
  public IReadOnlyList<GoNode> Nodes(string field) {
    if (!this._children.TryGetValue("list:" + field, out var children))
      this._children["list:" + field] = children = this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => new GoNode(item)).ToList()
        : new List<GoNode>();

    return (IReadOnlyList<GoNode>)children!;
  }

  /// <summary>A field that holds a text, or <see langword="null"/>.</summary>
  public string? Text(string field)
    => this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

  /// <summary>Whether a field holds <c>true</c>.</summary>
  public bool Flag(string field) => this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;

  /// <summary>A field that holds a number; 0 where it is missing.</summary>
  public long Number(string field)
    => this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

  /// <summary>A field that holds an unsigned number, such as the bits of a float constant, or <see langword="null"/>.</summary>
  public ulong? Unsigned(string field)
    => this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : null;

  private (int Line, int Column) Position(string field) {
    if (!this._element.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2)
      return (1, 1);

    return (value[0].GetInt32(), value[1].GetInt32());
  }

  /// <inheritdoc/>
  public override string ToString() => $"{this.Kind}({this.Line},{this.Column})";

}
