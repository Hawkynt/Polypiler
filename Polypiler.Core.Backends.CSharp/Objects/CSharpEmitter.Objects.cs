using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  private static string Visibility(IrVisibility visibility) => visibility switch {
    IrVisibility.Public => "public",
    IrVisibility.Internal => "internal",
    IrVisibility.Protected => "protected",
    _ => "private"
  };

  private sealed partial class Writer {

    private Dictionary<string, IrClassType>? _owners;
    private Dictionary<IrClassType, Dictionary<object, string>>? _classMembers;

    /// <summary>The receiver of the member being written, which is <c>this</c>; <see langword="null"/> in a function of the static class.</summary>
    private IrLocal? _receiver;

    /// <summary>Whether a member of a class is being written, which reaches the helpers through the static class.</summary>
    private bool _inClass;

    /// <summary>The parameter of the setter being written, which is <c>value</c>.</summary>
    private IrLocal? _value;

    /// <summary>A member of the static class by its name: through the static class where a class of the module names it.</summary>
    private CsExpression Qualified(string member) => this._inClass ? new CsMemberAccess(new CsName(this._class), member) : new CsName(member);

    /// <summary>A member of the static class that the classes of the module may reach: internal where it was private.</summary>
    private static CsMember Exposed(CsMember member) => member switch {
      CsField field => field with { Modifiers = [.. field.Modifiers.Select(Internal)] },
      CsMethod method => method with { Modifiers = [.. method.Modifiers.Select(Internal)] },
      CsTypeDeclaration type => type with { Modifiers = [.. type.Modifiers.Select(Internal)] },
      CsDelegateDeclaration type => type with { Modifiers = [.. type.Modifiers.Select(Internal)] },
      _ => member
    };

    private static string Internal(string modifier) => modifier == "private" ? "internal" : modifier;

    /// <summary>The class of every function of the module that is the body of a member.</summary>
    private Dictionary<string, IrClassType> Owners
      => this._owners ??= this._module.Types.OfType<IrClassType>().SelectMany(type => type.Functions.Select(function => (function, type))).ToDictionary(pair => pair.function, pair => pair.type);

    private bool IsMember(IrFunction function) => this.Owners.ContainsKey(function.Name) || this.Signatures.Contains(function.Name);

    private bool IsOwned(IrGlobal global) => this._module.Types.OfType<IrClassType>().Any(type => type.StaticFields.Contains(global));

    /// <summary>Whether a property whose field the compiler declares can be written: from C# 3 on.</summary>
    private bool AutoProperties => this.Has(CSharpFeature.AutoProperty);

    /// <summary>
    /// The C# name of every member of a class, by the field, property, method or static field it is:
    /// its own where C# can write it, the backing field of an auto-property the property's name where
    /// the language has auto-properties and <c>_&lt;name&gt;</c> where it has not.
    /// </summary>
    private Dictionary<object, string> MemberNames(IrClassType type) {
      this._classMembers ??= new(ReferenceEqualityComparer.Instance);
      if (this._classMembers.TryGetValue(type, out var names))
        return names;

      var owner = this._typeNames[type.Name].TrimStart('@');
      var sources = new List<(object Member, string Name)>();
      foreach (var field in type.Fields)
        sources.Add((field, type.PropertyOf(field) is { } property ? (this.AutoProperties ? property.Name : "_" + property.Name) : field.Name));

      sources.AddRange(type.Properties.Where(property => property.BackingField is null || !this.AutoProperties).Select(property => ((object)property, property.Name)));
      sources.AddRange(type.Methods.Where(method => method.Kind == IrMethodKind.Method).Select(method => ((object)method, method.Name)));
      sources.AddRange(type.StaticFields.Select(global => ((object)global, global.Name[(type.Name.Length + 1)..])));
      var spelled = Names(sources.Select(source => source.Name), [owner]);
      names = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
      foreach (var (member, name) in sources)
        names[member] = spelled[name];

      // An auto-property and its field are one name where the compiler declares the field.
      foreach (var property in type.Properties.Where(property => property.BackingField is not null && this.AutoProperties))
        names[property] = names[property.BackingField!];

      return this._classMembers[type] = names;
    }

    /// <summary>
    /// The C# name of a field of an object: the property of an auto-property where the compiler
    /// declares its field; where it does not, the field through the receiver and the property through
    /// any other object, whose private field a member of another class cannot name.
    /// </summary>
    private string ObjectFieldName(IrField field, IrExpr target) {
      var owner = target.Type.ClassType!.OwnerOf(field)!;
      if (owner.IsLibrary)
        return field.Name;

      return !this.AutoProperties && owner.PropertyOf(field) is { } property && !(target is IrLocalRef reference && reference.Local == this._receiver)
        ? this.MemberNames(owner)[property]
        : this.MemberNames(owner)[field];
    }

    /// <summary>The C# name of a method or of the property of an accessor.</summary>
    private string MethodName(IrMethod method, IrClassType owner)
      => this.MemberNames(owner)[method.Kind == IrMethodKind.Method ? method : owner.PropertyOf(method)!];

    /// <summary>The interfaces and classes of the module, each in the form the profile has (see the lowering tables of <c>docs/reference/classes.md</c>).</summary>
    private List<CsMember> Classes() {
      this._inClass = true;
      List<CsMember> classes = [.. this._module.Types.Where(type => type is IrClassType or IrInterfaceType { IsDisposable: false })
        .Select(type => type is IrClassType declared ? this.Class(declared) : this.Interface((IrInterfaceType)type))];
      this._inClass = false;
      return classes;
    }

    private CsTypeDeclaration Class(IrClassType type) {
      var name = this._typeNames[type.Name];
      var names = this.MemberNames(type);
      var functions = this._module.Functions.Where(function => this.Owners.GetValueOrDefault(function.Name) == type).ToDictionary(function => function.Name);
      var members = new List<CsMember>();
      foreach (var global in type.StaticFields)
        members.Add(this.Global(global) with { Modifiers = ["public", "static"], Name = names[global] });

      var positional = type.IsRecord && this.Has(CSharpFeature.Record) && this._table.HasApi("IsExternalInit", this._profile) ? this.Positional(type, functions) : null;
      foreach (var field in type.Fields.Where(field => type.PropertyOf(field) is null || !this.AutoProperties))
        members.Add(new CsField([type.PropertyOf(field) is null ? Visibility(field.Visibility) : "private", .. field.IsReadOnly ? (string[])["readonly"] : []],
          new CsType(this.TypeName(field.Type)), names[field], this.EmptyMemory(field.Type)));

      foreach (var property in type.Properties.Where(property => positional is null || property.BackingField is null))
        members.Add(this.Property(type, property, functions));

      if (positional is null)
        foreach (var constructor in type.Constructors)
          members.Add(this.Constructor(type, constructor, functions[constructor.Function]));

      foreach (var method in type.Methods.Where(method => method.Kind == IrMethodKind.Method)) {
        var function = functions[method.Function];
        this._receiver = method.IsStatic ? null : function.Parameters[0];
        var (parameters, body, expression) = this.Body(function, method.IsStatic ? 0 : 1, function.Body, true);
        this._receiver = null;
        if (method.Dispatch == IrDispatch.Abstract)
          (body, expression) = (null, null);

        members.Add(new CsMethod(Unsafe(this.MethodModifiers(method), function), new CsType(this.TypeName(function.ReturnType)), names[method], [], parameters, body, expression));
      }

      IReadOnlyList<string> declared = [Visibility(type.Visibility), .. type.IsAbstract ? ["abstract"] : this.Derived(type) ? (string[])[] : ["sealed"]];
      List<CsType> bases = [.. type.Base is { } @base ? [new CsType(this.ClassTypeName(@base))] : (CsType[])[], .. type.Interfaces.Select(contract => new CsType(this._typeNames[contract.Name]))];
      if (positional is not null)
        return new CsTypeDeclaration(declared, "record", name, positional, [], members.Count == 0 ? null : [.. members.Select(member => member with { Leading = CsTrivia.Blank })]) {
          OpenBlank = false
        };

      var equatable = type.IsRecord;
      if (equatable)
        members.AddRange(this.ClassEqualityMembers(name, [.. type.Fields.Select(field => (this.TypeName(field.Type), names[field]))]));

      return new CsTypeDeclaration(declared, "class", name, null, equatable ? [.. bases, new CsType($"{Base}IEquatable<{name}>")] : bases, [.. members.Select(member => member with { Leading = CsTrivia.Blank })]);
    }

    /// <summary>
    /// The parameters of a record written as <c>record R(…)</c>: one constructor that gives every field,
    /// each the init-only field of an auto-property, the parameter of its name, in order and nothing
    /// else; <see langword="null"/> for any other record.
    /// </summary>
    private List<CsParameter>? Positional(IrClassType type, Dictionary<string, IrFunction> functions) {
      if (type.Constructors is not [var only] || type.Fields.Any(field => type.PropertyOf(field) is not { IsInitOnly: true }))
        return null;

      var function = functions[only.Function];
      var parameters = function.Parameters.Skip(1).ToList();
      var stores = function.Body.Statements;
      var matches = stores.Count == type.Fields.Count && parameters.Count == type.Fields.Count && stores.Select((statement, index) => statement is IrFieldStore {
        Target: IrLocalRef { Local: var self }, Value: IrLocalRef { Local: var value }
      } store && self == function.Parameters[0] && ReferenceEquals(store.Field, type.Fields[index]) && value == parameters[index] && value.Name == type.Fields[index].Name).All(match => match);
      return matches ? [.. type.Fields.Select(field => new CsParameter(new CsType(this.TypeName(field.Type)), this.MemberNames(type)[field]))] : null;
    }

    /// <summary>
    /// A property: an auto-property where the language has them - <c>{ get; }</c> before C# 6 as
    /// <c>{ get; private set; }</c>, <c>init</c> before C# 9 as <c>set</c> - and otherwise a property
    /// over its field; a computed property with its accessors, a getter alone as an expression body
    /// from C# 6 on.
    /// </summary>
    private CsProperty Property(IrClassType type, IrProperty property, Dictionary<string, IrFunction> functions) {
      var names = this.MemberNames(type);
      // A computed property is as virtual as its accessors, which are both of one dispatch (#1368).
      var modifiers = property.BackingField is null && (property.Getter ?? property.Setter) is { } accessor ? [.. this.MethodModifiers(accessor)] : new[] { Visibility(property.Visibility) };
      var propertyType = new CsType(this.TypeName(property.Type));
      if (property.BackingField is { } field) {
        if (this.AutoProperties) {
          var setter = !property.HasSetter ? (this.Has(CSharpFeature.GetOnlyAutoProperty) ? null : "private set")
            : property.IsInitOnly && this.Has(CSharpFeature.InitAccessor) && this._table.HasApi("IsExternalInit", this._profile) && !this.WrittenOutside(type, field) ? "init" : "set";
          return new CsProperty(modifiers, propertyType, names[property], [new CsAccessor("get", null, null), .. setter is null ? [] : (CsAccessor[])[new CsAccessor(setter, null, null)]], null);
        }

        var backing = new CsMemberAccess(new CsName("this"), names[field]) { Type = field.Type };
        List<CsAccessor> written = [new CsAccessor("get", new CsBlock([new CsReturn(backing)]), null)];
        if (property.HasSetter)
          written.Add(new CsAccessor("set", new CsBlock([new CsExpressionStatement(new CsAssignment(backing, "=", new CsName("value")))]), null));

        return new CsProperty(modifiers, propertyType, names[property], written, null);
      }

      CsAccessor Accessor(IrMethod method, string keyword) {
        var function = functions[method.Function];
        this._receiver = function.Parameters[0];
        this._value = method.Kind == IrMethodKind.Setter ? function.Parameters[1] : null;
        var (_, body, _) = this.Body(function, method.Kind == IrMethodKind.Setter ? 2 : 1, function.Body, false);
        this._receiver = this._value = null;
        return new CsAccessor(keyword, body, null);
      }

      if (property.Setter is null && property.Getter is { } only && functions[only.Function].Body.Statements is [IrReturn] && this.Has(CSharpFeature.ExpressionBody)) {
        var function = functions[only.Function];
        this._receiver = function.Parameters[0];
        var (_, _, expression) = this.Body(function, 1, function.Body, true);
        this._receiver = null;
        return new CsProperty(modifiers, propertyType, names[property], null, expression);
      }

      return new CsProperty(modifiers, propertyType, names[property], [
        .. property.Getter is { } getter ? (CsAccessor[])[Accessor(getter, "get")] : [],
        .. property.Setter is { } set ? (CsAccessor[])[Accessor(set, "set")] : []
      ], null);
    }

    /// <summary>
    /// Whether a function other than a constructor of its class writes a field: an object initializer
    /// of the source is a store after the object is made in the IR, which an <c>init</c> accessor refuses.
    /// </summary>
    private bool WrittenOutside(IrClassType owner, IrField field)
      => this._module.Functions.Where(function => !owner.Constructors.Any(constructor => constructor.Function == function.Name))
        .Any(function => AllStatements(function.Body).OfType<IrFieldStore>().Any(store => ReferenceEquals(store.Field, field)));

    /// <summary>Every statement within a statement, the statement first.</summary>
    private static IEnumerable<IrStmt> AllStatements(IrStmt statement)
      => (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements)).SelectMany(AllStatements).Prepend(statement);

    /// <summary>
    /// A constructor; one whose body begins with the call of another constructor's function chains to
    /// it with <c>: this(…)</c>, or with <c>: base(…)</c> to one of the base class.
    /// </summary>
    private CsConstructor Constructor(IrClassType type, IrConstructor constructor, IrFunction function) {
      this._receiver = function.Parameters[0];
      var chain = function.Body.Statements is [IrExprStmt { Expression: IrCall call }, ..] && (this.Owners.ContainsKey(call.Function) || IrExceptionClass.ConstructorOf(call.Function) is not null) ? call : null;
      var toBase = chain is not null && this.Owners.GetValueOrDefault(chain.Function) != type;
      var statements = chain is null ? function.Body : new IrBlock([.. function.Body.Statements.Skip(1)]);
      var (parameters, body, _) = this.Body(function, 1, statements, false);
      IReadOnlyList<CsExpression>? chained = chain is null ? null : [.. chain.Arguments.Skip(1).Select(argument => {
        var value = this.Stored(argument);
        return Expressions(argument).Any(this.Wraps) && !Traps(Promoted(argument)) ? new CsCheckedExpression(false, value) { Type = value.Type } : value;
      })];
      this._receiver = null;
      // : base() is what C# calls without being asked.
      return new CsConstructor(Unsafe([Visibility(constructor.Visibility)], function), this._typeNames[type.Name], parameters, body!) {
        Chained = toBase && chained is [] ? null : chained, ChainsToBase = toBase && chained is not []
      };
    }

    /// <summary>
    /// The members a record class has, written out where the language has no records: <c>Equals(T)</c>
    /// comparing every field with <c>EqualityComparer&lt;F&gt;.Default</c> once the other is no
    /// <c>null</c>; <c>Equals(object)</c>, <c>GetHashCode()</c> and the two operators, which compare
    /// two <c>null</c>s as equal.
    /// </summary>
    private IEnumerable<CsMember> ClassEqualityMembers(string name, List<(string Type, string Name)> fields) {
      CsExpression Same(CsExpression left, CsExpression right) => new CsInvocation(CSharpFragments.Expression(Base + "Object.ReferenceEquals"), [left, right]);
      var notNull = new CsPrefixUnary("!", Same(new CsName("other"), new CsNullLiteral()));
      var equal = fields.Select(field => (CsExpression)new CsInvocation(new CsMemberAccess(DefaultComparer(field.Type), "Equals"), [Of("this", field.Name), Of("other", field.Name)]))
        .Aggregate((CsExpression)notNull, (left, right) => new CsBinary(left, "&&", right) { BreakBefore = true });
      yield return Method(["public"], "bool", "Equals", [new CsParameter(new CsType(name), "other")], new CsReturn(equal));
      yield return Method(["public", "override"], "bool", "Equals", [new CsParameter(new CsType("object"), "obj")], new CsReturn(new CsBinary(
        new CsIsType(new CsName("obj"), new CsType(name)), "&&", new CsInvocation(Of("this", "Equals"), [new CsCast(new CsType(name), new CsName("obj"))]))));
      yield return Method(["public", "override"], "int", "GetHashCode", [], [.. this.HashCode(fields)]);
      IReadOnlyList<CsParameter> operands = [new CsParameter(new CsType(name), "left"), new CsParameter(new CsType(name), "right")];
      var equals = new CsBinary(Same(new CsName("left"), new CsName("right")), "||", new CsBinary(
        new CsPrefixUnary("!", Same(new CsName("left"), new CsNullLiteral())), "&&", new CsInvocation(Of("left", "Equals"), [new CsName("right")])));
      yield return Method(["public", "static"], "bool", "operator ==", operands, new CsReturn(equals));
      yield return Method(["public", "static"], "bool", "operator !=", operands, new CsReturn(new CsPrefixUnary("!", new CsParenthesized(equals))));
    }

    /// <summary>The expressions of objects.</summary>
    private (CsExpression Node, int Binding) Object(IrExpr expression) {
      switch (expression) {
        case IrNew creation:
          return (new CsObjectCreation(new CsType(this.TypeName(creation.Type)), [.. creation.Arguments.Select(this.Stored)]), Primary);
        case IrNull:
          return (new CsNullLiteral(), Primary);
        case IrReferenceEquals { Right: IrNull, Left.Type.ClassType.IsRecord: false } identity:
          // A class without operators compares references with ==.
          return (new CsBinary(this.Operand(identity.Left, Unary), "==", new CsNullLiteral()), Loose);
        case IrReferenceEquals { Left: IrNull, Right.Type.ClassType.IsRecord: false } identity:
          return (new CsBinary(new CsNullLiteral(), "==", this.Operand(identity.Right, Unary)), Loose);
        case IrReferenceEquals identity:
          var first = this.Stored(identity.Left);
          return (new CsInvocation(CSharpFragments.Expression(Base + "Object.ReferenceEquals"), [first, this.Stored(identity.Right)]), Primary);
        case IrStructEquals equality:
          var left = this.Operand(equality.Left, Unary);
          return (new CsBinary(left, "==", this.Operand(equality.Right, Unary)), Loose);
        case IrFieldLoad load:
          return (new CsMemberAccess(this.Operand(load.Target, Primary), this.ObjectFieldName(load.Field, load.Target)), Primary);
        default:
          var call = (IrMethodCall)expression;
          var owner = this.Owners.GetValueOrDefault(call.Method.Function);
          var target = call.Target is null ? new CsName(this._typeNames[owner!.Name])
            : call.Kind == IrMethodCallKind.Base ? new CsName("base")
            : this.Operand(call.Target, Primary);
          var member = new CsMemberAccess(target, owner is null ? this.InterfaceMemberName(call.Method) : this.MethodName(call.Method, owner)) { Type = call.Type };
          return call.Method.Kind switch {
            IrMethodKind.Getter => (member, Primary),
            IrMethodKind.Setter => (new CsAssignment(member, "=", this.Stored(call.Arguments[0])), Loose),
            _ => (new CsInvocation(member, [.. call.Arguments.Select(this.Stored)]), Primary)
          };
      }
    }

  }

}
