using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Frontends.Go.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Go.Lowering;

/// <summary>A construct outside the subset, at the node it was found at.</summary>
internal sealed class GoUnsupportedException(DiagnosticDescriptor descriptor, string message, GoNode? node) : Exception(message) {

  /// <summary>The diagnostic.</summary>
  public DiagnosticDescriptor Descriptor { get; } = descriptor;

  /// <summary>The line of the node, or 0.</summary>
  public int Line { get; } = node?.Line ?? 0;

  /// <summary>The column of the node, or 0.</summary>
  public int Column { get; } = node?.Column ?? 0;

}

/// <summary>Lowers the bound tree of one Go file into a module of the IR.</summary>
internal sealed partial class Lowerer(int languageMinor) {

  private readonly Dictionary<string, IrStructType> _structs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, string> _defined = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IrFunction> _signatures = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IrGlobal> _globals = new(StringComparer.Ordinal);
  private readonly List<IrFunction> _helpers = [];
  private readonly Dictionary<string, string> _helperNames = new(StringComparer.Ordinal);
  private readonly List<GoCaveat> _caveats = [];

  /// <summary>The known differences the module has, each once.</summary>
  public IReadOnlyList<GoCaveat> Caveats => this._caveats;

  private void Caveat(GoCaveat caveat) {
    if (!this._caveats.Contains(caveat))
      this._caveats.Add(caveat);
  }

  private static GoUnsupportedException Unsupported(GoNode? node, string what)
    => new(GoFrontend.UnsupportedDescriptor, $"'{what}' is not supported by the Go frontend", node);

  private static GoUnsupportedException UnsupportedType(GoNode? node, string type)
    => new(GoFrontend.UnsupportedTypeDescriptor, $"type '{type}' is not supported by the Go frontend", node);

  /// <summary>The imports the subset knows: the exact operations of <c>math</c> and <c>math/bits</c>.</summary>
  private static readonly HashSet<string> _imports = new(StringComparer.Ordinal) { "math", "math/bits" };

  /// <summary>The module of a file: its struct types, globals and functions, the helpers the lowering needed after them.</summary>
  public IrModule Module(GoNode file) {
    var package = file.Node("Name")?.Text("Name") ?? "main";
    var declarations = file.Nodes("Decls");
    foreach (var declaration in declarations.Where(declaration => declaration.Is("GenDecl"))) {
      switch (declaration.Text("Tok")) {
        case "import":
          foreach (var spec in declaration.Nodes("Specs")) {
            var imported = Strings.Unquote(spec.Node("Path")?.Text("Value") ?? "\"\"");
            if (!_imports.Contains(imported))
              throw Unsupported(spec, $"import \"{imported}\"");
          }

          break;
        case "type":
          foreach (var spec in declaration.Nodes("Specs"))
            this.TypeDeclaration(spec);

          break;
      }
    }

    foreach (var structure in this._pending)
      structure.Type.Define([.. structure.Fields.Select(field => new IrField(field.Name, this.TypeOf(field.Type, field.Node), false))]);

    // The signatures first, so that a call can name a function declared further down.
    var functions = declarations.Where(declaration => declaration.Is("FuncDecl")).ToList();
    foreach (var function in functions)
      this.Signature(function);

    foreach (var declaration in declarations.Where(declaration => declaration.Is("GenDecl") && declaration.Text("Tok") == "var"))
      foreach (var spec in declaration.Nodes("Specs"))
        this.Global(spec);

    var lowered = functions.Select(this.Function).ToList();
    return new IrModule(package, [.. lowered, .. this._helpers], []) {
      Types = [.. this._structs.Values],
      Globals = [.. this._globalOrder],
      Caveats = [.. this._caveats.Select(caveat => caveat.Id)]
    };
  }

  private readonly List<(IrStructType Type, List<(string Name, string Type, GoNode Node)> Fields)> _pending = [];
  private readonly List<IrGlobal> _globalOrder = [];

  /// <summary>A type declaration: a struct type becomes a struct of the IR; any other defined type stands for its underlying type.</summary>
  private void TypeDeclaration(GoNode spec) {
    var name = spec.Node("Name")?.Text("Name") ?? "";
    if (spec.Node("TypeParams") is not null)
      throw Unsupported(spec, $"generic type {name}");

    var type = spec.Node("Type")!;
    if (spec.Flag("alias"))
      throw Unsupported(spec, $"type alias {name}");

    if (type.Is("StructType")) {
      var fields = new List<(string, string, GoNode)>();
      foreach (var field in type.Node("Fields")?.Nodes("List") ?? []) {
        var names = field.Nodes("Names");
        if (names.Count == 0)
          throw Unsupported(field, "embedded field");

        foreach (var fieldName in names)
          fields.Add((fieldName.Text("Name")!, fieldName.Type, fieldName));
      }

      var structure = new IrStructType(name);
      this._structs[name] = structure;
      this._pending.Add((structure, fields));
      return;
    }

    this._defined[name] = spec.Node("Name")!.Underlying;
  }

  /// <summary>The IR type of a Go type, as <c>go/types</c> writes it; refused where the subset has none.</summary>
  private IrType TypeOf(string type, GoNode? node) => this.TryType(type) ?? throw UnsupportedType(node, type);

  private IrType? TryType(string type) {
    switch (type) {
      case "bool" or "untyped bool":
        return IrType.Bool;
      case "int8":
        return IrType.I8;
      case "uint8" or "byte":
        return IrType.U8;
      case "int16":
        return IrType.I16;
      case "uint16":
        return IrType.U16;
      case "int32" or "rune" or "untyped rune":
        return IrType.I32;
      case "uint32":
        return IrType.U32;
      case "int64" or "int" or "untyped int":
        return IrType.I64;
      case "uint64" or "uint":
        return IrType.U64;
      case "float32":
        return IrType.F32;
      case "float64" or "untyped float":
        return IrType.F64;
      case "string" or "untyped string":
        return IrType.String;
    }

    if (type.StartsWith("[]", StringComparison.Ordinal))
      return this.TryType(type[2..]) is { } element ? IrType.Array(element) : null;

    if (this._structs.TryGetValue(type, out var structure))
      return IrType.Struct(structure);

    return this._defined.TryGetValue(type, out var underlying) ? this.TryType(underlying) : null;
  }

  /// <summary>The type of an expression node.</summary>
  private IrType TypeOf(GoNode node) => this.TypeOf(node.Type, node);

  private void Signature(GoNode function) {
    var name = function.Node("Name")!.Text("Name")!;
    if (function.Node("Recv") is not null)
      throw Unsupported(function, $"method {name}");

    var type = function.Node("Type")!;
    if (type.Node("TypeParams") is not null)
      throw Unsupported(function, $"generic function {name}");

    var results = type.Node("Results")?.Nodes("List") ?? [];
    var resultTypes = results.SelectMany(field => field.Nodes("Names").Count == 0 ? [field.Node("Type")!] : field.Nodes("Names")).ToList();
    if (resultTypes.Count > 1)
      throw Unsupported(function, $"function {name} with {resultTypes.Count} results");

    if (results.Any(field => field.Nodes("Names").Count > 0))
      throw Unsupported(function, $"named result of function {name}");

    var returns = resultTypes.Count == 0 ? IrType.Void : this.TypeOf(resultTypes[0]);
    var parameters = new List<IrLocal>();
    foreach (var field in type.Node("Params")?.Nodes("List") ?? []) {
      if (field.Node("Type")!.Is("Ellipsis"))
        throw Unsupported(field, $"variadic parameter of function {name}");

      foreach (var parameter in field.Nodes("Names"))
        parameters.Add(new IrLocal(parameter.Text("Name")!, this.TypeOf(parameter)));

      if (field.Nodes("Names").Count == 0)
        parameters.Add(new IrLocal("_" + parameters.Count.ToString(CultureInfo.InvariantCulture), this.TypeOf(field.Node("Type")!)));
    }

    this._signatures[name] = new IrFunction(name, char.IsUpper(name[0]), returns, parameters, [], new IrBlock([]));
  }

  /// <summary>A variable of the package: a global with its initializer, which the IR requires to be static.</summary>
  private void Global(GoNode spec) {
    var names = spec.Nodes("Names");
    var values = spec.Nodes("Values");
    if (values.Count != 0 && values.Count != names.Count)
      throw Unsupported(spec, "a variable declaration with fewer values than names");

    this.BeginFunction(null);
    for (var index = 0; index < names.Count; ++index) {
      var name = names[index];
      var type = this.TypeOf(name);
      var initializer = values.Count == 0 ? this.ZeroOf(type, name) : this.Value(values[index], type);
      if (this._statements.Count > 0)
        throw Unsupported(spec, "a variable initializer that needs statements");

      var global = new IrGlobal(name.Text("Name")!, type, initializer);
      this._globals[name.Declaration] = global;
      this._globalOrder.Add(global);
    }
  }

  /// <summary>The zero value of a type: 0, false, an empty string, an empty array, the default struct.</summary>
  private IrExpr ZeroOf(IrType type, GoNode node) => type switch {
    { IsStruct: true } => new IrStructDefault(type),
    { IsArray: true } => new IrNewArray(type, new IrConst(IrType.I32, 0)),
    { IsString: true } => new IrStrConst(""), { IsNumeric: true } or { Kind: IrTypeKind.Bool } => new IrConst(type, 0),
    _ => throw UnsupportedType(node, type.ToString())
  };

}
