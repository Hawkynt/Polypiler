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
internal sealed class Binder(FunctionModel model, List<Candidate> candidates, IReadOnlyDictionary<string, ModuleConstant> constants, string source) {

  private readonly Dictionary<string, Variable> _scope = [];
  private List<string> _declared = [];

  /// <summary>The variables of the module, which every name of a function that no local or parameter hides names.</summary>
  public IReadOnlyDictionary<string, Variable> Globals { get; init; } = new Dictionary<string, Variable>();

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
    foreach (var parameter in model.Parameters)
      this._scope[parameter.Name] = parameter;

    var body = ((FunctionBody)model.Candidate.Function.Body).Body;
    var rest = body.SkipWhile(this.IsPrologue).ToList();
    foreach (var statement in body.Take(body.Count - rest.Count))
      this.Statement(statement);

    // A read in the prologue is the coerced re-assignment itself, which is no use of its own.
    foreach (var parameter in model.Parameters.Where(parameter => parameter.Prologue is not null))
      parameter.Uses.Clear();

    foreach (var statement in rest)
      this.Statement(statement);
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
    foreach (var statement in statements)
      this.Statement(statement);

    this.Leave(outer);
  }

  private void Leave(List<string> outer) {
    foreach (var name in this._declared)
      this._scope.Remove(name);

    this._declared = outer;
  }

  private void Body(Statement statement) => this.Block(statement is BlockStatement block ? block.Body : [statement]);

  private void Statement(Statement statement) {
    switch (statement) {
      case BlockStatement block:
        this.Block(block.Body);
        break;
      case VariableDeclaration declaration:
        this.Declaration(declaration);
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
      case ReturnStatement result:
        model.Flows.Add(new Flow(FlowKind.Return, result));
        if (result.Argument is not null)
          this.Expression(result.Argument);

        break;
    }
  }

  private void Declaration(VariableDeclaration declaration) {
    foreach (var declarator in declaration.Declarations) {
      // The initializer is read before the name exists, as the lowering reads it.
      if (declarator.Init is not null)
        this.Expression(declarator.Init);

      switch (declarator.Id) {
        case Identifier id when this.Declare(id, declaration, declarator.Init) is { } variable && declarator.Init is not null:
          model.Flows.Add(new Flow(FlowKind.Assign, declarator.Init, variable));
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
    if (this._scope.ContainsKey(id.Name)) {
      model.Refused[id] = $"a second declaration of {id.Name}";
      return null;
    }

    var variable = new Variable(id.Name, id, false) {
      IsConstant = declaration.Kind == VariableDeclarationKind.Const,
      IsUninitialized = initializer is null
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
      case LogicalExpression logical:
        this.Expression(logical.Left);
        this.Expression(logical.Right);
        break;
      case BinaryExpression binary:
        var (left, right) = Operands(binary.Operator);
        this.Expression(binary.Left, left, binary);
        this.Expression(binary.Right, right, binary);
        break;
      case MemberExpression member:
        this.Expression(member.Object);
        // An element or the length is taken; the name of the property is read from the source, whatever kind of name it is.
        if (member.Object is Identifier array && model.Bindings.GetValueOrDefault(array) is Variable used && (member.Computed || source[member.Property.Start..member.Property.End] == "length"))
          used.UsedAsArray ??= member;

        if (member.Computed)
          this.Expression(member.Property);

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
      case MemberExpression element:
        this.Expression(element);
        this.Expression(assignment.Right);
        if (this.Holder(element) is { } holder)
          model.Flows.Add(new Flow(FlowKind.Store, assignment, holder));

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

    this.Expression(call.Callee);
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

    if (builtin is not null || this.Callee(call.Callee) is not { } callee)
      return;

    if (!model.Callees.Contains(callee))
      model.Callees.Add(callee);

    for (var index = 0; index < call.Arguments.Count; ++index)
      model.Flows.Add(new Flow(FlowKind.Argument, call.Arguments[index], null, callee, index));
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
    _ => null
  };

  /// <summary>
  /// The function a call names: a declaration by its name, a property through the variable its object
  /// literal is assigned to and the properties that lead to it. A local of the same name hides either.
  /// </summary>
  public Candidate? Callee(Node callee) {
    switch (callee) {
      case Identifier { Name: var name } identifier when !model.Bindings.ContainsKey(identifier):
        return candidates.Find(candidate => !candidate.IsProperty && candidate.Name == name);
      case MemberExpression { Computed: false, Property: Identifier { Name: var property } } member when Path(member.Object) is { } path && !model.Bindings.ContainsKey(path.Root):
        return candidates.Find(candidate => candidate.IsProperty && candidate.Owner == path.Text && candidate.Name == property);
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
