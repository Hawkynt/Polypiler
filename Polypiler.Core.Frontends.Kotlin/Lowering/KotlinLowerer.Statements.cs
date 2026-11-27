using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Kotlin.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Kotlin.Lowering;

internal sealed partial class KotlinLowerer {

  /// <summary>The statements of a block in a scope of their own, appended.</summary>
  private void Block(KBlock block, List<IrStmt> into) => this.Scoped(() => {
    foreach (var statement in block.Statements)
      this.Statement(statement, into);

    return 0;
  });

  /// <summary>A body - a block or one statement - as a block of the IR, in a scope of its own.</summary>
  private IrBlock Body(KNode body) {
    var statements = new List<IrStmt>();
    if (body is KBlock block)
      this.Block(block, statements);
    else
      this.Scoped(() => {
        this.Statement((KStatement)body, statements);
        return 0;
      });

    return new IrBlock(statements);
  }

  private void Statement(KStatement statement, List<IrStmt> into) {
    switch (statement) {
      case KBlock block:
        this.Block(block, into);
        break;
      case KVariable variable:
        this.Declare(variable, into);
        break;
      case KAssignment assignment:
        this.Assignment(assignment, into);
        break;
      case KExpressionStatement { Expression: var expression }:
        this.ExpressionStatement(expression, into);
        break;
      case KWhile loop:
        this.While(loop, into);
        break;
      case KDoWhile loop:
        this.DoWhile(loop, into);
        break;
      case KFor loop:
        this.For(loop, into);
        break;
      case KBreak or KContinue:
        this.Jump(statement, into);
        break;
      case KReturn result:
        if (result.Value is { } value)
          this.Return(value, into);
        else
          into.Add(new IrReturn(null));
        break;
      default:
        throw Refuse(KotlinFrontend.Function, statement, $"the local function '{((KLocalFunction)statement).Function.Name}' is outside the imported subset");
    }
  }

  /// <summary>
  /// <c>val</c> or <c>var</c>: a local of the declared type, or of the type of its value; a value of
  /// an <c>if</c> or a <c>when</c> with statements in its branches is assigned in each branch.
  /// </summary>
  private void Declare(KVariable variable, List<IrStmt> into) {
    var declared = variable.Type is { } type ? this.TypeOf(type) : null;
    if (variable.Initializer is null) {
      if (declared is null)
        throw Refuse(KotlinFrontend.Function, variable, $"the variable '{variable.Name}' has neither a type nor a value");

      var uninitialised = this.NewLocal(variable.Name, declared);
      into.Add(new IrDeclare(uninitialised, null));
      // A val without its value is assigned once later, which Kotlin's definite assignment checked.
      this.Scope.Add(variable.Name, new Variable(uninitialised, true));
      this.MarkChar(uninitialised, variable.Type);
      return;
    }

    if (Branching(variable.Initializer) && (declared ?? this.BranchType(variable.Initializer)) is { } branchType) {
      var target = this.NewLocal(variable.Name, branchType);
      into.Add(new IrDeclare(target, null));
      this.Assigned(variable.Initializer, target, into);
      this.Scope.Add(variable.Name, new Variable(target, variable.Mutable));
      this.MarkChar(target, variable.Type, variable.Initializer);
      return;
    }

    var value = this.Expression(variable.Initializer, declared);
    var local = this.NewLocal(variable.Name, declared ?? value.Type);
    value = this.Coerce(value, local.Type, variable.Initializer);
    this.Flush(into);
    into.Add(new IrDeclare(local, value));
    this.Scope.Add(variable.Name, new Variable(local, variable.Mutable));
    this.MarkChar(local, variable.Type, variable.Initializer);
    if (variable.Type is null && this.IsChar(value))
      this._charLocals.Add(local);
  }

  /// <summary>Whether a value is an <c>if</c> or a <c>when</c> whose branches hold more than one expression, which lowers to statements.</summary>
  private static bool Branching(KExpression value) => value switch {
    KIf { Else: not null } conditional => IsStatements(conditional.Then) || IsStatements(conditional.Else)
      || conditional.Else is KExpressionStatement { Expression: var inner } && inner is KIf or KWhen && Branching(inner),
    KWhen choice => true,
    _ => false
  };

  private static bool IsStatements(KNode? branch) => branch is KBlock { Statements.Count: not 1 } || branch is KBlock { Statements: [not KExpressionStatement] };

  /// <summary>The type of the value of a branching <c>if</c> or <c>when</c>: that of its first branch that has one.</summary>
  private IrType? BranchType(KExpression value) {
    var branches = value switch {
      KIf conditional => new[] { conditional.Then, conditional.Else! },
      KWhen choice => [.. choice.Entries.Select(entry => entry.Body)],
      _ => []
    };
    foreach (var branch in branches) {
      if (Last(branch) is not { } last || last is KThrowExpression or KJumpExpression)
        continue;

      if (Branching(last))
        return this.BranchType(last);

      // Typing the last expression must leave nothing behind: no local, no statement.
      var type = this.Probe(() => this.Scoped(() => {
        if (branch is KBlock block)
          foreach (var statement in block.Statements.Take(block.Statements.Count - 1))
            if (statement is KVariable variable)
              this.Declare(variable, []);

        return this.Expression(last, null).Type;
      }));
      return type;
    }

    return null;
  }

  /// <summary>The expression a branch ends with, or <see langword="null"/>.</summary>
  private static KExpression? Last(KNode branch) => branch switch {
    KBlock { Statements: [.., KExpressionStatement { Expression: var last }] } => last,
    KBlock { Statements: [.., KReturn or KBreak or KContinue] } => new KJumpExpression(new KBreak(null)),
    KExpressionStatement { Expression: var single } => single,
    KReturn or KBreak or KContinue => new KJumpExpression(new KBreak(null)),
    _ => null
  };

  /// <summary>
  /// An <c>if</c> or a <c>when</c> whose value goes to a local: each branch's statements, then the
  /// assignment of its last expression; a branch that ends in a jump or a throw assigns nothing.
  /// </summary>
  private void Assigned(KExpression value, IrLocal target, List<IrStmt> into) {
    void Branch(KNode branch, List<IrStmt> statements) => this.Scoped(() => {
      var block = branch as KBlock ?? new KBlock([(KStatement)branch]);
      foreach (var statement in block.Statements.Take(block.Statements.Count - 1))
        this.Statement(statement, statements);

      switch (block.Statements.LastOrDefault()) {
        case KExpressionStatement { Expression: var last } when Branching(last):
          this.Assigned(last, target, statements);
          break;
        case KExpressionStatement { Expression: KThrowExpression } raise:
          this.Statement(raise, statements);
          break;
        case KExpressionStatement { Expression: var last }:
          var lowered = this.Coerce(this.Expression(last, target.Type), target.Type, last);
          this.Flush(statements);
          statements.Add(new IrAssign(target, lowered));
          break;
        case { } other:
          this.Statement(other, statements);
          break;
        default:
          throw Refuse(KotlinFrontend.Control, branch, "an empty branch has no value");
      }

      return 0;
    });

    switch (value) {
      case KIf conditional: {
        var condition = this.Condition(conditional.Condition);
        this.Flush(into);
        var then = new List<IrStmt>();
        var otherwise = new List<IrStmt>();
        Branch(conditional.Then, then);
        Branch(conditional.Else!, otherwise);
        into.Add(new IrIf(condition, new IrBlock(then), new IrBlock(otherwise)));
        break;
      }
      case KWhen choice:
        this.When(choice, into, (body, statements) => Branch(body, statements), value: true);
        break;
    }
  }

  /// <summary>An expression evaluated for its effect: a call, an increment, a branching statement, a <c>println</c>.</summary>
  private void ExpressionStatement(KExpression expression, List<IrStmt> into) {
    switch (expression) {
      case KIf conditional: {
        var condition = this.Condition(conditional.Condition);
        this.Flush(into);
        var then = this.Body(conditional.Then);
        IrBlock? otherwise = conditional.Else is { } branch ? this.Body(branch) : null;
        into.Add(new IrIf(condition, then, otherwise is { Statements.Count: 0 } ? null : otherwise));
        return;
      }
      case KWhen choice:
        this.When(choice, into, (body, statements) => statements.AddRange(this.Body(body).Statements), value: false);
        return;
      case KThrowExpression raise:
        var exception = this.Expression(raise.Value, null);
        this.Flush(into);
        into.Add(new IrThrow(exception));
        return;
      case KPostfix { Operator: "++" or "--", Operand: KName global } when this.Scope.Find(global.Name) is null:
        this.Assignment(new KAssignment(global, ((KPostfix)expression).Operator[0] + "=", new KInteger(1, false, false) { At = expression.At }) { At = expression.At }, into);
        return;
      case KPrefix { Operator: "++" or "--", Operand: KName global } when this.Scope.Find(global.Name) is null:
        this.Assignment(new KAssignment(global, ((KPrefix)expression).Operator[0] + "=", new KInteger(1, false, false) { At = expression.At }) { At = expression.At }, into);
        return;
      case KPostfix { Operator: "++" or "--" } or KPrefix { Operator: "++" or "--" }:
        var step = this.Expression(expression, null);
        this.Flush(into);
        into.Add(new IrExprStmt(step));
        return;
      case KCall { Callee: KName { Name: "println" or "print" } } call:
        this.Print(call, into);
        return;
      case KCall { Callee: KName { Name: "require" or "check" } } call when call.Arguments.Count == 1:
        this.Requirement(call, into);
        return;
      case KCall { Callee: KName { Name: "repeat" }, Trailing: { } body } call when call.Arguments.Count == 1:
        this.Repeat(call, body, into);
        return;
    }

    var lowered = this.Expression(expression, null);
    this.Flush(into);
    if (lowered is IrThrowExpression raised)
      into.Add(new IrThrow(raised.Exception));
    else if (lowered is not (IrConst or IrLocalRef or IrStrConst or IrConstRef or IrGlobalRef))
      into.Add(new IrExprStmt(lowered));
  }

  /// <summary><c>println(text)</c>: the line of a string, or of the text Kotlin gives a value; <c>print</c> has no line of its own in the IR.</summary>
  private void Print(KCall call, List<IrStmt> into) {
    if (((KName)call.Callee).Name == "print")
      throw Refuse(KotlinFrontend.Function, call, "print without a line break has no form in the IR, which writes lines");

    var text = call.Arguments.Count switch {
      0 => new IrStrConst(""),
      1 => this.Text(this.Expression(call.Arguments[0], null), call.Arguments[0]),
      _ => throw Refuse(KotlinFrontend.Function, call, "println takes one argument")
    };
    this.Flush(into);
    into.Add(new IrWriteLine(text));
  }

  /// <summary>
  /// <c>require(condition)</c> and <c>check(condition)</c>, with the message of a trailing lambda:
  /// the throw of an <c>IllegalArgumentException</c> or an <c>IllegalStateException</c> where the
  /// condition fails, with Kotlin's messages <c>Failed requirement.</c> and <c>Check failed.</c>.
  /// </summary>
  private void Requirement(KCall call, List<IrStmt> into) {
    var require = ((KName)call.Callee).Name == "require";
    var condition = this.Condition(call.Arguments[0]);
    IrExpr message = new IrStrConst(require ? "Failed requirement." : "Check failed.");
    if (call.Trailing is { Parameters.Count: 0, Body: [KExpressionStatement { Expression: var text }] })
      message = this.Text(this.Expression(text, IrType.String), text);
    else if (call.Trailing is not null)
      throw Refuse(KotlinFrontend.Function, call, $"the message of {(require ? "require" : "check")} is one expression");

    this.Flush(into);
    var type = require ? IrExceptionClass.ArgumentException : IrExceptionClass.InvalidOperationException;
    into.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, condition), new IrBlock([new IrThrow(this.NewException(type, message))]), null));
  }

  /// <summary><c>repeat(n) { … }</c>: a loop over <c>0 until n</c> whose variable is the lambda's parameter, <c>it</c> by default.</summary>
  private void Repeat(KCall call, KLambda body, List<IrStmt> into) {
    if (body.Parameters.Count > 1)
      throw Refuse(KotlinFrontend.Function, body, "the action of repeat takes one parameter, the index");

    var name = body.Parameters.Count == 1 ? body.Parameters[0].Name : "it";
    var loop = new KFor(null, name, new KBinary(new KInteger(0, false, false) { At = call.At }, "until", call.Arguments[0]) { At = call.At }, new KBlock(body.Body) { At = body.At }) { At = call.At };
    this.For(loop, into);
  }

  /// <summary>The return of a value: a branching value returned from each branch.</summary>
  private void Return(KExpression value, List<IrStmt> into) {
    var result = this._function?.Result ?? IrType.Void;
    if (result == IrType.Void) {
      this.ExpressionStatement(value, into);
      into.Add(new IrReturn(null));
      return;
    }

    if (Branching(value)) {
      var target = this.NewLocal("result", result);
      into.Add(new IrDeclare(target, null));
      this.Assigned(value, target, into);
      into.Add(new IrReturn(new IrLocalRef(target)));
      return;
    }

    if (value is KThrowExpression or KJumpExpression) {
      this.ExpressionStatement(value, into);
      return;
    }

    var lowered = this.Coerce(this.Expression(value, result), result, value);
    this.Flush(into);
    into.Add(new IrReturn(lowered));
  }

  /// <summary>A condition, which must be a truth value.</summary>
  private IrExpr Condition(KExpression condition) {
    var lowered = this.Expression(condition, IrType.Bool);
    return lowered.Type == IrType.Bool ? lowered : throw Refuse(KotlinFrontend.Control, condition, $"a condition of the type {lowered.Type}, where Kotlin takes a Boolean");
  }

  #region Assignments

  private void Assignment(KAssignment assignment, List<IrStmt> into) {
    var op = assignment.Operator == "=" ? null : assignment.Operator[..^1];
    switch (assignment.Target) {
      case KName name: {
        var (type, store) = this.Place(name);
        IrExpr value;
        if (op is null && Branching(assignment.Value) && this.Scope.Find(name.Name) is { } variable) {
          this.Assigned(assignment.Value, variable.Local, into);
          return;
        }

        value = op is null
          ? this.Coerce(this.Expression(assignment.Value, type), type, assignment.Value)
          : this.Compound(op, this.Expression(name, null), assignment.Value, type, assignment);
        this.Flush(into);
        into.Add(store(value));
        return;
      }
      case KIndex index: {
        var array = this.Expression(index.Receiver, null);
        if (array.Type.IsString)
          throw Refuse(KotlinFrontend.Memory, index, "a string cannot be changed: Kotlin's String has no set operator");

        if (!array.Type.IsArray)
          throw Refuse(KotlinFrontend.Memory, index, $"the indexed assignment of a {array.Type}");

        var element = array.Type.Element!;
        var position = this.Coerce(this.Expression(index.Index, IrType.I32), IrType.I32, index.Index);
        IrExpr value;
        if (op is null) {
          value = this.Coerce(this.Expression(assignment.Value, element), element, assignment.Value);
        } else {
          // The array and the index are evaluated once, before the value, as Kotlin's get and set see them.
          array = this.Once(array, "array");
          position = this.Once(position, "index");
          value = this.Compound(op, new IrArrayLoad(array, position), assignment.Value, element, assignment);
        }

        this.Flush(into);
        into.Add(new IrStore(array, position, value));
        return;
      }
      default:
        throw Refuse(KotlinFrontend.Control, assignment, "the assignment to anything but a variable or an array element is outside the imported subset");
    }
  }

  /// <summary>The variable or property a name assigns: its type and the statement that stores a value.</summary>
  private (IrType Type, Func<IrExpr, IrStmt> Store) Place(KName name) {
    if (this.Scope.Find(name.Name) is { } variable) {
      if (!variable.Mutable)
        throw Refuse(KotlinFrontend.Function, name, $"'{name.Name}' is a val and cannot be reassigned");

      return (variable.Local.Type, value => new IrAssign(variable.Local, value));
    }

    if (this._properties.TryGetValue(name.Name, out var property)) {
      if (!property.Mutable)
        throw Refuse(KotlinFrontend.Function, name, $"'{name.Name}' is a val and cannot be reassigned");

      var global = property.Global!;
      return (global.Type, value => new IrGlobalAssign(global, value));
    }

    throw Refuse(KotlinFrontend.Function, name, $"the variable '{name.Name}' is not declared");
  }

  /// <summary><c>x op= value</c>: <c>x = x op value</c>, whose type must be the variable's - Kotlin has no narrowing compound assignment.</summary>
  private IrExpr Compound(string op, IrExpr current, KExpression value, IrType type, KNode at) {
    var combined = this.Operator(current, op, this.Expression(value, type), at);
    if (combined.Type != type)
      throw Refuse(KotlinFrontend.Scalar, at, $"'{op}=' gives a {combined.Type}, which the {type} it assigns cannot hold without a conversion");

    return combined;
  }

  #endregion

}
