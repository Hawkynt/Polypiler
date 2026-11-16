using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Calls of no value (#1389): a call of a function or a method none of whose implementations - the
/// method and its overrides in the classes of the module - returns, because each only throws, and a
/// conditional of two such calls. Where a local, a return, an assignment to a variable or an argument
/// takes one, the call runs as a statement and is followed by a throw that is never reached; the rest of
/// the block is dead and is not lowered. A value of no value used where the method could return - an
/// operand, an element, one branch of a conditional whose other has a value - is refused naming the call.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The message of the throw that follows a call of no value, which the call never returns to.</summary>
  internal const string NoValueMessage = "unreachable: the call before this throw only throws";

  /// <summary>Whether the statement just lowered ends in a call of no value, so that the rest of its block is dead.</summary>
  private bool _diverged;

  /// <summary>Whether an expression has no value: a call whose every implementation only throws, or a conditional of two such.</summary>
  private bool HasNoValue(Node node) => node switch {
    CallExpression => types.Of(node).Kind == JsKind.Unknown,
    ConditionalExpression choice => this.HasNoValue(choice.Consequent) && this.HasNoValue(choice.Alternate),
    _ => false
  };

  /// <summary>Whether evaluating an expression reaches a call of no value: it is one, or a call one of whose arguments does.</summary>
  private bool Diverges(Node node)
    => this.HasNoValue(node) || node is CallExpression { Arguments: var arguments } && !arguments.Any(argument => argument is SpreadElement) && arguments.Any(this.Diverges);

  /// <summary>The call of no value of an expression that holds one, which a refusal names.</summary>
  private Node NoValueCall(Node node) => node switch {
    ConditionalExpression choice => this.NoValueCall(this.Diverges(choice.Consequent) ? choice.Consequent : choice.Alternate),
    CallExpression call when !this.HasNoValue(call) => this.NoValueCall(call.Arguments.First(this.Diverges)),
    _ => node
  };

  /// <summary>Refuses an expression of no value where a value is needed, naming the call.</summary>
  private Refusal NoValue(Node node) => this.Unsupported(this.NoValueCall(node), $"the value of {types.Text(this.NoValueCall(node))}, a call that only throws");

  /// <summary>
  /// A statement a call of no value ends - a declaration, a return, an assignment to a variable or a call
  /// whose argument it is - lowered as far as it runs; says whether it was one. The rest of the block is then dead.
  /// </summary>
  private bool Diverging(Statement statement, List<IrStmt> into) {
    var value = statement switch {
      VariableDeclaration { Declarations: [{ Id: Identifier, Init: { } init }] } => init,
      ReturnStatement { Argument: { } argument } => argument,
      ExpressionStatement { Expression: AssignmentExpression { Operator: Operator.Assignment, Left: Identifier, Right: var right } } => right,
      // A call of no value as a statement of its own is the call; only a call it is an argument of needs more.
      ExpressionStatement { Expression: CallExpression call } when !this.HasNoValue(call) => call,
      _ => null
    };
    if (value is null || !this.Diverges(value))
      return false;

    this.Run(value, into);
    into.Add(new IrThrow(new IrNew(IrType.Object(IrExceptionClass.InvalidOperationException), IrExceptionClass.Constructor(IrExceptionClass.InvalidOperationException, 1), [new IrStrConst(NoValueMessage)])));
    this._diverged = true;
    return true;
  }

  /// <summary>Runs an expression that reaches a call of no value as statements, up to that call: what JavaScript evaluates before it, in its order.</summary>
  private void Run(Node node, List<IrStmt> into) {
    switch (node) {
      case ConditionalExpression choice:
        var condition = this.Condition(choice.Test);
        var (whenTrue, whenFalse) = this.Narrowed(choice.Test);
        var then = this.Within(whenTrue, choice.Consequent, () => this.Ran(choice.Consequent));
        var otherwise = this.Within(whenFalse, choice.Alternate, () => this.Ran(choice.Alternate));
        into.Add(new IrIf(condition, then, otherwise));
        break;
      case CallExpression call when this.HasNoValue(call):
        into.Add(new IrExprStmt(this.Expression(call)));
        break;
      default:
        // A call whose argument reaches a call of no value: its receiver and the arguments before that one are evaluated first; the call itself is never made.
        var outer = (CallExpression)node;
        if (outer.Callee is MemberExpression { Object: not (ThisExpression or Identifier or Super) } member)
          this.Once(this.Expression(member.Object), into);

        foreach (var argument in outer.Arguments.TakeWhile(argument => !this.Diverges(argument)).Where(argument => argument is not (Acornima.Ast.Literal or Identifier or ThisExpression)))
          this.Once(this.Expression(argument), into);

        this.Run(outer.Arguments.First(this.Diverges), into);
        break;
    }
  }

  private IrBlock Ran(Node node) {
    var into = new List<IrStmt>();
    this.Run(node, into);
    return new IrBlock(into);
  }

}
