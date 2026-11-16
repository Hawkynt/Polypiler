using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// Objects of the classes of the module: attributes read and assigned, properties, methods, the
/// members of enums with <c>.value</c> and <c>.name</c>, constructions, and the exceptions the module
/// declares beside the library's. An attribute outside the class's declared set is refused, never added.
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary>An exception class the module declares for a built-in the IR's library has not: declared on first use.</summary>
  private DeclaredException DeclaredException(string name) => ClassDeclarations.Exception(Classes.Builtin(name), this._context);

  /// <summary><c>x.a</c>: a constant of a module, a member of an enum, an attribute or a property of an object, <c>.value</c> and <c>.name</c> of a member.</summary>
  private (IrExpr, PyType) Attribute(PyNode attribute) {
    var owner = attribute.Node("value")!;
    var name = attribute.Text("attr")!;
    if (this.Scope.LibraryModule(owner) is not null) {
      return LibraryCallMap.Constant(this._module.Imports[owner.Text("id")!], name) is { } constant ? (constant.Value, constant.Type)
        : throw this.Refuse(attribute, PythonFrontend.EscapingFunctionDescriptor, SubsetCheck.Escaping(this.Quote(attribute)) + " (escaping function values: #753)");
    }

    if (this.Scope.UserModule(owner) is { } user) {
      return user.Constants.TryGetValue(name, out var value) ? this.ConstantValue(value)
        : throw this.Refuse(attribute, PythonFrontend.EscapingFunctionDescriptor, SubsetCheck.Escaping(this.Quote(attribute)) + " (escaping function values: #753)");
    }

    if (owner.Is("Name") && this.Scope.Resolve(owner) == Binding.Class && this.Scope.Class(owner) is { } type) {
      if (type.IsEnum && type.Members.FirstOrDefault(member => member.Name == name) is { Name: not null } member)
        return (new IrConst(type.EnumType!.Type, member.Value), PyType.Enum(type));

      throw this.Refuse(attribute, PythonFrontend.UnsupportedClassDescriptor, $"'{this.Quote(attribute)}, a class attribute,' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481");
    }

    return this.ReadAttribute(attribute, this.Value(owner));
  }

  /// <summary>An attribute of a value already evaluated: of an object or of a member of an enum; <c>None</c> raises <c>AttributeError</c>.</summary>
  private (IrExpr Value, PyType Type) ReadAttribute(PyNode attribute, (IrExpr Value, PyType Type) owner) {
    var name = attribute.Text("attr")!;
    if (owner.Type.Kind == PyKind.Optional)
      owner = (this.Effect(this.Helpers.Unwrap(owner.Value, PyTypes.Held(owner.Type.Element!), this.DeclaredException("AttributeError"), $"'NoneType' object has no attribute '{name}'")), owner.Type.Element!);

    // Of a member of an enum the inference refused every attribute but value and name.
    if (owner.Type.Kind == PyKind.Enum)
      return name == "value" ? (Convert(IrType.I64, owner.Value), PyType.Int)
        : (this.Effect(this.Helpers.EnumName(owner.Value, owner.Type.Class!.EnumType!, owner.Type.Class.Members.Select(member => (member.Name, member.Value)).ToList())), PyType.Str);

    if (owner.Type.Kind != PyKind.Object)
      throw this.Unsupported(attribute, $"the attribute {name} of a value of the type {owner.Type}, which raises AttributeError,");

    var type = owner.Type.Class!;
    if (type.Field(name) is { Ir: { } field } declared)
      return (new IrFieldLoad(owner.Value, field), declared.Type);

    if (type.Method(name) is { IsProperty: true } property)
      return (this.Effect(new IrMethodCall(PyTypes.Ir(property.Returns), type.IrMethods[name], owner.Value, [])), property.Returns);

    if (type.IsException && name == "args")
      throw this.Refuse(attribute, PythonFrontend.UnsupportedExceptionDescriptor, "exception construct 'args of an exception' is not supported by the Python frontend; str(e) gives its message");

    // A method taken from its object: the inference refused every attribute the class does not have.
    throw this.Refuse(attribute, PythonFrontend.EscapingFunctionDescriptor, SubsetCheck.Escaping(this.Quote(attribute)) + " (bound methods as values: #753)");
  }

  /// <summary><c>x.a = v</c>: the value first, then the object, then the store; a frozen dataclass or a named tuple refuses it, as it raises.</summary>
  private List<IrStmt> StoreAttribute(PyNode target, (IrExpr Value, PyType Type) value, (IrExpr Value, PyType Type)? heldOwner = null) {
    var result = new List<IrStmt>();
    if (heldOwner is null && value.Value is not (IrConst or IrLocalRef or IrStrConst))
      value = this.Once(value, result);

    var owner = heldOwner ?? this.Value(target.Node("value")!);
    var name = target.Text("attr")!;
    if (owner.Type.Kind == PyKind.Optional)
      owner = (this.Effect(this.Helpers.Unwrap(owner.Value, PyTypes.Held(owner.Type.Element!), this.DeclaredException("AttributeError"), $"'NoneType' object has no attribute '{name}'")), owner.Type.Element!);

    if (owner.Type.Kind != PyKind.Object)
      throw this.Unsupported(target, $"assignment to the attribute {name} of a value of the type {owner.Type}, which raises AttributeError,");

    // The inference refused an attribute the class does not have; the declaration one of a type no field can hold.
    var type = owner.Type.Class!;
    var declared = type.Field(name)!;
    var field = declared.Ir!;

    var inConstructor = this._function is { Name: "__init__" } constructor && ReferenceEquals(constructor.Owner, type) && target.Node("value") is { Kind: "Name" } self
      && self.Text("id") == constructor.Parameters[0].Name;
    if (type.IsFrozen && !inConstructor)
      throw this.Refuse(target, PythonFrontend.UnsupportedClassDescriptor, $"'assignment to the field {name} of the frozen {type.Name}, which raises FrozenInstanceError,' is outside the first Python subset (dataclasses, simple final classes, enums)");

    result.Add(new IrFieldStore(owner.Value, field, Fit(this.Coerce(value, declared.Type, target), field.Type)));
    return result;
  }

  /// <summary>
  /// <c>C(…)</c>: a new object, its <c>__init__</c> called with the arguments bound to its parameters;
  /// <c>E(v)</c> the member of an enum of a value, <c>ValueError</c> for none; an exception with its message.
  /// </summary>
  private (IrExpr, PyType) Construct(PyNode call, ClassModel type) {
    var arguments = call.Nodes("args");
    if (type.IsEnum) {
      if (arguments.Count != 1 || call.Nodes("keywords").Count > 0)
        throw this.Unsupported(call, $"{type.Name}() of other than one value");

      var value = this.Plain(this.Value(arguments[0]), arguments[0]);
      if (value.Type.Kind is not (PyKind.Int or PyKind.Bool))
        throw this.Unsupported(call, $"{type.Name}() of a value of the type {value.Type}, which raises ValueError,");

      return (this.Effect(this.Helpers.EnumOf(this.Integer(value).Item1, type.EnumType!, type.Members.Select(member => member.Value).ToList(), type.Name)), PyType.Enum(type));
    }

    if (type.IsException) {
      if (call.Nodes("keywords").Count > 0 || arguments.Count > 1)
        throw this.Refuse(call, PythonFrontend.UnsupportedExceptionDescriptor, $"exception construct 'an exception made with other arguments than a message: {this.Quote(call)}' is not supported by the Python frontend");

      IrExpr message = new IrStrConst("");
      if (arguments.Count == 1) {
        var given = this.Value(arguments[0]);
        message = given.Type.Kind == PyKind.Str ? given.Value
          : throw this.Refuse(arguments[0], PythonFrontend.UnsupportedExceptionDescriptor, $"exception construct 'an exception whose argument is no str' is not supported by the Python frontend");
      }

      return (this.NewException(type, message), PyType.Object(type));
    }

    var constructed = new IrNew(type.Reference, type.Constructor!, this.Bind(call, type.InitParameters, type.Name));
    return (this.Effect(constructed), PyType.Object(type));
  }

  /// <summary>A new exception of a class with a message.</summary>
  private IrExpr NewException(ClassModel type, IrExpr message) {
    if (type.Kind == ClassKind.BuiltinException && type.Ir is { IsLibrary: true } library)
      return new IrNew(IrType.Object(library), IrExceptionClass.Constructor(library, 1), [message]);

    var declared = type.Kind == ClassKind.BuiltinException ? ClassDeclarations.Exception(type, this._context) : new DeclaredException(type.Ir!, type.Constructor!);
    return new IrNew(IrType.Object(declared.Type), declared.Constructor, [message]);
  }

}
