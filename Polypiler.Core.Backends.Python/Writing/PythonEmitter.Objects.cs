using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>The module being written.</summary>
    private IrModule _module = new("", []);

    /// <summary>The class of every function of the module that is the body of a member: a constructor, a method or an accessor.</summary>
    private readonly Dictionary<string, IrClassType> _owners = new(StringComparer.Ordinal);

    /// <summary>The functions of the module by name.</summary>
    private readonly Dictionary<string, IrFunction> _byName = new(StringComparer.Ordinal);

    /// <summary>The Python name of every declared type of the module.</summary>
    private readonly Dictionary<IrTypeDeclaration, string> _typeNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>The name each field, property and method of a class is written with, by identity.</summary>
    private readonly Dictionary<object, string> _memberNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>The attribute every static field of a class is, by the name of its global.</summary>
    private readonly Dictionary<string, string> _staticNames = new(StringComparer.Ordinal);

    /// <summary>A static field of a class: <c>Class.attribute</c>.</summary>
    private PyExpression StaticField(IrGlobal global) => new PyAttribute(Name(this._typeNames[this._staticOwners[global.Name]]), this._staticNames[global.Name]);

    /// <summary>The struct types some field store of the module writes into, which are mutable dataclasses; every other one is frozen.</summary>
    private readonly HashSet<IrStructType> _mutableStructs = new(ReferenceEqualityComparer.Instance);

    /// <summary>The struct types and record classes the module compares by value, which get an <c>__eq__</c> of their own.</summary>
    private readonly HashSet<IrTypeDeclaration> _compared = new(ReferenceEqualityComparer.Instance);

    /// <summary>The class whose member is being written, or <see langword="null"/>.</summary>
    private IrClassType? _class;

    /// <summary>The receiver of the member being written, which is <c>self</c>; <see langword="null"/> in a function of the module.</summary>
    private IrLocal? _receiver;

    /// <summary>Whether the module declares types, whose annotations then have to be read lazily.</summary>
    private bool _declaresTypes;

    /// <summary>Whether a function of the module is the body of a member of a class or of a signature of an interface.</summary>
    private bool IsMember(IrFunction function) => this._owners.ContainsKey(function.Name) || this._signatures.Contains(function.Name);

    /// <summary>The functions that are signatures of interface methods, which have no body of their own.</summary>
    private readonly HashSet<string> _signatures = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes note of the declared types of a module: names the classes, structs, interfaces and enums
    /// in PascalCase, and every member of a class in snake_case - a private one behind an underscore,
    /// an override with the name of the method it overrides, an implementation of an interface method
    /// with the name of that method - each name once along the lineage of its class.
    /// </summary>
    private void NameTypes(IrModule module) {
      this._module = module;
      foreach (var function in module.Functions)
        this._byName.TryAdd(function.Name, function);

      // The library's exception classes are no types of a module: Python's own exceptions stand for them.
      foreach (var declaration in module.Types) {
        this._declaresTypes = true;
        var wanted = TypeName(declaration.Name);
        this._typeNames[declaration] = this.Take(wanted);
      }

      foreach (var contract in module.Types.OfType<IrInterfaceType>()) {
        foreach (var function in contract.Functions)
          this._signatures.Add(function);

        foreach (var method in contract.Methods)
          this._memberNames[method] = PythonNaming.Local(method.Name);

        foreach (var property in contract.Properties)
          this._memberNames[property] = PythonNaming.Local(property.Name);
      }

      // A class is named after its base, whose names it may not take.
      var taken = new Dictionary<IrClassType, HashSet<string>>(ReferenceEqualityComparer.Instance);
      foreach (var type in module.Types.OfType<IrClassType>().OrderBy(type => type.Lineage.Count())) {
        foreach (var function in type.Functions)
          this._owners[function] = type;

        foreach (var property in type.Properties) {
          if (property.Getter is { } getter)
            this._owners[getter.Function] = type;

          if (property.Setter is { } setter)
            this._owners[setter.Function] = type;
        }

        var names = type.Base is { IsLibrary: false } @base && taken.TryGetValue(@base, out var inherited)
          ? new HashSet<string>(inherited, StringComparer.Ordinal)
          : new HashSet<string>(["args", "with_traceback", "add_note"], StringComparer.Ordinal);
        taken[type] = names;
        string Named(string name, IrVisibility visibility) {
          var snake = PythonNaming.Snake(name).Trim('_');
          // An attribute may have the name of a built-in function: only a keyword takes an underscore.
          var bare = snake.Length == 0 ? "member" : PythonNaming.Usable(snake) == snake + "_" && !PythonNaming.Keywords.Contains(snake) ? snake : PythonNaming.Usable(snake);
          var wanted = (visibility is IrVisibility.Private or IrVisibility.Protected ? "_" : "") + bare;
          var chosen = PythonNaming.Free(wanted, candidate => names.Contains(candidate) || candidate.StartsWith("__", StringComparison.Ordinal));
          names.Add(chosen);
          return chosen;
        }

        foreach (var field in type.Fields)
          this._memberNames[field] = Named(type.PropertyOf(field) is { } property ? property.Name : field.Name, type.PropertyOf(field)?.Visibility ?? field.Visibility);

        foreach (var property in type.Properties.Where(property => property.BackingField is null))
          this._memberNames[property] = property.Getter?.Overrides is { } overridden && this.PropertyOfMethod(overridden) is { } root && this._memberNames.TryGetValue(root, out var rootName)
            ? rootName
            : this.InterfaceName(type, property) ?? Named(property.Name, property.Visibility);

        foreach (var method in type.Methods.Where(method => method.Kind == IrMethodKind.Method)) {
          if (method.Overrides is { } overridden && this._memberNames.TryGetValue(overridden.Root, out var name))
            this._memberNames[method] = name;
          else if (method.Implements.FirstOrDefault(member => this._memberNames.ContainsKey(member)) is { } member) {
            this._memberNames[method] = this._memberNames[member];
            names.Add(this._memberNames[member]);
          }
          else
            this._memberNames[method] = Named(method.Name, method.Visibility);
        }

        foreach (var global in type.StaticFields)
          this._staticNames[global.Name] = Named(global.Name.Contains('.') ? global.Name[(global.Name.LastIndexOf('.') + 1)..] : global.Name, IrVisibility.Public);
      }

      foreach (var function in module.Functions)
        foreach (var node in IrTree.Nodes(function.Body)) {
          switch (node) {
            case IrStructEquals equality when equality.Left.Type.Declaration is { } compared:
              this._compared.Add(compared);
              break;
            case IrGenericEquals equality when equality.Left.Type.Declaration is IrStructType or IrClassType { IsRecord: true }:
              this._compared.Add(equality.Left.Type.Declaration);
              break;
          }
        }

      foreach (var function in module.Functions)
        foreach (var statement in Statements(function.Body))
          if (statement is IrFieldStore { Target.Type.StructType: { } written })
            this._mutableStructs.Add(written);

      // A struct holding a mutable struct is mutable as well: its copy has to copy the field.
      foreach (var type in module.Types.OfType<IrStructType>())
        if (type.Fields.Any(field => field.Type.IsArray || field.Type.IsObject))
          this._mutableStructs.Add(type);

      bool changed;
      do {
        changed = false;
        foreach (var type in module.Types.OfType<IrStructType>())
          if (!this._mutableStructs.Contains(type) && type.Fields.Any(field => field.Type.StructType is { } nested && this._mutableStructs.Contains(nested)))
            changed |= this._mutableStructs.Add(type);
      } while (changed);

      foreach (var type in module.Types)
        if (type is IrStructType { IsEquatable: true } or IrClassType { IsRecord: true, IsLibrary: false })
          this._compared.Add(type);

      // A struct or record in a collection is compared by value there: as a key, an element of a set, or by Contains of a list.
      foreach (var function in module.Functions)
        foreach (var node in IrTree.Nodes(function.Body))
          if (node.Type is { IsCollection: true } collection && (collection.Key ?? collection.Element)?.Declaration is IrStructType or IrClassType { IsRecord: true, IsLibrary: false })
            this._compared.Add((collection.Key ?? collection.Element)!.Declaration!);
    }

    /// <summary>The name of a member that implements a property of an interface, or <see langword="null"/>.</summary>
    private string? InterfaceName(IrClassType type, IrProperty property) {
      foreach (var contract in type.AllInterfaces)
        foreach (var member in contract.Properties)
          if (member.Name == property.Name && this._memberNames.TryGetValue(member, out var name))
            return name;

      return null;
    }

    /// <summary>The property an accessor method belongs to, in any class of the module.</summary>
    private IrProperty? PropertyOfMethod(IrMethod method)
      => this._module.Types.OfType<IrClassType>().Select(type => type.PropertyOf(method)).FirstOrDefault(property => property is not null);

    /// <summary>A name of a type in PascalCase: the part after the last dot of a nested name joined to the outer one, made usable.</summary>
    private static string TypeName(string name) {
      var parts = name.Split(['.', '+', '`', '<', '>', ',', ' '], StringSplitOptions.RemoveEmptyEntries);
      var joined = string.Concat(parts.Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
      var cleaned = new string([.. joined.Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_')]);
      return PythonNaming.Usable(cleaned.Length == 0 ? "Type" : cleaned);
    }

    /// <summary>Every statement of a block, at any depth, lambdas included.</summary>
    private static IEnumerable<IrStmt> Statements(IrStmt statement) {
      yield return statement;
      foreach (var block in statement is IrBlock own ? [own] : IrTree.Blocks(statement))
        foreach (var inner in block.Statements)
          foreach (var nested in Statements(inner))
            yield return nested;

      foreach (var lambda in IrTree.Expressions(statement).SelectMany(IrTree.Nodes).OfType<IrLambda>())
        foreach (var nested in Statements(lambda.Body))
          yield return nested;
    }

    /// <summary>The Python name of a declared type: a class of the module, or the exception class of the library a class stands for.</summary>
    private string TypeNameOf(IrTypeDeclaration declaration)
      => declaration is IrClassType { IsLibrary: true } library ? this.LibraryClass(library) : this._typeNames[declaration];

    /// <summary>The name of a member of a class or of a field of a struct, which <see cref="NameTypes"/> or <see cref="Struct"/> gave it.</summary>
    private string MemberName(object member) => this._memberNames[member];

    /// <summary>The name of the member a method call reaches: the method, or the property of an accessor.</summary>
    private string MethodName(IrMethod method) {
      if (method.Kind == IrMethodKind.Method)
        return this.MemberName(method);

      if (this.PropertyOfMethod(method) is { } property)
        return this.MemberName(property);

      // An accessor of an interface property.
      return this.MemberName(this._module.Types.OfType<IrInterfaceType>().Select(contract => contract.PropertyOf(method)).First(property => property is not null)!);
    }

    /// <summary>
    /// The declarations of the types of the module, each after the types it needs when it is defined:
    /// the enums, the structs (a struct after the structs of its fields), the interfaces and the classes,
    /// a class after its base and its interfaces.
    /// </summary>
    private List<PyStatement> Types(IrModule module) {
      var declared = new List<PyStatement>();
      foreach (var enumeration in module.Types.OfType<IrEnumType>())
        declared.Add(this.Enumeration(enumeration));

      var structs = module.Types.OfType<IrStructType>().ToList();
      var done = new HashSet<IrStructType>(ReferenceEqualityComparer.Instance);
      while (done.Count < structs.Count) {
        var next = structs.FirstOrDefault(type => !done.Contains(type) && type.Fields.All(field => field.Type.StructType is not { } nested || done.Contains(nested) || !structs.Contains(nested)))
          ?? structs.First(type => !done.Contains(type));
        done.Add(next);
        declared.Add(this.Struct(next));
      }

      var interfaces = module.Types.OfType<IrInterfaceType>().ToList();
      var placed = new HashSet<IrInterfaceType>(ReferenceEqualityComparer.Instance);
      while (placed.Count < interfaces.Count) {
        var next = interfaces.FirstOrDefault(type => !placed.Contains(type) && type.BaseInterfaces.All(@base => placed.Contains(@base) || !interfaces.Contains(@base)))
          ?? interfaces.First(type => !placed.Contains(type));
        placed.Add(next);
        declared.Add(this.Interface(next));
      }

      foreach (var type in module.Types.OfType<IrClassType>().OrderBy(type => type.Lineage.Count()))
        declared.Add(this.Class(type));

      this._class = null;
      return declared;
    }

    /// <summary>
    /// An interface as a class without state whose methods raise <c>NotImplementedError</c>: what a
    /// class implementing it derives from, so that a type test of the interface is <c>isinstance</c>.
    /// Its properties are no members of it.
    /// </summary>
    private PyClassDef Interface(IrInterfaceType type) {
      this.BeginFunction($"the interface '{type.Name}'");
      var body = new List<PyStatement>();
      foreach (var method in type.Methods.Where(method => method.Kind == IrMethodKind.Method)) {
        var signature = this._byName.GetValueOrDefault(method.Function);
        var parameters = new List<PyParameter> { new("self", null) };
        if (signature is not null)
          parameters.AddRange(signature.Parameters.Skip(1).Select(parameter => new PyParameter(this.LocalName(parameter), this.Annotation(parameter.Type))));

        var returns = signature is null ? new PyNone() : this.Annotation(signature.ReturnType);
        body.Add(new PyFunctionDef(this.MemberName(method), parameters, returns, [new PyRaise(new PyName("NotImplementedError"))]));
        this.BeginFunction($"the interface '{type.Name}'");
      }

      // A property is left out: a class implements it with an attribute as often as with an accessor,
      // and a property of the base would refuse the attribute.

      var bases = type.BaseInterfaces.Select(@base => (PyExpression)new PyName(this.TypeNameOf(@base))).ToList();
      return new PyClassDef(this._typeNames[type], bases, Spaced(body));
    }

    /// <summary>
    /// A struct as a dataclass: frozen where nothing writes a field of it, so that sharing a value
    /// cannot be observed and no copy is needed; otherwise mutable, and copied wherever the IR copies a
    /// value. The fields keep their order and default to the struct's default. A struct the module
    /// compares has the equality of <see cref="IrStructEquals"/>.
    /// </summary>
    private PyClassDef Struct(IrStructType type) {
      this.BeginFunction($"the struct '{type.Name}'");
      var body = new List<PyStatement>();
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var field in type.Fields) {
        var name = PythonNaming.Free(PythonNaming.Local(field.Name), names.Contains);
        names.Add(name);
        this._memberNames[field] = name;
        var value = field.Type.StructType is { } nested
          ? new PyCall(new PyName("field"), []) { Keywords = [new("default_factory", new PyName(this.TypeNameOf(nested)))] }
          : this.FieldDefault(field.Type);
        body.Add(new PyAssign(new PyName(name), value) { Annotation = this.Annotation(field.Type) });
      }

      var mutable = this._mutableStructs.Contains(type);
      if (mutable && type.Fields.Any(field => field.Type.StructType is { } nested && this._mutableStructs.Contains(nested))) {
        // copy.copy of the struct copies the structs it holds as well, which are values of their own.
        var copied = type.Fields.Select(field => field.Type.StructType is { } nested && this._mutableStructs.Contains(nested)
          ? new PyCall(new PyAttribute(new PyName("copy"), "copy"), [new PyAttribute(new PyName("self"), this._memberNames[field])])
          : (PyExpression)new PyAttribute(new PyName("self"), this._memberNames[field])).ToList();
        body.Add(new PyFunctionDef("__copy__", [new("self", null)], new PyName(this._typeNames[type]), [new PyReturn(new PyCall(new PyName(this._typeNames[type]), copied))]));
      }

      var compared = this._compared.Contains(type);
      if (compared)
        body.AddRange(this.ValueEquality(type, type.Fields));

      List<PyKeyword> options = [];
      if (compared)
        options.Add(new("eq", new PyBool(false)));

      if (!mutable)
        options.Add(new("frozen", new PyBool(true)));

      PyExpression decorator = options.Count == 0 ? new PyName("dataclass") : new PyCall(new PyName("dataclass"), []) { Keywords = options };
      return new PyClassDef(this._typeNames[type], [], Spaced(body)) { Decorators = [decorator] };
    }

    /// <summary>
    /// <c>__eq__</c> and <c>__hash__</c> of a value type: field by field as <see cref="IrStructEquals"/>
    /// says - a float as <c>Equals</c> compares it, NaN equal to NaN, an array by identity.
    /// </summary>
    private IEnumerable<PyStatement> ValueEquality(IrTypeDeclaration type, IReadOnlyList<IrField> fields) {
      var name = this._typeNames[type];
      PyExpression test = new PyCall(new PyName("isinstance"), [new PyName("other"), new PyName(name)]);
      foreach (var field in fields) {
        var member = this._memberNames[field];
        PyExpression mine = new PyAttribute(new PyName("self"), member);
        PyExpression theirs = new PyAttribute(new PyName("other"), member);
        PyExpression same = field.Type.IsFloat
          ? new PyBoolOp(Compare(mine, "==", theirs), "or", new PyBoolOp(Compare(mine, "!=", mine), "and", Compare(theirs, "!=", theirs)))
          : field.Type.IsArray || (field.Type.IsObject && field.Type.ClassType is not { IsRecord: true }) ? Compare(mine, "is", theirs)
          : Compare(mine, "==", theirs);
        test = new PyBoolOp(test, "and", same);
      }

      yield return new PyFunctionDef("__eq__", [new("self", null), new("other", new PyName("object"))], new PyName("bool"), [new PyReturn(test)]);
      var hashed = fields.Where(field => !field.Type.IsArray).Select(field => {
        PyExpression value = new PyAttribute(new PyName("self"), this._memberNames[field]);
        // A NaN hashes by identity from Python 3.10; every NaN equals every other here.
        return field.Type.IsFloat ? new PyConditional(Compare(value, "==", value), value, new PyFloat(0.0)) : value;
      }).ToList();
      yield return new PyFunctionDef("__hash__", [new("self", null)], new PyName("int"), [new PyReturn(new PyCall(new PyName("hash"), [new PyTuple(hashed)]))]);
    }

    /// <summary>The members of a class, one blank line in front of every method but the first statement (PEP 8).</summary>
    private static List<PyStatement> Spaced(List<PyStatement> body)
      => [.. body.Select((statement, index) => index > 0 && statement is PyFunctionDef ? statement with { Leading = PyTrivia.Blank } : statement)];

    /// <summary>The value a field of a new object or struct has before anything writes it.</summary>
    private PyExpression FieldDefault(IrType type)
      => type.StructType is { } nested ? new PyCall(new PyName(this.TypeNameOf(nested)), [])
        : type == IrType.Bool ? new PyBool(false)
        : type.IsNumeric ? this.Zero(type.Integral)
        : type.IsMemory ? this.EmptyView(type)
        : new PyNone();

    /// <summary>
    /// A class of the module: its base and the interfaces its base does not implement yet as bases,
    /// its constructors as <c>__init__</c>, its methods, its accessors as properties, and for a record
    /// the equality of its fields.
    /// </summary>
    private PyClassDef Class(IrClassType type) {
      this._class = type;
      var name = this._typeNames[type];
      var bases = new List<PyExpression>();
      if (type.Base is { } @base)
        bases.Add(new PyName(this.TypeNameOf(@base)));

      var inherited = type.Base?.AllInterfaces.ToHashSet() ?? [];
      foreach (var contract in type.Interfaces.Where(contract => !inherited.Contains(contract)))
        if (!type.Interfaces.Any(other => other != contract && other.Closure.Contains(contract)))
          bases.Add(new PyName(this.TypeNameOf(contract)));

      var body = new List<PyStatement>();
      body.AddRange(this.Constructors(type));
      foreach (var method in type.Methods.Where(method => method.Kind == IrMethodKind.Method && method.Dispatch != IrDispatch.Abstract)) {
        var written = this.Member(this._byName[method.Function], type, method.IsStatic) with { Name = this.MemberName(method) };
        body.Add(method.IsStatic ? written with { Decorators = [new PyName("staticmethod")] } : written);
      }

      foreach (var method in type.Methods.Where(method => method.Kind == IrMethodKind.Method && method.Dispatch == IrDispatch.Abstract)) {
        var written = this.Member(this._byName[method.Function] with { Body = new IrBlock([]) }, type, false);
        body.Add(written with { Name = this.MemberName(method), Body = [new PyRaise(new PyName("NotImplementedError"))] });
      }

      foreach (var property in type.Properties.Where(property => property.BackingField is null)) {
        var member = this.MemberName(property);
        if (property.Getter is { } getter && this._byName.GetValueOrDefault(getter.Function) is { } read)
          body.Add(this.Member(read, type, getter.IsStatic) with { Name = member, Decorators = [new PyName("property")] });

        if (property.Setter is { } setter && this._byName.GetValueOrDefault(setter.Function) is { } write) {
          // A setter without a getter of its own still needs the property it sets.
          if (property.Getter is null)
            body.Add(new PyFunctionDef(member, [new("self", null)], this.Annotation(property.Type), [new PyRaise(new PyName("AttributeError"))]) { Decorators = [new PyName("property")] });

          body.Add(this.Member(write, type, setter.IsStatic) with { Name = member, Decorators = [new PyAttribute(new PyName(member), "setter")] });
        }
      }

      if (this._compared.Contains(type))
        body.AddRange(this.ValueEquality(type, type.AllFields.Where(field => !IrExceptionClass.Exception.Fields.Contains(field)).ToList()));

      // An __init__ that does nothing but what the inherited one does is left out.
      if (body.Count > 0 && body[0] is PyFunctionDef { Name: "__init__", Parameters.Count: 1 } init
        && (init.Body.Count == 0 || init.Body is [PyExpressionStatement { Expression: PyCall { Callee: PyAttribute { Name: "__init__", Target: PyCall { Callee: PyName { Name: "super" } } }, Arguments.Count: 0 } }]))
        body.RemoveAt(0);

      this._class = null;
      return new PyClassDef(name, bases, Spaced(body));
    }

    /// <summary>
    /// The constructors of a class. One is <c>__init__</c>, which first gives every field of the class
    /// its default; several are methods of the class, private to it, that <c>__init__</c> chooses from
    /// by the number of its arguments, as JavaScript's one constructor does.
    /// </summary>
    private List<PyStatement> Constructors(IrClassType type) {
      var defaults = type.Fields.Select(field => (PyStatement)new PyAssign(new PyAttribute(new PyName("self"), this.MemberName(field)), this.FieldDefault(field.Type)) {
        Annotation = null
      }).ToList();
      var constructors = type.Constructors.Select(constructor => this._byName.GetValueOrDefault(constructor.Function)).OfType<IrFunction>().ToList();
      if (constructors.Count == 0) {
        var only = new List<PyStatement>(defaults);
        if (type.Base is not null)
          only.Insert(0, new PyExpressionStatement(new PyCall(new PyAttribute(new PyCall(new PyName("super"), []), "__init__"), [])));

        return only.Count == 0 ? [] : [new PyFunctionDef("__init__", [new("self", null)], new PyNone(), only)];
      }

      if (constructors.Count == 1) {
        var written = this.Member(constructors[0], type, false);
        return [written with { Name = "__init__", Returns = new PyNone(), Body = [.. defaults, .. written.Body] }];
      }

      var arities = constructors.Select(function => function.Parameters.Count - 1).ToList();
      if (arities.Distinct().Count() != arities.Count)
        this.Refuse(InexpressibleDescriptor, $"'constructors of one arity' of the class '{type.Name}' cannot be expressed in Python profile '{profile.Name}'");

      var result = new List<PyStatement>();
      IReadOnlyList<PyStatement> choice = [new PyRaise(new PyCall(new PyName("TypeError"), [new PyString($"no constructor of {type.Name} takes these arguments")]))];
      for (var index = constructors.Count - 1; index >= 0; --index) {
        var call = new PyExpressionStatement(new PyCall(new PyAttribute(new PyName("self"), this.Initializer(type, constructors[index].Name)), [new PyStarred(new PyName("args"))]));
        choice = [new PyIf(Compare(new PyCall(new PyName("len"), [new PyName("args")]), "==", Integer(arities[index])), [call], choice)];
      }

      result.Add(new PyFunctionDef("__init__", [new("self", null), new("*args", new PyName("object"))], new PyNone(), [.. defaults, .. choice]));
      foreach (var function in constructors)
        result.Add(this.Member(function, type, false) with { Name = this.Initializer(type, function.Name) });

      return result;
    }

    /// <summary>The method a constructor of a class with several is written as: <c>__init&lt;k&gt;</c>, which Python makes private to the class.</summary>
    private string Initializer(IrClassType type, string function)
      => "__init" + (type.Constructors.Select(constructor => constructor.Function).ToList().IndexOf(function) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A function of a member as a method: <c>self</c> for its receiver, then its parameters.</summary>
    private PyFunctionDef Member(IrFunction function, IrClassType owner, bool isStatic) {
      this._class = owner;
      this._receiver = isStatic ? null : function.Parameters[0];
      try {
        return this.Function(function, this._receiver);
      } finally {
        this._receiver = null;
      }
    }

    /// <summary>The expressions of objects and structs; <see langword="null"/> for any other.</summary>
    private PyExpression? ObjectExpression(IrExpr expression) {
      switch (expression) {
        case IrNew creation:
          return this.New(creation);
        case IrNull:
          return new PyNone();
        case IrIsNull test:
          return Compare(this.Expression(test.Value), "is", new PyNone());
        case IrReferenceEquals identity:
          return Compare(this.Expression(identity.Left), "is", this.Expression(identity.Right));
        case IrStructEquals equality:
          if (equality.Left.Type.Declaration is { } declaration)
            this._compared.Add(declaration);

          return Compare(this.Place(equality.Left), "==", this.Place(equality.Right));
        case IrFieldLoad load:
          return this.FieldLoad(load);
        case IrMethodCall call:
          return this.MethodCall(call);
        case IrTypeTest { Target.ClassType: { IsLibrary: true } library } test when library == IrExceptionClass.NullReferenceException:
          // Python's AttributeError or TypeError of a member of None, but no InvalidCastError.
          var tested = this.Temporary();
          return this.Test(library, new PyNamedExpression(tested, this.Expression(test.Value))) is PyBoolOp { Left: PyCall first, Right: var second }
            ? new PyBoolOp(first, "and", Rebound(second, tested)) : this.Inexpressible("TypeTest");
        case IrTypeTest test:
          return new PyCall(Name("isinstance"), [this.Expression(test.Value), Name(this.TypeNameOf(test.Target.Declaration!))]);
        case IrCast cast when cast.Type.Declaration is { } target:
          return this.Helper(PythonHelpers.Cast, this.Expression(cast.Value), Name(this.TypeNameOf(target)));
        case IrAsCast cast when cast.Type.Declaration is { } target:
          var value = this.Temporary();
          return new PyConditional(new PyCall(Name("isinstance"), [new PyNamedExpression(value, this.Expression(cast.Value)), Name(this.TypeNameOf(target))]), Name(value), new PyNone());
        case IrStructNew creation:
          return new PyCall(Name(this.TypeNameOf(creation.Type.Declaration!)), [.. creation.Arguments.Select(this.Expression)]);
        case IrStructDefault creation:
          return new PyCall(Name(this.TypeNameOf(creation.Type.Declaration!)), []);
        default:
          return null;
      }
    }

    /// <summary>A test that read the value through an assignment expression, reading the variable it was assigned to instead.</summary>
    private static PyExpression Rebound(PyExpression test, string variable)
      => PythonSyntaxRewriter.Rewrite(test, node => node is PyNamedExpression named && named.Name == variable ? Name(variable) : node);

    /// <summary>
    /// A new object: <c>Class(arguments)</c>; of a library exception class its Python exception, with
    /// the message .NET gives it where the source gives none and the inner exception as its cause.
    /// </summary>
    private PyExpression New(IrNew creation) {
      var type = creation.Type.ClassType!;
      var arguments = creation.Arguments.Select(this.Expression).ToList();
      if (type.IsLibrary) {
        var message = arguments.Count > 0 ? arguments[0] : new PyString(IrExceptionClass.DefaultMessage(type));
        var created = new PyCall(Name(this.LibraryClass(type)), [message]);
        return arguments.Count > 1 ? this.Helper(PythonHelpers.Caused, created, arguments[1]) : created;
      }

      return new PyCall(Name(this.TypeNameOf(type)), arguments);
    }

    /// <summary>
    /// A field read: <c>target.name</c>; the message of an exception is its first argument, the inner
    /// exception its <c>__cause__</c>; the length of an exception's message the length of that text.
    /// </summary>
    private PyExpression FieldLoad(IrFieldLoad load) {
      var target = this.Place(load.Target);
      if (ReferenceEquals(load.Field, IrExceptionClass.Message))
        return this.Helper(PythonHelpers.Message, target);

      if (ReferenceEquals(load.Field, IrExceptionClass.InnerException))
        return new PyAttribute(target, "__cause__");

      return new PyAttribute(target, this.MemberName(load.Field));
    }

    /// <summary>
    /// A method call. A static method is called on its class, an instance method on its receiver -
    /// Python selects the override as a virtual call does - and a base call on <c>super()</c>. A getter
    /// reads its property.
    /// </summary>
    private PyExpression MethodCall(IrMethodCall call) {
      var name = this.MethodName(call.Method);
      // A static method, of a class of the module: the frontends have no static accessors.
      if (call.Target is null)
        return new PyCall(new PyAttribute(Name(this.TypeNameOf(this._owners[call.Method.Function])), name), [.. call.Arguments.Select(this.Expression)]);

      // A base call is a call of the method of the base class on the receiver, which only a member of
      // the class makes, outside any closure (the frontends refuse base in a lambda): super().
      var receiver = call.Kind == IrMethodCallKind.Base ? new PyCall(Name("super"), []) : this.Place(call.Target);

      // A setter returns nothing, so it is a statement (ObjectStatement), never a value.
      if (call.Method.Kind == IrMethodKind.Getter)
        return new PyAttribute(receiver, name);

      return new PyCall(new PyAttribute(receiver, name), [.. call.Arguments.Select(this.Expression)]);
    }

    /// <summary>
    /// The statements of objects: a field write, a setter, and the call of a constructor of the base class
    /// or of another constructor of the class that begins a constructor; <see langword="false"/> for any other.
    /// </summary>
    private bool ObjectStatement(IrStmt statement, List<PyStatement> written) {
      switch (statement) {
        case IrFieldStore store:
          this.FieldStore(store, written);
          return true;
        case IrExprStmt { Expression: IrMethodCall { Method.Kind: IrMethodKind.Setter, Target: { } target } call }:
          var place = this.Place(target);
          written.Add(new PyAssign(new PyAttribute(place, this.MethodName(call.Method)), this.Expression(call.Arguments[0])));
          return true;
        case IrExprStmt { Expression: IrCall call } when IrExceptionClass.ConstructorOf(call.Function) is { } constructor:
          written.AddRange(this.LibraryChain(call, constructor));
          return true;
        case IrExprStmt { Expression: IrCall call } when this._owners.TryGetValue(call.Function, out var owner) && this._class is not null && owner.Constructors.Any(constructor => constructor.Function == call.Function):
          var arguments = call.Arguments.Skip(1).Select(this.Expression).ToList();
          // A constructor calls one of its base or, : this(…), another of its own class, which then has several.
          written.Add(new PyExpressionStatement(owner != this._class
            ? new PyCall(new PyAttribute(new PyCall(Name("super"), []), "__init__"), arguments)
            : new PyCall(new PyAttribute(Name("self"), this.Initializer(owner, call.Function)), arguments)));

          return true;
        default:
          return false;
      }
    }

    /// <summary>
    /// The call of a library constructor that begins the constructor of a class derived from an
    /// exception class: <c>super().__init__(message)</c>, with the message .NET gives where the source
    /// gives none, and the inner exception as the cause.
    /// </summary>
    private IEnumerable<PyStatement> LibraryChain(IrCall call, IrConstructor constructor) {
      var owner = IrExceptionClass.All.First(type => type.Constructors.Contains(constructor));
      var arguments = call.Arguments.Skip(1).Select(this.Expression).ToList();
      PyExpression message = arguments.Count > 0 ? arguments[0]
        : owner == IrExceptionClass.Exception
          ? Binary(Binary(new PyString("Exception of type '"), "+", new PyAttribute(new PyCall(Name("type"), [Name("self")]), "__name__")), "+", new PyString("' was thrown."))
          : new PyString(IrExceptionClass.DefaultMessage(owner));
      yield return new PyExpressionStatement(new PyCall(new PyAttribute(new PyCall(Name("super"), []), "__init__"), [message]));
      if (arguments.Count > 1)
        yield return new PyAssign(new PyAttribute(Name("self"), "__cause__"), arguments[1]);
    }

    /// <summary>
    /// A field write. Python evaluates the value before the object; where the value can have an effect
    /// and the object is no variable, the object is computed into a variable of its own first.
    /// </summary>
    private void FieldStore(IrFieldStore store, List<PyStatement> written) {
      var target = this.Place(store.Target);
      if (!IsPlain(store.Target) && store.Target is not IrFieldLoad { Target: IrLocalRef } && !IsQuiet(store.Value)) {
        var holder = this.Temporary();
        written.Add(new PyAssign(Name(holder), target));
        target = Name(holder);
      }

      written.Add(new PyAssign(new PyAttribute(target, this.MemberName(store.Field)), this.Expression(store.Value)));
    }

    /// <summary>
    /// A struct read where the value is used in place - the object a field is read from or written to,
    /// an operand of an equality - which is not copied; any other expression as it is written.
    /// </summary>
    private PyExpression Place(IrExpr expression) {
      this._inPlace = true;
      return this.Expression(expression);
    }

    /// <summary>Whether the expression about to be written is used in place, so that a struct read of it is no copy.</summary>
    private bool _inPlace;

    /// <summary>Whether an expression reads a struct from a place a later read or write can still see: a variable, a field, an element.</summary>
    private static bool IsStructPlace(IrExpr expression)
      => expression is IrLocalRef or IrFieldLoad or IrArrayLoad or IrGlobalRef or IrUnwrap or IrCoalesce
        or IrCollection { Op: IrCollectionOp.ListGet or IrCollectionOp.MapGet or IrCollectionOp.MapTryGet };

    /// <summary>A struct read from a place, copied where the struct is mutable: <c>copy.copy(value)</c>.</summary>
    private PyExpression Copied(IrExpr expression, PyExpression written)
      => expression.Type.StructType is { } type && this._mutableStructs.Contains(type) && IsStructPlace(expression)
        ? new PyCall(new PyAttribute(Name("copy"), "copy"), [written])
        : written;

  }

}
