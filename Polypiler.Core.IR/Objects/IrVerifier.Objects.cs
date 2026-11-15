using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>What a function of the module is to a class: a constructor, an instance method or a static method.</summary>
  /// <param name="Owner">The class.</param>
  /// <param name="IsConstructor">Whether it is a constructor.</param>
  /// <param name="IsStatic">Whether it is a static method, which has no receiver.</param>
  /// <param name="Method">The method, for a method.</param>
  private sealed record Member(IrClassType Owner, bool IsConstructor, bool IsStatic, IrMethod? Method);

  /// <summary>
  /// Checks the members of the classes of a module: every constructor and method names a function of
  /// the module that no other member names, a constructor's and an instance method's function take
  /// the receiver of the class first, a constructor returns nothing, an accessor has the shape of
  /// its kind, an auto-property's field is the class's own and of its type, and a static field is a
  /// global of the module.
  /// </summary>
  /// <returns>Each member function by name.</returns>
  private static Dictionary<string, Member> Members(IrModule module, TypeTable types, Dictionary<string, IrFunction> functions, List<Diagnostic> diagnostics) {
    var members = new Dictionary<string, Member>();
    var globals = module.Globals.ToHashSet();
    foreach (var type in types.Classes) {
      void Require(bool holds, string message) {
        if (!holds)
          diagnostics.Add(Error(type.Name, message));
      }

      void Add(string function, Member member) {
        Require(members.TryAdd(function, member), $"the function '{function}' is the body of two members");
        if (!functions.TryGetValue(function, out var body)) {
          Require(false, $"the function '{function}' of a member does not exist");
          return;
        }

        if (!member.IsStatic)
          Require(body.Parameters.Count > 0 && body.Parameters[0].Type == IrType.Object(type), $"the function '{function}' does not take the receiver of {type.Name} first");

        if (member.IsConstructor)
          Require(body.ReturnType == IrType.Void, $"the constructor '{function}' returns a value");

        var arity = body.Parameters.Count;
        switch (member.Method?.Kind) {
          case IrMethodKind.Getter:
            Require(arity == 1 && body.ReturnType != IrType.Void, $"the getter '{function}' takes a value or returns none");
            break;
          case IrMethodKind.Setter:
            Require(arity == 2 && body.ReturnType == IrType.Void, $"the setter '{function}' does not take one value and return nothing");
            break;
        }
      }

      foreach (var constructor in type.Constructors)
        Add(constructor.Function, new(type, true, false, null));

      foreach (var method in type.Methods)
        Add(method.Function, new(type, false, method.IsStatic, method));

      foreach (var property in type.Properties) {
        Require(property.BackingField is null || (type.Owns(property.BackingField) && property.BackingField.Type == property.Type),
          $"the property '{property.Name}' is backed by a field that is not one of its class's own of its type");
        Require(property.BackingField is not null || property.Getter is not null || property.Setter is not null, $"the property '{property.Name}' has neither a field nor an accessor");
        foreach (var accessor in new[] { property.Getter, property.Setter }.OfType<IrMethod>())
          Require(type.Methods.Contains(accessor), $"an accessor of the property '{property.Name}' is not a method of {type.Name}");
      }

      foreach (var global in type.StaticFields)
        Require(globals.Contains(global), $"the static field '{global.Name}' is not a global of the module");
    }

    return members;
  }

  private sealed partial class FunctionCheck {

    /// <summary>The functions of the module that are members of a class.</summary>
    public IReadOnlyDictionary<string, Member> Members { get; init; } = new Dictionary<string, Member>();

    /// <summary>What the function checked is to its class, or <see langword="null"/> for a function of the module.</summary>
    private Member? Self => this.Members.GetValueOrDefault(function.Name);

    /// <summary>The receiver of the function checked, which it never assigns; <see langword="null"/> where it has none.</summary>
    private IrLocal? Receiver => this.Self is { IsStatic: false } ? function.Parameters[0] : null;

    /// <summary>Whether a field may be written through a target: a writable one anywhere, a read-only one of a class only by a constructor of that class through its new object.</summary>
    private bool MayWrite(IrExpr target, IrField field)
      => !field.IsReadOnly || (target.Type.IsObject && this.Self is { IsConstructor: true } self && target.Type.ClassType == self.Owner
        && target is IrLocalRef reference && reference.Local == this.Receiver);

    /// <summary>The checks of the nodes of objects.</summary>
    private void Object(IrExpr expression) {
      switch (expression) {
        case IrNew creation:
          foreach (var argument in creation.Arguments)
            this.Expression(argument);

          if (this.Class(creation.Type) is { } created) {
            this.Require(created.Constructors.Contains(creation.Constructor), $"a constructor that is not one of {created.Name} makes an object of it");
            this.Require(!created.IsAbstract, $"an object of the abstract class {created.Name} is made");
            if (functions.TryGetValue(creation.Constructor.Function, out var body))
              this.Arguments(creation.Arguments, body.Parameters.Skip(1), $"the constructor of {created.Name}");
          }

          break;
        case IrNull value:
          this.Reference(value.Type);
          break;
        case IrMethodCall call:
          this.MethodCall(call);
          break;
        default:
          var identity = (IrReferenceEquals)expression;
          this.Expression(identity.Left);
          this.Expression(identity.Right);
          this.Reference(identity.Left.Type);
          this.Require(identity.Right.Type.IsAssignableTo(identity.Left.Type) || identity.Left.Type.IsAssignableTo(identity.Right.Type),
            $"a reference to {identity.Left.Type} is compared with one of the type {identity.Right.Type}");
          break;
      }
    }

    private void MethodCall(IrMethodCall call) {
      if (call.Target is not null)
        this.Expression(call.Target);

      foreach (var argument in call.Arguments)
        this.Expression(argument);

      IrTypeDeclaration? owner = types.Classes.FirstOrDefault(type => type.Methods.Contains(call.Method));
      owner ??= types.Interfaces.FirstOrDefault(type => type.Methods.Contains(call.Method));
      if (owner is null || !functions.TryGetValue(call.Method.Function, out var body)) {
        this.Require(false, $"the method '{call.Method.Name}' is not one of a class of the module");
        return;
      }

      var method = call.Method;
      this.Require(method.IsStatic == call.Target is null, $"the method '{method.Name}' is called {(call.Target is null ? "without" : "with")} a receiver");
      var reference = owner is IrClassType type ? IrType.Object(type) : IrType.Object((IrInterfaceType)owner);
      if (call.Target is { } target)
        this.Require(target.Type.IsAssignableTo(reference), $"the method '{method.Name}' of {owner.Name} is called on the type {target.Type}");

      switch (call.Kind) {
        case IrMethodCallKind.Direct:
          this.Require(!method.IsVirtual, $"the virtual method '{method.Name}' is called directly");
          break;
        case IrMethodCallKind.Virtual:
          this.Require(method.IsVirtual && owner is IrClassType, $"the method '{method.Name}', which is no virtual method of a class, is called virtually");
          break;
        case IrMethodCallKind.Interface:
          this.Require(owner is IrInterfaceType, $"the method '{method.Name}', which is no method of an interface, is called through one");
          break;
        default:
          this.Require(owner is IrClassType && method.Dispatch is not (IrDispatch.Abstract or IrDispatch.Static) && call.Target is IrLocalRef { Local: var self } && self == this.Receiver
            && this.Self?.Owner is { } caller && caller != owner && caller.DerivesFrom((IrClassType)owner),
            $"the method '{method.Name}' is no method with a body of a base class called on the receiver");
          break;
      }

      this.Require(call.Type == body.ReturnType, $"the call of '{method.Name}' has the type {call.Type}, the method returns {body.ReturnType}");
      this.Arguments(call.Arguments, body.Parameters.Skip(method.IsStatic ? 0 : 1), $"the method '{method.Name}'");
    }

    private void Arguments(IReadOnlyList<IrExpr> arguments, IEnumerable<IrLocal> parameters, string what)
      => this.Require(Assignable(arguments, [.. parameters]), $"the arguments of {what} do not match its parameters");

    /// <summary>Whether there is one argument per parameter, each of the parameter's type or converting to it without an effect.</summary>
    private static bool Assignable(IReadOnlyList<IrExpr> arguments, IReadOnlyList<IrLocal> parameters)
      => arguments.Count == parameters.Count && arguments.Zip(parameters).All(pair => pair.First.Type.IsAssignableTo(pair.Second.Type));

    /// <summary>The class of a reference type the module declares, or <see langword="null"/> after reporting that it is none.</summary>
    private IrClassType? Class(IrType type) {
      this.Require(type.IsObject && type.InterfaceType is null, $"a reference to an object of a class is expected, not the type {type}");
      this.Declared(type);
      return type.ClassType is { } named && types.Declares(named) ? named : null;
    }

    /// <summary>A reference type the module declares: of a class or of an interface.</summary>
    private void Reference(IrType type) {
      this.Require(type.IsObject, $"a reference to an object is expected, not the type {type}");
      this.Declared(type);
    }

    /// <summary>
    /// A call by name of the function of a member: allowed only as the first statement of a
    /// constructor, of another constructor of its class or of a constructor of its base class on the
    /// new object, which is how one chains to another and how an object of a derived class is made.
    /// </summary>
    private void CallOfMember(IrCall call, bool chains) {
      if (!this.Members.TryGetValue(call.Function, out var callee))
        return;

      this.Require(chains && callee.IsConstructor && this.Self is { IsConstructor: true } self && (self.Owner == callee.Owner || self.Owner.Base == callee.Owner)
        && call.Arguments is [IrLocalRef { Local: var receiver }, ..] && receiver == this.Receiver,
        $"the function '{call.Function}' of a member is called by name");
    }

    /// <summary>
    /// The statements of a constructor that does not chain write every read-only array field of its
    /// class, which no default could fill, at the top level of its body; a constructor of a derived
    /// class begins with the call of a constructor of its base class or of another of its own.
    /// </summary>
    private void Constructed() {
      if (this.Self is not { IsConstructor: true } self)
        return;

      if (self.Owner.Base is { } @base)
        this.Require(function.Body.Statements is [IrExprStmt { Expression: IrCall first }, ..] && this.Members.TryGetValue(first.Function, out var called)
          && called.IsConstructor && (called.Owner == self.Owner || called.Owner == @base),
          $"the constructor does not begin with the call of a constructor of {@base.Name} or of {self.Owner.Name}");

      if (this.Chains(function.Body, self.Owner))
        return;

      var written = function.Body.Statements.OfType<IrFieldStore>().Where(store => store.Target is IrLocalRef reference && reference.Local == this.Receiver).Select(store => store.Field).ToHashSet();
      foreach (var field in self.Owner.Fields.Where(field => field.IsReadOnly && field.Type.IsArray && !written.Contains(field)))
        this.Require(false, $"the constructor does not give the read-only field '{field.Name}' an array");
    }

    /// <summary>Whether a body begins with a call of the function of another constructor of its class.</summary>
    private bool Chains(IrBlock body, IrClassType owner)
      => body.Statements is [IrExprStmt { Expression: IrCall call }, ..] && this.Members.TryGetValue(call.Function, out var callee) && callee.IsConstructor && callee.Owner == owner;

  }

  /// <summary>The classes among the declarations of a module.</summary>
  private sealed partial class TypeTable {

    private readonly HashSet<IrClassType> _classes = new(ReferenceEqualityComparer.Instance);

    public IEnumerable<IrClassType> Classes => this._classes;

    public void Add(IrClassType type) => this._classes.Add(type);

    public bool Declares(IrClassType type) => this._classes.Contains(type);

    private readonly HashSet<IrInterfaceType> _interfaces = new(ReferenceEqualityComparer.Instance);

    public IEnumerable<IrInterfaceType> Interfaces => this._interfaces;

    public void Add(IrInterfaceType type) => this._interfaces.Add(type);

    public bool Declares(IrInterfaceType type) => this._interfaces.Contains(type);

  }

}
