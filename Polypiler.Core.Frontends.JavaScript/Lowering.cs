using System;
using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Lowers the functions of one parsed file to IR once their types are inferred. Every number is
/// computed the way ECMA-262 computes it: <c>+ - * / %</c> in doubles, the bitwise operators and
/// <c>&lt;&lt;</c>, <c>&gt;&gt;</c> on <c>ToInt32</c> of their operands, <c>&gt;&gt;&gt;</c> on
/// <c>ToUint32</c>. An integer operation of the IR stands only where it is the same operation: a
/// <c>+</c> or <c>-</c> of 32-bit integers directly under a coercion, <c>Math.imul</c>, and the
/// operators on bounded <c>BigInt</c>s under <c>BigInt.asIntN(64, …)</c> and <c>asUintN</c>. A
/// construct outside the subset ends the function it stands in with one diagnostic; nothing is
/// guessed and no function is imported in part.
/// </summary>
internal sealed partial class Lowering(TypeInference types) {

  private readonly List<IrLocal> _locals = [];
  private readonly Dictionary<Node, IrExpr> _substitutes = [];
  private HashSet<Variable> _assigned = [];
  private FunctionModel _model = null!;
  private IrType _returnType = IrType.Void;

  /// <summary>Whether a lowered function reads or writes an array element.</summary>
  public bool AccessesElements { get; private set; }

  /// <summary>Whether a lowered function divides integers, which traps where JavaScript does not.</summary>
  public bool Divides { get; private set; }

  /// <summary>The constants of the enclosing scope that the lowered functions use.</summary>
  public HashSet<ModuleConstant> Constants { get; } = [];

  /// <summary>Lowers one function, or adds the reason it is refused to the diagnostics.</summary>
  public IrFunction? Function(FunctionModel model, List<Diagnostic> diagnostics) {
    this._model = model;
    this._locals.Clear();
    this._assigned = [];
    types.Enter(model);
    try {
      if (model.Problem is { } problem)
        throw new Refusal(problem);

      foreach (var variable in model.Parameters.Concat(model.Locals))
        this.CheckType(variable.Type, variable.Name, variable.Evidence ?? variable.Declaration);

      this.CheckType(model.Result, model.Candidate.Name, model.ResultEvidence ?? model.Candidate.Anchor);
      // A parameter has the type it is called with. Where the body assigns it something else, the body
      // works on a variable of its own that starts with the argument; its name carries a $, which no
      // name of the source can.
      var entry = new List<IrStmt>();
      foreach (var parameter in model.Parameters) {
        parameter.Parameter = parameter.Local = new IrLocal(parameter.Name, this.Storable(parameter.Entry, parameter.Declaration));
        var type = this.Storable(parameter.Type, parameter.Declaration);
        if (type == parameter.Parameter.Type)
          continue;

        parameter.Local = new IrLocal(parameter.Name + "$0", type);
        this._locals.Add(parameter.Local);
        entry.Add(new IrDeclare(parameter.Local, this.To(new Typed(new IrLocalRef(parameter.Parameter), parameter.Entry), type, parameter.Declaration)));
      }

      this._returnType = model.ReturnType;
      var body = new IrBlock([.. entry, .. this.Block(((FunctionBody)model.Candidate.Function.Body).Body).Statements]);
      if (this._returnType != IrType.Void && !AlwaysReturns(body))
        throw this.Unsupported(model.Candidate.Anchor, "a function that can end without returning its value");

      return new IrFunction(model.Candidate.Name, model.IsPublic, this._returnType, model.Parameters.Select(parameter => parameter.Parameter!).ToList(), this._locals.ToList(), body);
    } catch (Refusal refusal) {
      diagnostics.Add(refusal.Diagnostic);
      return null;
    }
  }

  /// <summary>Refuses a place that values of two kinds reach, and a <c>BigInt</c> that nothing bounds.</summary>
  private void CheckType(JsType type, string name, Node evidence) {
    if (type.Kind == JsKind.Conflict)
      throw this.Refuse(evidence, JavaScriptFrontend.MixedKinds, $"'{name}' holds both {type.Kinds.First} and {type.Kinds.Second}");

    if (type.Kind == JsKind.BigInt && type.Ir is null)
      throw this.Unbounded(evidence);
  }

  /// <summary>The IR type of a variable; a place nothing reached is a number.</summary>
  private IrType Storable(JsType type, Node node) {
    var storable = type.Ir ?? IrType.F64;
    return storable.IsStorable ? storable : throw this.Unsupported(node, $"{JsType.Spell(storable)} as the value of a variable");
  }

  /// <summary>
  /// Whether a block never ends: it returns on every path, or runs a loop without a condition that
  /// nothing breaks out of. Then no call ends without its value.
  /// </summary>
  private static bool AlwaysReturns(IrBlock block) => block.Statements.Any(statement => statement switch {
    IrReturn => true,
    IrBlock inner => AlwaysReturns(inner),
    IrIf { Else: not null } choice => AlwaysReturns(choice.Then) && AlwaysReturns(choice.Else),
    IrSwitch choice => choice.Sections.Any(section => section.IsDefault) && choice.Sections.All(section => AlwaysReturns(section.Body)),
    IrWhile { Condition: IrConst { Value: 1 } } loop => !Breaks(loop.Body),
    IrFor { Condition: IrConst { Value: 1 } } loop => !Breaks(loop.Body),
    _ => false
  });

  /// <summary>Whether a statement holds a <c>break</c> at any depth; one of a loop or switch inside it counts as well, which errs on the safe side.</summary>
  private static bool Breaks(IrStmt statement) => statement switch {
    IrBreak => true,
    IrBlock block => block.Statements.Any(Breaks),
    IrIf choice => Breaks(choice.Then) || (choice.Else is not null && Breaks(choice.Else)),
    IrWhile loop => Breaks(loop.Body),
    IrDoWhile loop => Breaks(loop.Body),
    IrFor loop => Breaks(loop.Body),
    IrSwitch choice => choice.Sections.Any(section => Breaks(section.Body)),
    _ => false
  };

  private IrBlock Block(IEnumerable<Statement> statements) {
    var into = new List<IrStmt>();
    foreach (var statement in statements)
      this.Statement(statement, into);

    return new IrBlock(into);
  }

  private IrBlock Body(Statement statement)
    => this.Block(statement is BlockStatement block ? block.Body : [statement]);

  private void Statement(Statement statement, List<IrStmt> into) {
    switch (statement) {
      case BlockStatement block:
        into.Add(this.Block(block.Body));
        break;
      case EmptyStatement or Directive:
        break;
      case VariableDeclaration declaration:
        this.Declaration(declaration, into);
        break;
      case ExpressionStatement expression:
        this.Effect(expression.Expression, into);
        break;
      case IfStatement conditional:
        into.Add(this.If(conditional));
        break;
      case WhileStatement loop:
        var before = this._assigned;
        var condition = this.Condition(loop.Test);
        into.Add(new IrWhile(condition, this.Body(loop.Body)));
        this._assigned = before;
        break;
      case DoWhileStatement loop:
        var entered = this._assigned;
        var pass = this.Body(loop.Body);

        // A pass that can be left early has not assigned what stands behind the jump.
        if (Contains(loop.Body, node => node is BreakStatement or ContinueStatement))
          this._assigned = entered;

        into.Add(new IrDoWhile(pass, this.Condition(loop.Test)));
        break;
      case ForStatement loop:
        into.Add(this.For(loop));
        break;
      case SwitchStatement choice:
        into.Add(this.Switch(choice));
        break;
      case BreakStatement:
        // A jump to a label stands inside the labeled statement, which is refused before it is reached.
        into.Add(new IrBreak());
        break;
      case ContinueStatement:
        into.Add(new IrContinue());
        break;
      case ReturnStatement result:
        into.Add(this.Return(result));
        break;
      default:
        // Every other statement that can stand in a function has a name of its own.
        throw this.Unsupported(statement, Describe(statement)!);
    }
  }

  /// <summary>
  /// An <c>if</c>. A variable declared without a value is assigned behind it when both branches
  /// assign it, or when the branch that does not leaves the function or the loop.
  /// </summary>
  private IrIf If(IfStatement conditional) {
    var condition = this.Condition(conditional.Test);
    var before = this._assigned;
    var then = this.Body(conditional.Consequent);
    var afterThen = Leaves(conditional.Consequent) ? null : this._assigned;
    this._assigned = before;
    IrBlock? otherwise = null;
    var afterElse = before;
    if (conditional.Alternate is not null) {
      otherwise = this.Body(conditional.Alternate);
      afterElse = Leaves(conditional.Alternate) ? null : this._assigned;
    }

    this._assigned = afterThen is null ? afterElse ?? before
      : afterElse is null ? afterThen
      : afterThen.Intersect(afterElse).ToHashSet();
    return new IrIf(condition, then, otherwise);
  }

  /// <summary>Whether a statement always ends with a jump, so that nothing behind it runs after it.</summary>
  private static bool Leaves(Statement statement) => statement switch {
    ReturnStatement or BreakStatement or ContinueStatement => true,
    BlockStatement block => block.Body.Count > 0 && Leaves(block.Body[^1]),
    IfStatement { Alternate: not null } choice => Leaves(choice.Consequent) && Leaves(choice.Alternate),
    _ => false
  };

  /// <summary>Whether a node or anything inside it, other than a function, matches.</summary>
  private static bool Contains(Node node, Func<Node, bool> matches)
    => matches(node) || (node is not IFunction && node.ChildNodes.Any(child => Contains(child, matches)));

  private IrReturn Return(ReturnStatement result) {
    if (result.Argument is null)
      return new IrReturn(null);

    if (this._returnType != IrType.Void)
      return new IrReturn(this.Value(result.Argument, this._returnType));

    // The value has no type: the expression is refused for what it is, or it is a call that returns nothing.
    this.Expression(result.Argument);
    throw this.Unsupported(result.Argument, "void as the value returned");
  }

  /// <summary>
  /// A <c>for</c> becomes a block holding its initializer and the loop. Without a <c>continue</c> the
  /// loop is a <c>while</c> whose body ends with the update, which is the same loop; with one it is
  /// the <c>for</c> of the IR, whose step also follows a pass that <c>continue</c> ended.
  /// </summary>
  private IrBlock For(ForStatement loop) {
    var statements = new List<IrStmt>();
    if (loop.Init is VariableDeclaration declaration)
      this.Declaration(declaration, statements);
    else if (loop.Init is Expression initializer)
      this.Effect(initializer, statements);

    var before = this._assigned;
    var condition = loop.Test is null ? new IrConst(IrType.Bool, 1) : this.Condition(loop.Test);
    var body = this.Body(loop.Body);
    this._assigned = before;
    var step = new List<IrStmt>();
    if (loop.Update is not null)
      this.Effect(loop.Update, step);

    this._assigned = before;
    statements.Add(Contains(loop.Body, node => node is ContinueStatement)
      ? new IrFor(condition, body, new IrBlock(step))
      : new IrWhile(condition, new IrBlock([.. body.Statements, .. step])));
    return new IrBlock(statements);
  }

  /// <summary>
  /// A <c>switch</c> on an integer. Labels without statements are grouped with the section that
  /// follows; a section ends in <c>break</c>, <c>return</c> or <c>continue</c> - as its last
  /// statement, or as the last one of every branch of that - or is the last one.
  /// A section that would fall through into the statements of the next is refused: the IR has no
  /// fall-through, and copying the statements would not be what the source says. A variable declared
  /// without a value is assigned behind the switch when there is a default and every section that
  /// reaches the end assigns it.
  /// </summary>
  private IrSwitch Switch(SwitchStatement choice) {
    var selector = this.Evaluate(choice.Discriminant);
    if (!selector.Type.IsWord)
      throw this.Unsupported(choice.Discriminant, $"a switch on {selector.Type.Describe()}");

    var type = selector.Type.Within(IrType.I32) ? IrType.I32 : IrType.U32;
    var before = this._assigned;
    var sections = new List<IrSwitchSection>();
    var seen = new HashSet<long>();
    var labels = new List<long>();
    var isDefault = false;
    var hasDefault = false;
    HashSet<Variable>? after = null;
    for (var index = 0; index < choice.Cases.Count; ++index) {
      var section = choice.Cases[index];
      if (section.Test is null) {
        isDefault = true;
      } else {
        var label = types.Of(section.Test);
        if (!label.IsInteger || !label.IsLiteral || label.Low != label.High || !label.Within(type))
          throw this.Unsupported(section.Test, $"the case label {types.Text(section.Test)}");

        if (!seen.Add((long)label.Low))
          throw this.Unsupported(section.Test, $"a second case label {label.Low}");

        labels.Add((long)label.Low);
      }

      var last = index + 1 == choice.Cases.Count;
      var statements = section.Consequent.ToList();
      if (statements.Count == 0 && !last)
        continue;

      if (!last && !Leaves(statements[^1]))
        throw this.Unsupported(section, "a switch section that falls through into the next");

      if (statements.Count > 0 && statements[^1] is BreakStatement)
        statements.RemoveAt(statements.Count - 1);

      this._assigned = before;
      hasDefault |= isDefault;
      sections.Add(new IrSwitchSection(labels, isDefault, this.Block(statements)));
      labels = [];
      isDefault = false;

      // What stands behind the switch runs after every section that neither leaves the function or
      // the loop nor is left early by a break inside it.
      if (statements.Count == 0 || statements[^1] is not (ReturnStatement or ContinueStatement)) {
        var ended = statements.Exists(statement => Contains(statement, node => node is BreakStatement)) ? before : this._assigned;
        after = after is null ? ended : after.Intersect(ended).ToHashSet();
      }
    }

    this._assigned = hasDefault && after is not null ? after : before;
    return new IrSwitch(Convert(selector.Expr, type), sections);
  }

  /// <summary>
  /// Declares locals. A local has the type the inference found for it and lives until its block
  /// ends, also when it is a <c>var</c>: a <c>var</c> used outside its block is then simply an
  /// unknown name, and a name declared while it is visible is refused, so no hoisting can be observed.
  /// </summary>
  private void Declaration(VariableDeclaration declaration, List<IrStmt> into) {
    foreach (var declarator in declaration.Declarations) {
      if (declarator.Id is not Identifier { Name: var name } id)
        throw this.Unsupported(declarator.Id, "a destructuring declaration");

      if (name.Contains('$'))
        throw this.Unsupported(id, $"the name {name}");

      if (this._model.Refused.TryGetValue(id, out var refused))
        throw this.Unsupported(id, refused);

      var variable = (Variable)this._model.Bindings[id];
      if (this.View(declarator, variable, into))
        continue;

      var local = new IrLocal(name, this.Storable(variable.Type, declarator.Init ?? id));
      var value = declarator.Init is null ? null : this.Value(declarator.Init, local.Type);
      variable.Local = local;
      this._locals.Add(local);
      into.Add(new IrDeclare(local, value));
    }
  }

  /// <summary>Lowers an expression that stands as a statement: an assignment, a step, a call, or several separated by commas.</summary>
  private void Effect(Expression expression, List<IrStmt> into) {
    switch (expression) {
      case AssignmentExpression assignment:
        this.Assignment(assignment, into);
        break;
      case UpdateExpression step:
        this.Step(step, into);
        break;
      case CallExpression call when this.ByteStore(call) is { } store:
        into.Add(store);
        break;
      case CallExpression call:
        into.Add(new IrExprStmt(this.Expression(call)));
        break;
      case SequenceExpression sequence:
        foreach (var part in sequence.Expressions)
          this.Effect(part, into);

        break;
      default:
        throw this.Unsupported(expression, Describe(expression) ?? $"{types.Text(expression)} as a statement");
    }
  }

  private void Assignment(AssignmentExpression assignment, List<IrStmt> into) {
    var compound = Binder.Compound(assignment.Operator);
    if (compound is null && assignment.Operator != Operator.Assignment)
      throw this.Unsupported(assignment, OperatorText(assignment.Operator));

    switch (assignment.Left) {
      case Identifier { Name: var name } target when this._model.Bindings.GetValueOrDefault(target) is Variable variable:
        if (variable.IsConstant)
          throw this.Unsupported(assignment, $"an assignment to the constant {name}");

        var local = variable.Local!;
        var value = compound is { } op
          ? this.To(this.Binary(op, target, assignment.Right, Coercion.None, assignment), local.Type, assignment)
          : this.Value(assignment.Right, local.Type);
        this._assigned = [.. this._assigned, variable];

        // x = x | 0 on an int32 coerces to what the variable already holds: nothing is left to do.
        if (value != new IrLocalRef(local))
          into.Add(new IrAssign(local, value));

        break;
      case Identifier { Name: var name } target when this._model.Bindings.ContainsKey(target):
        throw this.Unsupported(assignment, $"an assignment to the constant {name}");
      case MemberExpression { Computed: true } element:
        var holder = types.Of(element.Object);
        var array = this.Array(element.Object);
        var index = this.Index(element.Property);
        var elementType = array.Type.Element!;
        var coercion = holder.IsPlain || !elementType.IsInteger || elementType.IsLong ? Coercion.None : Coercion.Int32;
        Typed stored;
        if (compound is { } combining) {
          // What a compound assignment reads and writes is evaluated once, as JavaScript evaluates it once.
          array = this.Once(array, into);
          index = this.Once(index, into);
          this._substitutes[element] = Read(new IrArrayLoad(array, index));
          stored = this.Binary(combining, element, assignment.Right, coercion, assignment);
          this._substitutes.Remove(element);
        } else {
          stored = this.Evaluate(assignment.Right, coercion);
        }

        this.AccessesElements = true;
        into.Add(new IrStore(array, index, this.Stored(stored, elementType, holder.IsPlain, assignment.Right)));
        break;
      default:
        throw this.Unsupported(assignment.Left, types.Text(assignment.Left));
    }
  }

  /// <summary>
  /// A value as an array element holds it. A JavaScript <c>Array</c> keeps the value, whose type the
  /// inference made the type of the elements. A typed array converts: an integer array takes the
  /// number modulo the width of its elements, a <c>Float32Array</c> rounds to binary32.
  /// </summary>
  private IrExpr Stored(Typed value, IrType element, bool plain, Node node) {
    if (plain || element == IrType.F64)
      return this.To(value, element, node);

    if (element.IsLong)
      return value.Type.Kind == JsKind.BigInt ? Convert(value.Expr, element) : throw this.Unsupported(node, $"{value.Type.Describe()} as an element of a {JsType.Spell(element)} array");

    if (element == IrType.F32)
      return new IrConvert(element, this.Float(value, node));

    return Convert(value.Type.IsInteger ? value.Expr : this.Int32(value, node), element);
  }

  /// <summary>
  /// <c>i++</c> and <c>i--</c> as statements. On a variable it is <c>i = i + 1</c> in doubles; nothing
  /// proves the sum an integer. On an element of a typed array the store wraps or rounds as every
  /// store into that array does, which is the step of the IR.
  /// </summary>
  private void Step(UpdateExpression step, List<IrStmt> into) {
    if (step.Argument is not Identifier { Name: var name } target) {
      into.Add(new IrExprStmt(this.StepElement(step)));
      return;
    }

    var read = this.Expression(target);
    if (this._model.Bindings[target] is not Variable { IsConstant: false } variable)
      throw this.Unsupported(step, $"an assignment to the constant {name}");

    var op = step.Operator == Operator.Increment ? IrBinaryOp.Add : IrBinaryOp.Subtract;
    into.Add(new IrAssign(variable.Local!, new IrBinary(op, read, new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1)))));
  }

  /// <summary>A step of an array element: the IR's step, which reads and writes the element once and yields the value from before.</summary>
  private IrExpr StepElement(UpdateExpression step) {
    // What is stepped is a variable or a member; a member that is no element is a property, which the subset does not have.
    var element = (MemberExpression)step.Argument;
    if (!element.Computed)
      throw this.Unsupported(step, types.Text(step));

    var array = this.Array(element.Object);
    var index = this.Index(element.Property);
    this.AccessesElements = true;
    return new IrStep(new IrArrayLoad(array, index), step.Operator == Operator.Increment, true, false);
  }

  /// <summary>
  /// What a compound assignment reads and writes is evaluated once, as JavaScript evaluates it once:
  /// anything more than a variable or a constant goes into a temporary.
  /// </summary>
  private IrExpr Once(IrExpr expression, List<IrStmt> into) {
    if (expression is IrLocalRef or IrConst)
      return expression;

    var temporary = new IrLocal($"$t{this._locals.Count}", expression.Type);
    this._locals.Add(temporary);
    into.Add(new IrDeclare(temporary, expression));
    return new IrLocalRef(temporary);
  }

  private Refusal Unsupported(Node node, string what)
    => this.Refuse(node, JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet");

  private Refusal Unbounded(Node node)
    => this.Refuse(node, JavaScriptFrontend.NotBounded, $"the result of '{types.Text(node)}' has no bounded type");

  private Refusal Refuse(Node node, string code, string message) => new(types.Diagnose(node, code, message));

  /// <summary>The name a diagnostic gives a statement or an expression outside the subset, where it has one.</summary>
  private static string? Describe(Node node) => node switch {
    ThrowStatement => "throw",
    TryStatement => "try",
    ForInStatement => "for-in",
    ForOfStatement => "for-of",
    LabeledStatement => "a label",
    WithStatement => "with",
    DebuggerStatement => "debugger",
    ClassDeclaration or ClassExpression => "class",
    FunctionDeclaration or FunctionExpression or ArrowFunctionExpression => "a function inside a function",
    ThisExpression => "this",
    ObjectExpression => "an object literal",
    TemplateLiteral or TaggedTemplateExpression => "a template string",
    SequenceExpression => "the comma operator",
    AssignmentExpression => "an assignment inside an expression",
    ChainExpression => "?.",
    _ => null
  };

  private static string OperatorText(Operator op) => op switch {
    Operator.BitwiseOr => "|",
    Operator.BitwiseXor => "^",
    Operator.BitwiseAnd => "&",
    Operator.LeftShift => "<<",
    Operator.RightShift => ">>",
    Operator.UnsignedRightShift => ">>>",
    Operator.Addition => "+",
    Operator.Subtraction => "-",
    Operator.Multiplication => "*",
    Operator.Division => "/",
    Operator.Remainder => "%",
    Operator.Exponentiation => "**",
    Operator.Equality => "==",
    Operator.Inequality => "!=",
    Operator.StrictEquality => "===",
    Operator.StrictInequality => "!==",
    Operator.LessThan => "<",
    Operator.LessThanOrEqual => "<=",
    Operator.GreaterThan => ">",
    Operator.GreaterThanOrEqual => ">=",
    Operator.LogicalAnd => "&&",
    Operator.LogicalOr => "||",
    Operator.NullishCoalescing => "??",
    Operator.NullishCoalescingAssignment => "??=",
    Operator.LogicalAndAssignment => "&&=",
    Operator.LogicalOrAssignment => "||=",
    Operator.In => "in",
    Operator.InstanceOf => "instanceof",
    Operator.TypeOf => "typeof",
    Operator.Void => "void",
    _ => "delete"
  };

  /// <summary>Ends the lowering of the function a refused construct stands in.</summary>
  private sealed class Refusal(Diagnostic diagnostic) : Exception {

    public Diagnostic Diagnostic { get; } = diagnostic;

  }

}
