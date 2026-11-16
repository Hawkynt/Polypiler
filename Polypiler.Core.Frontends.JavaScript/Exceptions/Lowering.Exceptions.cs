using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Exceptions: <c>try</c>, <c>catch (e)</c>, <c>catch</c> and <c>finally</c>, <c>throw</c>, classes
/// that extend <c>Error</c> with <c>super(m)</c> and <c>super(m, { cause })</c>, <c>new Error(…)</c>,
/// <c>e.message</c>, <c>e.cause</c>, and <c>instanceof</c> a class of the file, <c>Error</c>,
/// <c>RangeError</c> or <c>TypeError</c> (<see cref="ErrorLibrary"/>). A catch catches every
/// exception into its binding, and the tests of its block stay type tests, which is what JavaScript
/// does; <c>throw e</c> of the binding is the rethrow of what was caught. A value that is no
/// <c>Error</c> may be thrown only where nothing observes it (<see cref="JavaScriptFrontend.NonErrorThrow"/>);
/// <c>return</c>, <c>break</c> and <c>continue</c> in a <c>finally</c> block are refused, since C# has
/// no form for them (CS0157).
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The tries whose protected block the statement being lowered stands in, the innermost on top.</summary>
  private readonly Stack<TryStatement> _tries = new();

  /// <summary>The bindings of the catch blocks the statement being lowered stands in, the innermost on top; <see langword="null"/> for one without.</summary>
  private readonly Stack<Variable?> _caught = new();

  /// <summary>The statements of exception handling; <see langword="false"/> for any other.</summary>
  private bool ExceptionStatement(Statement statement, List<IrStmt> into) {
    switch (statement) {
      case TryStatement attempt:
        into.Add(this.Try(attempt));
        return true;
      case ThrowStatement thrown:
        into.AddRange(this.Throw(thrown));
        return true;
      default:
        return false;
    }
  }

  /// <summary>A try: its block, one catch of every exception, and its finally block.</summary>
  private IrTry Try(TryStatement attempt) {
    var before = this._assigned;
    this._tries.Push(attempt);
    var body = this.Block(attempt.Block.Body);
    this._tries.Pop();
    // The block may have ended anywhere: what it assigned is not assigned for certain behind it.
    this._assigned = before;
    var catches = new List<IrCatch>();
    if (attempt.Handler is { } handler) {
      if (handler.Param is not (null or Identifier))
        throw this.Unsupported(handler.Param, "a destructuring catch binding");

      var variable = handler.Param is Identifier caught ? (Variable)this._model.Bindings[caught] : null;
      IrLocal? local = null;
      if (variable is not null) {
        local = new IrLocal(variable.Name, IrExceptionClass.Reference);
        variable.Local = local;
        this._locals.Add(local);
      }

      this._caught.Push(variable);
      catches.Add(new IrCatch(IrExceptionClass.Exception, local, null, this.Block(handler.Body.Body)));
      this._caught.Pop();
      this._assigned = before;
    }

    IrBlock? final = null;
    if (attempt.Finalizer is { } finalizer) {
      if (Jump(finalizer.Body, false) is { } jump)
        throw this.Unsupported(jump, jump is ReturnStatement ? "return in finally" : jump is BreakStatement ? "break in finally" : "continue in finally");

      final = this.Block(finalizer.Body);
    }

    return new IrTry(body, catches, final);
  }

  /// <summary>
  /// The first statement of a finally block that leaves it: a <c>return</c>, or a <c>break</c> or
  /// <c>continue</c> that no loop or switch inside the block takes.
  /// </summary>
  private static Statement? Jump(IEnumerable<Statement> statements, bool inLoop) {
    foreach (var statement in statements) {
      var found = statement switch {
        ReturnStatement => statement,
        BreakStatement or ContinueStatement => inLoop ? null : statement,
        BlockStatement block => Jump(block.Body, inLoop),
        IfStatement choice => Jump([choice.Consequent], inLoop) ?? (choice.Alternate is null ? null : Jump([choice.Alternate], inLoop)),
        WhileStatement loop => Jump([loop.Body], true),
        DoWhileStatement loop => Jump([loop.Body], true),
        ForStatement loop => Jump([loop.Body], true),
        ForOfStatement loop => Jump([loop.Body], true),
        SwitchStatement choice => choice.Cases.Select(section => Jump(section.Consequent, true)).FirstOrDefault(jump => jump is not null),
        TryStatement nested => Jump(nested.Block.Body, inLoop) ?? (nested.Handler is null ? null : Jump(nested.Handler.Body.Body, inLoop))
          ?? (nested.Finalizer is null ? null : Jump(nested.Finalizer.Body, inLoop)),
        _ => null
      };
      if (found is not null)
        return found;
    }

    return null;
  }

  /// <summary>
  /// A throw: of an exception, or again of the one a catch caught when its binding is thrown and
  /// never assigned; a value that is no <c>Error</c> only where nothing observes it, then as the
  /// throw of an <c>Error</c> after the value is evaluated.
  /// </summary>
  private List<IrStmt> Throw(ThrowStatement thrown) {
    if (thrown.Argument is Identifier name && this._caught.Count > 0 && this._caught.Peek() is { } caught
        && this._model.Bindings.GetValueOrDefault(name) == caught && !this._model.Flows.Any(flow => flow.Target == caught))
      return [new IrRethrow()];

    var value = this.Expression(thrown.Argument);
    if (value.Type.ClassType is { } type && IrExceptionClass.IsException(type))
      return [new IrThrow(value)];

    // A string thrown out of the function - 'Result() not implemented' of a base class - is an Error of that message to the caller (#1365).
    if (value.Type.IsString && (!this._tries.TryPeek(out var around) || around.Handler is null)) {
      this.ThrowsText = true;
      return [new IrThrow(new IrNew(IrExceptionClass.Reference, IrExceptionClass.Constructor(IrExceptionClass.Exception, 1), [value]))];
    }

    if (this.Observer(thrown) is { } place)
      throw this.Refuse(thrown, JavaScriptFrontend.NonErrorThrowDescriptor, $"a thrown value that is not an Error reaches {place}, which observes it");

    var error = new IrNew(IrExceptionClass.Reference, IrExceptionClass.Constructor(IrExceptionClass.Exception, 0), []);
    return value is IrConst or IrStrConst ? [new IrThrow(error)] : [new IrExprStmt(value), new IrThrow(error)];
  }

  /// <summary>
  /// What observes a value thrown here: the catch of the innermost try whose block holds the throw
  /// when it reads its binding, or the caller of the function when no try catches it; <see langword="null"/>
  /// when a catch without a binding, or with one it never reads, takes it.
  /// </summary>
  private string? Observer(ThrowStatement thrown) {
    if (!this._tries.TryPeek(out var attempt) || attempt.Handler is not { } handler)
      return $"the caller of '{this._model.Candidate.Name}'";

    if (handler.Param is Identifier caught && this._model.Bindings.GetValueOrDefault(caught) is Variable binding && binding.Uses.Count > 0)
      return $"the catch at line {this.LineOf(handler)}";

    return null;
  }

  /// <summary>
  /// Whether an assignment is the name an error gives itself, <c>this.name = '…'</c> in a constructor
  /// of a class derived from <c>Error</c>: every backend names the class it writes, so it is dropped.
  /// </summary>
  private bool NamesError(AssignmentExpression assignment)
    => assignment is { Operator: Operator.Assignment, Left: MemberExpression { Object: ThisExpression, Computed: false, Property: Identifier { Name: "name" } }, Right: StringLiteral }
      && this._model.Candidate is { Member: MemberKind.Constructor, Class: { } owner } && IrExceptionClass.IsException(owner.Ir) && owner.Field("name") is null;

  /// <summary>The line a node starts on.</summary>
  private int LineOf(Node node) => node.Location.Start.Line;

  /// <summary>
  /// The expressions of the native errors, or <see langword="null"/> for any other: <c>new Error(m)</c>,
  /// <c>new Error(m, { cause })</c>, and <c>instanceof</c> a native error the importer does not know.
  /// </summary>
  private IrExpr? ErrorExpression(Node node) {
    switch (node) {
      case NewExpression creation when types.Names.ClassNamed(creation.Callee) is { IsLibrary: true } library:
        if (library.Ir != IrExceptionClass.Exception)
          throw this.Unsupported(creation, $"new {library.Name}");

        var arguments = this.ErrorArguments(creation, creation.Arguments);
        return new IrNew(IrExceptionClass.Reference, IrExceptionClass.Constructor(IrExceptionClass.Exception, arguments.Count), arguments);
      case BinaryExpression { Operator: Operator.InstanceOf, Right: Identifier { Name: var name } } test
          when ErrorLibrary.Others.Contains(name) && !this._model.Bindings.ContainsKey(test.Right):
        throw this.Unsupported(test, $"instanceof {name}");
      default:
        return null;
    }
  }

  /// <summary>The arguments of the constructor of <c>Error</c>: none, the message, or the message and <c>{ cause }</c>.</summary>
  private List<IrExpr> ErrorArguments(Node call, IReadOnlyList<Node> arguments) => arguments switch {
    [] => [],
    [var message] => [this.Value(message, IrType.String)],
    [var message, ObjectExpression { Properties: [Property { Computed: false, Key: Identifier { Name: "cause" }, Value: var cause }] }]
      => [this.Value(message, IrType.String), this.Value(cause, IrExceptionClass.Reference)],
    _ => throw this.Unsupported(call, "the arguments of an Error other than a message and { cause }")
  };

  /// <summary>The call of the constructor of a native error a class of the file extends: <c>super(m)</c> or <c>super(m, { cause })</c>.</summary>
  private IrCall LibraryConstructor(BaseReference reference, Node node, IrLocalRef self) {
    var arguments = this.ErrorArguments(node, reference.Arguments);
    return new IrCall(IrExceptionClass.Constructor(reference.Owner.Ir, arguments.Count).Function, IrType.Void, [self, .. arguments]);
  }

}
