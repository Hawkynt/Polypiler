using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The class of every function of the module that is the body of a member.</summary>
    private readonly Dictionary<string, IrClassType> _owners = [];

    /// <summary>The name each field, auto-property and method of a class is written with, by identity.</summary>
    private readonly Dictionary<object, string> _memberNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>The receiver of the member being written, which is <c>this</c>; <see langword="null"/> in a function of the module.</summary>
    private IrLocal? _receiver;

    /// <summary>Whether a class of the module has a private member that is written as a property with a leading underscore.</summary>
    private bool _underscored;

    /// <summary>Whether a function of the module is the body of a member of a class.</summary>
    private bool IsMember(IrFunction function) => this._owners.ContainsKey(function.Name);

    /// <summary>
    /// Takes note of the classes of a module and names their members: a private member is
    /// <c>#name</c> where the level has private names and <c>_name</c> where it has not - the leading
    /// underscores of the source name dropped first - and an overloaded method gets <c>$k</c> appended.
    /// </summary>
    private void NameClasses(IrModule module) {
      foreach (var type in module.Types.OfType<IrClassType>()) {
        foreach (var function in type.Functions)
          this._owners[function] = type;

        var taken = new HashSet<string>();
        string Named(string name, IrVisibility visibility) {
          if (visibility != IrVisibility.Private)
            return taken.Add(name) ? name : Named(name + "$", IrVisibility.Public);

          // A record compares its fields in a helper outside the class, which no private name reaches.
          var hidden = this.Native("#private") && !type.IsRecord ? "#" : "_";
          this._underscored |= hidden == "_";
          var bare = name.TrimStart('_');
          var chosen = hidden + (bare.Length == 0 ? name : bare);
          for (var number = 1; !taken.Add(chosen); ++number)
            chosen = $"{hidden}{name}${number}";

          return chosen;
        }

        foreach (var field in type.Fields)
          this._memberNames[field] = Named(type.PropertyOf(field) is { } property ? property.Name : field.Name, field.Visibility);

        foreach (var property in type.Properties.Where(property => property.BackingField is null))
          this._memberNames[property] = Named(property.Name, property.Visibility);

        foreach (var method in type.Methods.Where(method => method.Kind == IrMethodKind.Method))
          this._memberNames[method] = Named(method.Name, method.Visibility);

        foreach (var global in type.StaticFields) {
          var local = global.Name[(type.Name.Length + 1)..];
          this._globals[global] = $"{this.Name(type.Name)}.{Named(local, IrVisibility.Public)}";
        }
      }
    }

    /// <summary>The name of a field, an accessor's property or a method as the class writes it.</summary>
    private string MemberName(IrField field) => this._memberNames[field];

    private string MemberName(IrMethod method, IrClassType owner)
      => method.Kind == IrMethodKind.Method ? this._memberNames[method] : this._memberNames[owner.PropertyOf(method)!];

    /// <summary>The method of the class a constructor is written as when the class has several: <c>$init&lt;k&gt;</c>.</summary>
    private static string Initializer(IrClassType type, string function)
      => "$init" + (type.Constructors.Select(constructor => constructor.Function).ToList().IndexOf(function) + 1);

    /// <summary>
    /// The declarations of a class: an ECMAScript 2015 class where the level has classes, otherwise a
    /// constructor function with its methods on its prototype; then the static fields where the level
    /// has none of its own, assigned after the class. Several constructors become methods
    /// <c>$init&lt;k&gt;</c> that the one constructor of JavaScript chooses from by the number of its arguments.
    /// </summary>
    private List<JsStatement> Class(IrClassType type, IrModule module) {
      this._construct = $"the class '{type.Name}'";
      var name = this.Name(type.Name);
      var functions = module.Functions.Where(function => this._owners.GetValueOrDefault(function.Name) == type).ToDictionary(function => function.Name);
      var classes = this.Native("class");
      var fields = classes && this.Native("class fields");
      var statics = classes && this.Native("static fields");
      var accessors = classes && this.Native("class accessor");
      var defaults = fields ? [] : type.Fields.Select(field => (JsStatement)new JsExpressionStatement(new JsAssignment(new JsMember(new JsThis(), this.MemberName(field)), "=", this.FieldDefault(field.Type)))).ToList();

      // The constructor of JavaScript.
      var parameters = new List<string>();
      var body = new List<JsStatement>(defaults);
      if (type.Constructors.Count == 1) {
        var (written, block) = this.Member(functions[type.Constructors[0].Function], type);
        parameters = written;
        body.AddRange(block.Statements);
      } else {
        var arities = type.Constructors.Select(constructor => functions[constructor.Function].Parameters.Count - 1).ToList();
        if (arities.Distinct().Count() != arities.Count && this._refused.Add("constructors of one arity"))
          this._problems.Add(InexpressibleDescriptor.At($"'constructors of one arity' of the class '{type.Name}' cannot be expressed in JavaScript profile '{this._profile.LevelName}'"));

        parameters = [.. Enumerable.Range(0, arities.Max()).Select(index => $"a{index}")];
        JsStatement? choice = null;
        for (var index = type.Constructors.Count - 1; index >= 0; --index) {
          var call = new JsExpressionStatement(new JsCall(new JsMember(new JsThis(), Initializer(type, type.Constructors[index].Function)), [.. parameters.Take(arities[index]).Select(Id)]));
          choice = choice is null ? new JsBlock([call]) : new JsIf(Infix(new JsMember(Id("arguments"), "length"), "===", Number(arities[index])), new JsBlock([call]), choice);
        }

        body.Add(choice!);
      }

      var members = new List<JsClassMember>();
      var after = new List<JsStatement>();
      if (fields)
        members.AddRange(type.Fields.Select(field => new JsClassMember(JsClassMemberKind.Field, this.MemberName(field), false, [], null, this.FieldDefault(field.Type))));

      if (statics)
        members.AddRange(type.StaticFields.Select(global => new JsClassMember(JsClassMemberKind.Field, this._globals[global].Split('.')[1], true, [], null, this.GlobalValue(global))));
      else
        after.AddRange(type.StaticFields.Select(global => (JsStatement)new JsExpressionStatement(new JsAssignment(this.GlobalName(global), "=", this.GlobalValue(global)))));

      members.Add(new JsClassMember(JsClassMemberKind.Constructor, "constructor", false, parameters, new JsBlock(body), null));
      JsExpression Prototype() => new JsMember(Id(name), "prototype");
      if (type.Constructors.Count > 1)
        foreach (var constructor in type.Constructors) {
          var (written, block) = this.Member(functions[constructor.Function], type);
          var initializer = Initializer(type, constructor.Function);
          if (classes)
            members.Add(new JsClassMember(JsClassMemberKind.Method, initializer, false, written, block, null));
          else
            after.Add(new JsExpressionStatement(new JsAssignment(new JsMember(Prototype(), initializer), "=", new JsFunctionExpression(null, written, block))));
        }

      foreach (var method in type.Methods.Where(method => method.Kind == IrMethodKind.Method)) {
        var (written, block) = this.Member(functions[method.Function], type);
        var member = this.MemberName(method, type);
        if (classes)
          members.Add(new JsClassMember(JsClassMemberKind.Method, member, method.IsStatic, written, block, null));
        else
          after.Add(new JsExpressionStatement(new JsAssignment(new JsMember(method.IsStatic ? Id(name) : Prototype(), member), "=", new JsFunctionExpression(null, written, block))));
      }

      foreach (var property in type.Properties.Where(property => property.BackingField is null)) {
        var member = this._memberNames[property];
        var getter = property.Getter is { } read ? this.Member(functions[read.Function], type) : default;
        var setter = property.Setter is { } write ? this.Member(functions[write.Function], type) : default;
        if (accessors) {
          if (property.Getter is not null)
            members.Add(new JsClassMember(JsClassMemberKind.Getter, member, false, [], getter.Body, null));

          if (property.Setter is not null)
            members.Add(new JsClassMember(JsClassMemberKind.Setter, member, false, setter.Parameters, setter.Body, null));

          continue;
        }

        this.Native("Object.defineProperty");
        var descriptor = new List<JsProperty>();
        if (property.Getter is not null)
          descriptor.Add(new JsProperty("get", new JsFunctionExpression(null, [], getter.Body)));

        if (property.Setter is not null)
          descriptor.Add(new JsProperty("set", new JsFunctionExpression(null, setter.Parameters, setter.Body)));

        descriptor.Add(new JsProperty("configurable", new JsBoolean(true)));
        after.Add(new JsExpressionStatement(Invoke("Object.defineProperty", Prototype(), new JsString(member), new JsObject(descriptor) { Multiline = true })));
      }

      JsStatement declaration;
      if (classes) {
        declaration = new JsClass(name, members);
      } else {
        var constructor = members.Single(member => member.Kind == JsClassMemberKind.Constructor);
        declaration = new JsFunctionDeclaration(name, constructor.Parameters, constructor.Body!);
      }

      var exported = type.Visibility == IrVisibility.Public && this._profile.Module == JavaScriptModuleFormat.Esm;
      if (type.Visibility == IrVisibility.Public && !exported)
        this._exported.Add(name);

      return [(exported ? new JsExport(declaration) : declaration) with { Leading = new JsTrivia(1, [$"class {name}"]) }, .. after];
    }

    /// <summary>The value a field of a new object has before its constructor runs: zero, false, the default struct, an empty view or <c>null</c>.</summary>
    private JsExpression FieldDefault(IrType type)
      => type.StructType is { } nested ? this.Default(nested)
        : type == IrType.Bool ? new JsBoolean(false)
        : type.IsMemory ? this.EmptyView(type)
        : type.IsObject || type.IsArray ? new JsNull()
        : this.Zero(type);

    /// <summary>The first value of a static field: its initializer, or the zero of its type.</summary>
    private JsExpression GlobalValue(IrGlobal global) => global.Initializer is { } initializer ? this.Value(initializer) : this.Constant(global.Type, 0).Node;

    /// <summary>The parameters after the receiver and the body of a member's function, in which the receiver is <c>this</c>.</summary>
    private (List<string> Parameters, JsBlock Body) Member(IrFunction function, IrClassType owner) {
      this._receiver = owner.Methods.Any(method => method.Function == function.Name && method.IsStatic) ? null : function.Parameters[0];
      // A member is called from outside like a public function: a struct it is given is the caller's.
      var (parameters, body) = this.Body(function with { IsPublic = true }, this._receiver is null ? 0 : 1);
      this._receiver = null;
      return (parameters, body);
    }

    /// <summary>The expressions of objects.</summary>
    private (JsExpression Node, bool Atomic) Object(IrExpr expression) {
      switch (expression) {
        case IrNew creation:
          return (new JsNew(Id(this.Name(creation.Type.ClassType!.Name)), [.. creation.Arguments.Select(this.Value)]), true);
        case IrNull:
          return (new JsNull(), true);
        case IrReferenceEquals identity:
          var left = this.Operand(identity.Left);
          return (Infix(left, "===", this.Operand(identity.Right)), false);
        case IrStructEquals equality:
          var helper = this.StructHelper("equals", equality.Left.Type.ClassType!);
          var first = this.Text(equality.Left);
          return (Invoke(helper, first, this.Text(equality.Right)), true);
        case IrFieldLoad load:
          return (new JsMember(this.Operand(load.Target), this.MemberName(load.Field)), true);
        default:
          var call = (IrMethodCall)expression;
          var owner = this._owners[call.Method.Function];
          var target = call.Target is null ? Id(this.Name(owner.Name)) : this.Operand(call.Target);
          var member = new JsMember(target, this.MemberName(call.Method, owner));
          return call.Method.Kind switch {
            IrMethodKind.Getter => (member, true),
            IrMethodKind.Setter => (new JsAssignment(member, "=", this.Value(call.Arguments[0])), false),
            _ => (new JsCall(member, [.. call.Arguments.Select(this.Value)]), true)
          };
      }
    }

    /// <summary>A call of a constructor's function from another constructor: the method <c>$init&lt;k&gt;</c> on <c>this</c>.</summary>
    private JsCall Chained(IrCall call) {
      var owner = this._owners[call.Function];
      return new JsCall(new JsMember(new JsThis(), Initializer(owner, call.Function)), [.. call.Arguments.Skip(1).Select(this.Value)]);
    }

    /// <summary>
    /// The equality of a record class as a helper of the module: the same object or both <c>null</c>,
    /// or two objects whose fields are equal.
    /// </summary>
    private JsFunctionDeclaration RecordEquals(string name, IrClassType type) {
      var compared = type.Fields.Select(field => this.FieldEquals(field, this.MemberName(field))).ToList();
      JsExpression both = Infix(Infix(Id("a"), "!==", new JsNull()), "&&", Infix(Id("b"), "!==", new JsNull()));
      foreach (var field in compared)
        both = Infix(both, "&&", field);

      return new JsFunctionDeclaration(name, ["a", "b"], new JsBlock([new JsReturn(Infix(Infix(Id("a"), "===", Id("b")), "||", both))]));
    }

  }

}
