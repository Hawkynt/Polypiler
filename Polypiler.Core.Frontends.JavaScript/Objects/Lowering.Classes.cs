using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The objects of the classes of the file: <c>this</c>, <c>new</c>, <c>null</c>, fields, accessors and
/// methods of an object, static fields and methods of a class, and <c>===</c> between references.
/// Whatever makes an object more than its class says - a property added outside the constructor, a
/// method taken from its object, <c>call</c>, <c>apply</c> and <c>bind</c> - is refused by name.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The receiver of the member being lowered, <c>this</c>; <see langword="null"/> in a function of the module and a static method.</summary>
  private IrLocal? _receiver;

  /// <summary>The receiver of a member function, and the statements its constructor starts with: the values of the field declarations of its class.</summary>
  private (IrLocal? Receiver, List<IrStmt> Prologue) Member(FunctionModel model) {
    if (model.Candidate is not { Class: { } owner, IsStatic: false } candidate)
      return (null, []);

    var receiver = new IrLocal("this", IrType.Object(owner.Ir));
    this._receiver = receiver;
    var prologue = candidate.Member != MemberKind.Constructor ? [] : owner.Fields.Where(field => field.Initializer is not null)
      .Select(field => (IrStmt)new IrFieldStore(new IrLocalRef(receiver), field.Ir!, this.Value(field.Initializer!, field.Ir!.Type))).ToList();
    return (receiver, prologue);
  }

  /// <summary>The source of every call of a method, by identity, which a diagnostic about the call points at.</summary>
  public Dictionary<IrMethodCall, Node> Sites { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>A call of a method, its source noted.</summary>
  private IrMethodCall Site(IrMethodCall call, Node node) {
    this.Sites[call] = node;
    return call;
  }

  /// <summary>How a call on an object reaches a method: by the class of the object where a class derived from the method's own overrides it.</summary>
  private static IrMethodCallKind Dispatch(IrMethod method) => method.IsVirtual ? IrMethodCallKind.Virtual : IrMethodCallKind.Direct;

  /// <summary>An expression about objects, or <see langword="null"/> for any other.</summary>
  private IrExpr? Instance(Node node) {
    if (Inheritance.Of(node, this._model.Candidate.Class, types.Names.ClassNamed) is { } inherited)
      return this.Inherited(inherited, node);

    if (this.ErrorExpression(node) is { } error)
      return error;

    switch (node) {
      case BinaryExpression { Operator: Operator.InstanceOf } test when types.Names.ClassNamed(test.Right) is { } tested:
        var value = this.Expression(test.Left);
        return value.Type.IsObject ? new IrTypeTest(value, IrType.Object(tested.Ir)) : throw this.Unsupported(test, $"instanceof of {types.Text(test.Left)}, which is no object of a class");
      case MemberExpression { Object: Super } read:
        var ancestor = this._model.Candidate.Class?.Base ?? throw this.Unsupported(read, "super outside a derived class");
        return Binder.KeyOf(read) is { } name && ancestor.Member(MemberKind.Getter, name) is { } getter
          ? new IrMethodCall(types.ModelOf(getter).ReturnType, ClassModel.MethodOf(getter), new IrLocalRef(this._receiver!), [])
          : throw this.Unsupported(read, $"{types.Text(read)}, which reads no getter of the base class");
      case ThisExpression:
        // Outside a method or a constructor this is no object of a class.
        return this._receiver is { } receiver ? new IrLocalRef(receiver) : throw this.Unsupported(node, "this");
      case NullLiteral:
        // null takes the type of the place it goes to (see To) or of what it is compared with.
        return new IrNull(IrType.Void);
      case NewExpression creation when types.Names.ClassNamed(creation.Callee) is { } created:
        return new IrNew(IrType.Object(created.Ir), created.Creator!, this.Arguments(creation, creation.Arguments, created.Constructor));
      case MemberExpression { Object: ThisExpression self } when this._receiver is null:
        throw this.Unsupported(self, "this");
      case CallExpression { Callee: CallExpression called }:
        // What a call yields is never a function of the subset; a call of call, apply or bind is refused by name.
        this.Instance(called);
        return null;
      case CallExpression { Callee: MemberExpression { Object: MemberExpression inner } bound } when Binder.KeyOf(bound) is "call" or "apply" or "bind" && types.Of(inner.Object).Class is not null:
        throw this.Unsupported(bound, "call, apply or bind of a method");
      case MemberExpression member when this._model.Bindings.GetValueOrDefault(member) is Variable field:
        return Read(field);
      case MemberExpression { Computed: false, Object: MemberExpression inner } member when Binder.KeyOf(member) is "call" or "apply" or "bind" && types.Of(inner.Object).Class is not null:
        throw this.Unsupported(member, "call, apply or bind of a method");
      case MemberExpression member when types.Of(member.Object).Class is { } owner:
        return this.Property(member, owner);
      case CallExpression { Callee: MemberExpression callee } call when types.Names.Callee(callee) is { Class: { } owner, IsStatic: true } method:
        return new IrMethodCall(types.ModelOf(method).ReturnType, owner.Methods[method], null, this.Arguments(call, call.Arguments, method));
      case CallExpression { Callee: MemberExpression { Computed: false } callee } call when types.Of(callee.Object).Class is { } owner:
        if (Binder.KeyOf(callee) is not { } key || owner.Member(MemberKind.Method, key) is not { } invoked)
          throw this.Unsupported(callee, $"the method {types.Text(callee.Property)} of {owner.Name}, which it does not have");

        var target = this.Expression(callee.Object);
        var reached = ClassModel.MethodOf(invoked);
        return this.Site(new IrMethodCall(types.ModelOf(invoked).ReturnType, reached, target, this.Arguments(call, call.Arguments, invoked)) { Kind = Dispatch(reached) }, call);
      case MemberExpression { Object: MemberExpression { Property: Identifier { Name: "prototype" } } } member:
        throw this.Unsupported(member, "prototype mutation");
      case Identifier identifier when !this._model.Bindings.ContainsKey(identifier) && types.Names.ClassNamed(identifier) is not null:
        throw this.Unsupported(identifier, $"the class {identifier.Name} used as a value");
      default:
        return null;
    }
  }

  /// <summary>The arguments of a call of a member, each in the type of its parameter.</summary>
  private List<IrExpr> Arguments(Node call, IReadOnlyList<Node> arguments, Candidate? callee) {
    if (arguments.Any(argument => argument is SpreadElement))
      throw this.Unsupported(call, "a spread argument");

    var parameters = callee is null ? [] : types.ModelOf(callee).Parameters;
    if (callee is not null && types.ModelOf(callee).Problem is not null)
      throw this.Unsupported(call, $"a call of {callee.Name}, which cannot be imported");

    if (arguments.Count != parameters.Count)
      throw this.Unsupported(call, $"a call with {arguments.Count} arguments for {parameters.Count} parameters");

    return [.. arguments.Select((argument, index) => this.Value(argument, parameters[index].Parameter?.Type ?? this.Storable(parameters[index].Entry, argument)))];
  }

  /// <summary>A property read of an object: a field, or the call of a getter.</summary>
  private IrExpr Property(MemberExpression member, ClassModel owner) {
    var key = Binder.KeyOf(member) ?? throw this.Unsupported(member, "a computed member name");
    if (owner.Field(key) is { } field)
      return Read(new IrFieldLoad(this.Expression(member.Object), field.Ir!));

    if (owner.Member(MemberKind.Getter, key) is { } getter)
      return new IrMethodCall(types.ModelOf(getter).ReturnType, ClassModel.MethodOf(getter), this.Expression(member.Object), []);

    throw this.Unsupported(member, owner.Member(MemberKind.Method, key) is not null
      ? "a method taken from its object"
      : owner.Member(MemberKind.Setter, key) is not null ? $"a read of the property {key}, which has no getter" : $"the property {key} of {owner.Name}, which it does not have");
  }

  /// <summary>
  /// An assignment to a property: a static field of a class, a field of an object, or the setter of
  /// one; says whether it was one. The object is evaluated once for a compound assignment.
  /// </summary>
  private bool ObjectAssignment(AssignmentExpression assignment, Operator? compound, List<IrStmt> into) {
    if (assignment.Left is not MemberExpression member)
      return false;

    if (this._model.Bindings.GetValueOrDefault(member) is Variable global) {
      var place = Read(global);
      var stored = compound is { } op
        ? this.To(this.Binary(op, member, assignment.Right, Coercion.None, assignment), place.Type, assignment)
        : this.Value(assignment.Right, place.Type);
      into.Add(Write(global, stored));
      return true;
    }

    if (types.Of(member.Object).Class is not { } owner)
      return false;

    var key = Binder.KeyOf(member) ?? throw this.Unsupported(member, "a computed member name");
    var field = owner.Field(key);
    var setter = owner.Member(MemberKind.Setter, key);
    if (field is null && setter is null)
      throw this.Unsupported(assignment, owner.HasInstanceMember(key) ? $"an assignment to the method or getter {key}" : "a property added outside the constructor");

    var target = this.Expression(member.Object);
    var type = field?.Ir!.Type ?? types.ModelOf(setter!).Parameters[0].Parameter!.Type;
    IrExpr value;
    if (compound is { } combining) {
      target = this.Once(target, into);
      this._substitutes[member] = field is not null ? Read(new IrFieldLoad(target, field.Ir!)) : this.Property(member, owner);
      value = this.To(this.Binary(combining, member, assignment.Right, Coercion.None, assignment), type, assignment);
      this._substitutes.Remove(member);
    } else {
      value = this.Value(assignment.Right, type);
    }

    into.Add(field is not null ? new IrFieldStore(target, field.Ir!, value) : new IrExprStmt(new IrMethodCall(IrType.Void, ClassModel.MethodOf(setter!), target, [value])));
    return true;
  }

  /// <summary>
  /// A call of the constructor of the base class - the first statement of a constructor of a derived
  /// class, which <see cref="Function"/> checks - or of a method of a base class on <c>this</c>, which
  /// selects that very method.
  /// </summary>
  private IrExpr Inherited(BaseReference reference, Node node) {
    var self = new IrLocalRef(this._receiver ?? throw this.Unsupported(node, "super outside a method"));
    if (reference.IsConstructor) {
      if (this._model.Candidate.Member != MemberKind.Constructor)
        throw this.Unsupported(node, "a call of the constructor of the base class outside a constructor");

      return reference.Owner.IsLibrary ? this.LibraryConstructor(reference, node, self)
        : new IrCall(reference.Owner.Creator!.Function, IrType.Void, [self, .. this.Arguments(node, reference.Arguments, reference.Member)]);
    }

    if (reference.Member is not { } method)
      throw this.Unsupported(node, $"the method {reference.Key} of {reference.Owner.Name}, which it does not have");

    var called = ClassModel.MethodOf(method);
    return new IrMethodCall(types.ModelOf(method).ReturnType, called, self, this.Arguments(node, reference.Arguments, method)) {
      Kind = called.IsVirtual ? IrMethodCallKind.Base : IrMethodCallKind.Direct
    };
  }

  /// <summary><c>++</c> and <c>--</c> of a static field or of a field of an object, as statements, in doubles; says whether it was one.</summary>
  private bool ObjectStep(UpdateExpression step, List<IrStmt> into) {
    if (step.Argument is not MemberExpression member)
      return false;

    var one = new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1));
    var op = step.Operator == Operator.Increment ? IrBinaryOp.Add : IrBinaryOp.Subtract;
    if (this._model.Bindings.GetValueOrDefault(member) is Variable global) {
      into.Add(Write(global, new IrBinary(op, Read(global), one)));
      return true;
    }

    if (types.Of(member.Object).Class is not { } owner)
      return false;

    if (Binder.KeyOf(member) is not { } key || owner.Field(key) is not { } field)
      throw this.Unsupported(step, $"{(step.Operator == Operator.Increment ? "++" : "--")} of {types.Text(member)}, which is no field");

    var target = this.Once(this.Expression(member.Object), into);
    var current = new IrFieldLoad(target, field.Ir!);
    into.Add(new IrFieldStore(target, field.Ir!, field.Ir!.Type == IrType.F64 ? new IrBinary(op, current, one) : throw this.Unsupported(step, $"{(step.Operator == Operator.Increment ? "++" : "--")} of the field {key} of the type {field.Ir.Type}")));
    return true;
  }

  /// <summary><c>===</c> and <c>!==</c> of references: identity; <c>null</c> takes the type of the other side.</summary>
  private IrExpr Identity(Operator op, Node left, Node right, Node whole) {
    var first = this.Expression(left);
    var second = this.Expression(right);
    // The inference compares a reference only with a reference or null, never null with null.
    var type = first is IrNull { Type.Kind: IrTypeKind.Void } ? second.Type : first.Type;
    IrExpr Typed(IrExpr value) => value is IrNull { Type.Kind: IrTypeKind.Void } ? new IrNull(type) : value;
    var same = new IrReferenceEquals(Typed(first), Typed(second));
    return op == Operator.StrictEquality ? same : new IrUnary(IrUnaryOp.LogicalNot, same);
  }

}
