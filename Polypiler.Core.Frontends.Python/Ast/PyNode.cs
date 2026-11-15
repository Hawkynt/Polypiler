using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Polypiler.Core.Frontends.Python.Ast;

/// <summary>
/// A node of the tree the helper printed: the class of the <c>ast</c> node, its range and its fields,
/// read from the JSON as they are asked for. The tree is never changed, and a field read twice gives
/// the same node objects, so that a node can be a key.
/// </summary>
internal sealed class PyNode {

  private readonly JsonElement _element;
  private readonly Dictionary<string, object?> _children = [];

  /// <summary>Wraps an object of the wire format.</summary>
  /// <param name="element">The JSON object; it carries the class under <c>_</c>.</param>
  public PyNode(JsonElement element) => this._element = element;

  /// <summary>The class of the node, for example <c>BinOp</c>.</summary>
  public string Kind => this._element.GetProperty("_").GetString()!;

  /// <summary>The 1-based line the node starts on; 1 for a node without a range, such as an operator.</summary>
  public int Line => this.Number("line");

  /// <summary>The 1-based column, in characters, the node starts at.</summary>
  public int Column => this.Number("col");

  /// <summary>The 1-based line the node ends on.</summary>
  public int EndLine => this.Number("endLine");

  /// <summary>The 1-based column just after the node's last character.</summary>
  public int EndColumn => this.Number("endCol");

  /// <summary>Whether the node is of a class.</summary>
  public bool Is(string kind) => this.Kind == kind;

  /// <summary>A field that holds one node, or <see langword="null"/> where it is missing or <c>None</c>.</summary>
  public PyNode? Node(string field) {
    if (!this._children.TryGetValue("node:" + field, out var child))
      this._children["node:" + field] = child = this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Object ? new PyNode(value) : null;

    return (PyNode?)child;
  }

  /// <summary>A field that holds a list of nodes; empty where it is missing.</summary>
  public IReadOnlyList<PyNode> Nodes(string field) {
    if (!this._children.TryGetValue("list:" + field, out var children))
      this._children["list:" + field] = children = this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => new PyNode(item)).ToList()
        : new List<PyNode>();

    return (IReadOnlyList<PyNode>)children!;
  }

  /// <summary>A field that holds a text, or <see langword="null"/>.</summary>
  public string? Text(string field)
    => this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

  /// <summary>The class of the operator a field holds, for example <c>Add</c> of a <c>BinOp</c>.</summary>
  public string Operator(string field = "op") => this.Node(field)!.Kind;

  /// <summary>A field that holds a list of texts, such as the names of a <c>global</c> statement.</summary>
  public IReadOnlyList<string> Texts(string field)
    => this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
      ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
      : [];

  private int Number(string field)
    => this._element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 1;

  /// <inheritdoc/>
  public override string ToString() => $"{this.Kind}({this.Line},{this.Column})";

}
