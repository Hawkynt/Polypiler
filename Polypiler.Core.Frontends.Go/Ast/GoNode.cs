using System.Collections.Generic;
using Polypiler.Core.Frontends.Go.Checking;

namespace Polypiler.Core.Frontends.Go.Ast;

/// <summary>
/// A node of the syntax tree of a Go file: its kind - the name of the <c>go/ast</c> type it stands for,
/// such as <c>BinaryExpr</c> - its position, its fields under the names <c>go/ast</c> gives them, and
/// what the type checker knows of it: the type and mode of an expression, the exact value of a
/// constant, the object an identifier names. The parser sets the fields, the checker the rest.
/// </summary>
internal sealed class GoNode(string kind, int offset, int line, int column) {

  private readonly Dictionary<string, object?> _fields = [];

  /// <summary>The <c>go/ast</c> type of the node, for example <c>BinaryExpr</c>.</summary>
  public string Kind { get; } = kind;

  /// <summary>The byte offset of the node in the source.</summary>
  public int Offset { get; } = offset;

  /// <summary>The 1-based line the node starts on.</summary>
  public int Line { get; } = line;

  /// <summary>The 1-based column, in bytes, the node starts at.</summary>
  public int Column { get; } = column;

  /// <summary>Whether the node is of a type.</summary>
  public bool Is(string kind) => this.Kind == kind;

  /// <summary>A field that holds one node, or <see langword="null"/> where it is missing.</summary>
  public GoNode? Node(string field) => this._fields.GetValueOrDefault(field) as GoNode;

  /// <summary>A field that holds a list of nodes; empty where it is missing.</summary>
  public IReadOnlyList<GoNode> Nodes(string field) => this._fields.GetValueOrDefault(field) as IReadOnlyList<GoNode> ?? [];

  /// <summary>A field that holds a text, or <see langword="null"/>.</summary>
  public string? Text(string field) => this._fields.GetValueOrDefault(field) as string;

  /// <summary>Whether a field holds <see langword="true"/>.</summary>
  public bool Flag(string field) => this._fields.GetValueOrDefault(field) is true;

  /// <summary>Sets a field: a node, a list of nodes, a text or a flag.</summary>
  public GoNode With(string field, object? value) {
    this._fields[field] = value;
    return this;
  }

  /// <summary>The type the checker gives an expression, or the type of the object an identifier names; <see langword="null"/> where none.</summary>
  public GoType? TypeOfNode { get; set; }

  /// <summary>The type as <c>go/types</c> writes it; empty where none.</summary>
  public string Type => this.TypeOfNode?.ToString() ?? "";

  /// <summary>The underlying type of <see cref="Type"/>, written the same way.</summary>
  public string Underlying => this.TypeOfNode?.Underlying.ToString() ?? "";

  /// <summary>The mode of an expression: <c>type</c>, <c>builtin</c>, <c>value</c>, <c>constant</c>, <c>void</c>, <c>novalue</c>; empty for no expression.</summary>
  public string Mode { get; set; } = "";

  /// <summary>The exact value of a constant expression, or <see langword="null"/>.</summary>
  public GoConstant? Value { get; set; }

  /// <summary>The exact value of a constant expression as <c>constant.Value.ExactString</c> writes it, or <see langword="null"/>.</summary>
  public string? Constant => this.Value?.ExactString;

  /// <summary>The object an identifier names, or <see langword="null"/>.</summary>
  public GoObject? Target { get; set; }

  /// <summary>The kind of the object an identifier names: <c>var</c>, <c>const</c>, <c>func</c>, <c>type</c>, <c>builtin</c>, <c>pkg</c>, <c>nil</c>, <c>label</c>.</summary>
  public string? Object => this.Target?.KindName;

  /// <summary>The position of the declaration of the object an identifier names, as a key; empty where none or outside the file.</summary>
  public string Declaration => this.Target is { Line: > 0 } target ? $"{target.Line}:{target.Column}" : "";

  /// <summary>The import path of the package an identifier names, or of the package its object belongs to.</summary>
  public string? Package => this.Target?.Package;

  /// <inheritdoc/>
  public override string ToString() => $"{this.Kind}({this.Line},{this.Column})";

}
