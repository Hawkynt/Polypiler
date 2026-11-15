using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Lowering;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;

namespace Polypiler.Core.Frontends.Python.Typing;

/// <summary>Where an expression stands: its module, its function (none at module level) and the variables of the comprehensions and lambdas in it.</summary>
/// <param name="Module">The module.</param>
/// <param name="Function">The function, or <see langword="null"/> for a module-level expression.</param>
/// <param name="Bound">The variables of comprehensions and lambdas, by the name nodes that read them.</param>
internal sealed record Scope(ModuleModel Module, FunctionModel? Function, IReadOnlyDictionary<PyNode, Variable> Bound) {

  /// <summary>What a name node stands for.</summary>
  public Binding Resolve(PyNode name) => ScopeResolver.Resolve(this.Function, this.Module, name.Text("id")!, this.Bound, name);

  /// <summary>The variable a name node reads or assigns, or <see langword="null"/> where it is no variable of a function.</summary>
  public Variable? Variable(PyNode name) {
    if (this.Bound.TryGetValue(name, out var bound))
      return bound;

    var id = name.Text("id") ?? name.Text("name") ?? name.Text("arg")!;
    return this.Function is { } function && this.Resolve(name) is Binding.Local or Binding.Captured ? function.Find(id) : null;
  }

  /// <summary>The class a name of a class node binds: of the module, imported from a module of the import, or a built-in exception.</summary>
  public ClassModel? Class(PyNode name) {
    var id = name.Text("id")!;
    if (this.Module.Imports.TryGetValue(id, out var binding))
      return binding.User is { } user && binding.Name is { } imported ? user.Classes.GetValueOrDefault(imported) : null;

    return Classes.Of(this.Module, id);
  }

  /// <summary>The function a name of a function node binds: of the module, nested, or imported from a module of the import.</summary>
  public FunctionModel? FunctionNamed(PyNode name) {
    var id = name.Text("id")!;
    return this.Resolve(name) switch {
      Binding.Function => this.Module.Functions[id],
      Binding.Nested => ScopeResolver.NestedFunction(this.Function!, id),
      Binding.Import when this.Module.Imports[id] is { User: { } user, Name: { } imported } => user.Functions.GetValueOrDefault(imported),
      _ => null
    };
  }

  /// <summary>The module of the import an attribute's owner names: <c>helper</c> of <c>helper.f</c>.</summary>
  public ModuleModel? UserModule(PyNode owner)
    => owner.Is("Name") && this.Resolve(owner) == Binding.Import && this.Module.Imports[owner.Text("id")!] is { User: { } user, Name: null } ? user : null;

  /// <summary>The module of the standard library an attribute's owner names: <c>math</c> of <c>math.sqrt</c>.</summary>
  public string? LibraryModule(PyNode owner)
    => owner.Is("Name") && this.Resolve(owner) == Binding.Import && this.Module.Imports[owner.Text("id")!] is { User: null, Name: null } binding ? binding.Module : null;

  /// <summary>The scope of the same function in another module: of an imported function.</summary>
  public Scope Of(FunctionModel function, ModuleModel module) => this with { Module = module, Function = function };

}

/// <summary>
/// The type of the value of an expression, from the types of its operands, as the Python Language
/// Reference and the built-in types define them: <c>/</c> always gives a <c>float</c>, <c>//</c>,
/// <c>%</c>, <c>+</c>, <c>-</c>, <c>*</c> an <c>int</c> of two integers (a <c>bool</c> counts as one) and a
/// <c>float</c> otherwise, <c>**</c> a <c>float</c> also for an integer to a negative constant power,
/// the bitwise operators a <c>bool</c> of two <c>bool</c>s, a comparison a <c>bool</c>; <c>+</c> of two
/// sequences a sequence, an index of a list its element, of bytes an <c>int</c>, of a str a str. An
/// operand of an unknown type makes the result unknown.
/// </summary>
internal static class ExpressionTypes {

  /// <summary>The type of an expression where it stands.</summary>
  public static PyType Of(PyNode expression, Scope scope) {
    PyType Inner(string field) => Of(expression.Node(field)!, scope);
    switch (expression.Kind) {
      case "Constant":
        return expression.Text("kind") switch {
          "int" => PyType.Int,
          "float" => PyType.Float,
          "bool" => PyType.Bool,
          "none" => PyType.None,
          "str" => PyType.Str,
          "bytes" => PyType.Bytes,
          _ => PyType.Unknown
        };
      case "Name":
        return Name(expression, scope);
      case "BinOp":
        return Binary(expression.Operator()!, Inner("left"), Inner("right"), expression.Node("right")!);
      case "UnaryOp":
        var operand = Value(Inner("operand"));
        return expression.Operator() == "Not" ? PyType.Bool
          : operand.Kind == PyKind.Unknown ? PyType.Unknown
          : operand.Kind == PyKind.Float && expression.Operator() != "Invert" ? PyType.Float
          : operand.IsNumber ? PyType.Int : PyType.Unknown;
      case "Compare":
        return PyType.Bool;
      case "BoolOp":
        var values = expression.Nodes("values");
        // Of `or`, an operand before the last is the value only where it is true, so not None.
        return Joined(values.Select((value, index) => index < values.Count - 1 && expression.Operator() == "Or" ? Truthy(Of(value, scope)) : Of(value, scope)));
      case "IfExp":
        return Joined([Inner("body"), Inner("orelse")]);
      case "NamedExpr":
        return Inner("value");
      case "List":
        return PyType.List(Joined(expression.Nodes("elts").Select(element => Of(element, scope)), true));
      case "Tuple":
        return PyType.Tuple([.. expression.Nodes("elts").Select(element => Of(element, scope))]);
      case "ListComp":
        return PyType.List(Inner("elt"));
      case "GeneratorExp":
        return PyType.Iterator(Inner("elt"));
      case "Subscript":
        return Subscript(expression, scope);
      case "Attribute":
        return Attribute(expression, scope);
      case "Call":
        return Call(expression, scope);
      default:
        return PyType.Unknown;
    }
  }

  /// <summary>The type an optional value has where it is used as a value: what it holds; any other type itself.</summary>
  public static PyType Value(PyType type) => type.Kind == PyKind.Optional ? type.Element! : type.Kind == PyKind.Enum && type.Class!.Kind == ClassKind.IntEnum ? PyType.Int : type;

  /// <summary>The type of a value where it is true: an optional holds a value there.</summary>
  private static PyType Truthy(PyType type) => type.Kind == PyKind.Optional ? type.Element! : type;

  private static PyType Name(PyNode name, Scope scope) {
    if (scope.Variable(name) is { } variable)
      return variable.Type;

    var id = name.Text("id")!;
    return scope.Resolve(name) switch {
      Binding.Constant => scope.Module.Constants.TryGetValue(id, out var constant) ? constant.Type : PyType.Unknown,
      Binding.Global => scope.Module.GlobalVariables[id].Type,
      Binding.Import when LibraryCallMap.Constant(scope.Module.Imports[id], null) is { } library => library.Type,
      Binding.Import when scope.Module.Imports[id] is { User: { } user, Name: { } imported } && user.Constants.TryGetValue(imported, out var value) => value.Type,
      _ => PyType.Unknown
    };
  }

  /// <summary>The element a value of a type gives when it is indexed or iterated; <see langword="null"/> for a type that is no sequence.</summary>
  public static PyType? Element(PyType type) => type.Kind switch {
    PyKind.List or PyKind.Table or PyKind.Iterator => type.Element,
    PyKind.Str => PyType.Str,
    PyKind.Bytes or PyKind.ByteArray => PyType.Int,
    PyKind.Tuple when type.Items.Count > 0 && type.Items.All(item => item == type.Items[0]) => type.Items[0],
    PyKind.Unknown => PyType.Unknown,
    _ => null
  };

  private static PyType Subscript(PyNode subscript, Scope scope) {
    var target = Of(subscript.Node("value")!, scope);
    var index = subscript.Node("slice")!;
    if (index.Is("Slice"))
      return target.Kind switch {
        PyKind.List or PyKind.Str or PyKind.Bytes or PyKind.ByteArray => target,
        PyKind.Table => PyType.List(target.Element!),
        _ => PyType.Unknown
      };

    if (target.Kind == PyKind.Tuple)
      return Constants.Integer(index) is { } position && position >= -target.Items.Count && position < target.Items.Count
        ? target.Items[(int)(position < 0 ? position + target.Items.Count : position)] : PyType.Unknown;

    if (target.Kind == PyKind.Object && target.Class!.Method("__getitem__") is { } getter)
      return getter.Returns;

    return Element(target) ?? PyType.Unknown;
  }

  private static PyType Attribute(PyNode attribute, Scope scope) {
    var owner = attribute.Node("value")!;
    var name = attribute.Text("attr")!;
    if (scope.LibraryModule(owner) is not null)
      return LibraryCallMap.Constant(scope.Module.Imports[owner.Text("id")!], name)?.Type ?? PyType.Unknown;

    if (owner.Is("Name") && scope.Resolve(owner) == Binding.Class && scope.Class(owner) is { IsEnum: true } enumeration)
      return enumeration.Members.Any(member => member.Name == name) ? PyType.Enum(enumeration) : PyType.Unknown;

    if (scope.UserModule(owner) is { } user && user.Constants.TryGetValue(name, out var constant))
      return constant.Type;

    var type = Of(owner, scope);
    if (type.Kind == PyKind.Optional)
      type = type.Element!;

    if (type.Kind == PyKind.Enum)
      return name switch { "value" => PyType.Int, "name" => PyType.Str, _ => PyType.Unknown };

    if (type.Kind != PyKind.Object)
      return PyType.Unknown;

    if (type.Class!.Field(name) is { } field)
      return field.Type;

    return type.Class.Method(name) is { IsProperty: true } property ? property.Returns : PyType.Unknown;
  }

  private static PyType Call(PyNode call, Scope scope) {
    var callee = call.Node("func")!;
    var arguments = call.Nodes("args");
    if (callee.Is("Attribute"))
      return Method(call, callee, scope);

    if (!callee.Is("Name"))
      return PyType.Unknown;

    var name = callee.Text("id")!;
    var binding = scope.Resolve(callee);
    if (scope.FunctionNamed(callee) is { } function)
      return function.Returns;

    if (binding is Binding.Class or Binding.Import && scope.Class(callee) is { } type)
      return type.IsEnum ? PyType.Enum(type) : PyType.Object(type);

    if (binding == Binding.Import && scope.Module.Imports[name] is { User: null } library)
      return library.Module == "math" && library.Name is { } mathName ? LibraryCallMap.MathResult(mathName) ?? PyType.Unknown
        : library.Module == "struct" ? StructResult(library.Name!, arguments) : PyType.Unknown;

    if (binding != Binding.BuiltIn)
      return PyType.Unknown;

    var types = arguments.Select(argument => Of(argument, scope)).ToList();
    PyType First() => types.Count > 0 ? types[0] : PyType.Unknown;
    PyType ElementOfFirst() => Element(First()) ?? PyType.Unknown;
    switch (name) {
      case "int" or "round" or "len" or "ord":
        return PyType.Int;
      case "float":
        return PyType.Float;
      case "bool" or "any" or "all" or "isinstance":
        return PyType.Bool;
      case "str" or "chr":
        return PyType.Str;
      case "bytes":
        return PyType.Bytes;
      case "bytearray":
        return PyType.ByteArray;
      case "abs":
        var absolute = Value(First());
        return absolute.Kind is PyKind.Unknown or PyKind.Float ? absolute : PyType.Int;
      case "pow" when types.Count == 2:
        return Binary("Pow", types[0], types[1], arguments[1]);
      case "divmod" when types.Count == 2:
        var quotient = Binary("FloorDiv", types[0], types[1], arguments[1]);
        return PyType.Tuple([quotient, quotient]);
      case "list" or "sorted":
        return types.Count == 0 ? PyType.List(PyType.Unknown) : Element(First()) is { } element ? PyType.List(element.Kind == PyKind.Bool && First().IsBytes ? PyType.Int : element) : PyType.Unknown;
      case "reversed":
        return Element(First()) is { } reversed ? PyType.Iterator(reversed) : PyType.Unknown;
      case "tuple":
        return First().Kind == PyKind.Tuple ? First() : PyType.Unknown;
      case "sum":
        var summed = Value(ElementOfFirst());
        var start = types.Count > 1 ? Value(types[1]) : PyType.Int;
        return Binary("Add", summed.Kind == PyKind.Bool ? PyType.Int : summed, start, call);
      case "min" or "max":
        if (types.Count == 1)
          return ElementOfFirst();

        return Joined(types);
      case "range":
        return PyType.Iterator(PyType.Int);
      case "enumerate":
        return PyType.Iterator(PyType.Tuple([PyType.Int, ElementOfFirst()]));
      case "zip" when types.Count == 2:
        return PyType.Iterator(PyType.Tuple([ElementOfFirst(), Element(types[1]) ?? PyType.Unknown]));
      case "map" when arguments.Count == 2:
        return PyType.Iterator(FunctionResult(arguments[0], scope));
      case "filter" when arguments.Count == 2:
        return PyType.Iterator(Element(types[1]) ?? PyType.Unknown);
      default:
        return Classes.IsBuiltinException(name) ? PyType.Object(Classes.Builtin(name)) : PyType.Unknown;
    }
  }

  /// <summary>The type of the result of a function value passed to <c>map</c>: a lambda's body, or a function of the module.</summary>
  public static PyType FunctionResult(PyNode function, Scope scope)
    => function.Is("Lambda") ? Of(function.Node("body")!, scope) : scope.FunctionNamed(function)?.Returns ?? PyType.Unknown;

  private static PyType StructResult(string name, IReadOnlyList<PyNode> arguments) => name switch {
    "pack" => PyType.Bytes,
    "calcsize" => PyType.Int,
    "unpack" or "unpack_from" when arguments.Count > 0 && StructFormat.Parse(arguments[0]) is { Format: { } format } => format.Type,
    _ => PyType.Unknown
  };

  private static PyType Method(PyNode call, PyNode callee, Scope scope) {
    var owner = callee.Node("value")!;
    var name = callee.Text("attr")!;
    var arguments = call.Nodes("args");
    if (scope.LibraryModule(owner) is { } library)
      return library == "math" ? LibraryCallMap.MathResult(name) ?? PyType.Unknown : library == "struct" ? StructResult(name, arguments) : PyType.Unknown;

    if (scope.UserModule(owner) is { } user)
      return user.Functions.TryGetValue(name, out var function) ? function.Returns
        : user.Classes.TryGetValue(name, out var type) ? (type.IsEnum ? PyType.Enum(type) : PyType.Object(type)) : PyType.Unknown;

    if (owner.Is("Name") && scope.Resolve(owner) == Binding.Class)
      return scope.Class(owner)?.Method(name) is { IsStatic: true } method ? method.Returns : PyType.Unknown;

    if (owner.Is("Name") && owner.Text("id") == "int" && scope.Resolve(owner) == Binding.BuiltIn)
      return name == "from_bytes" ? PyType.Int : PyType.Unknown;

    var receiver = Of(owner, scope);
    if (receiver.Kind == PyKind.Optional)
      receiver = receiver.Element!;

    return (receiver.Kind, name) switch {
      (PyKind.List, "append" or "extend") => PyType.None,
      (PyKind.List, "pop") => receiver.Element!,
      (PyKind.List, "copy") => receiver,
      (PyKind.Str, "encode") => PyType.Bytes,
      (PyKind.Int or PyKind.Bool, "to_bytes") => PyType.Bytes,
      (PyKind.Object, _) when receiver.Class!.Method(name) is { IsProperty: false } method => method.Returns,
      _ => PyType.Unknown
    };
  }

  /// <summary>The type of a binary operation.</summary>
  /// <param name="op">The class of the operator, for example <c>Add</c>.</param>
  /// <param name="left">The type of the left operand.</param>
  /// <param name="right">The type of the right operand.</param>
  /// <param name="rightNode">The right operand, whose sign decides the type of an integer power when it is a constant.</param>
  public static PyType Binary(string op, PyType left, PyType right, PyNode rightNode) {
    left = Value(left);
    right = Value(right);
    if (left.Kind == PyKind.Unknown || right.Kind == PyKind.Unknown)
      return PyType.Unknown;

    if (!left.IsNumber || !right.IsNumber)
      return Sequence(op, left, right);

    var floating = left.Kind == PyKind.Float || right.Kind == PyKind.Float;
    return op switch {
      "Div" => PyType.Float,
      "Pow" => floating || Constants.Integer(rightNode) < 0 ? PyType.Float : PyType.Int,
      "LShift" or "RShift" => PyType.Int,
      "BitAnd" or "BitOr" or "BitXor" => left.Kind == PyKind.Bool && right.Kind == PyKind.Bool ? PyType.Bool : PyType.Int,
      _ => floating ? PyType.Float : PyType.Int
    };
  }

  /// <summary>The type of <c>+</c> and <c>*</c> of sequences: concatenation of two of one type, repetition by an integer.</summary>
  private static PyType Sequence(string op, PyType left, PyType right) {
    switch (op) {
      case "Add" when left.Kind == PyKind.Tuple && right.Kind == PyKind.Tuple:
        return PyType.Tuple([.. left.Items, .. right.Items]);
      case "Add" when left.Kind is PyKind.List or PyKind.Str or PyKind.Bytes or PyKind.ByteArray && (left == right || (left.IsBytes && right.IsBytes) || PyTypes.Join(left, right) is { Kind: PyKind.List }):
        return left.Kind == PyKind.List ? PyTypes.Join(left, right)! : left;
      case "Mult" when left.Kind is PyKind.List or PyKind.Str or PyKind.Bytes or PyKind.ByteArray && right.Kind is PyKind.Int or PyKind.Bool:
        return left;
      case "Mult" when right.Kind is PyKind.List or PyKind.Str or PyKind.Bytes or PyKind.ByteArray && left.Kind is PyKind.Int or PyKind.Bool:
        return right;
      default:
        return PyType.Unknown;
    }
  }

  /// <summary>The join of the types of alternative values, unknown where one is unknown or where no type holds them all.</summary>
  /// <param name="types">The types.</param>
  /// <param name="partial">Whether an unknown type is left out instead, as the elements of a list display whose other elements tell.</param>
  public static PyType Joined(IEnumerable<PyType> types, bool partial = false) {
    PyType? result = PyType.Unknown;
    foreach (var type in types) {
      if (type.Kind == PyKind.Unknown && !partial)
        return PyType.Unknown;

      result = PyTypes.Join(result!, type);
      if (result is null)
        return PyType.Unknown;
    }

    return result!;
  }

}
