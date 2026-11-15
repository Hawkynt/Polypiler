using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;

namespace Polypiler.Core.Frontends.Python.Typing;

/// <summary>
/// The type a type hint names: <c>int</c>, <c>float</c>, <c>bool</c>, <c>str</c>, <c>bytes</c>,
/// <c>bytearray</c>, <c>None</c>, <c>list[T]</c>, <c>tuple[A, B]</c>, <c>Optional[T]</c> and
/// <c>T | None</c>, <c>Iterator[T]</c> and its kin for a generator, and a class or enum of the module.
/// The names of <c>typing</c> count only where an import binds them.
/// </summary>
internal static class TypeHints {

  /// <summary>The names a hint gives the supported types, for the message of an unsupported one.</summary>
  public const string Supported = "int, float, bool, str, bytes, bytearray, list[T], tuple[…], T | None and the classes of the module";

  /// <summary>The type a hint names, or <see langword="null"/> for a hint the import does not know.</summary>
  public static PyType? Of(PyNode hint, ModuleModel module) {
    switch (hint.Kind) {
      case "Name":
        return Named(hint.Text("id")!, module);
      case "Constant" when hint.Text("kind") == "none":
        return PyType.None;
      case "Constant" when hint.Text("kind") == "str":
        return new HintText(hint.Text("value")!, module).Read();
      case "Attribute" when hint.Node("value") is { Kind: "Name" } owner && module.Imports.TryGetValue(owner.Text("id")!, out var binding)
          && binding.User is { } user && binding.Name is null:
        return user.Classes.TryGetValue(hint.Text("attr")!, out var imported) ? Class(imported) : null;
      case "BinOp" when hint.Operator() == "BitOr":
        return Union(Of(hint.Node("left")!, module), Of(hint.Node("right")!, module));
      case "Subscript":
        return Generic(hint, module);
      default:
        return null;
    }
  }

  /// <summary>
  /// A hint written as a string, a forward reference such as <c>'Optional[Node]'</c>: names, <c>None</c>,
  /// subscripts and <c>|</c>, read as the same hint written as code.
  /// </summary>
  private sealed class HintText(string text, ModuleModel module) {

    private int _at;

    public PyType? Read() {
      var type = this.Union();
      this.Blank();
      return this._at == text.Length ? type : null;
    }

    private void Blank() {
      while (this._at < text.Length && char.IsWhiteSpace(text[this._at]))
        ++this._at;
    }

    private bool Take(char character) {
      this.Blank();
      if (this._at < text.Length && text[this._at] == character) {
        ++this._at;
        return true;
      }

      return false;
    }

    private PyType? Union() {
      var type = this.Term();
      while (type is not null && this.Take('|'))
        type = TypeHints.Union(type, this.Term());

      return type;
    }

    private PyType? Term() {
      this.Blank();
      var start = this._at;
      while (this._at < text.Length && (char.IsLetterOrDigit(text[this._at]) || text[this._at] is '_' or '.'))
        ++this._at;

      var name = text[start..this._at];
      if (name.Length == 0)
        return null;

      if (!this.Take('['))
        return Named(name, module);

      var arguments = new List<PyType?>();
      do {
        arguments.Add(this.Union());
      } while (this.Take(','));

      return this.Take(']') ? Generic(name.Split('.')[^1], arguments) : null;
    }

  }

  private static PyType? Named(string name, ModuleModel module) {
    if (module.Classes.TryGetValue(name, out var type))
      return Class(type);

    if (module.Imports.TryGetValue(name, out var binding) && binding.User is { } user && binding.Name is { } imported && user.Classes.TryGetValue(imported, out var declared))
      return Class(declared);

    return name switch {
      "int" => PyType.Int,
      "float" => PyType.Float,
      "bool" => PyType.Bool,
      "str" => PyType.Str,
      "bytes" => PyType.Bytes,
      "bytearray" => PyType.ByteArray,
      "None" => PyType.None,
      _ when Classes.IsBuiltinException(name) => PyType.Object(Classes.Builtin(name)),
      _ => null
    };
  }

  private static PyType Class(ClassModel type) => type.IsEnum ? PyType.Enum(type) : PyType.Object(type);

  private static PyType? Union(PyType? left, PyType? right) {
    if (left is null || right is null)
      return null;

    if (left.Kind == PyKind.None && PyTypes.Optionable(right))
      return PyType.Optional(right);

    return right.Kind == PyKind.None && PyTypes.Optionable(left) ? PyType.Optional(left) : null;
  }

  private static PyType? Generic(PyNode hint, ModuleModel module) {
    var head = hint.Node("value")!;
    var index = hint.Node("slice")!;
    var arguments = index.Is("Tuple") ? index.Nodes("elts") : [index];
    var name = head.Is("Name") && !module.Imports.ContainsKey(head.Text("id")!) ? head.Text("id") : new ClassCollector(module, []).Library(head, "typing") ?? new ClassCollector(module, []).Library(head, "collections.abc");
    var types = arguments.Select(argument => argument.Is("Constant") && argument.Text("kind") == "ellipsis" ? null : Of(argument, module)).ToList();
    return Generic(name, types);
  }

  /// <summary>The type a generic hint names, by its name and the types of its arguments.</summary>
  private static PyType? Generic(string? name, IReadOnlyList<PyType?> types) {
    switch (name) {
      case "list" or "List" when types is [{ } element]:
        return PyType.List(element);
      case "tuple" or "Tuple" when types.Count > 0 && types.All(item => item is not null && item.Kind != PyKind.None):
        return PyType.Tuple([.. types!]);
      case "Optional" when types is [{ } underlying] && PyTypes.Optionable(underlying):
        return PyType.Optional(underlying);
      case "Union" when types.Count == 2:
        return Union(types[0], types[1]);
      case "Iterator" or "Iterable" or "Generator" when types.Count >= 1 && types[0] is { } element:
        return PyType.Iterator(element);
      default:
        return null;
    }
  }

}
