using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Polypiler.Core.Frontends.Python.Ast;

/// <summary>The value of a complex constant, such as <c>2j</c>: CPython's <c>complex</c>, two binary64 parts.</summary>
/// <param name="Real">The real part.</param>
/// <param name="Imaginary">The imaginary part.</param>
internal readonly record struct PyComplex(double Real, double Imaginary);

/// <summary>The value of the constant <c>...</c>.</summary>
internal sealed class PyEllipsis {

  /// <summary>The one ellipsis.</summary>
  public static PyEllipsis Value { get; } = new();

  private PyEllipsis() {
  }

}

/// <summary>
/// A node of the tree of a Python source in the shape of CPython's <c>ast</c> module: the class of the
/// node (<c>BinOp</c>), its fields under their names in the order of <c>_fields</c>, and, for the
/// classes that have them, its range - as <c>lineno</c>, <c>col_offset</c>, <c>end_lineno</c> and
/// <c>end_col_offset</c> in bytes of UTF-8, and as 1-based lines and columns in characters. A node is
/// never changed; a field read twice gives the same node objects, so that a node can be a key.
/// </summary>
/// <remarks>
/// A field holds a node, a list of nodes (an entry may be <see langword="null"/>, as the keys of a
/// <c>Dict</c> are for <c>**</c>), a text (an identifier), a list of texts, a number (<c>level</c>,
/// <c>conversion</c>, <c>is_async</c>, <c>simple</c>), or for <c>Constant</c> and <c>MatchSingleton</c>
/// the value: <see langword="null"/> for <c>None</c>, <see cref="bool"/>, <see cref="BigInteger"/>,
/// <see cref="double"/>, <see cref="PyComplex"/>, <see cref="string"/>, a <see cref="byte"/> array or
/// <see cref="PyEllipsis"/>. A str with a surrogate code point also keeps its code points, which the
/// UTF-16 of a <see cref="string"/> cannot tell from a pair.
/// </remarks>
internal sealed class PyNode {

  private static readonly IReadOnlyList<PyNode> _noNodes = [];

  private readonly (string Name, object? Value)[] _fields;

  /// <summary>Makes a node.</summary>
  /// <param name="kind">The class, such as <c>BinOp</c>.</param>
  /// <param name="range">The range, or <see langword="null"/> for a class without one.</param>
  /// <param name="fields">The fields in the order of the class's <c>_fields</c>.</param>
  public PyNode(string kind, PyRange? range, (string Name, object? Value)[] fields) {
    this.Kind = kind;
    this.Range = range;
    this._fields = fields;
  }

  /// <summary>The class of the node, for example <c>BinOp</c>.</summary>
  public string Kind { get; }

  /// <summary>The range, or <see langword="null"/> for a node of a class without one, such as <c>comprehension</c>.</summary>
  public PyRange? Range { get; }

  /// <summary>The fields in the order of <c>_fields</c>.</summary>
  public IReadOnlyList<(string Name, object? Value)> Fields => this._fields;

  /// <summary>The 1-based line the node starts on; 1 for a node without a range, such as an operator.</summary>
  public int Line => this.Range?.Line ?? 1;

  /// <summary>The 1-based column, in characters, the node starts at.</summary>
  public int Column => this.Range?.Column ?? 1;

  /// <summary>The 1-based line the node ends on.</summary>
  public int EndLine => this.Range?.EndLine ?? 1;

  /// <summary>The 1-based column just after the node's last character.</summary>
  public int EndColumn => this.Range?.EndColumn ?? 1;

  /// <summary>Whether the node is of a class.</summary>
  public bool Is(string kind) => this.Kind == kind;

  /// <summary>The value of a field, or <see langword="null"/> where the node has no such field.</summary>
  public object? Get(string field) {
    foreach (var (name, value) in this._fields)
      if (name == field)
        return value;

    return null;
  }

  /// <summary>The same node with a field replaced.</summary>
  public PyNode With(string field, object? value)
    => new(this.Kind, this.Range, [.. this._fields.Select(entry => entry.Name == field ? (entry.Name, value) : entry)]);

  /// <summary>A field that holds one node, or <see langword="null"/> where it is missing or <c>None</c>.</summary>
  public PyNode? Node(string field) => this.Get(field) as PyNode;

  /// <summary>A field that holds a list of nodes, without its <c>None</c> entries; empty where it is missing.</summary>
  public IReadOnlyList<PyNode> Nodes(string field)
    => this.Get(field) is IReadOnlyList<PyNode?> list ? list.All(item => item is not null) ? (IReadOnlyList<PyNode>)list : [.. list.OfType<PyNode>()] : _noNodes;

  /// <summary>The node at a position of a field that holds a list, or <see langword="null"/> where the position holds <c>None</c> or is missing.</summary>
  public PyNode? Item(string field, int index)
    => this.Get(field) is IReadOnlyList<PyNode?> list && index < list.Count ? list[index] : null;

  /// <summary>A field that holds a text, or <see langword="null"/>.</summary>
  public string? Text(string field) => this.Get(field) as string;

  /// <summary>The code points of a str constant that holds a surrogate; empty for any other.</summary>
  public IReadOnlyList<int> CodePoints => this.Get("value") is string text && this.Kind is "Constant" && text.Any(char.IsSurrogate)
    ? this.Get("#codePoints") as IReadOnlyList<int> ?? [] : [];

  /// <summary>Whether a str constant holds a lone high surrogate right before a lone low one, which UTF-16 cannot keep apart from a pair.</summary>
  public bool SurrogatePair => this.Get("#surrogatePair") is true;

  /// <summary>The value of a <c>Constant</c> or <c>MatchSingleton</c>.</summary>
  public object? Value => this.Get("value");

  /// <summary>
  /// The type of the value of a <c>Constant</c> or <c>MatchSingleton</c> by Python's name: <c>int</c>,
  /// <c>float</c>, <c>complex</c>, <c>bool</c>, <c>none</c>, <c>str</c>, <c>bytes</c> or <c>ellipsis</c>.
  /// </summary>
  public string ConstantKind => this.Value switch {
    null => "none",
    bool => "bool",
    BigInteger => "int",
    double => "float",
    PyComplex => "complex",
    string => "str",
    byte[] => "bytes",
    _ => "ellipsis"
  };

  /// <summary>The class of the operator a field holds, for example <c>Add</c> of a <c>BinOp</c>.</summary>
  public string Operator(string field = "op") => this.Node(field)!.Kind;

  /// <summary>A field that holds a list of texts, such as the names of a <c>global</c> statement.</summary>
  public IReadOnlyList<string> Texts(string field) => this.Get(field) as IReadOnlyList<string> ?? [];

  /// <summary>A field that holds a number, such as the level of a relative import; 1 where it is missing.</summary>
  public int Number(string field) => this.Get(field) is int number ? number : 1;

}

/// <summary>The range of a node: CPython's attributes in bytes, and the 1-based columns in characters.</summary>
/// <param name="Line">The line it starts on (<c>lineno</c>).</param>
/// <param name="Offset">The byte offset it starts at (<c>col_offset</c>).</param>
/// <param name="EndLine">The line it ends on (<c>end_lineno</c>).</param>
/// <param name="EndOffset">The byte offset just after it (<c>end_col_offset</c>).</param>
/// <param name="Column">The 1-based character column it starts at.</param>
/// <param name="EndColumn">The 1-based character column just after it.</param>
internal sealed record PyRange(int Line, int Offset, int EndLine, int EndOffset, int Column, int EndColumn);
