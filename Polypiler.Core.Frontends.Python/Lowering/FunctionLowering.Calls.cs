using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.Frontends.Python.Typing;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// Calls: of the functions of the import - module-level, nested, imported from another module of it,
/// methods - with positional and keyword arguments bound as Python binds them; of the constructors of
/// classes; of the built-ins and of the standard library's call map, each by the name its binding
/// resolves to, never by its bare text.
/// </summary>
internal sealed partial class FunctionLowering {

  private (IrExpr, PyType) Call(PyNode call, PyType? expected) {
    var callee = call.Node("func")!;
    if (callee.Is("Attribute"))
      return this.AttributeCall(call, callee, expected);

    var name = callee.Text("id")!;
    var binding = this.Scope.Resolve(callee);
    if (this.Scope.FunctionNamed(callee) is { } function)
      return this.CallFunction(call, function, null);

    if (binding is Binding.Class or Binding.Import && this.Scope.Class(callee) is { } type)
      return this.Construct(call, type);

    if (binding == Binding.Import) {
      var library = this._module.Imports[name];
      return this.Library(call, library.Module, library.Name!);
    }

    return this.Builtin(call, name, expected);
  }

  /// <summary>A call of a function of the import: by name, through the value of a nested one, or as a method on a receiver.</summary>
  private (IrExpr, PyType) CallFunction(PyNode call, FunctionModel function, IrExpr? receiver) {
    var arguments = this.Bind(call, [.. function.Arguments], function.Qualified);
    var result = function.Returns;
    var type = PyTypes.Ir(result);
    if (function.Parent is not null)
      return (this.Effect(new IrInvoke(type, Ref(this.NestedLocal(function)), arguments)), result);

    if (function.Owner is { } owner)
      return (this.Effect(new IrMethodCall(type, owner.IrMethods[function.Name], function.IsStatic ? null : receiver, arguments)), result);

    return (this.Effect(new IrCall(function.IrName, type, arguments)), result);
  }

  /// <summary>
  /// The arguments of a call in the order of the parameters: positional ones first, then those named,
  /// then the defaults of the parameters left, which were evaluated once, where the <c>def</c> ran. The
  /// arguments are evaluated in the order written; where that is not the order of the parameters, every
  /// argument that could tell is held in a temporary first.
  /// </summary>
  private List<IrExpr> Bind(PyNode call, IReadOnlyList<Variable> parameters, string callee) {
    var positional = call.Nodes("args");
    var keywords = call.Nodes("keywords");
    var slots = new (IrExpr Value, PyType Type)?[parameters.Count];
    var evaluated = new List<int>();
    Refusal Wrong(string what) => this.Refuse(call, PythonFrontend.UnsupportedFunctionDescriptor, $"'{what} in {this.Quote(call)}, which raises TypeError,' is not supported for functions by the Python frontend");
    for (var index = 0; index < positional.Count; ++index) {
      if (index >= parameters.Count || parameters[index].Kind == ParameterKind.KeywordOnly)
        throw Wrong($"more positional arguments than {callee} takes");

      var before = this._effects;
      slots[index] = this.Value(positional[index], parameters[index].Type);
      if (keywords.Count > 0)
        slots[index] = this.Kept(positional[index], slots[index]!.Value, before);

      evaluated.Add(index);
    }

    foreach (var keyword in keywords) {
      var name = keyword.Text("arg")!;
      var index = parameters.ToList().FindIndex(parameter => parameter.Name == name);
      if (index < 0 || parameters[index].Kind == ParameterKind.PositionalOnly)
        throw Wrong($"the keyword argument {name}, which {callee} has no parameter for");

      if (slots[index] is not null)
        throw Wrong($"a second value for {name}");

      var before = this._effects;
      var value = this.Value(keyword.Node("value")!, parameters[index].Type);
      // A keyword argument evaluated before one that comes earlier in the parameters is held, so that the call keeps the order written.
      slots[index] = evaluated.Any(other => other > index) || keywords.Count > 1 ? this.Kept(keyword.Node("value")!, value, before) : value;
      evaluated.Add(index);
    }

    var result = new List<IrExpr>();
    for (var index = 0; index < parameters.Count; ++index) {
      var parameter = parameters[index];
      if (slots[index] is { } given) {
        result.Add(this.Coerce(given, parameter.Type, call));
        continue;
      }

      if (parameter.DefaultValue is not { } fallback)
        throw Wrong($"no argument for the parameter {parameter.Name} of {callee}");

      result.Add(this.Coerce(this.ConstantValue(fallback), parameter.Type, call));
    }

    return result;
  }

  /// <summary>A call of a method or of a function reached through an attribute: of a module, of a class, of a value.</summary>
  private (IrExpr, PyType) AttributeCall(PyNode call, PyNode callee, PyType? expected) {
    var owner = callee.Node("value")!;
    var name = callee.Text("attr")!;
    if (this.Scope.LibraryModule(owner) is { } library)
      return this.Library(call, library, name);

    if (this.Scope.UserModule(owner) is { } user) {
      if (user.Functions.TryGetValue(name, out var imported))
        return this.CallFunction(call, imported, null);

      return user.Classes.TryGetValue(name, out var type) ? this.Construct(call, type)
        : throw this.Refuse(callee, PythonFrontend.UnresolvedDescriptor, $"'{this.Quote(callee)}' is neither part of this import nor in the Python call map");
    }

    // The inference refused every attribute of a class but its static methods.
    if (owner.Is("Name") && this.Scope.Resolve(owner) == Binding.Class && this.Scope.Class(owner) is { } declared)
      return this.CallFunction(call, declared.Method(name)!, null);

    if (owner.Is("Name") && owner.Text("id") == "int" && this.Scope.Resolve(owner) == Binding.BuiltIn)
      return name == "from_bytes" ? this.FromBytes(call) : throw this.Unsupported(callee, $"int.{name}");

    var receiver = this.Value(owner);
    if (receiver.Type.Kind == PyKind.Optional)
      receiver = (this.Effect(this.Helpers.Unwrap(receiver.Value, PyTypes.Held(receiver.Type.Element!), this.DeclaredException("AttributeError"), $"'NoneType' object has no attribute '{name}'")), receiver.Type.Element!);

    if (this.ListMethod(call, name, receiver) is { } listed)
      return listed;

    if (this.CollectionMethod(call, name, receiver) is { } collected)
      return collected;

    switch (receiver.Type.Kind) {
      case PyKind.Str when name == "encode":
        return this.Encode(call, receiver);
      case PyKind.Int or PyKind.Bool when name == "to_bytes":
        return this.ToBytes(call, receiver);
      case PyKind.Object when receiver.Type.Class!.Method(name) is { IsProperty: false } method:
        if (method.IsStatic && receiver.Value is not (IrLocalRef or IrConst))
          throw this.Unsupported(callee, $"the static method {name} called through a value that has to be evaluated");

        return this.CallFunction(call, method, receiver.Value);
      case PyKind.Object:
        throw this.Refuse(callee, PythonFrontend.DynamicAttributeDescriptor,
          $"attribute '{name}' of class '{receiver.Type.Class!.Name}' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported");
      case PyKind.Str or PyKind.Bytes or PyKind.ByteArray:
        throw this.Refuse(callee, PythonFrontend.UnsupportedStringDescriptor, $"string operation '{name}' is not supported by the Python frontend");
      default:
        throw this.Refuse(callee, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{name}' of a value of the type {receiver.Type} is not supported by the Python frontend");
    }
  }

  /// <summary>The built-ins: conversions, numbers, sequences, characters; <c>isinstance</c> against the classes of the module.</summary>
  private (IrExpr, PyType) Builtin(PyNode call, string name, PyType? expected) {
    if (this.SequenceBuiltin(call, name, expected) is { } sequence)
      return sequence;

    var arguments = call.Nodes("args");
    var arity = name switch {
      "pow" or "divmod" or "isinstance" => (2, 2),
      "int" or "float" or "bool" or "str" => (0, 1),
      "bytes" or "bytearray" => (0, 2),
      "range" => (1, 3),
      "enumerate" or "zip" or "map" or "filter" => (1, 2),
      _ => (1, 1)
    };
    if (call.Nodes("keywords").Count > 0 || arguments.Count < arity.Item1 || arguments.Count > arity.Item2)
      throw this.Unsupported(call, this.Quote(call));

    switch (name) {
      case "bytes" or "bytearray":
        return this.MakeBytes(call, name == "bytes");
      case "isinstance":
        return this.IsInstance(call);
      case "divmod" when arguments.Count == 2:
        return this.DivModValue(call);
      case "range" or "enumerate" or "zip" or "map" or "filter":
        return this.Iterable(call);
      case "int" or "float" or "bool" or "str" when arguments.Count == 0:
        return name switch {
          "int" => (Big(0), PyType.Int),
          "float" => (Float(0), PyType.Float),
          "bool" => (Bool(false), PyType.Bool),
          _ => (new IrStrConst(""), PyType.Str)
        };
    }

    var argument = this.Value(arguments[0]);
    switch (name) {
      case "len" when arguments.Count == 1:
        return this.Len(call, argument);
      case "ord" when arguments.Count == 1 && argument.Type.Kind == PyKind.Str:
        return (this.Effect(this.Helpers.Ord(argument.Value)), PyType.Int);
      case "chr" when arguments.Count == 1 && ExpressionTypes.Value(argument.Type) is { Kind: PyKind.Int or PyKind.Bool }:
        this.Notes.Characters = true;
        return (this.Effect(this.Helpers.Chr(this.Integer(this.Plain(argument, call)).Item1)), PyType.Str);
      case "str" when argument.Type.Kind == PyKind.Str:
        return argument;
      case "str" when argument.Type is { Kind: PyKind.Object, Class.IsException: true }:
        // The str of an exception made with one argument is its message.
        return (new IrFieldLoad(argument.Value, IrExceptionClass.Message), PyType.Str);
      case "str":
        throw this.Refuse(call, PythonFrontend.UnsupportedStringDescriptor, $"string operation 'str() of a value of the type {argument.Type}' is not supported by the Python frontend");
      case "bool":
        return (this.Truth(argument), PyType.Bool);
      case "pow" when arguments.Count == 2:
        return this.Operate(call, "Pow", argument, this.Value(arguments[1]));
    }

    if (!ExpressionTypes.Value(argument.Type).IsNumber || arguments.Count != 1 || name is not ("int" or "float" or "abs" or "round"))
      throw this.Unsupported(call, $"{name}() of a value of the type {argument.Type}");

    argument = this.Plain(argument, call);
    switch (name) {
      case "float":
        return (Floating(argument), PyType.Float);
    }

    if (argument.Type.Kind != PyKind.Float) {
      var integer = this.Integer(argument).Item1;
      return (name == "abs" ? this.Effect(this.Helpers.AbsInt(integer)) : integer, PyType.Int);
    }

    return name switch {
      "abs" => (Intrinsic(IrIntrinsicOp.Abs, IrType.F64, argument.Value), PyType.Float),
      // round() rounds half to even, int() toward zero; both raise as int() does.
      "round" => (this.Effect(this.Helpers.IntOfFloat(Intrinsic(IrIntrinsicOp.RoundEven, IrType.F64, argument.Value))), PyType.Int),
      _ => (this.Effect(this.Helpers.IntOfFloat(argument.Value)), PyType.Int)
    };
  }

  /// <summary><c>divmod(a, b)</c> as a value: the tuple of the quotient and the remainder, both of the same operands.</summary>
  private (IrExpr, PyType) DivModValue(PyNode call) {
    var arguments = call.Nodes("args");
    var before = this._effects;
    var a = this.Kept(arguments[0], this.Value(arguments[0]), before);
    var b = this.Kept(arguments[1], this.Value(arguments[1]), before);
    var quotient = this.Operate(call, "FloorDiv", a, b);
    var remainder = this.Operate(call, "Mod", a, b);
    var type = PyType.Tuple([quotient.Type, remainder.Type]);
    return (MakeStruct(PyTypes.Ir(type), [quotient.Value, remainder.Value]), type);
  }

  /// <summary>
  /// <c>isinstance(x, C)</c>: for an object a test of its class, an exception's included; for any other
  /// value the answer its static type gives - a <c>bool</c> is an <c>int</c> too.
  /// </summary>
  private (IrExpr, PyType) IsInstance(PyNode call) {
    var arguments = call.Nodes("args");
    var value = this.Value(arguments[0]);
    var classes = arguments[1].Is("Tuple") ? arguments[1].Nodes("elts") : [arguments[1]];
    IrExpr? result = null;
    foreach (var named in classes) {
      if (!named.Is("Name"))
        throw this.Unsupported(call, $"isinstance against {this.Quote(named)}");

      IrExpr test;
      if (this.Scope.Resolve(named) == Binding.Class && this.Scope.Class(named) is { } declared && value.Type.Kind == PyKind.Object) {
        if (declared.Ir is null)
          ClassDeclarations.Exception(declared, this._context);

        // LookupError is also IndexError and KeyError, which do not derive from the class declared for it.
        test = value.Type.Class!.DerivesFrom(declared) ? Not(new IrIsNull(value.Value))
          : Classes.Caught(declared).Select(caught => (IrExpr)new IrTypeTest(value.Value, IrType.Object(caught))).Aggregate((any, next) => Binary(IrBinaryOp.LogicalOr, any, next));
      } else {
        var holds = named.Text("id") switch {
          "int" => value.Type.Kind is PyKind.Int or PyKind.Bool || (value.Type.Kind == PyKind.Enum && value.Type.Class!.Kind == ClassKind.IntEnum),
          "float" => value.Type.Kind == PyKind.Float,
          "bool" => value.Type.Kind == PyKind.Bool,
          "str" => value.Type.Kind == PyKind.Str,
          "bytes" => value.Type.Kind == PyKind.Bytes,
          "bytearray" => value.Type.Kind == PyKind.ByteArray,
          "list" => value.Type.Kind == PyKind.List,
          "tuple" => value.Type.Kind == PyKind.Tuple,
          _ => throw this.Unsupported(call, $"isinstance of a value of the type {value.Type} against {this.Quote(named)}")
        };
        if (value.Value is not (IrLocalRef or IrConst or IrStrConst))
          throw this.Unsupported(call, $"isinstance of {this.Quote(arguments[0])}, which has to be evaluated");

        test = Bool(holds);
      }

      result = result is null ? test : Binary(IrBinaryOp.LogicalOr, result, test);
    }

    return (result!, PyType.Bool);
  }

  /// <summary>
  /// The functions of the standard library's call map: <c>math.sqrt</c> and its kin, with the results
  /// and the exceptions CPython gives them; <c>struct.pack</c> and its kin.
  /// </summary>
  private (IrExpr, PyType) Library(PyNode call, string module, string name) {
    if (module == "struct")
      return this.Struct(call, name);

    if (module != "math" || call.Nodes("keywords").Count > 0)
      throw this.Refuse(call, PythonFrontend.UnresolvedDescriptor, $"'{module}.{name}' is neither part of this import nor in the Python call map");

    var arguments = call.Nodes("args").Select(argument => this.Plain(this.Value(argument), argument)).ToList();
    if (arguments.Any(argument => !argument.Type.IsNumber) || arguments.Count != (name is "copysign" or "gcd" ? 2 : 1))
      throw this.Unsupported(call, $"math.{name} of these arguments, which raises TypeError,");

    switch (name) {
      case "sqrt":
        return (this.Effect(this.Helpers.Sqrt(Floating(arguments[0]))), PyType.Float);
      case "fabs":
        return (Intrinsic(IrIntrinsicOp.Abs, IrType.F64, Floating(arguments[0])), PyType.Float);
      case "copysign":
        return (Intrinsic(IrIntrinsicOp.CopySign, IrType.F64, Floating(arguments[0]), Floating(arguments[1])), PyType.Float);
      case "isnan":
        return (Intrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, Floating(arguments[0])), PyType.Bool);
      case "isinf":
        return (Intrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, Floating(arguments[0])), PyType.Bool);
      case "isfinite":
        return (Intrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, Floating(arguments[0])), PyType.Bool);
    }

    if (name is "isqrt" or "gcd") {
      if (arguments.Any(argument => argument.Type.Kind == PyKind.Float))
        throw this.Unsupported(call, $"math.{name} of a float, which raises TypeError,");

      return name == "isqrt"
        ? (this.Effect(this.Helpers.ISqrt(this.Integer(arguments[0]).Item1)), PyType.Int)
        : (this.Effect(this.Helpers.Gcd(this.Integer(arguments[0]).Item1, this.Integer(arguments[1]).Item1)), PyType.Int);
    }

    // floor, ceil, trunc: an int as it is; a float rounded, then converted as int() converts it.
    if (arguments[0].Type.Kind != PyKind.Float)
      return (this.Integer(arguments[0]).Item1, PyType.Int);

    var rounding = name switch { "floor" => IrIntrinsicOp.Floor, "ceil" => IrIntrinsicOp.Ceiling, _ => IrIntrinsicOp.Truncate };
    return (this.Effect(this.Helpers.IntOfFloat(Intrinsic(rounding, IrType.F64, arguments[0].Value))), PyType.Int);
  }

}
