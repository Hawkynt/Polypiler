using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Go.Ast;
using Polypiler.Core.Frontends.Go.Lowering;
using Polypiler.Core.Frontends.Go.Syntax;

namespace Polypiler.Core.Frontends.Go.Checking;

/// <summary>An error of Go's type system at a node: the source is no valid Go.</summary>
internal sealed class GoCheckException(GoNode? node, string message) : Exception(message) {

  /// <summary>The node the error is at.</summary>
  public GoNode? Node { get; } = node;

}

/// <summary>The modes of an operand, as <c>go/types</c> has them.</summary>
internal enum GoMode {
  NoValue,
  Builtin,
  TypeExpr,
  Constant,
  Variable,
  MapIndex,
  Value,
  CommaOk
}

/// <summary>An operand: what an expression is - its mode, its type, its value - while it is checked.</summary>
internal sealed class GoOperand(GoMode mode, GoType type, GoNode expr, GoConstant? value = null) {

  /// <summary>The mode.</summary>
  public GoMode Mode { get; set; } = mode;

  /// <summary>The type.</summary>
  public GoType Type { get; set; } = type;

  /// <summary>The value of a constant.</summary>
  public GoConstant? Value { get; set; } = value;

  /// <summary>The expression the operand is of.</summary>
  public GoNode Expr { get; set; } = expr;

  /// <summary>The built-in a <see cref="GoMode.Builtin"/> operand names.</summary>
  public string? Builtin { get; init; }

  /// <summary>Whether the operand is <c>nil</c>.</summary>
  public bool IsNil => this.Mode == GoMode.Value && this.Type is GoBasic { Kind: GoBasicKind.UntypedNil };

  /// <summary>The operand as a message names it, for example <c>x (variable of type int32)</c>.</summary>
  public override string ToString() {
    var text = GoNodeText.Of(this.Expr);
    var untyped = GoTypes.IsUntyped(this.Type);
    var what = this.Mode switch {
      GoMode.Builtin => "built-in",
      GoMode.TypeExpr => "type",
      GoMode.Constant when untyped => $"{this.Type} constant",
      GoMode.Constant => "constant",
      GoMode.Variable => "variable",
      GoMode.MapIndex => "map index expression",
      _ when this.IsNil => "untyped nil",
      _ when untyped => $"{this.Type} value",
      _ => "value"
    };
    if (this.Mode == GoMode.Constant && this.Value is { } value && value.ToString() != text)
      what += " " + value;

    if (!untyped && this.Mode is not GoMode.NoValue and not GoMode.Builtin and not GoMode.TypeExpr)
      what += " of type " + this.Type;

    return this.Mode == GoMode.TypeExpr ? $"{text} (type)" : this.IsNil ? "nil" : $"{text} ({what})";
  }

}

/// <summary>
/// The type checker of Go for one package file (The Go Programming Language Specification), its rules
/// derived from the specification and from <c>go/types</c> of the Go distribution (BSD-3-Clause, see
/// <c>THIRD-PARTY-NOTICE.Go.txt</c>): scopes and the resolution of every name, the lazy checking of
/// package-level declarations in dependency order with their cycles, untyped constants computed exactly
/// and given their type by their context, representability and overflow, assignability and
/// convertibility, the operators, calls, the built-ins, composite literals, every statement, the
/// terminating statements, labels, unused variables and imports, and the order of initialization. It
/// records on every node what the lowering needs: the type, the mode, the exact value, the object.
/// A construct it does not model - generics, channels, interfaces with embedded elements, type
/// assertions, imports other than <c>math</c> and <c>math/bits</c> - is refused with
/// <see cref="GoUnsupportedException"/>, so that a verdict it gives is always Go's.
/// </summary>
internal sealed partial class GoChecker {

  private static readonly Lazy<GoScope> _math = new(() => Library(GoLibrary.Math, "math"));
  private static readonly Lazy<GoScope> _bits = new(() => Library(GoLibrary.Bits, "math/bits"));

  private readonly int _minor;
  private readonly string? _libraryPath;
  private readonly GoScope _universe;
  private readonly GoScope _package;
  private readonly GoScope _file;
  private readonly Dictionary<GoNode, Untyped> _untyped = new(ReferenceEqualityComparer.Instance);
  private readonly List<GoObject> _imports = [];
  private readonly List<GoObject> _globals = [];
  private readonly List<Action> _later = [];
  private GoScope _scope;
  private GoConstant? _iota;
  private GoObject? _declaration;
  private FunctionContext? _function;

  /// <summary>What the checker knows of an untyped expression until its type is final.</summary>
  private sealed class Untyped(GoMode mode, GoType type, GoConstant? value) {

    public bool IsLhs { get; set; }

    public GoMode Mode { get; } = mode;

    public GoType Type { get; set; } = type;

    public GoConstant? Value { get; set; } = value;

  }

  private GoChecker(int minor, string? libraryPath) {
    this._minor = minor;
    this._libraryPath = libraryPath;
    this._universe = Universe();
    this._package = new GoScope(this._universe, "package");
    this._file = new GoScope(this._package, "file");
    this._scope = this._file;
  }

  /// <summary>The name of the package.</summary>
  public string PackageName { get; private set; } = "";

  /// <summary>The constants declared at the package level, in the order of their declaration.</summary>
  public IReadOnlyList<GoObject> Constants => [.. this._globals.Where(@object => @object.Kind == GoObjectKind.Const)];

  /// <summary>Type-checks a file.</summary>
  /// <param name="file">The <c>File</c> node the parser built.</param>
  /// <param name="minor">The minor version of Go 1 the source is written for.</param>
  /// <returns>The checker, with what it found.</returns>
  /// <exception cref="GoCheckException">The source is no valid Go.</exception>
  /// <exception cref="GoUnsupportedException">The source uses a construct the checker does not model.</exception>
  public static GoChecker Check(GoNode file, int minor) {
    var checker = new GoChecker(minor, null);
    checker.CheckFile(file);
    return checker;
  }

  private static GoScope Library(string source, string path) {
    var checker = new GoChecker(int.MaxValue, path);
    checker.CheckFile(GoParser.ParseFile(new GoSource(source)));
    return checker._package;
  }

  private bool IsLibrary => this._libraryPath is not null;

  private static GoCheckException Error(GoNode? node, string message) => new(node, message);

  private static GoUnsupportedException Unsupported(GoNode? node, string what)
    => new(GoFrontend.UnsupportedDescriptor, $"'{what}' is not supported by the Go frontend", node);

  // ---------------------------------------------------------------- universe

  private GoScope Universe() {
    var universe = new GoScope(null, "universe");
    foreach (var kind in Enum.GetValues<GoBasicKind>().Where(kind => kind < GoBasicKind.UntypedBool))
      universe.Insert(new GoObject(GoObjectKind.TypeName, GoTypes.Basic(kind).Name, GoTypes.Basic(kind)));

    universe.Insert(new GoObject(GoObjectKind.TypeName, "byte", GoTypes.Byte));
    universe.Insert(new GoObject(GoObjectKind.TypeName, "rune", GoTypes.Rune));
    universe.Insert(new GoObject(GoObjectKind.TypeName, "any", GoTypes.Any));
    var error = new GoNamed("error", null);
    error.Resolve(new GoInterface([new GoMethod("Error", new GoSignature([], [new GoParameter("", GoTypes.String)], false))]));
    universe.Insert(new GoObject(GoObjectKind.TypeName, "error", error));
    universe.Insert(new GoObject(GoObjectKind.TypeName, "comparable", null));
    universe.Insert(new GoObject(GoObjectKind.Const, "true", GoTypes.UntypedBool) { Value = GoConstant.MakeBool(true) });
    universe.Insert(new GoObject(GoObjectKind.Const, "false", GoTypes.UntypedBool) { Value = GoConstant.MakeBool(false) });
    universe.Insert(new GoObject(GoObjectKind.Const, "iota", GoTypes.Basic(GoBasicKind.UntypedInt)));
    universe.Insert(new GoObject(GoObjectKind.Nil, "nil", GoTypes.UntypedNil));
    foreach (var builtin in new[] { "append", "cap", "clear", "close", "complex", "copy", "delete", "imag", "len", "make", "max", "min", "new", "panic", "print", "println", "real", "recover" })
      universe.Insert(new GoObject(GoObjectKind.Builtin, builtin, null));

    return universe;
  }

  // ---------------------------------------------------------------- the file

  private void CheckFile(GoNode file) {
    var name = file.Node("Name")!;
    this.PackageName = name.Text("Name")!;
    if (this.PackageName == "_")
      throw Error(name, "invalid package name _");

    var functions = new List<(GoNode Declaration, GoObject? Object)>();
    var methods = new List<GoNode>();
    foreach (var declaration in file.Nodes("Decls")) {
      if (declaration.Is("FuncDecl")) {
        this.CollectFunction(declaration, functions, methods);
        continue;
      }

      switch (declaration.Text("Tok")) {
        case "import":
          foreach (var spec in declaration.Nodes("Specs"))
            this.CollectImport(spec);

          break;
        case "const":
          this.CollectConstants(declaration);
          break;
        case "var":
          foreach (var spec in declaration.Nodes("Specs"))
            this.CollectVariables(spec);

          break;
        default:
          foreach (var spec in declaration.Nodes("Specs"))
            this.CollectType(spec);

          break;
      }
    }

    // An import's name must not be declared in the package as well.
    foreach (var import in this._imports)
      if (this._package.Local(import.Name) is { } clash)
        throw Error(clash.Declaration, $"{import.Name} already declared through import of package {import.Package}");

    foreach (var type in this._globals.Where(@object => @object.Kind == GoObjectKind.TypeName).ToList())
      this.Resolve(type);

    foreach (var method in methods)
      this.CollectMethod(method);

    foreach (var @object in this._globals.ToList())
      this.Resolve(@object);

    this.RunLater();
    foreach (var (declaration, @object) in functions)
      this.FunctionBody(declaration, @object);

    this.RunLater();
    this.InitializationCycles();
    if (!this.IsLibrary)
      foreach (var import in this._imports.Where(import => !import.Used))
        throw Error(import.Declaration, import.Name == LastElement(import.Package!)
          ? $"\"{import.Package}\" imported and not used"
          : $"\"{import.Package}\" imported as {import.Name} and not used");
  }

  private void RunLater() {
    while (this._later.Count > 0) {
      var work = this._later[0];
      this._later.RemoveAt(0);
      work();
    }
  }

  private static string LastElement(string path) => path[(path.LastIndexOf('/') + 1)..];

  private GoObject Declare(GoScope scope, GoNode identifier, GoObjectKind kind, GoType? type, Action<GoObject>? configure = null) {
    var name = identifier.Text("Name")!;
    var @object = new GoObject(kind, name, type, this.IsLibrary ? 0 : identifier.Line, this.IsLibrary ? 0 : identifier.Column) {
      Package = this._libraryPath,
      Declaration = identifier,
      IsGlobal = scope == this._package
    };
    configure?.Invoke(@object);
    identifier.Target = @object;
    identifier.TypeOfNode = type;
    if (name == "_")
      return @object;

    if (scope == this._package && kind != GoObjectKind.Func && (name == "init" || name == "main" && this.PackageName == "main"))
      throw Error(identifier, $"cannot declare {name} - must be func");

    if (scope.Insert(@object) is { } existing)
      throw Error(identifier, existing.Line > 0 ? $"{name} redeclared in this block\n\tother declaration of {name} at line {existing.Line}" : $"{name} redeclared in this block");

    return @object;
  }

  private void CollectImport(GoNode spec) {
    var pathNode = spec.Node("Path")!;
    var path = System.Text.Encoding.UTF8.GetString(GoConstant.Unquote(pathNode.Text("Value")!));
    if (path.Length == 0 || path.Any(character => character < '!' || "!\"#$%&'()*,:;<=>?[\\]^`{|}�".Contains(character)))
      throw Error(pathNode, $"invalid import path: {pathNode.Text("Value")}");

    var members = path switch {
      "math" => _math.Value,
      "math/bits" => _bits.Value,
      _ => throw Unsupported(spec, $"import \"{path}\"")
    };
    var nameNode = spec.Node("Name");
    var name = nameNode?.Text("Name") ?? LastElement(path);
    if (name == ".")
      throw Unsupported(spec, "dot import");

    if (name == "_")
      return;

    var identifier = nameNode ?? pathNode;
    var @object = new GoObject(GoObjectKind.PackageName, name, null, identifier.Line, identifier.Column) { Package = path, Members = members, Declaration = spec };
    if (nameNode is not null)
      nameNode.Target = @object;

    if (this._file.Insert(@object) is not null)
      throw Error(identifier, $"{name} redeclared in this block");

    this._imports.Add(@object);
  }

  private void CollectConstants(GoNode declaration) {
    GoNode? type = null;
    IReadOnlyList<GoNode> values = [];
    foreach (var spec in declaration.Nodes("Specs")) {
      if (spec.Node("Type") is not null || spec.Nodes("Values").Count > 0) {
        type = spec.Node("Type");
        values = spec.Nodes("Values");
      }

      var names = spec.Nodes("Names");
      CheckConstantArity(names, values);
      var iota = GoConstant.MakeInt(int.Parse(spec.Text("iota")!, System.Globalization.CultureInfo.InvariantCulture));
      for (var index = 0; index < names.Count; ++index) {
        var (typeNode, valueNode) = (type, index < values.Count ? values[index] : null);
        var @object = this.Declare(this._package, names[index], GoObjectKind.Const, null, item => item.Resolution = GoResolution.Unresolved);
        this._constantSpecs[@object] = (typeNode, valueNode, iota);
        this._globals.Add(@object);
      }
    }
  }

  private readonly Dictionary<GoObject, (GoNode? Type, GoNode? Value, GoConstant Iota)> _constantSpecs = new(ReferenceEqualityComparer.Instance);

  private static void CheckConstantArity(IReadOnlyList<GoNode> names, IReadOnlyList<GoNode> values) {
    if (names.Count > values.Count)
      throw Error(names[values.Count], "missing init expr for const declaration");

    if (names.Count < values.Count)
      throw Error(values[names.Count], "extra init expr");
  }

  private void CollectVariables(GoNode spec) {
    var names = spec.Nodes("Names");
    var values = spec.Nodes("Values");
    if (values.Count > 1 && values.Count != names.Count)
      throw Error(spec, $"assignment mismatch: {Count(names.Count, "variable")} but {Count(values.Count, "value")}");

    for (var index = 0; index < names.Count; ++index) {
      var @object = this.Declare(this._package, names[index], GoObjectKind.Var, null, item => item.Resolution = GoResolution.Unresolved);
      this._variableSpecs[@object] = (spec, index);
      this._globals.Add(@object);
    }
  }

  private readonly Dictionary<GoObject, (GoNode Spec, int Index)> _variableSpecs = new(ReferenceEqualityComparer.Instance);

  private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

  private void CollectType(GoNode spec) {
    var name = spec.Node("Name")!;
    if (spec.Node("TypeParams") is not null)
      throw Unsupported(spec, $"generic type {name.Text("Name")}");

    var named = spec.Flag("alias") ? null : new GoNamed(name.Text("Name")!, this._libraryPath);
    var @object = this.Declare(this._package, name, GoObjectKind.TypeName, named, item => item.Resolution = GoResolution.Unresolved);
    this._typeSpecs[@object] = spec;
    this._globals.Add(@object);
  }

  private readonly Dictionary<GoObject, GoNode> _typeSpecs = new(ReferenceEqualityComparer.Instance);

  private void CollectFunction(GoNode declaration, List<(GoNode, GoObject?)> functions, List<GoNode> methods) {
    var name = declaration.Node("Name")!;
    var functionName = name.Text("Name")!;
    if (declaration.Node("Type")!.Node("TypeParams") is not null)
      throw Unsupported(declaration, $"generic function {functionName}");

    if (declaration.Node("Recv") is not null) {
      methods.Add(declaration);
      functions.Add((declaration, null));
      return;
    }

    if (functionName == "init" || functionName == "main" && this.PackageName == "main") {
      var type = declaration.Node("Type")!;
      if (type.Node("Params")!.Nodes("List").Count > 0 || type.Node("Results") is { } results && results.Nodes("List").Count > 0)
        throw Error(name, $"func {functionName} must have no arguments and no return values");
    }

    if (functionName == "init") {
      var init = new GoObject(GoObjectKind.Func, "init", null, name.Line, name.Column) { Declaration = name };
      name.Target = init;
      this._functionDeclarations[init] = declaration;
      init.Resolution = GoResolution.Unresolved;
      this._globals.Add(init);
      functions.Add((declaration, init));
      return;
    }

    var @object = this.Declare(this._package, name, GoObjectKind.Func, null, item => item.Resolution = GoResolution.Unresolved);
    this._functionDeclarations[@object] = declaration;
    this._globals.Add(@object);
    functions.Add((declaration, @object));
  }

  private readonly Dictionary<GoObject, GoNode> _functionDeclarations = new(ReferenceEqualityComparer.Instance);

  // ---------------------------------------------------------------- package-level declarations

  /// <summary>Checks the declaration of a package-level object when it is first needed, in the context of the package.</summary>
  private void Resolve(GoObject @object) {
    if (@object.Resolution == GoResolution.Resolved)
      return;

    if (@object.Resolution == GoResolution.Resolving) {
      if (@object.Kind == GoObjectKind.TypeName && @object.Type is GoNamed)
        return;

      if (@object.Kind == GoObjectKind.TypeName)
        throw Unsupported(@object.Declaration, $"a cycle through the type alias {@object.Name}");

      throw Error(@object.Declaration, @object.Kind == GoObjectKind.Var ? $"initialization cycle: {@object.Name} refers to itself" : $"invalid recursive declaration of {@object.Name}");
    }

    @object.Resolution = GoResolution.Resolving;
    var (scope, iota, declaration, function) = (this._scope, this._iota, this._declaration, this._function);
    this._scope = this._file;
    this._function = null;
    try {
      switch (@object.Kind) {
        case GoObjectKind.Const:
          var (typeNode, valueNode, value) = this._constantSpecs[@object];
          this._iota = value;
          this._declaration = null;
          this.ConstantDeclaration(@object, typeNode, valueNode!);
          break;
        case GoObjectKind.Var:
          this._iota = null;
          this._declaration = @object;
          this.GlobalVariable(@object);
          break;
        case GoObjectKind.TypeName:
          this._iota = null;
          this._declaration = null;
          this.TypeDeclaration(@object, this._typeSpecs[@object]);
          break;
        case GoObjectKind.Func:
          this._iota = null;
          this._declaration = null;
          var functionNode = this._functionDeclarations[@object];
          @object.Type = this.Signature(functionNode.Node("Type")!, null);
          functionNode.Node("Name")!.TypeOfNode = @object.Type;
          break;
      }
    } finally {
      (this._scope, this._iota, this._declaration, this._function) = (scope, iota, declaration, function);
    }

    @object.Resolution = GoResolution.Resolved;
  }

  private void GlobalVariable(GoObject @object) {
    var (spec, index) = this._variableSpecs[@object];
    var names = spec.Nodes("Names");
    var values = spec.Nodes("Values");
    var declared = spec.Node("Type") is { } typeNode ? this.TypeOf(typeNode) : null;
    @object.Type = declared;
    if (values.Count == 0) {
      @object.Declaration!.TypeOfNode = declared;
      return;
    }

    if (values.Count == names.Count) {
      // Each variable of the specification on its own: the others may be needed by this one's initializer.
      var operand = this.Expression(values[index], declared);
      this.InitVariable(@object, operand, "variable declaration");
      @object.Declaration!.TypeOfNode = @object.Type;
      return;
    }

    // n:1 - one call or comma-ok expression initializes all of them together.
    var objects = names.Select(name => name.Target!).ToList();
    foreach (var item in objects) {
      item.Resolution = GoResolution.Resolving;
      item.Type = declared;
    }

    this.InitVariables(objects, values, null);
    foreach (var item in objects) {
      item.Resolution = GoResolution.Resolved;
      item.Declaration!.TypeOfNode = item.Type;
      if (item != @object)
        item.Dependencies.UnionWith(@object.Dependencies);
    }
  }

  private void InitializationCycles() {
    foreach (var variable in this._globals.Where(@object => @object.Kind == GoObjectKind.Var)) {
      var seen = new HashSet<GoObject>(ReferenceEqualityComparer.Instance);
      var pending = new Stack<GoObject>(variable.Dependencies);
      while (pending.Count > 0) {
        var next = pending.Pop();
        if (next == variable)
          throw Error(variable.Declaration, variable.Dependencies.Contains(variable) ? $"initialization cycle: {variable.Name} refers to itself" : $"initialization cycle for {variable.Name}");

        if (seen.Add(next))
          foreach (var dependency in next.Dependencies)
            pending.Push(dependency);
      }
    }
  }

  /// <summary>Notes that the declaration being checked refers to a package-level variable or function.</summary>
  private void Depend(GoObject @object) {
    if (this._declaration is { } declaration && @object.Kind is GoObjectKind.Var or GoObjectKind.Func && (@object.IsGlobal || @object.Kind == GoObjectKind.Func && @object.Package is null))
      declaration.Dependencies.Add(@object);
  }

  // ---------------------------------------------------------------- recording

  private static string ModeName(GoMode mode) => mode switch {
    GoMode.TypeExpr => "type",
    GoMode.Builtin => "builtin",
    GoMode.Constant => "constant",
    GoMode.NoValue => "void",
    _ => "value"
  };

  private void Record(GoOperand x) {
    var node = x.Expr;
    node.Mode = ModeName(x.Mode);
    node.TypeOfNode = x.Mode is GoMode.NoValue or GoMode.Builtin ? null : x.Type;
    node.Value = x.Mode == GoMode.Constant ? x.Value : null;
    if (GoTypes.IsUntyped(x.Type) && x.Mode is GoMode.Constant or GoMode.Value)
      this._untyped[node] = new Untyped(x.Mode, x.Type, x.Value);
    else
      this._untyped.Remove(node);
  }

  private static GoNode Unparen(GoNode node) => node.Is("ParenExpr") ? Unparen(node.Node("X")!) : node;

  /// <summary>
  /// Gives an untyped expression and the untyped operands it was computed from their type
  /// (<c>updateExprType</c>): once the type is typed or final, a constant is checked to be representable
  /// in it and the left operand of a shift to be an integer.
  /// </summary>
  private void UpdateExprType(GoNode node, GoType type, bool final) {
    if (!this._untyped.TryGetValue(node, out var old))
      return;

    switch (node.Kind) {
      case "ParenExpr":
        this.UpdateExprType(node.Node("X")!, type, final);
        break;
      case "UnaryExpr" when old.Value is null:
        this.UpdateExprType(node.Node("X")!, type, final);
        break;
      case "BinaryExpr" when old.Value is null:
        var op = node.Text("Op")!;
        if (op is "==" or "!=" or "<" or "<=" or ">" or ">=") {
        } else if (op is "<<" or ">>") {
          this.UpdateExprType(node.Node("X")!, type, final);
        } else {
          this.UpdateExprType(node.Node("X")!, type, final);
          this.UpdateExprType(node.Node("Y")!, type, final);
        }

        break;
    }

    if (!final && GoTypes.IsUntyped(type)) {
      old.Type = type;
      node.TypeOfNode = type;
      return;
    }

    this._untyped.Remove(node);
    if (old.IsLhs && !GoTypes.IsInteger(type))
      throw Error(node, $"invalid operation: shifted operand {GoNodeText.Of(node)} (type {type}) must be integer");

    if (old.Value is { } value) {
      var operand = new GoOperand(old.Mode, old.Type, node, value);
      this.ConvertUntyped(operand, type);
    }

    node.TypeOfNode = type;
    node.Mode = ModeName(old.Mode);
    node.Value = old.Value;
  }

  private void UpdateExprValue(GoNode node, GoConstant value) {
    if (this._untyped.TryGetValue(node, out var old))
      old.Value = value;

    node.Value = value;
  }

}
