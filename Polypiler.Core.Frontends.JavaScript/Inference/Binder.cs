using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// Reads the body of one function once, before any type is known: it gives every identifier the
/// variable or constant it names and lists how values reach the variables, the result and the
/// parameters of the functions called. It refuses nothing. What the subset does not have it leaves
/// alone, and the lowering, which meets the constructs in source order, names the first one.
/// <para>
/// The scoping is the lowering's: a declaration lives until its block ends, also a <c>var</c>, and a
/// name declared while it is visible is a second declaration.
/// </para>
/// </summary>
internal sealed partial class Binder(FunctionModel model, List<Candidate> candidates, IReadOnlyDictionary<string, ModuleConstant> constants, string source) {

  private readonly Dictionary<string, Variable> _scope = [];
  private List<string> _declared = [];

  /// <summary>The names the scope has from the functions around a nested function, which a declaration of its own may hide.</summary>
  private readonly HashSet<string> _inherited = [];

  /// <summary>The variables of the functions around that a declaration of the body hides, by name, until its block ends.</summary>
  private readonly Dictionary<string, Variable> _hidden = [];

  /// <summary>The callee of the call being read, which is no use of a function as a value.</summary>
  private Node? _callee;

  /// <summary>Makes the candidate of a function nested in the body and binds it, with the variables it sees.</summary>
  public System.Func<FunctionModel, IFunction, string, IReadOnlyDictionary<string, Variable>, Candidate>? Nest { get; init; }

  /// <summary>The variables of the module, which every name of a function that no local or parameter hides names.</summary>
  public IReadOnlyDictionary<string, Variable> Globals { get; init; } = new Dictionary<string, Variable>();

  /// <summary>The copy of a function each call the import gave one calls, by the callee of the call.</summary>
  public IReadOnlyDictionary<Node, Candidate> Redirects { get; init; } = new Dictionary<Node, Candidate>();

  /// <summary>The classes of the file by name, which a name that nothing else of the function names names.</summary>
  public IReadOnlyDictionary<string, ClassModel> Classes { get; init; } = new Dictionary<string, ClassModel>();

  /// <summary>The class a name names: one of the file, where no local, parameter, variable or constant of its name hides it.</summary>
  public ClassModel? ClassNamed(Node node)
    => node is Identifier { Name: var name } && !this._scope.ContainsKey(name) && !this.Globals.ContainsKey(name) && !constants.ContainsKey(name)
      && this.Classes.TryGetValue(name, out var type) ? type : null;

  /// <summary>The key of a member as the source spells it - a name, or <c>#name</c> for a private one - or <see langword="null"/> for a computed one.</summary>
  public static string? KeyOf(MemberExpression member) => member switch {
    { Computed: false, Property: Identifier { Name: var name } } => name,
    { Computed: false, Property: PrivateIdentifier { Name: var name } } => "#" + name,
    _ => null
  };

  /// <summary>The static field a member expression names, <c>Class.key</c>, or <see langword="null"/>.</summary>
  private Variable? StaticField(Node node)
    => node is MemberExpression member && this.ClassNamed(member.Object) is { } owner && KeyOf(member) is { } key && owner.Statics.TryGetValue(key, out var field) ? field : null;

  /// <summary>The enum objects of the file, which a name names that nothing else of its name hides.</summary>
  public IReadOnlyDictionary<string, EnumObject> Enums { get; init; } = new Dictionary<string, EnumObject>();

  /// <summary>
  /// Binds the initializers of the variables of the module, in source order. Each sees the variables
  /// declared before it and the constants; a later variable it names is in its temporal dead zone.
  /// </summary>
  public void BindInitializers(IReadOnlyList<Variable> globals) {
    foreach (var global in globals) {
      if (global.Initializer is { } initializer) {
        this.Expression(initializer);
        model.Flows.Add(new Flow(FlowKind.Assign, initializer, global));
      }

      this._scope[global.Name] = global;
    }
  }

  /// <summary>The local, the parameter or else the variable of the module a name names in the current scope.</summary>
  private bool Lookup(string name, out Variable variable)
    => this._scope.TryGetValue(name, out variable!) || this.Globals.TryGetValue(name, out variable!);

  /// <summary>Binds the parameters and the body.</summary>
  public void Bind() {
    // The fields a class declares with a value get it when its constructor starts, before its parameters are seen.
    if (model.Candidate is { Member: MemberKind.Constructor, Class: { } owner })
      foreach (var field in owner.Fields.Where(field => field.Initializer is not null)) {
        this.Expression(field.Initializer!);
        model.Flows.Add(new Flow(FlowKind.Assign, field.Initializer!, field.Variable));
      }

    // A nested function sees the variables of the functions around it, which its own may hide.
    foreach (var (name, outer) in model.Candidate.Scope ?? new Dictionary<string, Variable>()) {
      this._scope[name] = outer;
      this._inherited.Add(name);
    }

    foreach (var parameter in model.Parameters) {
      this._scope[parameter.Name] = parameter;
      this._inherited.Remove(parameter.Name);
    }

    // The concise body of an arrow is the value it returns.
    if (model.Candidate.Function.Body is Expression concise) {
      model.Flows.Add(new Flow(FlowKind.Return, concise));
      this.Expression(concise);
      return;
    }

    var body = ((FunctionBody)model.Candidate.Function.Body).Body;
    var functions = this.Hoist(body);
    var rest = body.SkipWhile(this.IsPrologue).ToList();
    foreach (var statement in body.Take(body.Count - rest.Count))
      this.Statement(statement);

    // A read in the prologue is the coerced re-assignment itself, which is no use of its own.
    foreach (var parameter in model.Parameters.Where(parameter => parameter.Prologue is not null))
      parameter.Uses.Clear();

    foreach (var statement in rest)
      this.Statement(statement);

    this.Bind(functions);
  }

  /// <summary>
  /// The function declarations of a block, each declared as a variable before anything of the block
  /// is read: a declaration is hoisted to the start of its block (ECMA-262 §10.2.11), so that the
  /// block may call it before it stands.
  /// </summary>
  private List<(FunctionDeclaration Function, Variable? Variable)> Hoist(IEnumerable<Statement> statements) {
    var found = new List<(FunctionDeclaration, Variable?)>();
    foreach (var function in statements.OfType<FunctionDeclaration>())
      found.Add((function, function.Id is { } id ? this.Declare(id, false, false) : null));

    return found;
  }

  /// <summary>Binds the hoisted function declarations of a block once the block is read: each sees every variable of the block.</summary>
  private void Bind(List<(FunctionDeclaration Function, Variable? Variable)> functions) {
    foreach (var (function, variable) in functions) {
      var nested = this.Nested(function, function.Id?.Name ?? "function");
      if (variable is not null && nested is not null)
        model.Flows.Add(new Flow(FlowKind.Assign, function, variable));
    }
  }

  /// <summary>The candidate of a function nested in the body, bound with the variables visible here.</summary>
  private Candidate? Nested(IFunction function, string name) {
    if (this.Nest is not { } nest)
      return null;

    var candidate = nest(model, function, name, new Dictionary<string, Variable>(this._scope));
    model.Nested[(Node)function] = candidate;
    return candidate;
  }

  /// <summary>
  /// Whether a statement at the start of the body re-assigns a parameter to itself coerced,
  /// <c>x = x | 0</c> or <c>x = x &gt;&gt;&gt; 0</c>: from there on the parameter is that integer.
  /// </summary>
  private bool IsPrologue(Statement statement) {
    if (statement is not ExpressionStatement { Expression: AssignmentExpression { Operator: Operator.Assignment, Left: Identifier { Name: var name }, Right: var right } }
        || right is not BinaryExpression { Operator: Operator.BitwiseOr or Operator.UnsignedRightShift, Left: Identifier { Name: var read }, Right: NumericLiteral { Value: 0 } } coercion
        || name != read
        || !this._scope.TryGetValue(name, out var parameter)
        || parameter.Prologue is not null)
      return false;

    parameter.Prologue = (coercion.Operator == Operator.BitwiseOr ? Coercion.Int32 : Coercion.Uint32, coercion);
    return true;
  }

  private void Block(IEnumerable<Statement> statements) {
    var outer = this._declared;
    this._declared = [];
    var list = statements.ToList();
    var functions = this.Hoist(list);
    foreach (var statement in list)
      this.Statement(statement);

    this.Bind(functions);
    this.Leave(outer);
  }

  private void Leave(List<string> outer) {
    foreach (var name in this._declared) {
      this._scope.Remove(name);
      if (this._hidden.Remove(name, out var hidden)) {
        this._scope[name] = hidden;
        this._inherited.Add(name);
      }
    }

    this._declared = outer;
  }

  private void Body(Statement statement) => this.Block(statement is BlockStatement block ? block.Body : [statement]);

  private void Statement(Statement statement) {
    switch (statement) {
      case ExpressionStatement or ForOfStatement when this.KeyLoop(statement):
        break;
      case BlockStatement block:
        this.Block(block.Body);
        break;
      case VariableDeclaration declaration:
        this.Declaration(declaration);
        break;
      case ExpressionStatement { Expression: YieldExpression produced } when model.Candidate.Function.Generator:
        // A yield that stands as a statement produces the next value of the generator (#1327).
        if (produced.Argument is not null) {
          this.Expression(produced.Argument);
          model.Flows.Add(new Flow(FlowKind.Yield, produced.Argument, Index: produced.Delegate ? 1 : 0));
        }

        break;
      case ExpressionStatement expression:
        this.Expression(expression.Expression);
        break;
      case IfStatement conditional:
        this.Expression(conditional.Test);
        this.Body(conditional.Consequent);
        if (conditional.Alternate is not null)
          this.Body(conditional.Alternate);

        break;
      case WhileStatement loop:
        this.Expression(loop.Test);
        this.Body(loop.Body);
        break;
      case DoWhileStatement loop:
        this.Body(loop.Body);
        this.Expression(loop.Test);
        break;
      case ForStatement loop:
        var outer = this._declared;
        this._declared = [];
        if (loop.Init is VariableDeclaration declared)
          this.Declaration(declared);
        else if (loop.Init is not null)
          this.Expression(loop.Init);

        if (loop.Test is not null)
          this.Expression(loop.Test);

        this.Body(loop.Body);
        if (loop.Update is not null)
          this.Expression(loop.Update);

        this.Leave(outer);
        break;
      case SwitchStatement choice:
        this.Expression(choice.Discriminant);
        foreach (var section in choice.Cases) {
          if (section.Test is not null)
            this.Expression(section.Test);

          this.Block(section.Consequent);
        }

        break;
      case ForOfStatement loop:
        this.ForOf(loop);
        break;
      case ReturnStatement { Argument: null } when model.Candidate.Function.Generator:
        // return; ends a generator, which returns no value of its own.
        break;
      case ReturnStatement result:
        model.Flows.Add(new Flow(FlowKind.Return, result));
        if (result.Argument is not null)
          this.Expression(result.Argument);

        break;
      case ThrowStatement thrown:
        this.Expression(thrown.Argument);
        break;
      case TryStatement attempt:
        this.Try(attempt);
        break;
    }
  }

  /// <summary>
  /// A try: its blocks, and the binding of its catch, which holds an exception - an object of
  /// <c>Error</c> (<see cref="ErrorLibrary"/>) - and lives until the catch block ends.
  /// </summary>
  private void Try(TryStatement attempt) {
    this.Block(attempt.Block.Body);
    if (attempt.Handler is { } handler) {
      var outer = this._declared;
      this._declared = [];
      if (handler.Param is Identifier caught) {
        if (this._scope.ContainsKey(caught.Name)) {
          model.Refused[caught] = $"a second declaration of {caught.Name}";
        } else if (this.Classes.Values.FirstOrDefault(type => type.Ir == Polypiler.Core.IR.IrExceptionClass.Exception) is { } error) {
          var variable = new Variable(caught.Name, caught, false) { Type = JsType.Object(error) };
          model.Locals.Add(variable);
          model.Bindings[caught] = variable;
          this._scope[caught.Name] = variable;
          this._declared.Add(caught.Name);
        }
      }

      foreach (var statement in handler.Body.Body)
        this.Statement(statement);

      this.Leave(outer);
    }

    if (attempt.Finalizer is { } finalizer)
      this.Block(finalizer.Body);
  }

  private void Declaration(VariableDeclaration declaration) {
    foreach (var declarator in declaration.Declarations) {
      // The initializer is read before the name exists, as the lowering reads it.
      if (declarator.Init is not null)
        this.Expression(declarator.Init);

      switch (declarator.Id) {
        case Identifier id when this.Declare(id, declaration, declarator.Init) is { } variable && declarator.Init is not null:
          model.Flows.Add(new Flow(FlowKind.Assign, declarator.Init, variable));
          // The one object a const made by new holds is what properties copied into it extend (#1388).
          if (variable.IsConstant && declarator.Init is NewExpression creation)
            variable.Creation = creation;

          break;
        case ObjectPattern pattern when declarator.Init is not null:
          // const { x, y } = p declares x and y, each with the value of its key; anything else in the pattern the lowering refuses.
          foreach (var property in pattern.Properties)
            if (TypeInference.Plain(property) is { Value: Identifier id } && this.Declare(id, declaration, declarator.Init) is { } part)
              model.Flows.Add(new Flow(FlowKind.Field, property, part, Source: declarator.Init));

          break;
      }
    }
  }

  /// <summary>Declares a local, or records that the scoping rules refuse a second declaration of its name.</summary>
  private Variable? Declare(Identifier id, VariableDeclaration declaration, Node? initializer) {
    var variable = this.Declare(id, declaration.Kind == VariableDeclarationKind.Const, initializer is null);
    // The type a /** @type {T} */ before a declaration of one name states (#1416).
    if (variable is not null && declaration.Declarations.Count == 1 && this.Documentation(declaration) is { } comment)
      variable.Spelling = JsDoc.TypeSpelling(comment);

    return variable;
  }

  /// <summary>The <c>/** … */</c> comment that ends right before a node, with nothing but white space between the two, or <see langword="null"/>.</summary>
  private string? Documentation(Node node) {
    var before = source[..node.Start].TrimEnd();
    var open = before.LastIndexOf("/**", System.StringComparison.Ordinal);
    return before.EndsWith("*/", System.StringComparison.Ordinal) && open >= 0 && before.IndexOf("*/", open + 3, System.StringComparison.Ordinal) == before.Length - 2
      ? before[open..] : null;
  }

  /// <summary>
  /// Declares a local, or records that the scoping rules refuse a second declaration of its name. A
  /// variable of a function around a nested one is hidden until the block of the declaration ends.
  /// </summary>
  private Variable? Declare(Identifier id, bool constant, bool uninitialized) {
    if (this._scope.TryGetValue(id.Name, out var visible)) {
      if (!this._inherited.Remove(id.Name)) {
        model.Refused[id] = $"a second declaration of {id.Name}";
        return null;
      }

      this._hidden[id.Name] = visible;
    }

    var variable = new Variable(id.Name, id, false) {
      IsConstant = constant,
      IsUninitialized = uninitialized
    };
    model.Locals.Add(variable);
    model.Bindings[id] = variable;
    this._scope[id.Name] = variable;
    this._declared.Add(id.Name);
    return variable;
  }

  /// <summary>Reads an expression. <paramref name="coercion"/> is what the place it stands in applies to its value.</summary>
  private void Expression(Node node, Coercion coercion = Coercion.None, Node? parent = null) {
    switch (node) {
      case Identifier identifier:
        this.Read(identifier, coercion, parent ?? identifier);
        break;
      case ArrowFunctionExpression or FunctionExpression:
        // A nested function: its body is bound as a function of its own, which sees the variables here.
        this.Nested((IFunction)node, parent is VariableDeclarator { Id: Identifier named } ? named.Name : "lambda");
        break;
      case IFunction or ClassExpression:
        // Nothing of the subset; what stands inside belongs to another scope.
        break;
      case ObjectExpression literal:
        // The values of an object literal are read where the literal stands; what else it holds the lowering refuses.
        foreach (var property in literal.Properties)
          if (TypeInference.Plain(property) is { } entry)
            this.Expression(entry.Value);

        break;
      case AssignmentExpression assignment:
        this.Assignment(assignment);
        break;
      case UpdateExpression { Argument: MemberExpression stepping } step when this.StaticField(stepping) is { } field:
        model.Bindings[stepping] = field;
        field.Uses.Add((Coercion.None, step));
        model.Flows.Add(new Flow(FlowKind.Step, step, field));
        break;
      case UpdateExpression { Argument: MemberExpression { Computed: false } property } step when KeyOf(property) is not null:
        this.Expression(property);
        model.Flows.Add(new Flow(FlowKind.FieldStep, step, Source: property.Object));
        break;
      case NewExpression creation when this.ClassNamed(creation.Callee) is { } created:
        foreach (var argument in creation.Arguments)
          this.Expression(argument);

        if (created.Initial is { } constructor) {
          if (!model.Callees.Contains(constructor))
            model.Callees.Add(constructor);

          for (var index = 0; index < creation.Arguments.Count; ++index)
            model.Flows.Add(new Flow(FlowKind.Argument, creation.Arguments[index], null, constructor, index));
        }

        break;
      case MemberExpression member when this.StaticField(member) is { } field:
        model.Bindings[member] = field;
        field.Uses.Add((coercion, parent ?? member));
        break;
      case UpdateExpression step:
        if (step.Argument is Identifier stepped) {
          this.Read(stepped, Coercion.None, step);
          if (model.Bindings.GetValueOrDefault(stepped) is Variable variable)
            model.Flows.Add(new Flow(FlowKind.Step, step, variable));
        } else {
          this.Expression(step.Argument);
          if (this.Holder(step.Argument) is { } holder)
            model.Flows.Add(new Flow(FlowKind.StepElement, step, holder));
        }

        break;
      case UnaryExpression { Operator: Operator.BitwiseNot } complement:
        this.Expression(complement.Argument, Coercion.Int32, complement);
        break;
      case LogicalExpression { Operator: Operator.NullishCoalescing, Left: Identifier coalesced } logical:
        this.Expression(coalesced);
        if (model.Bindings.GetValueOrDefault(coalesced) is Variable read)
          read.ComparedWithNull = true;

        this.Expression(logical.Right);
        break;
      case LogicalExpression logical:
        this.Expression(logical.Left);
        this.Expression(logical.Right);
        break;
      case BinaryExpression { Operator: Operator.InstanceOf } test when this.ClassNamed(test.Right) is not null:
        this.Expression(test.Left);
        break;
      case BinaryExpression { Operator: Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality } compared
          when IsAbsentLiteral(compared.Left) || IsAbsentLiteral(compared.Right):
        this.Expression(compared.Left, Coercion.None, compared);
        this.Expression(compared.Right, Coercion.None, compared);
        if ((IsAbsentLiteral(compared.Left) ? compared.Right : compared.Left) is Identifier tested && model.Bindings.GetValueOrDefault(tested) is Variable testedVariable) {
          var absent = IsAbsentLiteral(compared.Left) ? compared.Left : compared.Right;
          var loose = compared.Operator is Operator.Equality or Operator.Inequality;
          testedVariable.ComparedWithNull |= loose || absent is NullLiteral;
          testedVariable.ComparedWithUndefined |= loose || absent is not NullLiteral;
        }
        break;
      case BinaryExpression binary:
        var (left, right) = Operands(binary.Operator);
        this.Expression(binary.Left, left, binary);
        this.Expression(binary.Right, right, binary);
        break;
      case MemberExpression { Computed: false } held when !ReferenceEquals(held, this._callee) && this.Callee(held) is { IsProperty: true } property:
        // A function of an object of functions named where no call is: the function as a value (#1408).
        model.FunctionValues[held] = property;
        if (!model.Callees.Contains(property))
          model.Callees.Add(property);

        break;
      case MemberExpression member:
        this.Expression(member.Object);
        // An element or the length is taken; the name of the property is read from the source, whatever kind of name it is.
        // A method with a callback is one of an array as well (#1329).
        if (member.Object is Identifier array && model.Bindings.GetValueOrDefault(array) is Variable used && !(member.Computed && this.IsKey(member.Property))
            && (member.Computed || source[member.Property.Start..member.Property.End] == "length" || (member.Property is Identifier { Name: var method } && TypeInference.IsCallbackMethod(method))))
          used.UsedAsArray ??= member;

        if (member.Computed)
          this.Expression(member.Property);

        break;
      case CallExpression call when Inheritance.Of(call, model.Candidate.Class, this.ClassNamed) is { } inherited:
        foreach (var argument in inherited.Arguments)
          this.Expression(argument);

        if (inherited.Member is { } reached) {
          if (!model.Callees.Contains(reached))
            model.Callees.Add(reached);

          for (var index = 0; index < inherited.Arguments.Count; ++index)
            model.Flows.Add(new Flow(FlowKind.Argument, inherited.Arguments[index], null, reached, index));
        }

        break;
      case CallExpression call:
        this.Call(call);
        break;
      default:
        foreach (var child in node.ChildNodes)
          this.Expression(child);

        break;
    }
  }

  /// <summary>Whether an expression is an absent value as the source spells it: <c>null</c>, <c>undefined</c> - unless a variable of that name hides it - or <c>void 0</c>.</summary>
  public bool IsAbsentLiteral(Node node) => node switch {
    NullLiteral => true,
    Identifier { Name: "undefined" } identifier => !this.Lookup("undefined", out _) && !constants.ContainsKey(identifier.Name),
    UnaryExpression { Operator: Operator.Void, Argument: Literal } => true,
    _ => false
  };

  /// <summary>The coercions a binary operator applies to its two operands.</summary>
  public static (Coercion Left, Coercion Right) Operands(Operator op) => op switch {
    Operator.BitwiseOr or Operator.BitwiseXor or Operator.BitwiseAnd or Operator.LeftShift or Operator.RightShift => (Coercion.Int32, Coercion.Int32),
    Operator.UnsignedRightShift => (Coercion.Uint32, Coercion.Int32),
    _ => (Coercion.None, Coercion.None)
  };

  private void Read(Identifier identifier, Coercion coercion, Node use) {
    if (this.Lookup(identifier.Name, out var variable)) {
      model.Bindings[identifier] = variable;
      variable.Uses.Add((coercion, use));
    } else if (constants.TryGetValue(identifier.Name, out var constant)) {
      model.Bindings[identifier] = constant;
    } else if (!ReferenceEquals(identifier, this._callee) && candidates.Find(candidate => candidate is { IsProperty: false, Class: null } && candidate.Name == identifier.Name) is { } function) {
      // A function of the file named where no call is: the function as a value.
      model.FunctionValues[identifier] = function;
      if (!model.Callees.Contains(function))
        model.Callees.Add(function);
    } else if (this.Enums.TryGetValue(identifier.Name, out var named)) {
      model.Bindings[identifier] = named;
    }
  }

  private void Assignment(AssignmentExpression assignment) {
    var op = Compound(assignment.Operator);
    switch (assignment.Left) {
      case Identifier target:
        if (this.Lookup(target.Name, out var variable)) {
          model.Bindings[target] = variable;
          if (op is { } compound) {
            variable.Uses.Add((Operands(compound).Left, assignment));
            model.Flows.Add(new Flow(FlowKind.Compound, assignment, variable));
          } else {
            model.Flows.Add(new Flow(FlowKind.Assign, assignment.Right, variable));
          }
        } else if (constants.TryGetValue(target.Name, out var constant)) {
          model.Bindings[target] = constant;
        }

        this.Expression(assignment.Right, op is { } applied ? Operands(applied).Right : Coercion.None, assignment);
        break;
      case MemberExpression element when this.StaticField(element) is { } field:
        model.Bindings[element] = field;
        if (op is { } combined) {
          field.Uses.Add((Operands(combined).Left, assignment));
          model.Flows.Add(new Flow(FlowKind.Compound, assignment, field));
        } else {
          model.Flows.Add(new Flow(FlowKind.Assign, assignment.Right, field));
        }

        this.Expression(assignment.Right);
        break;
      case MemberExpression { Computed: false, Object: Identifier truncated, Property: Identifier { Name: "length" } } length
          when this.Lookup(truncated.Name, out var resized):
        this.Expression(length);
        this.Expression(assignment.Right);
        model.Flows.Add(new Flow(FlowKind.Grow, assignment, resized));
        break;
      case MemberExpression { Computed: false, Object: MemberExpression truncatedField, Property: Identifier { Name: "length" } } fieldLength when KeyOf(truncatedField) is not null:
        this.Expression(fieldLength);
        this.Expression(assignment.Right);
        model.Flows.Add(new Flow(FlowKind.FieldGrow, assignment, Source: truncatedField));
        break;
      case MemberExpression { Computed: false } element when KeyOf(element) is not null:
        this.Expression(element);
        this.Expression(assignment.Right);
        model.Flows.Add(new Flow(FlowKind.FieldStore, assignment, Source: element.Object));
        break;
      case MemberExpression { Computed: true } keyed when this.IsKey(keyed.Property):
        // A store at the key of a key loop is the store into the field of that name, which the lowering checks (#1373).
        this.Expression(keyed);
        this.Expression(assignment.Right);
        if (keyed.Object is Identifier { Name: var stored } && this.Lookup(stored, out var copiedInto) && ((Identifier)keyed.Property).Name is var name
            && this._scope.TryGetValue(name, out var key) && key.KeysOf is { } source)
          model.Flows.Add(new Flow(FlowKind.KeyedStore, assignment, copiedInto, Source: source));

        break;
      case MemberExpression element:
        this.Expression(element);
        this.Expression(assignment.Right);
        if (this.Holder(element) is { } holder) {
          model.Flows.Add(new Flow(FlowKind.Store, assignment, holder));
          // A write at a[a.length] appends.
          if (element is { Object: Identifier { Name: var appended }, Property: MemberExpression { Computed: false, Object: Identifier { Name: var measured }, Property: Identifier { Name: "length" } } }
              && appended == measured)
            model.Flows.Add(new Flow(FlowKind.Grow, assignment, holder));
        }

        break;
      default:
        this.Expression(assignment.Right);
        break;
    }
  }

  /// <summary>The variable that holds the array an element expression names, or <see langword="null"/>.</summary>
  private Variable? Holder(Node element)
    => element is MemberExpression { Computed: true, Object: Identifier array } ? model.Bindings.GetValueOrDefault(array) as Variable : null;

  /// <summary>The binary operator of a compound assignment; <see langword="null"/> for <c>=</c> and for what the subset does not have.</summary>
  public static Operator? Compound(Operator assignment) => assignment switch {
    Operator.AdditionAssignment => Operator.Addition,
    Operator.SubtractionAssignment => Operator.Subtraction,
    Operator.MultiplicationAssignment => Operator.Multiplication,
    Operator.DivisionAssignment => Operator.Division,
    Operator.RemainderAssignment => Operator.Remainder,
    Operator.BitwiseAndAssignment => Operator.BitwiseAnd,
    Operator.BitwiseOrAssignment => Operator.BitwiseOr,
    Operator.BitwiseXorAssignment => Operator.BitwiseXor,
    Operator.LeftShiftAssignment => Operator.LeftShift,
    Operator.RightShiftAssignment => Operator.RightShift,
    Operator.UnsignedRightShiftAssignment => Operator.UnsignedRightShift,
    Operator.ExponentiationAssignment => Operator.Exponentiation,
    _ => null
  };

  /// <summary>
  /// A <c>for…of</c>: the iterable is read, then the variable declared, whose values are the code
  /// points of a string; a variable iterated over is used as a string.
  /// </summary>
  private void ForOf(ForOfStatement loop) {
    this.Expression(loop.Right);
    if (loop.Right is Identifier iterated && model.Bindings.GetValueOrDefault(iterated) is Variable used)
      used.UsedAsString ??= loop;

    var outer = this._declared;
    this._declared = [];
    if (loop.Left is VariableDeclaration { Declarations: [{ Id: Identifier id }] } declaration && this.Declare(id, declaration, loop) is { } variable)
      model.Flows.Add(new Flow(FlowKind.Assign, loop, variable));

    // for (const [k, v] of m): the key and the value of every entry of a Map (#1328).
    if (loop.Left is VariableDeclaration { Declarations: [{ Id: ArrayPattern { Elements: [Identifier key, Identifier value] } }] } entries) {
      if (this.Declare(key, entries, loop) is { } keyVariable)
        model.Flows.Add(new Flow(FlowKind.Entry, loop, keyVariable, Index: 0));

      if (this.Declare(value, entries, loop) is { } valueVariable)
        model.Flows.Add(new Flow(FlowKind.Entry, loop, valueVariable, Index: 1));
    }

    this.Body(loop.Body);
    this.Leave(outer);
  }

  /// <summary>The methods only a string has among the values of the subset; calling one on a variable uses it as a string.</summary>
  public static readonly IReadOnlySet<string> StringMethods = new HashSet<string>(System.StringComparer.Ordinal) {
    "charCodeAt", "charAt", "codePointAt", "substring", "startsWith", "endsWith", "trim", "trimStart", "trimEnd",
    "padStart", "padEnd", "repeat", "replace", "replaceAll", "split", "localeCompare", "toUpperCase", "toLowerCase", "normalize"
  };

  private void Call(CallExpression call) {
    if (call.Callee is MemberExpression { Computed: false, Object: Identifier receiver, Property: Identifier { Name: var name } } access
        && StringMethods.Contains(name) && this._scope.TryGetValue(receiver.Name, out var text))
      text.UsedAsString ??= access;

    // Array.isArray(x) is what a function that requires an Array of x asks (#1374).
    if (call.Callee is MemberExpression { Computed: false, Object: Identifier { Name: "Array" } holder, Property: Identifier { Name: "isArray" } } && !this._scope.ContainsKey(holder.Name)
        && call.Arguments is [Identifier tested] && this._scope.TryGetValue(tested.Name, out var array))
      array.TestedAsArray ??= call;

    // Number(s), parseFloat(s) and parseInt(s) parse their first argument, which is text (#1315).
    if (call.Callee is Identifier { Name: "Number" or "parseFloat" or "parseInt" } parser && !this._scope.ContainsKey(parser.Name) && this.Callee(parser) is null
        && call.Arguments is [Identifier argument, ..] && this._scope.TryGetValue(argument.Name, out var parsed))
      parsed.UsedAsString ??= call;

    this._callee = call.Callee;
    this.Expression(call.Callee);
    this._callee = null;
    // push, pop and splice change the length of the array a variable holds, which makes it a list (#1328); the
    // searches of the IR are those of lists, so indexOf and includes make a list of a JavaScript Array as well.
    if (call.Callee is MemberExpression { Computed: false, Object: Identifier grown, Property: Identifier { Name: "push" or "pop" or "splice" or "indexOf" or "includes" or "set" or "add" } }
        && model.Bindings.GetValueOrDefault(grown) is Variable list)
      model.Flows.Add(new Flow(FlowKind.Grow, call, list));

    // The same through a field, of this object or another, whose class only the types say (#1365).
    if (call.Callee is MemberExpression { Computed: false, Object: MemberExpression field, Property: Identifier { Name: "push" or "pop" or "splice" or "indexOf" or "includes" or "set" or "add" } }
        && KeyOf(field) is not null)
      model.Flows.Add(new Flow(FlowKind.FieldGrow, call, Source: field));

    var builtin = this.Builtin(call);
    for (var index = 0; index < call.Arguments.Count; ++index) {
      var coercion = builtin switch {
        "Math.imul" => Coercion.Int32,
        "Math.clz32" => Coercion.Uint32,
        // The value a DataView stores is converted as the method says: ToInt32 or ToUint32, then cut, or modulo 2^64; a number stays one.
        null when index == 1 && call.Callee is MemberExpression { Computed: false, Property: Identifier { Name: var method } }
          && method.StartsWith("set", System.StringComparison.Ordinal) && JavaScriptIntegerApi.ViewType(method) is { } stored
          => stored.IsFloat ? Coercion.None : stored.IsLong ? (stored.IsSigned ? Coercion.Int64 : Coercion.Uint64) : stored.IsSigned ? Coercion.Int32 : Coercion.Uint32,
        _ => Coercion.None
      };
      this.Expression(call.Arguments[index], coercion, call);
    }

    // A method called on an object is one of the class of the object, which only the types say.
    if (builtin is null && this.Callee(call.Callee) is null && call.Callee is MemberExpression { Computed: false } invoked && KeyOf(invoked) is not null
        && this.ClassNamed(invoked.Object) is null)
      for (var index = 0; index < call.Arguments.Count; ++index)
        model.Flows.Add(new Flow(FlowKind.MethodArgument, call.Arguments[index], null, null, index, call));

    // A call of anything else that is no method is a call of a function value, whose functions the types say;
    // so is a call of a field that holds one, this._f(x), which only the types tell from a method (#1408).
    if (builtin is null && this.Callee(call.Callee) is null)
      model.Flows.Add(new Flow(FlowKind.Invoke, call));

    if (builtin is not null || this.Callee(call.Callee) is not { } callee)
      return;

    if (!model.Callees.Contains(callee))
      model.Callees.Add(callee);

    for (var index = 0; index < call.Arguments.Count; ++index)
      model.Flows.Add(new Flow(FlowKind.Argument, call.Arguments[index], null, callee, index, call));

    // A parameter no argument is given for is undefined, unless it has a default.
    for (var index = call.Arguments.Count; index < callee.Function.Params.Count; ++index)
      if (callee.Function.Params[index] is not AssignmentPattern)
        model.Flows.Add(new Flow(FlowKind.Argument, call, null, callee, index, call) { Missing = true });
  }

  /// <summary>
  /// The operation of JavaScript's own library a call names - <c>Math.imul</c>, <c>BigInt.asUintN</c>,
  /// <c>Number.isInteger</c>, <c>BigInt</c>, <c>isNaN</c> - or <see langword="null"/>. A local or a
  /// constant of the same name hides the library, and a function of the file hides a global function.
  /// </summary>
  public string? Builtin(CallExpression call) => call.Callee switch {
    Identifier { Name: "BigInt" } global when !model.Bindings.ContainsKey(global) => "BigInt",
    Identifier { Name: "isNaN" or "isFinite" } global when !model.Bindings.ContainsKey(global) && !candidates.Exists(candidate => !candidate.IsProperty && candidate.Name == global.Name)
      => global.Name,
    MemberExpression member => this.Library(member),
    _ => null
  };

  /// <summary>
  /// The property of a library object a member expression names - <c>Math.PI</c>, <c>Number.isNaN</c>,
  /// <c>BigInt.asIntN</c> - or <see langword="null"/>. A local or a constant of the same name hides the library.
  /// </summary>
  public string? Library(MemberExpression member) => member switch {
    { Computed: false, Object: Identifier { Name: "Math" or "Number" or "BigInt" or "String" } library, Property: Identifier property } when !model.Bindings.ContainsKey(library)
      => $"{library.Name}.{property.Name}",
    { Computed: false, Object: Identifier { Name: "Array" } array, Property: Identifier { Name: "isArray" } } when !model.Bindings.ContainsKey(array) => "Array.isArray",
    _ => null
  };

  /// <summary>
  /// The function a call names: a declaration by its name, a property through the variable its object
  /// literal is assigned to and the properties that lead to it. A local of the same name hides either.
  /// </summary>
  public Candidate? Callee(Node callee) {
    switch (callee) {
      case MemberExpression member when this.ClassNamed(member.Object) is { } owner && KeyOf(member) is { } key:
        return owner.Member(MemberKind.Method, key, true);
      case Identifier { Name: var name } identifier when !model.Bindings.ContainsKey(identifier):
        return this.Redirects.GetValueOrDefault(identifier) ?? candidates.Find(candidate => !candidate.IsProperty && candidate.Name == name);
      case MemberExpression { Computed: false, Property: Identifier { Name: var property } } member when Path(member.Object) is { } path && !model.Bindings.ContainsKey(path.Root):
        return this.Redirects.GetValueOrDefault(member) ?? candidates.Find(candidate => candidate.IsProperty && candidate.Owner == path.Text && candidate.Name == property);
      default:
        return null;
    }
  }

  /// <summary>The dotted path an expression of identifiers and plain properties spells, with the identifier it starts at.</summary>
  private static (string Text, Identifier Root)? Path(Node node) => node switch {
    Identifier identifier => (identifier.Name, identifier),
    MemberExpression { Computed: false, Property: Identifier { Name: var name } } member when Path(member.Object) is { } owner => ($"{owner.Text}.{name}", owner.Root),
    _ => null
  };

}
