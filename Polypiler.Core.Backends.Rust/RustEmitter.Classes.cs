using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>What a function of the module is to the class it belongs to.</summary>
    private enum MemberKind {
      /// <summary>A method, a property accessor or a constructor's body: the object is its first parameter.</summary>
      Instance,
      /// <summary>A static method: no object.</summary>
      Static,
      /// <summary>The function that makes an object (<see cref="ObjectLowering.NewPrefix"/>).</summary>
      Maker
    }

    /// <summary>A function of the module that belongs to a class: the class, the name it has in the class's <c>impl</c> and what it is.</summary>
    private sealed record Member(IrClassType Class, string Name, MemberKind Kind, IrConstructor? Constructor = null);

    /// <summary>How a value of a class type is held where it is read.</summary>
    private enum Held {
      /// <summary>A new <c>Rc</c>, the expression's own.</summary>
      Value,
      /// <summary>A variable of type <c>Rc</c>.</summary>
      Place,
      /// <summary>A reference to an <c>Rc</c>: a parameter, or <c>self: &amp;Rc&lt;Self&gt;</c>.</summary>
      Reference,
      /// <summary><c>&amp;self</c>: the object without its <c>Rc</c>.</summary>
      Self,
      /// <summary>A reference to the object without its <c>Rc</c>: the object of a function of a hierarchy.</summary>
      Borrowed,
      /// <summary>A new <c>Option&lt;Rc&gt;</c>.</summary>
      OptionalValue,
      /// <summary>A variable of type <c>Option&lt;Rc&gt;</c>.</summary>
      OptionalPlace,
      /// <summary><c>None</c>, the null of the source.</summary>
      Null,
      /// <summary>An object of a single-owner class: <c>self</c>, a variable, a parameter or a new object, never null.</summary>
      Plain
    }

    private Nullness _nullness = Nullness.Of(new IrModule("module", []), new HashSet<string>());

    private readonly Dictionary<string, Member> _members = new(StringComparer.Ordinal);

    /// <summary>The members whose receiver is <c>self: &amp;Rc&lt;Self&gt;</c>, since they pass the object on; every other one has <c>&amp;self</c>.</summary>
    private readonly HashSet<string> _sharedSelf = new(StringComparer.Ordinal);

    /// <summary>The paths of the standard library the module names, each brought in by a <c>use</c>.</summary>
    private readonly SortedSet<string> _uses = new(StringComparer.Ordinal);

    /// <summary>The classes of the module, the ones of the library (exceptions) left out.</summary>
    private List<IrClassType> _classes = [];

    /// <summary>
    /// Finds the members of every class, names them in the class's <c>impl</c> and finds out which
    /// pass their object on, which needs its <c>Rc</c>: a member that uses <c>this</c> otherwise than to
    /// read or write a field or to call a member that needs no <c>Rc</c>.
    /// </summary>
    private void Classes(IrModule module) {
      this._classes = [.. module.Types.OfType<IrClassType>().Where(type => !type.IsLibrary && !IrExceptionClass.IsException(type))];
      this.Hierarchies(module);
      var functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
      foreach (var type in this._classes) {
        // A class of a hierarchy keeps only its makers in its impl; its methods are functions of the module.
        if (this._hierarchy.Contains(type)) {
          foreach (var (constructor, index) in type.Constructors.Select((constructor, index) => (constructor, index)).Where(_ => !type.IsAbstract)) {
            var name = "new" + (index == 0 ? "" : "_" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (functions.ContainsKey(ObjectLowering.NewPrefix + constructor.Function)) {
              this._members[ObjectLowering.NewPrefix + constructor.Function] = new Member(type, name, MemberKind.Maker, constructor);
              this._functions[ObjectLowering.NewPrefix + constructor.Function] = name;
            }
          }

          continue;
        }

        var members = new List<(string Function, string Name, MemberKind Kind, IrConstructor? Constructor)>();
        foreach (var (constructor, index) in type.Constructors.Select((constructor, index) => (constructor, index))) {
          var suffix = index == 0 ? "" : "_" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
          members.Add((ObjectLowering.NewPrefix + constructor.Function, "new" + suffix, MemberKind.Maker, constructor));
          members.Add((constructor.Function, "init" + suffix, MemberKind.Instance, constructor));
        }

        var methods = type.Methods.Concat(type.Properties.SelectMany(property => new[] { property.Getter, property.Setter }).OfType<IrMethod>())
          .DistinctBy(method => method.Function).ToList();
        var taken = new HashSet<string>(members.Select(member => member.Name), StringComparer.Ordinal);
        var names = RustNames.Unique([.. methods.Select(method => method.Name)], name => RustNames.Snake(name, this.Edition), taken);
        foreach (var (method, name) in methods.Zip(names))
          members.Add((method.Function, name, method.IsStatic ? MemberKind.Static : MemberKind.Instance, null));

        foreach (var (function, name, kind, constructor) in members.Where(member => functions.ContainsKey(member.Function))) {
          this._members[function] = new Member(type, name, kind, constructor);
          this._functions[function] = name;
        }
      }

      var receivers = this._members.Where(pair => pair.Value.Kind == MemberKind.Instance).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
      this._nullness = Nullness.Of(module, new HashSet<string>(receivers.Concat(this._free.Keys), StringComparer.Ordinal));
      for (var changed = true; changed;) {
        changed = false;
        foreach (var name in receivers.Where(name => !this._sharedSelf.Contains(name)))
          if (this.PassesOn(functions[name]))
            changed |= this._sharedSelf.Add(name);
      }
    }

    /// <summary>Whether a member uses its object otherwise than through a field or a member that needs no <c>Rc</c>.</summary>
    private bool PassesOn(IrFunction function) {
      // An iterator method keeps its object in its struct: an Rc of its own.
      if (IrSequences.IsIterator(function))
        return true;

      var self = function.Parameters[0];
      var nodes = ArrayOwnership.Statements(function.Body).SelectMany(statement => IrTree.Expressions(statement)).SelectMany(IrTree.Nodes).ToList();
      var uses = nodes.Count(node => node is IrLocalRef reference && reference.Local == self);
      var plain = nodes.Count(node => node is IrFieldLoad { Target: IrLocalRef target } && target.Local == self
        || node is IrCall { Arguments: [IrLocalRef receiver, ..] } call && receiver.Local == self && this._members.TryGetValue(call.Function, out var member)
          && member.Kind == MemberKind.Instance && !this._sharedSelf.Contains(call.Function));
      plain += ArrayOwnership.Statements(function.Body).Count(statement => statement is IrFieldStore { Target: IrLocalRef reference } && reference.Local == self);
      return uses > plain;
    }

    /// <summary>The fields some function of the module reads or writes.</summary>
    private HashSet<IrField>? _touchedFields;

    /// <summary>
    /// The fields of a class the struct keeps: those some function of the module reads or writes. A field
    /// nothing touches keeps its default for good, which no one can tell apart from no field at all, and
    /// Rust would warn about it as dead code; the equality of a record compares it to the same default.
    /// </summary>
    private IReadOnlyList<IrField> KeptFields(IrClassType type) {
      this._touchedFields ??= [.. this._module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrFieldLoad>().Select(load => load.Field)
        .Concat(this._module.Functions.SelectMany(function => IrClosures.Within(function.Body)).OfType<IrFieldStore>().Select(store => store.Field))];
      return [.. type.AllFields.Where(this._touchedFields.Contains)];
    }

    /// <summary>Whether a variable of the function being written may hold null.</summary>
    private bool MayBeNull(IrLocal local) => this._nullness.MayBeNull(this._function.Name, local);

    /// <summary>Whether a value in the function being written may be null.</summary>
    private bool MayBeNull(IrExpr value) => this._nullness.MayBeNull(this._function.Name, value);

    /// <summary>The Rust name of a class.</summary>
    private string ClassName(IrType type) => type.Declaration is { } declaration && this._types.TryGetValue(declaration, out var name)
      ? this._traits.Contains(declaration) ? "dyn " + name : this.StructName((IrClassType)declaration)
      : ((RsPath)this.Refuse($"the type {type}")).Name;

    /// <summary><c>Rc&lt;Name&gt;</c>, or <c>Option&lt;Rc&lt;Name&gt;&gt;</c> for a reference that may be null.</summary>
    private string ObjectType(IrType type, bool optional) {
      if (this.IsPlain(type) && !optional)
        return this.ClassName(type);

      this._uses.Add("std::rc::Rc");
      var shared = $"Rc<{this.ClassName(type)}>";
      return optional ? $"Option<{shared}>" : shared;
    }

    /// <summary>Whether a variable of a class type holds the object as a reference to an <c>Rc</c>: a parameter that is not null and not assigned.</summary>
    private bool IsObjectReference(IrLocal local) => !this._owned.Contains(local) && this._function.Parameters.Contains(local) && !this.MayBeNull(local) && !this._reassigned.Contains(local) && !this.IsPolymorphic(local.Type);

    /// <summary>The type of a parameter of a class type: <c>&amp;Rc&lt;T&gt;</c>, or <c>Option&lt;Rc&lt;T&gt;&gt;</c> where it may be null.</summary>
    private string ObjectParameterType(IrLocal parameter)
      => this.MayBeNull(parameter) || this.IsPolymorphic(parameter.Type) ? this.ObjectType(parameter.Type, this.MayBeNull(parameter)) : "&" + this.ObjectType(parameter.Type, false);

    /// <summary>The members of the module whose object is the first parameter: the function being written, if it is one.</summary>
    private Member? CurrentMember => this._members.GetValueOrDefault(this._function.Name);

    /// <summary>A value of a class type and how it is held.</summary>
    private (RsExpression Expression, Held Held) Object(IrExpr value) {
      if (this.IsPlain(value.Type))
        return (this.PlainObject(value), Held.Plain);

      switch (value) {
        case IrNull:
          return (new RsPath("None") { Type = value.Type }, Held.Null);
        case IrLocalRef reference when this.CurrentMember is { Kind: MemberKind.Instance } && this._function.Parameters[0] == reference.Local && !this.IsKept(reference.Local):
          return (new RsPath("self") { Type = value.Type }, this._sharedSelf.Contains(this._function.Name) ? Held.Reference : Held.Self);
        case IrLocalRef reference when this._free.ContainsKey(this._function.Name) && this._function.Parameters[0] == reference.Local:
          return (new RsPath(this._locals[reference.Local]) { Type = value.Type }, Held.Borrowed);
        case IrCast cast:
          return this.Conversion(cast.Value, cast.Type, true);
        case IrAsCast cast:
          return this.Conversion(cast.Value, cast.Type, false);
        case IrLocalRef reference: {
          var path = new RsPath(this._locals[reference.Local]) { Type = value.Type };
          return (path, this.MayBeNull(reference.Local) ? Held.OptionalPlace : this.IsObjectReference(reference.Local) ? Held.Reference : Held.Place);
        }
        case IrCall call:
          return (this.Expression(call), this._nullness.ReturnsNull(call.Function) ? Held.OptionalValue : Held.Value);
        case IrFieldLoad load:
          return (this.Expression(load), Held.OptionalValue);
        case IrConditional choice when this.MayBeNull(choice):
          return (this.Choice(choice, branch => this.ObjectValue(branch, true)), Held.OptionalValue);
        case IrConditional choice:
          return (this.Choice(choice, branch => this.ObjectValue(branch, false)), Held.Value);
        default:
          return (this.Refuse(value.GetType().Name[2..] + " of a class"), Held.Value);
      }
    }

    private RsExpression NullReference(RsExpression optional) => this.Checked(optional, IrTrap.NullReference);

    private RsExpression Cloned(RsExpression shared) {
      this._uses.Add("std::rc::Rc");
      return new RsCall(new RsPath("Rc::clone"), [shared]) { Type = shared.Type };
    }

    /// <summary>
    /// A value of a class type as a place of a type owns it: as <see cref="ObjectValue(IrExpr, bool)"/>
    /// does, converted to the place's trait where the value's own is another (an interface of its
    /// class, a base class), which an <c>Rc</c> of a struct is by itself.
    /// </summary>
    private RsExpression ObjectValue(IrExpr value, bool optional, IrType target) {
      if (this.IsUpcast(value, optional, target))
        return this.Upcast(this.ObjectValue(value, optional), optional, target);
      if (!this.IsPolymorphic(target) || !this.IsPolymorphic(value.Type) || value.Type == target || value is IrNull)
        return this.ObjectValue(value, optional);
      if (optional)
        return this.Method(this.ObjectValue(value, true), "and_then", target, new RsPath($"{ObjectTrait}::{this.Conversion("into_", target)}"));

      return this.Checked(this.Method(this.ObjectValue(value, false), this.Conversion("into_", target), target), IrTrap.InvalidCast);
    }

    /// <summary>A value of a class type as a variable or a result of a type holds it: converted to the type's trait where it is an <c>Option</c> of a struct, else as it is.</summary>
    private RsExpression ObjectValueAs(IrExpr value, bool optional, IrType target)
      => this.IsUpcast(value, optional, target) ? this.Upcast(this.ObjectValue(value, optional), optional, target) : this.ObjectValue(value, optional);

    /// <summary>Whether a value is of a struct and goes to a place of a trait.</summary>
    private bool IsUpcast(IrExpr value, bool optional, IrType target)
      => value is not IrNull && value.Type != target && this.IsPolymorphic(target) && !this.IsPolymorphic(value.Type) && !this.IsPlain(value.Type);

    /// <summary>
    /// An <c>Option&lt;Rc&lt;B&gt;&gt;</c> of a struct as the <c>Option</c> of a trait it implements:
    /// <c>b.map(|o| o as Rc&lt;dyn A&gt;)</c>, as an <c>Rc</c> of a struct is converted by itself and an
    /// <c>Option</c> of one is not; <c>Some(B::new())</c> is left as it is, its <c>Rc</c> converted where it is made.
    /// An <c>Rc</c> that is never null is converted by itself, apart from <c>Rc::clone(&amp;b)</c>, which Rust would
    /// make an <c>Rc&lt;dyn A&gt;</c> of the clone's own type parameter and then reject <c>&amp;b</c>: it is cast,
    /// <c>Rc::clone(&amp;b) as Rc&lt;dyn A&gt;</c>.
    /// </summary>
    private RsExpression Upcast(RsExpression shared, bool optional, IrType target) => shared switch {
      RsCall { Callee: RsPath { Name: "Some" } } => shared,
      RsCall { Callee: RsPath { Name: "Rc::clone" } } when !optional => new RsCast(shared, this.ObjectType(target, false)) { Type = target },
      _ when !optional => shared,
      _ => this.Method(shared, "map", target, new RsClosure(false, [new RsParameter("o", "")], new RsCast(new RsPath("o"), this.ObjectType(target, false))))
    };

    /// <summary>A value of a class type as a place owns it: <c>Rc&lt;T&gt;</c>, or <c>Option&lt;Rc&lt;T&gt;&gt;</c> where it may be null.</summary>
    private RsExpression ObjectValue(IrExpr value, bool optional) {
      var (expression, held) = this.Object(value);
      RsExpression Some(RsExpression shared) => optional ? new RsCall(new RsPath("Some"), [shared]) { Type = value.Type } : shared;
      return held switch {
        Held.Value or Held.Plain => Some(expression),
        Held.Place => Some(this.Cloned(new RsReference(false, expression))),
        Held.Reference => Some(this.Cloned(expression)),
        Held.OptionalValue => optional ? expression : this.NullReference(expression),
        Held.OptionalPlace => optional ? new RsMethodCall(expression, "clone", []) { Type = value.Type } : this.NullReference(new RsMethodCall(expression, "clone", [])),
        Held.Null => optional ? expression : this.Trap(IrTrap.NullReference),
        _ => this.Refuse("the object of a method that is no Rc")
      };
    }

    /// <summary>A value of a class type as a reference to its <c>Rc</c>, for a parameter that is never null.</summary>
    private RsExpression ObjectBorrow(IrExpr value) {
      var (expression, held) = this.Object(value);
      return held switch {
        Held.Value => new RsReference(false, expression) { Type = value.Type },
        Held.Place => new RsReference(false, expression) { Type = value.Type },
        Held.Reference => expression,
        Held.OptionalValue => new RsReference(false, this.NullReference(expression)) { Type = value.Type },
        Held.OptionalPlace => this.NonNull(expression),
        Held.Null => this.Trap(IrTrap.NullReference),
        _ => this.Refuse("the object of a method that is no Rc")
      };
    }

    /// <summary>
    /// <c>ply_non_null(&amp;x)</c>: the object a variable that may be null holds, a null one trapping -
    /// a function rather than <c>x.as_ref().expect(..)</c> in place, which clippy's
    /// <c>unnecessary_unwrap</c> flags after a test of the variable it cannot see the reason for.
    /// </summary>
    private RsExpression NonNull(RsExpression optional) => this.Results
      ? this.Checked(new RsMethodCall(this.Operand(optional, 15), "as_ref", []) { Type = optional.Type }, IrTrap.NullReference)
      : new RsCall(new RsPath(this.StringHelper("ply_non_null")), [new RsReference(false, optional)]) { Type = optional.Type };

    /// <summary>A value of a class type as the receiver of a method or a field: the object, a null one trapping.</summary>
    private RsExpression ObjectReceiver(IrExpr value) {
      var (expression, held) = this.Object(value);
      return held switch {
        Held.OptionalValue => this.NullReference(expression),
        Held.OptionalPlace => this.NonNull(expression),
        Held.Null => this.Trap(IrTrap.NullReference),
        _ => this.Receiver(expression)
      };
    }

    /// <summary>A value of a class type as an argument: a reference to the <c>Rc</c>, or the <c>Option</c> where the parameter may be null.</summary>
    private RsExpression ObjectArgument(string callee, int index, IrExpr value) {
      if (index == 0 && this._members.TryGetValue(callee, out var member) && member.Kind == MemberKind.Instance)
        return new RsPath("self");
      if (index == 0 && this._free.TryGetValue(callee, out var owner))
        return this.ObjectRef(value, this._traits.Contains(owner));
      if (this.IsPlain(value.Type))
        return this.PlainArgument(callee, index, value);

      var function = this._module.Functions.FirstOrDefault(function => function.Name == callee);
      if (function is null || index >= function.Parameters.Count)
        return this.ObjectBorrow(value);

      var parameter = function.Parameters[index];
      var optional = this._nullness.MayBeNull(callee, parameter);
      return optional || this.IsPolymorphic(parameter.Type) ? this.ObjectValue(value, optional, parameter.Type) : this.ObjectBorrow(value);
    }

    /// <summary>A value of a class type as one comparison operand of two: an <c>Option&lt;&amp;Rc&lt;T&gt;&gt;</c>, or the reference itself where both are never null.</summary>
    private RsExpression ObjectOperand(IrExpr value, bool optional) {
      if (!optional)
        return this.ObjectBorrow(value);

      var (expression, held) = this.Object(value);
      return held switch {
        Held.OptionalValue or Held.OptionalPlace => new RsMethodCall(this.Receiver(expression), "as_ref", []) { Type = value.Type },
        Held.Null => expression,
        _ => new RsCall(new RsPath("Some"), [this.ObjectBorrow(value)]) { Type = value.Type }
      };
    }

    /// <summary>Whether a value of a class type is <c>null</c> itself.</summary>
    private static bool IsNullLiteral(IrExpr value) => value is IrNull;

    /// <summary>
    /// <c>ReferenceEquals(a, b)</c>: <c>Rc::ptr_eq</c> of two objects; a comparison with <c>null</c> is
    /// <c>is_none()</c>, or <c>false</c> of a value that never is; two that may be null are compared
    /// by the addresses they hold, <c>None</c> equal to <c>None</c>.
    /// </summary>
    private RsExpression SameObject(IrExpr left, IrExpr right) {
      if (IsNullLiteral(left) || IsNullLiteral(right))
        return this.IsNull(IsNullLiteral(left) ? right : left);

      this._uses.Add("std::rc::Rc");
      if (!this.MayBeNull(left) && !this.MayBeNull(right))
        return new RsCall(new RsPath("Rc::ptr_eq"), [this.ObjectBorrow(left), this.ObjectBorrow(right)]) { Type = IrType.Bool };

      RsExpression Address(IrExpr value) => new RsMethodCall(this.ObjectOperand(value, true), "map", [new RsPath("Rc::as_ptr")]);
      return this.Binary(Address(left), "==", Address(right), IrType.Bool);
    }

    /// <summary>A test for <c>null</c>: <c>is_none()</c>, or <c>false</c> of a value that never is.</summary>
    private RsExpression IsNull(IrExpr value) {
      if (IsNullLiteral(value))
        return new RsLiteral("true") { Type = IrType.Bool };

      var (expression, held) = this.Object(value);
      return held is Held.OptionalPlace or Held.OptionalValue
        ? new RsMethodCall(this.Receiver(expression), "is_none", []) { Type = IrType.Bool }
        : new RsLiteral("false") { Type = IrType.Bool };
    }

    /// <summary>The value equality of two records: the fields compared, <c>null</c> equal to <c>null</c> only.</summary>
    private RsExpression ObjectEquals(IrExpr left, IrExpr right) {
      if (IsNullLiteral(left) || IsNullLiteral(right))
        return this.IsNull(IsNullLiteral(left) ? right : left);

      var optional = this.MayBeNull(left) || this.MayBeNull(right);
      if (optional)
        return this.Binary(this.ObjectOperand(left, true), "==", this.ObjectOperand(right, true), IrType.Bool);

      // Two objects compared as the Rc they are, a reference to one through it.
      RsExpression Shared(IrExpr value) => this.Object(value) switch {
        (var expression, Held.Reference) => new RsUnary("*", expression) { Type = value.Type },
        (_, Held.Self) => this.Refuse("the object of a method that is no Rc"),
        (var expression, _) => expression
      };
      return this.Binary(Shared(left), "==", Shared(right), IrType.Bool);
    }

    /// <summary>Whether a field is a <c>Cell</c>, which holds a value copied in and out, rather than a <c>RefCell</c>.</summary>
    private static bool IsCellField(IrField field) => IsValue(field.Type);

    /// <summary>The Rust type of a field of a class: a value in a <c>Cell</c>, anything else in a <c>RefCell</c>.</summary>
    private string ClassFieldType(IrField field) {
      if (IsCellField(field)) {
        this._uses.Add("std::cell::Cell");
        return $"Cell<{this.Type(field.Type)}>";
      }

      this._uses.Add("std::cell::RefCell");
      return field.Type switch {
        { IsObject: true } => $"RefCell<{this.ObjectType(field.Type, true)}>",
        { IsString: true } => $"RefCell<{OwnedString}>",
        { IsArray: true } => $"RefCell<Vec<{this.ElementType(field.Type)}>>",
        _ => $"RefCell<{((RsPath)this.Refuse($"a field of the type {field.Type}")).Name}>"
      };
    }

    /// <summary>A field of an object read: <c>o.f.get()</c> of a value, a copy of anything else.</summary>
    private RsExpression ObjectField(IrFieldLoad load) {
      if (this.IsPlain(load.Target.Type))
        return this.PlainField(load);

      var field = this.FieldCell(load.Target, load.Field);
      return IsCellField(load.Field)
        ? new RsMethodCall(field, "get", []) { Type = load.Type }
        : new RsMethodCall(new RsMethodCall(field, "borrow", []), "clone", []) { Type = load.Type };
    }

    /// <summary>
    /// A field of an object written: <c>o.f.set(v)</c> of a value; of anything else the value first,
    /// then <c>*o.f.borrow_mut() = v</c>, so that no borrow the value takes is still held.
    /// </summary>
    private void ObjectFieldStore(IrFieldStore store, List<RsStatement> into) {
      if (this.IsPlain(store.Target.Type)) {
        this.PlainFieldStore(store, into);
        return;
      }

      var field = this.FieldCell(store.Target, store.Field);
      if (IsCellField(store.Field)) {
        into.Add(new RsExpressionStatement(new RsMethodCall(field, "set", [this.Expression(store.Value)])));
        return;
      }

      var value = store.Field.Type.IsObject ? this.ObjectValue(store.Value, true, store.Field.Type)
        : store.Field.Type.IsArray ? this.Vector(store.Value, false)
        : this.StringValue(store.Value);
      if (IrTree.Nodes(store.Value).Any(node => node is IrFieldLoad or IrCall)) {
        var name = RustNames.Unique(["value"], name => name, this._taken)[0];
        into.Add(new RsLet(name, false, null, value));
        value = new RsPath(name);
      }

      into.Add(new RsExpressionStatement(new RsAssign(new RsUnary("*", new RsMethodCall(field, "borrow_mut", [])), "=", value)));
    }

    /// <summary>An array a field of an object holds, borrowed from its <c>RefCell</c>: <c>o.f.borrow()</c>, <c>o.f.borrow_mut()</c>.</summary>
    private RsExpression FieldArray(IrFieldLoad load, bool mutable)
      => this.IsPlain(load.Target.Type) ? new RsField(this.Receiver(this.PlainObject(load.Target)), this.FieldName(load.Field)) { Type = load.Type }
        : new RsMethodCall(this.FieldCell(load.Target, load.Field), mutable ? "borrow_mut" : "borrow", []) { Type = load.Type };

    /// <summary>Whether an array is the one a field of an object holds.</summary>
    private static bool IsFieldArray(IrExpr array) => array is IrFieldLoad { Target.Type.IsObject: true };

    /// <summary>A call of a member of a class: <c>Name::new(..)</c>, <c>Name::f(..)</c>, <c>object.f(..)</c>.</summary>
    private RsExpression MemberCall(IrCall call, Member member) {
      var arguments = this.Arguments(call);
      if (member.Kind != MemberKind.Instance)
        return new RsCall(new RsPath($"{this.StructName(member.Class)}::{member.Name}"), arguments) { Type = call.Type };

      var receiver = this._sharedSelf.Contains(call.Function) ? this.Receiver(SelfBorrowed(this.ObjectBorrow(call.Arguments[0]))) : this.ObjectReceiver(call.Arguments[0]);
      return new RsMethodCall(receiver, member.Name, [.. arguments.Skip(1)]) { Type = call.Type };
    }

    /// <summary>The receiver of a method that takes <c>self: &amp;Rc&lt;Self&gt;</c>, which the call borrows itself: <c>counter.f()</c> for <c>(&amp;counter).f()</c> (clippy's <c>needless_borrow</c>).</summary>
    private static RsExpression SelfBorrowed(RsExpression borrowed) => borrowed is RsReference { Mutable: false } reference ? reference.Operand : borrowed;

    /// <summary>
    /// The items of a class: <c>pub struct Name</c> with a <c>Cell</c> or <c>RefCell</c> per field and
    /// the derives that hold, and an <c>impl</c> with its members - <c>new</c> per constructor, which
    /// makes the object in an <c>Rc</c> and runs the constructor's body (<c>init</c>) on it, and the
    /// methods and property accessors with <c>&amp;self</c>, or <c>self: &amp;Rc&lt;Self&gt;</c> where
    /// they pass the object on.
    /// </summary>
    private IEnumerable<RsItem> Class(IrClassType type, IReadOnlyDictionary<string, RsFunction> written) {
      this._construct = $"the class '{type.Name}'";
      if (this._ownership.Single.Contains(type)) {
        foreach (var item in this.PlainClass(type, written))
          yield return item;
        yield break;
      }

      var name = this._types[type];

      var fields = this.KeptFields(type).Select(field => new RsFieldDeclaration(this.FieldName(field), "", this.ClassFieldType(field))).ToList();
      var derives = new List<string> { "Default" };
      var floats = this.KeptFields(type).Any(field => field.Type.IsFloat || field.Type.StructType is { } nested && HasFloat(nested));
      if (type.IsRecord && !floats)
        derives.Add("PartialEq");
      if (type.IsRecord && floats)
        this.Refuse("a record with a floating-point field");

      yield return new RsStruct(name, "pub", fields) { Attributes = [$"derive({string.Join(", ", derives)})"] };
      var members = this._members.Where(pair => pair.Value.Class == type && written.ContainsKey(pair.Key)).ToList();
      var items = new List<RsItem>();
      foreach (var (function, member) in members.Where(pair => pair.Value.Kind == MemberKind.Maker))
        items.Add(this.Maker(written[function], member));
      foreach (var (function, member) in members.Where(pair => pair.Value.Kind != MemberKind.Maker))
        items.Add(written[function]);

      if (items.Count > 0)
        yield return new RsImpl(null, name, items);
    }

    /// <summary><c>pub fn new(..) -&gt; Rc&lt;Self&gt;</c>: the object made with every field at its default, the constructor's body run on it.</summary>
    private RsFunction Maker(RsFunction written, Member member) {
      var self = new RsPath("this");
      // A class without fields is a unit struct, which is its own value (clippy's default_constructed_unit_structs).
      RsExpression empty = this.KeptFields(member.Class).Count == 0 ? new RsPath("Self") : new RsCall(new RsPath("Self::default"), []);
      var make = new RsLet("this", false, null, new RsCall(new RsPath("Rc::new"), [empty]));
      var arguments = written.Parameters.Select(parameter => (RsExpression)new RsPath(parameter.Name)).ToList();
      RsStatement run;
      if (this._free.TryGetValue(member.Constructor!.Function, out var owner)) {
        // The constructor of a hierarchy is a function of the module, generic over the class's trait where it has one.
        var target = this._traits.Contains(owner) ? new RsReference(false, new RsUnary("*", self)) : new RsReference(false, self);
        run = new RsExpressionStatement(new RsCall(new RsPath(this._functions[member.Constructor.Function]), [target, .. arguments]));
      } else {
        var init = this._members.First(pair => pair.Value.Kind == MemberKind.Instance && pair.Value.Constructor == member.Constructor).Value;
        run = new RsExpressionStatement(new RsMethodCall(self, init.Name, arguments));
      }

      return written with { Visibility = "pub", Returns = "Rc<Self>", Body = new RsBlock([make, run], self) };
    }

    /// <summary>A member written as a function of its class: its name there, <c>pub</c>, the object as <c>self</c>.</summary>
    private RsFunction AsMember(RsFunction function, IrFunction source, Member member) {
      var parameters = function.Parameters.ToList();
      if (member.Kind == MemberKind.Instance)
        parameters[0] = this._sharedSelf.Contains(source.Name) ? new RsParameter("self", "&Rc<Self>")
          : new RsParameter(this._ownership.Single.Contains(member.Class) && this._ownership.MutatesParameter(source, 0) ? "&mut self" : "&self", "");

      var visible = member.Kind == MemberKind.Instance && member.Constructor is not null ? "" : "pub";
      return function with { Name = member.Name, Visibility = visible, Parameters = parameters };
    }

    /// <summary>
    /// The globals of the module as statics of the process, as .NET's static fields are: an integer or
    /// a truth value in its atomic type, read and written with relaxed ordering, which one thread
    /// observes in program order. A global of any other type is refused by name.
    /// </summary>
    private IEnumerable<RsItem> Globals(IrModule module) {
      foreach (var global in module.Globals) {
        this._construct = $"the global '{global.Name}'";
        if (Atomic(global.Type) is not { } atomic) {
          if (this.LockedGlobal(global) is { } locked)
            yield return locked;
          continue;
        }

        this._uses.Add("std::sync::atomic::" + atomic);
        var initial = global.Initializer switch {
          null => global.Type == IrType.Bool ? new RsLiteral("false") : new RsLiteral("0"),
          IrConst constant => Literal(constant),
          _ => this.Refuse("a global with a computed first value")
        };
        yield return new RsConst(this._globals[global.Name], "", atomic, new RsCall(new RsPath(atomic + "::new"), [initial])) { IsStatic = true };
      }
    }

    private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);

    /// <summary>The atomic type of the standard library a global of a type is, or <see langword="null"/>: none for an enum, which is a type of its own.</summary>
    private static string? Atomic(IrType type) => type.IsEnum ? null : type.Kind switch {
      IrTypeKind.Bool => "AtomicBool",
      IrTypeKind.I8 or IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.U16 or IrTypeKind.I32 or IrTypeKind.U32 or IrTypeKind.I64 or IrTypeKind.U64
        => "Atomic" + char.ToUpperInvariant(TypeName(type)![0]) + TypeName(type)![1..],
      _ => null
    };

    /// <summary>The current value of a global: <c>NAME.load(Ordering::Relaxed)</c>.</summary>
    private RsExpression GlobalValue(IrGlobalRef reference) {
      this._uses.Add("std::sync::atomic::Ordering");
      return new RsMethodCall(new RsPath(this._globals[reference.Global.Name]), "load", [new RsPath("Ordering::Relaxed")]) { Type = reference.Type };
    }

    /// <summary>A global written: <c>NAME.store(v, Ordering::Relaxed)</c>.</summary>
    private RsExpression GlobalStore(IrGlobalAssign assign) {
      this._uses.Add("std::sync::atomic::Ordering");
      return new RsMethodCall(new RsPath(this._globals[assign.Global.Name]), "store", [this.Expression(assign.Value), new RsPath("Ordering::Relaxed")]);
    }

    /// <summary>
    /// A step of a global: <c>fetch_add</c> or <c>fetch_sub</c>, which wrap and yield the value from
    /// before, the new one computed from it where the expression yields that; a step that traps on
    /// overflow loads the value, steps it with <c>checked_add</c> and stores it.
    /// </summary>
    private RsExpression GlobalStep(IrStep step, IrGlobalRef global) {
      this._uses.Add("std::sync::atomic::Ordering");
      var name = new RsPath(this._globals[global.Global.Name]);
      var relaxed = new RsPath("Ordering::Relaxed");
      var one = new RsLiteral("1") { Type = global.Type };
      if (!step.Trap) {
        var old = new RsMethodCall(name, step.Increment ? "fetch_add" : "fetch_sub", [one, relaxed]) { Type = global.Type };
        return step.Postfix ? old : this.Method(old, step.Increment ? "wrapping_add" : "wrapping_sub", global.Type, one);
      }

      var load = new RsLet("old", false, null, new RsMethodCall(name, "load", [relaxed]));
      var value = new RsLet("new", false, null, this.Checked(this.Method(new RsPath("old") { Type = global.Type }, step.Increment ? "checked_add" : "checked_sub", global.Type, one), IrTrap.IntegerOverflow));
      var store = new RsExpressionStatement(new RsMethodCall(name, "store", [new RsPath("new"), relaxed]));
      return new RsBlockExpression(new RsBlock([load, value, store], new RsPath(step.Postfix ? "old" : "new"))) { Type = global.Type };
    }

    /// <summary>Names the globals of the module in screaming snake case, apart from its constants.</summary>
    private void NameGlobals(IrModule module) {
      var taken = new HashSet<string>(this._constants.Values, StringComparer.Ordinal);
      var names = module.Globals.Select(global => global.Name).ToList();
      foreach (var (name, rust) in names.Zip(RustNames.Unique(names, name => RustNames.Screaming(name[(name.LastIndexOf('.') + 1)..], this.Edition), taken)))
        this._globals[name] = rust;
    }

  }

}
