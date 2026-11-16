using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The name of the trait every object of a hierarchy has, through which a value is tested for and converted to another type.</summary>
    private const string ObjectTrait = "PlyObject";

    /// <summary>The interfaces and the classes something derives from: each a trait, a value of it an <c>Rc&lt;dyn Trait&gt;</c>.</summary>
    private readonly HashSet<IrTypeDeclaration> _traits = new(ReferenceEqualityComparer.Instance);

    /// <summary>The classes with a base class, a class derived from them or an interface.</summary>
    private readonly HashSet<IrClassType> _hierarchy = new(ReferenceEqualityComparer.Instance);

    /// <summary>The functions of the methods of a hierarchy, each a function of the module with the object as a reference first, with the class that declares it.</summary>
    private readonly Dictionary<string, IrClassType> _free = new(StringComparer.Ordinal);

    /// <summary>The functions nothing writes: the methods of interfaces and the abstract methods, which have no body.</summary>
    private readonly HashSet<string> _bodiless = new(StringComparer.Ordinal);

    /// <summary>The types a value is tested for or converted to: each an <c>is_</c> and an <c>into_</c> method of <see cref="ObjectTrait"/>.</summary>
    private readonly HashSet<IrTypeDeclaration> _converted = new(ReferenceEqualityComparer.Instance);

    /// <summary>The members of the classes as they are written, by function.</summary>
    private IReadOnlyDictionary<string, RsFunction> _written = new Dictionary<string, RsFunction>();

    /// <summary>The name of the method of every slot: a virtual method's root, an interface's method.</summary>
    private readonly Dictionary<IrMethod, string> _slots = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether a type is a trait: an interface or a class something derives from.</summary>
    private bool IsPolymorphic(IrType type) => type.Declaration is { } declaration && this._traits.Contains(declaration);

    /// <summary>The struct of a class: its own name, with <c>Object</c> after it where the name is the trait of its hierarchy.</summary>
    private string StructName(IrClassType type) => this._types[type] + (this._traits.Contains(type) ? "Object" : "");

    /// <summary>
    /// Finds the hierarchies of the module: the interfaces and every class something derives from are
    /// traits; the methods of a class in a hierarchy are functions of the module that take the object
    /// as a reference, generic over the class's trait where it has one; abstract methods and the
    /// methods of interfaces have no function.
    /// </summary>
    private void Hierarchies(IrModule module) {
      foreach (var type in this._classes) {
        if (type.Base is { IsLibrary: false } parent)
          this._traits.Add(parent);
        foreach (var ancestor in type.Lineage.Skip(1).Where(ancestor => !ancestor.IsLibrary))
          this._traits.Add(ancestor);
      }

      foreach (var type in module.Types.OfType<IrInterfaceType>()) {
        this._traits.Add(type);
        foreach (var method in type.Methods) {
          this._bodiless.Add(method.Function);
          this._slots[method] = RustNames.Snake(method.Name, this.Edition);
        }
      }

      foreach (var type in this._classes.Where(type => type.Base is not null || this._traits.Contains(type) || type.Interfaces.Count > 0)) {
        this._hierarchy.Add(type);
        foreach (var method in type.Methods) {
          if (method.Dispatch == IrDispatch.Abstract)
            this._bodiless.Add(method.Function);
          else if (!method.IsStatic)
            this._free[method.Function] = type;
          if (method.IsVirtual && method.Overrides is null)
            this._slots[method] = RustNames.Snake(method.Name, this.Edition);
        }

        foreach (var constructor in type.Constructors)
          this._free[constructor.Function] = type;
      }
    }

    /// <summary>The traits a struct implements: its own class's, its base classes', its interfaces' and theirs.</summary>
    private IEnumerable<IrTypeDeclaration> TraitsOf(IrClassType type)
      => type.Lineage.Where(this._traits.Contains).Cast<IrTypeDeclaration>().Concat(type.AllInterfaces);

    /// <summary>The trait that declares a slot, and the slot's method name in it.</summary>
    private (string Trait, string Name) Slot(IrMethod method) {
      var root = method.Root;
      var owner = this._module.Types.FirstOrDefault(type => type is IrClassType classType && classType.Methods.Contains(root) || type is IrInterfaceType interfaceType && interfaceType.Methods.Contains(root));
      return owner is not null && this._slots.TryGetValue(root, out var name) ? (this._types[owner], name) : (((RsPath)this.Refuse("a virtual method outside the module")).Name, "");
    }

    /// <summary>The object of a hierarchy as a reference, for a method of its trait or a function of its class.</summary>
    /// <param name="value">The object.</param>
    /// <param name="generic">Whether the reference goes to a generic parameter, which takes the reference as it is, never through an <c>Rc</c>.</param>
    private RsExpression ObjectRef(IrExpr value, bool generic) {
      var (expression, held) = this.Object(value);
      RsExpression Deref(RsExpression shared) => generic
        ? new RsReference(false, new RsUnary("*", shared)) { Type = value.Type }
        : new RsReference(false, shared) { Type = value.Type };
      return held switch {
        Held.Borrowed or Held.Self => expression,
        Held.Value or Held.Place => Deref(this.Operand(expression, 13)),
        Held.Reference => generic ? new RsReference(false, new RsUnary("*", new RsUnary("*", expression))) { Type = value.Type } : expression,
        Held.OptionalValue => Deref(this.NullReference(expression)),
        Held.OptionalPlace => generic ? new RsReference(false, new RsUnary("*", new RsUnary("*", this.NonNull(expression)))) { Type = value.Type } : this.NonNull(expression),
        _ => this.Trap(IrTrap.NullReference)
      };
    }

    /// <summary>A call that the object's class selects the method of: the method of its slot's trait, <c>Trait::m(&amp;*o, ..)</c>.</summary>
    private RsExpression VirtualCall(IrMethodCall call) {
      var (trait, name) = this.Slot(call.Method);
      var arguments = new List<RsExpression> { this.ObjectRef(call.Target!, true) };
      var callee = this._module.Functions.FirstOrDefault(function => function.Name == call.Method.Function);
      arguments.AddRange(call.Arguments.Select((argument, index) => argument.Type.IsObject ? this.ObjectArgument(call.Method.Function, index + 1, argument)
        : argument.Type.IsString ? this.StringArgument(argument)
        : argument.Type.IsArray ? this.Borrow(argument, callee is not null && this._arrays.TryGetValue(callee.Name, out var facts) && facts.MutableParameters.Contains(index + 1))
        : this.Expression(argument)));
      return new RsCall(new RsPath($"{trait}::{name}"), arguments) { Type = call.Type };
    }

    /// <summary>A field of an object of a trait: through the accessor the trait of the field's class has, <c>Trait::field_f(o)</c>.</summary>
    private RsExpression FieldCell(IrExpr target, IrField field) {
      var owner = this._classes.FirstOrDefault(type => type.Fields.Contains(field));
      if (owner is null || !this.IsPolymorphic(target.Type))
        return new RsField(this.ObjectReceiver(target), this.FieldName(field));

      return new RsCall(new RsPath($"{this._types[owner]}::{this.Accessor(field)}"), [this.ObjectRef(target, true)]);
    }

    /// <summary>The name of the accessor of a field in its class's trait.</summary>
    private string Accessor(IrField field) => "field_" + this.FieldName(field);

    /// <summary><c>value is T</c>: true of a value of a type that is a T where it is not null; otherwise the <c>is_t</c> method.</summary>
    private RsExpression TypeTest(IrExpr value, IrType target) {
      if (value.Type.IsAssignableTo(target) && this.MayBeNull(value))
        return this.Method(this.Object(value).Expression, "is_some", IrType.Bool);

      var method = this.Conversion("is_", target);
      var (expression, held) = this.Object(value);
      return held switch {
        Held.OptionalPlace or Held.OptionalValue => this.Method(this.Method(expression, "as_deref", value.Type), "is_some_and", IrType.Bool, new RsPath($"{ObjectTrait}::{method}")),
        Held.Null => new RsLiteral("false") { Type = IrType.Bool },
        _ => this.Method(this.ObjectReceiver(value), method, IrType.Bool)
      };
    }

    /// <summary>
    /// <c>value as T</c>: a conversion upwards as the value itself; any other the <c>into_t</c> method of
    /// an <c>Rc</c> of it, <c>None</c> where it is no T; <c>(T)value</c> traps instead with <c>InvalidCast</c>,
    /// and keeps a null as it is.
    /// </summary>
    private (RsExpression Expression, Held Held) Conversion(IrExpr value, IrType target, bool trap) {
      if (value.Type.IsAssignableTo(target)) {
        var optional = this.MayBeNull(value);
        return (this.ObjectValue(value, optional, target), optional ? Held.OptionalValue : Held.Value);
      }

      var method = this.Conversion("into_", target);
      var path = new RsPath($"{ObjectTrait}::{method}");
      if (this.MayBeNull(value)) {
        var optional = this.ObjectValue(value, true);
        if (!trap)
          return (this.Method(optional, "and_then", target, path), Held.OptionalValue);

        return (new RsCall(new RsPath(this.StringHelper("ply_cast")), [optional, path]) { Type = target }, Held.OptionalValue);
      }

      var converted = this.Method(this.ObjectValue(value, false), method, target);
      return trap ? (this.Checked(converted, IrTrap.InvalidCast), Held.Value) : (converted, Held.OptionalValue);
    }

    /// <summary>The name of the method of <see cref="ObjectTrait"/> that tests for or converts to a type.</summary>
    private string Conversion(string prefix, IrType target) {
      if (target.Declaration is not { } declaration || !this._types.ContainsKey(declaration))
        return ((RsPath)this.Refuse($"a conversion to {target}")).Name;

      this._converted.Add(declaration);
      return prefix + RustNames.Snake(this._types[declaration], this.Edition);
    }

    /// <summary>
    /// The items of the hierarchies: <see cref="ObjectTrait"/> with a test and a conversion per type a
    /// value is tested for or converted to; a trait per interface - its methods - and per class
    /// something derives from - an accessor per field it declares, its slots, those it implements
    /// calling its function, and its other methods calling theirs; per class with objects of its own a
    /// struct of every field of its lineage, its constructors, and an implementation of every trait it has.
    /// </summary>
    private IEnumerable<RsItem> HierarchyItems() {
      if (this._hierarchy.Count == 0)
        yield break;

      var converted = this._module.Types.Where(this._converted.Contains).ToList();
      var root = converted.Count > 0;
      if (root) {
        var methods = new List<RsItem>();
        foreach (var target in converted) {
          var snake = RustNames.Snake(this._types[target], this.Edition);
          methods.Add(new RsFunction("is_" + snake, "", [new RsParameter("&self", "")], "bool", Abstract));
          methods.Add(new RsFunction("into_" + snake, "", [new RsParameter("self", "Rc<Self>")], $"Option<{this.SharedType(target)}>", Abstract));
        }

        yield return new RsTrait(ObjectTrait, "pub", [], methods);
      }

      foreach (var type in this._module.Types.Where(this._traits.Contains)) {
        this._construct = $"the type '{type.Name}'";
        var name = this._types[type];
        var supertraits = new List<string>();
        if (root)
          supertraits.Add(ObjectTrait);

        var items = new List<RsItem>();
        if (type is IrInterfaceType face) {
          supertraits.AddRange(face.BaseInterfaces.Select(other => this._types[other]));
          foreach (var method in face.Methods)
            items.Add(this.SlotDeclaration(method, null));
        } else {
          var classType = (IrClassType)type;
          if (classType.Base is { IsLibrary: false } parent)
            supertraits.Add(this._types[parent]);
          supertraits.AddRange(classType.Interfaces.Select(other => this._types[other]));
          foreach (var field in classType.Fields)
            items.Add(new RsFunction(this.Accessor(field), "", [new RsParameter("&self", "")], "&" + this.ClassFieldType(field), Abstract));
          foreach (var method in classType.Methods.Where(method => method.IsVirtual && method.Overrides is null))
            items.Add(this.SlotDeclaration(method, method.Dispatch == IrDispatch.Abstract ? null : method.Function));
        }

        yield return new RsTrait(name, "pub", supertraits, items);
      }

      foreach (var type in this._classes.Where(this._hierarchy.Contains).Where(type => !type.IsAbstract)) {
        this._construct = $"the class '{type.Name}'";
        var name = this.StructName(type);
        var fields = type.AllFields.Select(field => new RsFieldDeclaration(this.FieldName(field), "", this.ClassFieldType(field))).ToList();
        yield return new RsStruct(name, "pub", fields) { Attributes = ["derive(Default)"] };
        var makers = this._members.Where(pair => pair.Value.Class == type && pair.Value.Kind == MemberKind.Maker && this._written.ContainsKey(pair.Key))
          .Select(pair => (RsItem)this._written[pair.Key]).ToList();
        if (makers.Count > 0)
          yield return new RsImpl(null, name, makers);

        if (root) {
          var conversions = new List<RsItem>();
          foreach (var target in converted) {
            var snake = RustNames.Snake(this._types[target], this.Edition);
            var isOne = target is IrClassType targetClass ? type.DerivesFrom(targetClass) : type.AllInterfaces.Contains((IrInterfaceType)target);
            conversions.Add(new RsFunction("is_" + snake, "", [new RsParameter("&self", "")], "bool", new RsBlock([], new RsLiteral(isOne ? "true" : "false"))));
            conversions.Add(new RsFunction("into_" + snake, "", [new RsParameter("self", "Rc<Self>")], $"Option<{this.SharedType(target)}>",
              new RsBlock([], isOne ? new RsCall(new RsPath("Some"), [new RsPath("self")]) : new RsPath("None"))));
          }

          yield return new RsImpl(ObjectTrait, name, conversions);
        }

        foreach (var trait in TraitsOf(type).Distinct())
          yield return new RsImpl(this._types[trait], name, [.. this.Implementation(type, trait)]);
      }
    }

    private static readonly RsBlock Abstract = new([], null) { IsDeclaration = true };

    /// <summary><c>Rc&lt;dyn Trait&gt;</c> or <c>Rc&lt;Struct&gt;</c>.</summary>
    private string SharedType(IrTypeDeclaration type) => type switch {
      IrClassType classType when !this._traits.Contains(type) => $"Rc<{this.StructName(classType)}>",
      _ => $"Rc<dyn {this._types[type]}>"
    };

    /// <summary>A method of a trait: required, or with a body that calls its function.</summary>
    private RsFunction SlotDeclaration(IrMethod method, string? function) {
      var source = this._module.Functions.FirstOrDefault(candidate => candidate.Name == method.Function);
      var name = this._slots.GetValueOrDefault(method) ?? RustNames.Snake(method.Name, this.Edition);
      if (source is null)
        return new RsFunction(name, "", [new RsParameter("&self", "")], null, Abstract);

      var (parameters, returns) = this.Signature(source);
      var body = function is null ? Abstract : new RsBlock([], new RsCall(new RsPath(this._functions[function]), [new RsPath("self"), .. parameters.Skip(1).Select(parameter => (RsExpression)new RsPath(parameter.Name))]));
      return new RsFunction(name, "", parameters, returns, body);
    }

    /// <summary>The parameters and the result of a method of a trait, the object as <c>&amp;self</c>.</summary>
    private (List<RsParameter> Parameters, string? Returns) Signature(IrFunction source) {
      var saved = this._function;
      this._function = source;
      this._arrayFacts = this._arrays.GetValueOrDefault(source.Name) ?? new();
      var parameters = new List<RsParameter> { new("&self", "") };
      parameters.AddRange(source.Parameters.Skip(1).Select((parameter, index) => new RsParameter(RustNames.Snake(parameter.Name, this.Edition), this.ParameterType(source, index + 1))));
      var returns = this.ResultType(source);
      this._function = saved;
      return (parameters, returns);
    }

    /// <summary>The methods of a trait a struct implements: the accessors of the fields, and every slot whose method for the struct is not the trait's own.</summary>
    private IEnumerable<RsItem> Implementation(IrClassType type, IrTypeDeclaration trait) {
      if (trait is IrClassType classType) {
        foreach (var field in classType.Fields)
          yield return new RsFunction(this.Accessor(field), "", [new RsParameter("&self", "")], "&" + this.ClassFieldType(field),
            new RsBlock([], new RsReference(false, new RsField(new RsPath("self"), this.FieldName(field)))));

        foreach (var slot in classType.Methods.Where(method => method.IsVirtual && method.Overrides is null)) {
          var selected = type.Select(slot);
          if (ReferenceEquals(selected, slot) && slot.Dispatch != IrDispatch.Abstract)
            continue;

          yield return this.Delegate(slot, this._functions[selected.Function], null);
        }

        yield break;
      }

      foreach (var member in ((IrInterfaceType)trait).Methods) {
        if (type.Implementation(member) is not { } implementation) {
          this.Refuse("an interface method without an implementation");
          continue;
        }

        if (implementation.IsVirtual) {
          var (owner, name) = this.Slot(implementation);
          yield return this.Delegate(member, null, $"{owner}::{name}");
        } else {
          yield return this.Delegate(member, this._functions[implementation.Function], null);
        }
      }
    }

    /// <summary>A method of a trait for a struct that calls a function, or the method of another trait.</summary>
    private RsFunction Delegate(IrMethod slot, string? function, string? method) {
      var source = this._module.Functions.First(candidate => candidate.Name == slot.Function);
      var (parameters, returns) = this.Signature(source);
      var arguments = new List<RsExpression> { new RsPath("self") };
      arguments.AddRange(parameters.Skip(1).Select(parameter => (RsExpression)new RsPath(parameter.Name)));
      return new RsFunction(this._slots.GetValueOrDefault(slot.Root) ?? RustNames.Snake(slot.Name, this.Edition), "", parameters, returns,
        new RsBlock([], new RsCall(new RsPath(function ?? method!), arguments)));
    }

  }

}
