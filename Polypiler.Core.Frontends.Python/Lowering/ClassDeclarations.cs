using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// The IR declarations of the classes of the import: an enum of <c>i64</c> members; a sealed class of
/// the attributes of a plain class, a dataclass or a named tuple, with its methods and its constructor
/// - the class's own <c>__init__</c>, or one made from a dataclass's fields - and the <c>__eq__</c> a
/// dataclass compares its fields with; a class of exceptions derived from the class of its base, whose
/// constructor takes the message.
/// </summary>
internal static class ClassDeclarations {

  /// <summary>Declares the enums of a module: their members are known from the source alone.</summary>
  public static void Enums(ModuleModel module, LoweringContext context) {
    foreach (var type in module.Classes.Values.Where(type => type.IsEnum)) {
      type.EnumType = new IrEnumType(type.Name, IrType.I64, [.. type.Members.Select(member => new IrEnumMember(member.Name, member.Value))]) { IsPublic = true };
      context.Types.Add(type.EnumType);
    }
  }

  /// <summary>Gives the methods and the parameters of the constructor of every record class: what calls bind to, known before the types are.</summary>
  public static void Signatures(ModuleModel module) {
    foreach (var type in module.Classes.Values.Where(type => type.IsRecord)) {
      var prefix = module.IsMain ? "" : module.Name.Replace('.', '_') + "_";
      foreach (var method in type.Methods.Values)
        method.IrName = $"{prefix}{type.Name}.{method.Name}";

      if (type.Method("__init__") is { } init) {
        type.InitParameters.AddRange(init.Arguments);
      } else if (type.Kind is ClassKind.Dataclass or ClassKind.NamedTuple) {
        foreach (var field in type.Fields)
          type.InitParameters.Add(new Variable(field.Name, field.Node) { Default = field.Default });
      }
    }
  }

  /// <summary>
  /// Declares the record classes and the exception classes of a module once their attributes are
  /// typed; a field of a type an IR class cannot hold adds a diagnostic.
  /// </summary>
  public static void Records(ModuleModel module, LoweringContext context, List<Diagnostic> diagnostics) {
    // Every class is made before any field is: a field may hold an object of the class itself or of one declared later.
    foreach (var type in module.Classes.Values.Where(type => type.IsRecord)) {
      type.Ir = new IrClassType(type.Name) { IsSealed = true };
      type.Ir.Inherit(null, []);
    }

    foreach (var type in module.Classes.Values) {
      if (type.IsException) {
        var base_ = type.Base!.Kind == ClassKind.BuiltinException ? Exception(type.Base, context).Type : type.Base.Ir!;
        var declared = Declare(type.Name, base_, context);
        type.Ir = declared.Type;
        type.Constructor = declared.Constructor;
        continue;
      }

      if (!type.IsRecord)
        continue;

      var made = type.Ir!;
      var fields = new List<IrField>();
      foreach (var field in type.Fields) {
        var fieldType = PyTypes.Held(field.Type);
        if (!Holds(field.Type)) {
          diagnostics.Add(module.File.At(field.Node, PythonFrontend.UnsupportedClassDescriptor,
            $"'the attribute {field.Name} of {type.Name} of the type {field.Type}' is outside the first Python subset (dataclasses, simple final classes, enums): an attribute holds a number, a truth value, bytes, a tuple of numbers, an enum member or an object"));
          continue;
        }

        field.Ir = new IrField(field.Name, fieldType, type.IsFrozen);
        fields.Add(field.Ir);
      }

      made.Define(fields);
      context.Types.Add(made);
    }
  }

  /// <summary>Whether an attribute of a class can hold values of a type: what a field of an IR class can be.</summary>
  public static bool Holds(PyType type) => type.IsNumber || type.IsBytes || type.Kind is PyKind.Object or PyKind.Enum || (type.Kind == PyKind.Optional && type.Element!.Kind == PyKind.Object)
    || (type.Kind == PyKind.Tuple && type.Items.All(item => item.IsNumber || item.IsBytes || item.Kind is PyKind.Enum or PyKind.Tuple));

  /// <summary>
  /// Gives every record class its members: the IR methods of its methods, its constructor and the
  /// <c>__eq__</c> a dataclass compares its fields with; the functions made for them are the module's.
  /// </summary>
  public static void Members(ModuleModel module, LoweringContext context, FunctionLowering equality) {
    foreach (var type in module.Classes.Values.Where(type => type.IsRecord && type.Ir is not null)) {
      var methods = new List<IrMethod>();
      foreach (var method in type.Methods.Values.Where(method => method.Name != "__init__")) {
        var made = new IrMethod(method.Name, method.IrName, method.IsStatic) { Dispatch = method.IsStatic ? IrDispatch.Static : IrDispatch.Instance };
        type.IrMethods[method.Name] = made;
        methods.Add(made);
      }

      var prefix = module.IsMain ? "" : module.Name.Replace('.', '_') + "_";
      var constructorName = $"{prefix}{type.Name}.__init__";
      type.Constructor = new IrConstructor(constructorName);
      if (type.Method("__init__") is { } init) {
        init.IrName = constructorName;
      } else {
        var self = new IrLocal("self", type.Reference);
        var parameters = type.InitParameters.Select(parameter => new IrLocal(parameter.Name, PyTypes.Ir(parameter.Type))).ToList();
        var stores = type.Fields.Zip(parameters).Where(pair => pair.First.Ir is not null)
          .Select(pair => (IrStmt)new IrFieldStore(Ref(self), pair.First.Ir!, Fit(Ref(pair.Second), pair.First.Ir!.Type))).ToList();
        context.Made.Add(new IrFunction(constructorName, false, IrType.Void, [self, .. parameters], [], Block(stores)));
      }

      if (type.ComparesFields && type.Method("__eq__") is null) {
        var name = $"{prefix}{type.Name}.__eq__";
        var (self, other) = (new IrLocal("self", type.Reference), new IrLocal("other", type.Reference));
        var compared = type.Fields.Where(field => field.Ir is not null)
          .Select(field => equality.FieldEquality(new IrFieldLoad(Ref(self), field.Ir!), new IrFieldLoad(Ref(other), field.Ir!), field.Type))
          .Aggregate((IrExpr)Bool(true), (all, next) => Binary(IrBinaryOp.LogicalAnd, all, next));
        // A dataclass compares itself with another object of its class only; the import gives == two objects of one class.
        context.Made.Add(new IrFunction(name, false, IrType.Bool, [self, other], [], Block(Return(Binary(IrBinaryOp.LogicalOr, new IrReferenceEquals(Ref(self), Ref(other)), compared)))));
        var made = new IrMethod("__eq__", name, false) { Dispatch = IrDispatch.Instance };
        type.IrMethods["__eq__"] = made;
        methods.Add(made);
      }

      type.Ir!.DefineMembers([type.Constructor], methods, [], []);
    }
  }

  /// <summary>
  /// The class the module declares for a built-in exception the library has not - <c>LookupError</c>,
  /// <c>NotImplementedError</c>, <c>UnicodeEncodeError</c>, <c>struct.error</c> - derived from the class
  /// of the built-in Python derives it from; declared on first use.
  /// </summary>
  public static DeclaredException Exception(ClassModel builtin, LoweringContext context) {
    if (builtin.Ir is { IsLibrary: true } library)
      return new DeclaredException(library, IrExceptionClass.Constructor(library, 1));

    if (context.Exceptions.TryGetValue(builtin.Name, out var known))
      return known;

    var base_ = Exception(builtin.Base!, context).Type;
    // struct.error is no name a class of every target can have: C# warns of a name of lower-case letters only.
    var declared = Declare(builtin.Name == "error" ? "StructError" : builtin.Name, base_, context);
    builtin.Ir = declared.Type;
    builtin.Constructor = declared.Constructor;
    context.Exceptions[builtin.Name] = declared;
    return declared;
  }

  /// <summary>Declares an exception class derived from another, with the constructor that passes its message on.</summary>
  private static DeclaredException Declare(string name, IrClassType base_, LoweringContext context) {
    var type = new IrClassType(name);
    type.Inherit(base_, []);
    type.Define([]);
    var constructor = new IrConstructor($"{name}.__init__");
    var (self, message) = (new IrLocal("self", IrType.Object(type)), new IrLocal("message", IrType.String));
    var chained = base_.IsLibrary ? IrExceptionClass.Constructor(base_, 1).Function : base_.Constructors[0].Function;
    context.Made.Add(new IrFunction(constructor.Function, false, IrType.Void, [self, message], [], Block(new IrExprStmt(new IrCall(chained, IrType.Void, [Ref(self), Ref(message)])))));
    type.DefineMembers([constructor], [], [], []);
    context.Types.Add(type);
    return new DeclaredException(type, constructor);
  }

}
