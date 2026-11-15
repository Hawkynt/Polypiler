using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The classes of a file: ECMAScript 2015 classes, also with <c>extends</c> of a class of the file,
/// and constructor functions of ECMAScript 5 - a function declaration that assigns a property of
/// <c>this</c> or whose prototype the top level of the file gives methods
/// (<c>C.prototype.m = function …</c>), accessors (<c>Object.defineProperty(C.prototype, "p", { get, set })</c>)
/// or the prototype of another (<c>B.prototype = Object.create(C.prototype)</c>). Each member is a
/// candidate of its own, named <c>Class.member</c>, which no function of the module can be named.
/// </summary>
internal sealed partial class FunctionCollector {

  private readonly List<ClassModel> _classes = [];

  /// <summary>The classes of the file, in source order.</summary>
  public IReadOnlyList<ClassModel> Classes => this._classes;

  /// <summary>The native errors, which a class of the file may extend: <c>Error</c> only.</summary>
  public ErrorLibrary? Errors { get; init; }

  /// <summary>The statements of the top level that declare a class or give it a member, which are declarations to the check of module state.</summary>
  public HashSet<Node> ClassStatements { get; } = [];

  /// <summary>The names of the constructor functions of the file.</summary>
  private static HashSet<string> ConstructorFunctions(Program program) {
    var names = new HashSet<string>();
    foreach (var statement in program.Body) {
      if (PrototypeOwner(statement) is { } owner)
        names.Add(owner);

      // The class a constructor function's prototype is made from is one as well, whatever it declares.
      if (statement is ExpressionStatement { Expression: AssignmentExpression { Right: CallExpression { Arguments: [MemberExpression { Object: Identifier inherited, Property: Identifier { Name: "prototype" } }] } } }
          && PrototypeOwner(statement) is not null)
        names.Add(inherited.Name);

      if ((statement is ExportNamedDeclaration { Declaration: FunctionDeclaration exported } ? exported : statement as FunctionDeclaration) is { } function && AssignsThis(function.Body))
        names.Add(function.Id!.Name);
    }

    return names;
  }

  /// <summary>The class whose prototype a statement gives a member, <c>C.prototype.m = …</c> or <c>Object.defineProperty(C.prototype, …)</c>, or <see langword="null"/>.</summary>
  private static string? PrototypeOwner(Statement statement) => statement switch {
    ExpressionStatement { Expression: AssignmentExpression { Left: MemberExpression { Object: MemberExpression { Object: Identifier owner, Property: Identifier { Name: "prototype" } } } } } => owner.Name,
    ExpressionStatement { Expression: AssignmentExpression { Left: MemberExpression { Object: Identifier owner, Property: Identifier { Name: "prototype" } }, Right: CallExpression } } => owner.Name,
    ExpressionStatement { Expression: CallExpression { Callee: MemberExpression { Object: Identifier { Name: "Object" }, Property: Identifier { Name: "defineProperty" } }, Arguments: [MemberExpression { Object: Identifier owner, Property: Identifier { Name: "prototype" } }, ..] } } => owner.Name,
    _ => null
  };

  /// <summary>Whether a body assigns a property of <c>this</c> outside the functions it declares, which have a <c>this</c> of their own.</summary>
  private static bool AssignsThis(Node node)
    => node is AssignmentExpression { Left: MemberExpression { Object: ThisExpression } }
      || node.ChildNodes.Any(child => child is not (FunctionExpression or FunctionDeclaration) && AssignsThis(child));

  /// <summary>The key of a member as the source spells it: a name, or <c>#name</c> for a private one.</summary>
  private static string? KeyOf(Node key) => key switch {
    Identifier identifier => identifier.Name,
    PrivateIdentifier hidden => "#" + hidden.Name,
    _ => null
  };

  /// <summary>Takes a statement that declares a class; says whether it was one.</summary>
  private bool ClassOf(Statement statement, bool isScript, HashSet<string> constructors) {
    switch (statement) {
      case ClassDeclaration declaration:
        this.Class(declaration, statement, isScript);
        return true;
      case ExportNamedDeclaration { Declaration: ClassDeclaration declaration }:
        this.Class(declaration, statement, true);
        return true;
      case FunctionDeclaration declaration when constructors.Contains(declaration.Id!.Name):
        this.ConstructorFunction(declaration, statement, isScript);
        return true;
      case ExportNamedDeclaration { Declaration: FunctionDeclaration declaration } when constructors.Contains(declaration.Id!.Name):
        this.ConstructorFunction(declaration, statement, true);
        return true;
      default:
        return false;
    }
  }

  /// <summary>The name of the function of a member: <c>Class.ctor</c>, <c>Class.get_key</c>, <c>Class.set_key</c> or <c>Class.key</c>.</summary>
  private static string MemberName(ClassModel owner, MemberKind kind, string key) => kind switch {
    MemberKind.Constructor => $"{owner.Name}.ctor",
    MemberKind.Getter => $"{owner.Name}.get_{ClassModel.IrName(key)}",
    MemberKind.Setter => $"{owner.Name}.set_{ClassModel.IrName(key)}",
    _ => $"{owner.Name}.{ClassModel.IrName(key)}"
  };

  private Candidate Member(ClassModel owner, MemberKind kind, string key, bool isStatic, Node anchor, IFunction function) {
    var candidate = new Candidate(MemberName(owner, kind, key), anchor, function) {
      Class = owner, Member = kind, Key = key, IsStatic = isStatic, IsPublic = owner.IsPublic, Documentation = this.Documentation(anchor),
      Problem = function is ArrowFunctionExpression ? "an arrow function as a member" : null
    };
    this.Add(candidate);
    if (kind == MemberKind.Constructor)
      owner.Constructor = candidate;
    else
      owner.Members.Add(candidate);

    return candidate;
  }

  /// <summary>A static field: a variable of the module named <c>Class.key</c>, which the class owns.</summary>
  private void Static(ClassModel owner, string key, Node declaration, Node? initializer) {
    var variable = new Variable($"{owner.Name}.{ClassModel.IrName(key)}", declaration, false) { IsGlobal = true, Initializer = initializer, IsUninitialized = initializer is null };
    owner.Statics[key] = variable;
    this._globals.Add(variable);
  }

  /// <summary>An ECMAScript 2015 class: its constructor, methods, accessors, fields and static fields.</summary>
  private void Class(ClassDeclaration declaration, Node statement, bool isPublic) {
    var owner = new ClassModel(declaration.Id!.Name, declaration) { IsPublic = isPublic };
    this._classes.Add(owner);
    this.ClassStatements.Add(statement);
    if (declaration.SuperClass is Identifier { Name: var inherited })
      owner.BaseName = inherited;
    else if (declaration.SuperClass is { } other)
      owner.Problem = "extends " + source[other.Start..other.End];

    foreach (var element in declaration.Body.Body) {
      switch (element) {
        case MethodDefinition { Computed: false } method when KeyOf(method.Key) is { } key:
          var kind = method.Kind switch {
            PropertyKind.Constructor => MemberKind.Constructor,
            PropertyKind.Get => MemberKind.Getter,
            PropertyKind.Set => MemberKind.Setter,
            _ => MemberKind.Method
          };
          var candidate = this.Member(owner, kind, key, method.Static, method, (IFunction)method.Value);
          if (method.Static && kind is MemberKind.Getter or MemberKind.Setter)
            candidate.Problem ??= "a static accessor";

          break;
        case PropertyDefinition { Computed: false, Static: true } field when KeyOf(field.Key) is { } key:
          this.Static(owner, key, field.Key, field.Value);
          break;
        case PropertyDefinition { Computed: false } field when KeyOf(field.Key) is { } key:
          owner.Fields.Add(new ClassField(key, new Variable(ClassModel.IrName(key), field.Key, false), field.Value));
          break;
        case StaticBlock:
          owner.Problem ??= "a static block";
          break;
        // Explicit resource management is in no level a profile targets: a resource scope of JavaScript is a try ... finally.
        case MethodDefinition { Computed: true, Key: MemberExpression { Computed: false, Object: Identifier { Name: "Symbol" }, Property: Identifier { Name: "dispose" or "asyncDispose" } } key }:
          owner.Problem ??= $"a [{source[key.Start..key.End]}] method";
          break;
        default:
          owner.Problem ??= "a computed member name";
          break;
      }
    }

    if (owner.Constructor is { } constructor)
      Assigned(owner, constructor.Function.Body);
  }

  /// <summary>A constructor function of ECMAScript 5; its methods follow at the top level of the file.</summary>
  private void ConstructorFunction(FunctionDeclaration declaration, Node statement, bool isPublic) {
    var owner = new ClassModel(declaration.Id!.Name, declaration) { IsPublic = isPublic };
    this._classes.Add(owner);
    this.ClassStatements.Add(statement);
    this.Member(owner, MemberKind.Constructor, "constructor", false, statement, declaration);
    Assigned(owner, declaration.Body);
  }

  /// <summary>
  /// The fields a constructor assigns through <c>this</c>, at any depth of its body but inside another
  /// function, in source order. <c>this.name = '…'</c> of a derived class is the name an error gives
  /// itself (<see cref="Lowering"/> drops it), no field.
  /// </summary>
  private static void Assigned(ClassModel owner, Node node) {
    if (node is AssignmentExpression { Left: MemberExpression { Object: ThisExpression, Computed: false } member } assignment
        && KeyOf(member.Property) is { } key && owner.Field(key) is null && !(key == "name" && assignment.Right is StringLiteral && owner.BaseName is not null))
      owner.Fields.Add(new ClassField(key, new Variable(ClassModel.IrName(key), member.Property, false), null));

    foreach (var child in node.ChildNodes.Where(child => child is not (FunctionExpression or FunctionDeclaration or ArrowFunctionExpression)))
      Assigned(owner, child);
  }

  /// <summary>
  /// The members the top level of the file gives a constructor function: methods on its prototype,
  /// accessors defined on it, static methods and static fields on the function itself.
  /// </summary>
  private void Prototypes(Program program) {
    foreach (var statement in program.Body) {
      switch (statement) {
        case ExpressionStatement { Expression: AssignmentExpression { Operator: Operator.Assignment, Left: MemberExpression { Computed: false } target, Right: var value } }
          when target.Object is MemberExpression { Object: Identifier { Name: var name }, Property: Identifier { Name: "prototype" } } && this.ClassNamed(name) is { } owner:
          this.ClassStatements.Add(statement);
          if (owner.Declaration is FunctionDeclaration && owner.BaseName is not null && KeyOf(target.Property) == "constructor" && value is Identifier { Name: var itself } && itself == name)
            break;

          if (owner.Declaration is ClassDeclaration || value is not FunctionExpression prototyped || KeyOf(target.Property) is not { } named)
            owner.Problem ??= "prototype mutation";
          else
            this.Member(owner, MemberKind.Method, named, false, statement, prototyped);

          break;
        case ExpressionStatement { Expression: AssignmentExpression { Operator: Operator.Assignment, Left: MemberExpression { Computed: false, Object: Identifier { Name: var name }, Property: Identifier { Name: var member } } target, Right: var value } }
          when this.ClassNamed(name) is { Declaration: FunctionDeclaration } owner:
          this.ClassStatements.Add(statement);
          if (member == "prototype" && value is CallExpression {
            Callee: MemberExpression { Object: Identifier { Name: "Object" }, Property: Identifier { Name: "create" } },
            Arguments: [MemberExpression { Object: Identifier { Name: var inherited }, Property: Identifier { Name: "prototype" } }]
          }
              && owner.BaseName is null && owner.Members.Count == 0)
            owner.BaseName = inherited;
          else if (member == "prototype")
            owner.Problem ??= owner.Members.Count > 0 ? "a method put on the prototype before Object.create replaces it" : "prototype mutation";
          else if (value is FunctionExpression function)
            this.Member(owner, MemberKind.Method, member, true, statement, function);
          else
            this.Static(owner, member, target.Property, value);

          break;
        case ExpressionStatement { Expression: CallExpression { Arguments: [_, StringLiteral { Value: var key }, ObjectExpression descriptor] } } when PrototypeOwner(statement) is { } name && this.ClassNamed(name) is { } owner:
          this.ClassStatements.Add(statement);
          foreach (var property in descriptor.Properties) {
            switch (property) {
              case Property { Computed: false, Key: Identifier { Name: "get" }, Value: FunctionExpression getter }:
                this.Member(owner, MemberKind.Getter, key, false, statement, getter);
                break;
              case Property { Computed: false, Key: Identifier { Name: "set" }, Value: FunctionExpression setter }:
                this.Member(owner, MemberKind.Setter, key, false, statement, setter);
                break;
              case Property { Computed: false, Key: Identifier { Name: "configurable" or "enumerable" } }:
                break;
              default:
                owner.Problem ??= "a property descriptor other than get, set, configurable and enumerable";
                break;
            }
          }

          break;
        case ExpressionStatement when PrototypeOwner(statement) is { } name && this.ClassNamed(name) is { } owner:
          this.ClassStatements.Add(statement);
          owner.Problem ??= "prototype mutation";
          break;
        case ExpressionStatement {
          Expression: CallExpression {
            Callee: MemberExpression { Object: Identifier { Name: "Object" }, Property: Identifier { Name: "setPrototypeOf" } },
            Arguments: [var target, ..]
          }
        } when (target is MemberExpression { Object: Identifier changed } ? changed : target as Identifier) is { } chained && this.ClassNamed(chained.Name) is { } owner:
          this.ClassStatements.Add(statement);
          owner.Problem ??= "Object.setPrototypeOf";
          break;
      }
    }

    this.Hierarchy();
  }

  /// <summary>
  /// Gives every class the class of the file it extends, refusing one that is no class of the file or
  /// a hierarchy that loops, and settles which methods override which: a method of a derived class
  /// replaces the method of its key of the nearest base class. A field or an accessor of a base class
  /// declared again, and a method with the key of a field or an accessor, is refused.
  /// </summary>
  private void Hierarchy() {
    foreach (var owner in this._classes.Where(owner => owner.BaseName is not null)) {
      owner.Base = this.ClassNamed(owner.BaseName!) ?? (owner.BaseName == "Error" ? this.Errors?.Error : null);
      if (owner.Base is null)
        owner.Problem ??= $"extends {owner.BaseName}, which is no class of the file";
      else if (owner.Base.Lineage.Contains(owner))
        (owner.Problem, owner.Base) = (owner.Problem ?? "a class that extends itself", null);
    }

    foreach (var owner in this._classes.Where(owner => owner.Base is not null)) {
      var ancestors = owner.Base!.Lineage.ToList();
      // Below ES2022 a private #x is the property _x, which one of a base class would share.
      if (owner.Fields.Select(field => field.Key).Concat(owner.Members.Select(member => member.Key)).FirstOrDefault(key => key.StartsWith('#')
          && ancestors.Any(ancestor => ancestor.Fields.Any(field => field.Key == key) || ancestor.Members.Any(member => member.Key == key))) is { } hidden)
        owner.Problem ??= $"the member {hidden} of a base class declared again";

      foreach (var field in owner.Fields.ToList()) {
        if (field.IsPrivate || !ancestors.Any(ancestor => ancestor.OwnField(field.Key) is not null || ancestor.Members.Any(member => member.Key == field.Key && !member.IsStatic)))
          continue;

        if (field.Initializer is not null || ancestors.All(ancestor => ancestor.OwnField(field.Key) is null))
          owner.Problem ??= $"the member {field.Key} of a base class declared again";
        else
          owner.Fields.Remove(field);
      }

      foreach (var member in owner.Members.Where(member => !member.IsStatic && !member.Key.StartsWith('#'))) {
        var inherited = ancestors.SelectMany(ancestor => ancestor.Members).FirstOrDefault(other => other.Key == member.Key && !other.IsStatic);
        if (inherited is not null && member.Member == MemberKind.Method && inherited.Member == MemberKind.Method) {
          member.Overrides = inherited;
          inherited.IsOverridden = true;
          if (member.Function.Params.Count != inherited.Function.Params.Count)
            owner.Problem ??= "an override with another number of parameters";
        } else if (inherited is not null || ancestors.Any(ancestor => ancestor.OwnField(member.Key) is not null)) {
          owner.Problem ??= $"the member {member.Key} of a base class declared again";
        }
      }
    }
  }

  private ClassModel? ClassNamed(string name) => this._classes.FirstOrDefault(owner => owner.Name == name);

}
