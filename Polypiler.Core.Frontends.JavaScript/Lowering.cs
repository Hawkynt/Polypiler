using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Lowers the functions of one parsed file to IR. A value is typed the way ECMA-262 types it: the
/// bitwise operators and <c>&lt;&lt;</c>, <c>&gt;&gt;</c> yield an <c>int32</c> after <c>ToInt32</c> of
/// their operands, <c>&gt;&gt;&gt;</c> yields a <c>uint32</c>, <c>Math.imul</c> multiplies in 32 bits,
/// and <c>+</c> and <c>-</c> yield a number that is accepted only where the source coerces it at
/// once. A construct outside the subset ends the function it stands in with one diagnostic; nothing
/// is guessed and no function is imported in part.
/// </summary>
internal sealed partial class Lowering(string path, string source, List<Candidate> candidates) {

  private static readonly Regex _whiteSpace = new(@"\s+", RegexOptions.CultureInvariant);

  private readonly Dictionary<Candidate, object> _signatures = [];
  private readonly Dictionary<Candidate, List<Candidate>> _callees = [];
  private readonly Dictionary<string, IrLocal> _scope = [];
  private readonly HashSet<string> _constants = [];
  private readonly List<IrLocal> _locals = [];
  private List<string> _declared = [];
  private List<Candidate> _called = [];
  private IrType _returnType = IrType.Void;

  /// <summary>Whether a lowered function reads or writes an array element.</summary>
  public bool AccessesElements { get; private set; }

  /// <summary>Whether a lowered function divides or takes a remainder.</summary>
  public bool Divides { get; private set; }

  /// <summary>Whether a lowered function steps a variable with <c>++</c> or <c>--</c>.</summary>
  public bool Steps { get; private set; }

  /// <summary>The functions a lowered function calls; empty when it was refused before its body.</summary>
  public IReadOnlyList<Candidate> Callees(Candidate candidate)
    => this._callees.TryGetValue(candidate, out var callees) ? callees : [];

  /// <summary>Lowers one function, or adds the reason it is refused to the diagnostics.</summary>
  public IrFunction? Function(Candidate candidate, bool isPublic, List<Diagnostic> diagnostics) {
    this._scope.Clear();
    this._constants.Clear();
    this._locals.Clear();
    this._declared = [];
    this._called = [];
    try {
      var signature = this.SignatureOf(candidate);
      this._returnType = signature.ReturnType;
      foreach (var parameter in signature.Parameters)
        this._scope[parameter.Name] = parameter;

      var body = this.Block(((FunctionBody)candidate.Function.Body).Body);
      if (signature.ReturnType != IrType.Void && !AlwaysReturns(body))
        throw this.Unsupported(candidate.Anchor, "a function that can end without returning its value");

      this._callees[candidate] = this._called;
      return new IrFunction(candidate.Name, isPublic, signature.ReturnType, signature.Parameters, this._locals.ToList(), body);
    } catch (Refusal refusal) {
      diagnostics.Add(refusal.Diagnostic);
      return null;
    }
  }

  /// <summary>Whether a block returns on every path, so that no call ends without the value JSDoc promises.</summary>
  private static bool AlwaysReturns(IrBlock block) => block.Statements.Any(statement => statement switch {
    IrReturn => true,
    IrBlock inner => AlwaysReturns(inner),
    IrIf { Else: not null } choice => AlwaysReturns(choice.Then) && AlwaysReturns(choice.Else),
    _ => false
  });

  /// <summary>The types JSDoc gives a function; found once, and refused the same way every time it is asked for.</summary>
  private Signature SignatureOf(Candidate candidate) {
    if (!this._signatures.TryGetValue(candidate, out var known)) {
      try {
        known = this.ReadSignature(candidate);
      } catch (Refusal refusal) {
        known = refusal;
      }

      this._signatures[candidate] = known;
    }

    return known as Signature ?? throw (Refusal)known;
  }

  private Signature ReadSignature(Candidate candidate) {
    var function = candidate.Function;
    if (candidate.Problem is not null)
      throw this.Unsupported(candidate.Anchor, candidate.Problem);

    if (function.Generator || function.Async)
      throw this.Unsupported(candidate.Anchor, function.Generator ? "a generator" : "an async function");

    this.CheckName(candidate.Anchor, candidate.Name);
    var parameters = new List<IrLocal>();
    foreach (var parameter in function.Params) {
      if (parameter is not Identifier { Name: var name })
        throw this.Unsupported(parameter, parameter is RestElement ? "a rest parameter" : parameter is AssignmentPattern ? "a default parameter value" : "a destructured parameter");

      this.CheckName(parameter, name);
      if (parameters.Exists(earlier => earlier.Name == name))
        throw this.Unsupported(parameter, $"a second parameter named {name}");

      if (JsDoc.ParameterType(candidate.Documentation, name) is not { } type)
        throw this.Refuse(parameter, JavaScriptFrontend.MissingType, $"parameter '{name}' of '{candidate.Name}' has no supported JSDoc type");

      parameters.Add(new IrLocal(name, type));
    }

    return new Signature(
      JsDoc.ResultType(candidate.Documentation) ?? throw this.Refuse(candidate.Anchor, JavaScriptFrontend.MissingType, $"the result of '{candidate.Name}' has no supported JSDoc type"),
      parameters);
  }

  /// <summary>
  /// A name with a <c>$</c> is refused: the IR and the emitters build the names of their temporaries
  /// and helpers with it, which is safe only while no imported name can contain it.
  /// </summary>
  private void CheckName(Node node, string name) {
    if (name.Contains('$'))
      throw this.Unsupported(node, $"the name {name}");
  }

  private IrBlock Block(IEnumerable<Statement> statements) {
    var outer = this._declared;
    this._declared = [];
    var into = new List<IrStmt>();
    foreach (var statement in statements)
      this.Statement(statement, into);

    this.Leave(outer);
    return new IrBlock(into);
  }

  /// <summary>Ends a scope: what it declared is no longer visible.</summary>
  private void Leave(List<string> outer) {
    foreach (var name in this._declared) {
      this._scope.Remove(name);
      this._constants.Remove(name);
    }

    this._declared = outer;
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
        into.Add(new IrIf(
          this.Condition(conditional.Test),
          this.Body(conditional.Consequent),
          conditional.Alternate is null ? null : this.Body(conditional.Alternate)));
        break;
      case WhileStatement loop:
        into.Add(new IrWhile(this.Condition(loop.Test), this.Body(loop.Body)));
        break;
      case ForStatement loop:
        into.Add(this.For(loop));
        break;
      case ReturnStatement result:
        into.Add(this.Return(result));
        break;
      default:
        // Every other statement that can stand in a function has a name of its own.
        throw this.Unsupported(statement, Describe(statement)!);
    }
  }

  private IrReturn Return(ReturnStatement result) {
    if (this._returnType == IrType.Void)
      return result.Argument is null ? new IrReturn(null) : throw this.Unsupported(result, "a return value in a function without @returns");

    if (result.Argument is null)
      throw this.Unsupported(result, "a return without a value");

    return new IrReturn(this.To(this.Expression(result.Argument), this._returnType, result.Argument));
  }

  /// <summary>
  /// A <c>for</c> becomes a block holding its initializer and a <c>while</c> whose body ends with the
  /// update. That is the same loop, because the subset has no <c>continue</c>.
  /// </summary>
  private IrBlock For(ForStatement loop) {
    var outer = this._declared;
    this._declared = [];
    var statements = new List<IrStmt>();
    if (loop.Init is VariableDeclaration declaration)
      this.Declaration(declaration, statements);
    else if (loop.Init is Expression initializer)
      this.Effect(initializer, statements);

    var condition = loop.Test is null ? new IrConst(IrType.Bool, 1) : this.Condition(loop.Test);
    var body = new List<IrStmt>(this.Body(loop.Body).Statements);
    if (loop.Update is not null)
      this.Effect(loop.Update, body);

    statements.Add(new IrWhile(condition, new IrBlock(body)));
    this.Leave(outer);
    return new IrBlock(statements);
  }

  /// <summary>
  /// Declares locals. A local has the type of its initializer and lives until its block ends, also
  /// when it is a <c>var</c>: a <c>var</c> used outside its block is then simply an unknown name, and
  /// a name declared while it is visible is refused, so no hoisting can be observed.
  /// </summary>
  private void Declaration(VariableDeclaration declaration, List<IrStmt> into) {
    foreach (var declarator in declaration.Declarations) {
      if (declarator.Id is not Identifier { Name: var name })
        throw this.Unsupported(declarator.Id, "a destructuring declaration");

      if (declarator.Init is null)
        throw this.Unsupported(declarator, "a declaration without an initializer");

      this.CheckName(declarator.Id, name);
      var value = this.Exact(this.Expression(declarator.Init), declarator.Init);
      if (!value.Type.IsStorable)
        throw this.Unsupported(declarator.Init, $"{Spell(value.Type)} as the value of a variable");

      if (this._scope.ContainsKey(name))
        throw this.Unsupported(declarator.Id, $"a second declaration of {name}");

      var local = new IrLocal(name, value.Type);
      this._locals.Add(local);
      this._scope[name] = local;
      this._declared.Add(name);
      if (declaration.Kind == VariableDeclarationKind.Const)
        this._constants.Add(name);

      into.Add(new IrDeclare(local, value.Expr));
    }
  }

  /// <summary>Lowers an expression that stands as a statement: an assignment, a step or a call.</summary>
  private void Effect(Expression expression, List<IrStmt> into) {
    switch (expression) {
      case AssignmentExpression assignment:
        this.Assignment(assignment, into);
        break;
      case UpdateExpression step:
        this.Step(step, into);
        break;
      case CallExpression call:
        into.Add(new IrExprStmt(this.Expression(call).Expr));
        break;
      default:
        throw this.Unsupported(expression, Describe(expression) ?? $"{this.Text(expression)} as a statement");
    }
  }

  private void Assignment(AssignmentExpression assignment, List<IrStmt> into) {
    Operator? operation = assignment.Operator switch {
      Operator.Assignment => Operator.Unknown,
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
      _ => null
    };
    if (operation is not { } op)
      throw this.Unsupported(assignment, OperatorText(assignment.Operator));

    var compound = op != Operator.Unknown;
    switch (assignment.Left) {
      case Identifier { Name: var name } when this._scope.TryGetValue(name, out var local):
        if (this._constants.Contains(name))
          throw this.Unsupported(assignment, $"an assignment to the constant {name}");

        var assigned = compound
          ? this.Combine(op, new Value(new IrLocalRef(local), false, local.Type == IrType.U8), assignment.Left, this.Expression(assignment.Right), assignment.Right, assignment)
          : this.Expression(assignment.Right);
        into.Add(new IrAssign(local, this.To(assigned, local.Type, compound ? assignment : assignment.Right)));
        break;
      case MemberExpression { Computed: true } element:
        var array = this.Array(element.Object);
        var index = this.Index(element.Property);
        if (compound) {
          array = this.Once(array, into);
          index = this.Once(index, into);
        }

        var elementType = array.Type.Element!;
        var stored = compound
          ? this.Combine(op, new Value(new IrArrayLoad(array, index), false, elementType == IrType.U8), assignment.Left, this.Expression(assignment.Right), assignment.Right, assignment)
          : this.Expression(assignment.Right);
        this.AccessesElements = true;
        // A typed array takes any number modulo the width of its elements, which is the conversion.
        into.Add(new IrStore(array, index, Convert(this.Integer(stored, assignment.Right), elementType)));
        break;
      default:
        throw this.Unsupported(assignment.Left, this.Text(assignment.Left));
    }
  }

  /// <summary>
  /// <c>i++</c> and <c>i--</c> as statements, in either position, on a 32-bit local: the local plus or
  /// minus one in the local's own type, which is <c>(i + 1) | 0</c> or <c>&gt;&gt;&gt; 0</c>.
  /// </summary>
  private void Step(UpdateExpression step, List<IrStmt> into) {
    if (step.Argument is not Identifier { Name: var name } || !this._scope.TryGetValue(name, out var local) || !local.Type.IsWord)
      throw this.Unsupported(step, $"{this.Text(step)} on anything but an int32 or uint32 variable");

    if (this._constants.Contains(name))
      throw this.Unsupported(step, $"an assignment to the constant {name}");

    this.Steps = true;
    var op = step.Operator == Operator.Increment ? IrBinaryOp.Add : IrBinaryOp.Subtract;
    into.Add(new IrAssign(local, new IrBinary(op, new IrLocalRef(local), new IrConst(local.Type, 1))));
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

  private Refusal NotAnInteger(Node node)
    => this.Refuse(node, JavaScriptFrontend.NotAnInteger, $"the result of '{this.Text(node)}' is a JavaScript number; coerce it with | 0 or >>> 0");

  private Refusal Refuse(Node node, string code, string message) {
    var start = node.Location.Start;
    return new Refusal(new Diagnostic(code, DiagnosticSeverity.Error, message, path, start.Line, start.Column + 1));
  }

  /// <summary>The source text of a node on one line.</summary>
  private string Text(Node node) => _whiteSpace.Replace(source[node.Start..node.End], " ");

  /// <summary>The name a diagnostic gives a statement or an expression outside the subset, where it has one.</summary>
  private static string? Describe(Node node) => node switch {
    BreakStatement => "break",
    ContinueStatement => "continue",
    SwitchStatement => "switch",
    DoWhileStatement => "do",
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
    ArrayExpression => "an array literal",
    TemplateLiteral or TaggedTemplateExpression => "a template string",
    SequenceExpression => "the comma operator",
    AssignmentExpression => "an assignment inside an expression",
    ChainExpression => "?.",
    _ => null
  };

  /// <summary>The JSDoc spelling of an IR type.</summary>
  private static string Spell(IrType type) => type.Kind switch {
    IrTypeKind.Void => "void",
    IrTypeKind.Bool => "boolean",
    IrTypeKind.U8 => "uint8",
    IrTypeKind.I32 => "int32",
    IrTypeKind.U32 => "uint32",
    _ => Spell(type.Element!) + "[]"
  };

  private static string OperatorText(Operator op) => op switch {
    Operator.Equality => "==",
    Operator.Inequality => "!=",
    Operator.Exponentiation => "**",
    Operator.ExponentiationAssignment => "**=",
    Operator.NullishCoalescing => "??",
    Operator.NullishCoalescingAssignment => "??=",
    Operator.LogicalAndAssignment => "&&=",
    Operator.LogicalOrAssignment => "||=",
    Operator.In => "in",
    Operator.InstanceOf => "instanceof",
    Operator.UnaryPlus => "unary +",
    Operator.TypeOf => "typeof",
    Operator.Void => "void",
    Operator.Delete => "delete",
    Operator.Division => "/",
    Operator.Remainder => "%",
    Operator.StrictEquality => "===",
    Operator.StrictInequality => "!==",
    Operator.LessThan => "<",
    Operator.LessThanOrEqual => "<=",
    Operator.GreaterThan => ">",
    _ => ">="
  };

  /// <summary>The types of a function.</summary>
  private sealed record Signature(IrType ReturnType, List<IrLocal> Parameters);

  /// <summary>Ends the lowering of the function a refused construct stands in.</summary>
  private sealed class Refusal(Diagnostic diagnostic) : Exception {

    public Diagnostic Diagnostic { get; } = diagnostic;

  }

}
