using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  /// <summary>
  /// Lowers statements in a context: every statement but the last is in void context, the last in
  /// <paramref name="want"/> - the caller's for the body of a sub, which perl decides at run time.
  /// </summary>
  private void LowerStatements(IReadOnlyList<PerlStatement> nodes, List<IrStmt> statements, HashSet<PerlStatement>? consumed, PerlWant want, bool allowImplicitReturn) {
    var last = nodes.LastOrDefault(n => !IsOpless(n) && (consumed is null || !consumed.Contains(n)));
    foreach (var node in nodes) {
      if (IsOpless(node))
        continue;
      this._line = node.Line;
      this._integer = node.Integer;
      if (consumed is not null && consumed.Contains(node))
        continue;
      var isLast = ReferenceEquals(node, last);
      this.LowerStatement(node, statements, isLast ? want : PerlWant.Void, allowImplicitReturn && isLast);
    }
  }

  private void LowerStatement(PerlStatement node, List<IrStmt> statements, PerlWant want, bool implicitReturn) {
    switch (node) {
      case PerlIf ifStatement:
        statements.Add(this.LowerIf(ifStatement, want));
        return;
      case PerlWhile loop:
        this.LowerWhile(loop, node.Label, statements);
        return;
      case PerlCFor loop:
        this.LowerCFor(loop, node.Label, statements);
        return;
      case PerlForeach loop:
        this.LowerForEachLoop(loop.Variables, loop.Declarator, loop.List, loop.Body.Statements, loop.Continue, node.Label, statements);
        return;
      case PerlBareBlock block:
        this.LowerBareBlock(block, node.Label, want, implicitReturn, statements);
        return;
      case PerlTry tryStatement:
        this.LowerTryCatch(tryStatement, statements);
        return;
      case PerlDefer:
        throw this.Fail(PerlFrontend.ErrorHandlingUnsupported, "defer");
      case PerlPackage { Body: { } body }:
        this.LowerStatements(body.Statements, statements, null, want, implicitReturn);
        return;
      case PerlExpressionStatement expressionStatement:
        this.LowerExpressionStatement(expressionStatement, statements, want, implicitReturn);
        return;
    }
  }

  private void LowerExpressionStatement(PerlExpressionStatement node, List<IrStmt> statements, PerlWant want, bool implicitReturn) {
    switch (node.Modifier) {
      case "if" or "unless":
        this.LowerLogicalStatement(node.Modifier == "if" ? "and" : "or", node.ModifierExpression!, node.Expression, want, implicitReturn, statements);
        return;
      case "while" or "until":
        if (node.Expression is PerlDoBlock)
          throw this.Fail(PerlFrontend.ControlUnsupported, $"do BLOCK {node.Modifier}");
        this.LowerWhileLoop(node.Modifier == "until", node.ModifierExpression!, [new PerlExpressionStatement(node.Expression) { Line = node.Line, Integer = node.Integer, Package = node.Package }], node.Label, statements);
        return;
      case "for":
        this.LowerForEachLoop([], null, node.ModifierExpression!, [new PerlExpressionStatement(node.Expression) { Line = node.Line, Integer = node.Integer, Package = node.Package }], null, node.Label, statements);
        return;
    }
    this.LowerExpressionAsStatement(node.Expression, statements, want, implicitReturn);
  }

  /// <summary>An expression as a statement: control flow (<c>return</c>, <c>last</c>, <c>die</c>, conditionals), an assignment, or a value.</summary>
  private void LowerExpressionAsStatement(PerlExpr expression, List<IrStmt> statements, PerlWant want, bool implicitReturn) {
    if (this.TryLowerBlessStatement(expression, statements))
      return;
    if (this.TryLowerMooseSetterStmt(expression, statements))
      return;

    switch (expression) {
      case PerlJump { Keyword: "goto" or "redo" or "dump" } jump:
        throw this.Fail(PerlFrontend.ControlUnsupported, jump.Keyword);
      case PerlJump { Keyword: "return" } jump:
        this.LowerReturnStatement(jump.Value, statements);
        return;
      case PerlJump { Keyword: "last" or "next" } jump:
        this.LowerLoopJump(jump, statements);
        return;
      case PerlCall { Builtin: true, Name: "die" } die:
        this.LowerDieStatement(die, statements);
        return;
      case PerlEvalBlock eval when want == PerlWant.Void:
        this.LowerEvalStatement(eval, statements);
        return;
      case PerlBinary { Operator: "&&" or "and" } binary:
        if (this.LowerLogicalStatement("and", binary.Left, binary.Right, want, implicitReturn, statements))
          return;
        break;
      case PerlBinary { Operator: "||" or "or" } binary:
        if (this.LowerLogicalStatement("or", binary.Left, binary.Right, want, implicitReturn, statements))
          return;
        break;
      case PerlTernary ternary when want == PerlWant.Void || IsStatementExpression(ternary.Then) || IsStatementExpression(ternary.Else): {
        var cond = this.Truth(this.LiftExpr(ternary.Condition, PerlWant.Scalar));
        var yes = this.LowerSingleBody(ternary.Then, want);
        var no = this.LowerSingleBody(ternary.Else, want);
        statements.Add(new IrIf(cond, yes, no.Statements.Count > 0 ? no : null));
        return;
      }
      case PerlList { Items.Count: 1 } list when want == PerlWant.Void && list.Items[0] is PerlBinary or PerlTernary:
        this.LowerExpressionAsStatement(list.Items[0], statements, want, implicitReturn);
        return;
    }

    if (this.TryLowerAssignmentStatement(expression, statements))
      return;

    if (implicitReturn && this._currentReturnType != IrType.Void) {
      var val = this.LiftExpr(expression, want);
      statements.Add(new IrReturn(this.ToType(val, this._currentReturnType)));
      return;
    }

    if (want == PerlWant.Void && IsUselessConstant(expression))
      return;

    var expr = this.LiftExpr(expression, want);
    statements.Add(new IrExprStmt(expr));
  }

  /// <summary>A constant in void context, which perl drops.</summary>
  private bool IsUselessConstant(PerlExpr expression) => expression is PerlNumber or PerlString || this.Fold(expression) is not null && expression is not (PerlCall or PerlPattern);

  /// <summary>
  /// <c>A &amp;&amp; B</c> (and <c>B if A</c>) or <c>A || B</c> (and <c>B unless A</c>) as a statement: an
  /// <c>if</c> in void context or when <c>B</c> is a statement; a value otherwise.
  /// </summary>
  private bool LowerLogicalStatement(string kind, PerlExpr condition, PerlExpr body, PerlWant want, bool implicitReturn, List<IrStmt> statements) {
    if (this.Fold(condition) is { } folded) {
      var taken = kind == "and" ? folded.Truth : !folded.Truth;
      if (taken)
        this.LowerExpressionAsStatement(body, statements, want, implicitReturn);
      else if (implicitReturn && this._currentReturnType != IrType.Void)
        statements.Add(new IrReturn(this.ToType(this.LiftExpr(condition, want), this._currentReturnType)));
      return true;
    }
    if (want != PerlWant.Void && !IsStatementExpression(body)) {
      if (!implicitReturn) {
        statements.Add(new IrExprStmt(this.LowerLogical(kind, condition, body, want)));
        return true;
      }
      return this.ReturnLogical(kind, condition, body, want, statements);
    }
    var cond = this.Truth(this.LiftExpr(condition, PerlWant.Scalar));
    if (kind == "or")
      cond = new IrUnary(IrUnaryOp.LogicalNot, cond);
    var thenBlock = this.LowerSingleBody(body, want);
    statements.Add(new IrIf(cond, thenBlock, null));
    return true;
  }

  private bool ReturnLogical(string kind, PerlExpr condition, PerlExpr body, PerlWant want, List<IrStmt> statements) {
    var val = this.LowerLogical(kind, condition, body, want);
    statements.Add(new IrReturn(this.ToType(val, this._currentReturnType)));
    return true;
  }

  private static readonly HashSet<string> _statementJumps = new(StringComparer.Ordinal) { "return", "last", "next" };

  private static readonly HashSet<string> _statementLogic = new(StringComparer.Ordinal) { "&&", "and", "||", "or" };

  /// <summary>Whether perl compiles an expression into a statement-like op: a block, a jump, a conditional, an assignment.</summary>
  private static bool IsStatementExpression(PerlExpr expression) => expression switch {
    PerlJump jump => _statementJumps.Contains(jump.Keyword),
    PerlCall { Builtin: true, Name: "die" } => true,
    PerlBinary binary => _statementLogic.Contains(binary.Operator),
    PerlTernary => true,
    PerlAssign { Operator: "=" } => true,
    PerlList { Parenthesized: true, Items: [var only] } => IsStatementExpression(only),
    _ => false
  };

  private IrStmt LowerIf(PerlIf node, PerlWant want) {
    if (node.Unless && node.Clauses.Count == 1 && node.Else is null) {
      var cond = new IrUnary(IrUnaryOp.LogicalNot, this.Truth(this.LiftExpr(node.Clauses[0].Condition, PerlWant.Scalar)));
      return new IrIf(cond, this.LowerBlock(node.Clauses[0].Body, want), null);
    }
    return this.LowerIfClauses(node, 0, want);
  }

  private IrStmt LowerIfClauses(PerlIf node, int index, PerlWant want) {
    var (condition, body) = node.Clauses[index];
    var cond = this.Truth(this.LiftExpr(condition, PerlWant.Scalar));
    IrBlock? otherwise = null;
    if (index + 1 < node.Clauses.Count)
      otherwise = new IrBlock([this.LowerIfClauses(node, index + 1, want)]);
    else if (node.Else is not null)
      otherwise = this.LowerBlock(node.Else, want);
    var then = this.LowerBlock(body, want);
    if (index == 0 && node.Unless) {
      var swapped = otherwise ?? new IrBlock([]);
      return new IrIf(cond, swapped, then.Statements.Count > 0 ? then : null);
    }
    return new IrIf(cond, then, otherwise is { Statements.Count: > 0 } ? otherwise : null);
  }

  private IrBlock LowerBlock(PerlBlock block, PerlWant want) {
    var items = new List<IrStmt>();
    var saved = (this._line, this._integer);
    this.LowerStatements(block.Statements, items, null, want, allowImplicitReturn: false);
    (this._line, this._integer) = saved;
    return new(items);
  }

  private IrBlock LowerSingleBody(PerlExpr expression, PerlWant want) {
    var items = new List<IrStmt>();
    switch (expression) {
      case PerlDoBlock block:
        this.LowerStatements(block.Body.Statements, items, null, want, allowImplicitReturn: false);
        break;
      case PerlList { Parenthesized: true, Items: [var only] }:
        return this.LowerSingleBody(only, want);
      default:
        this.LowerExpressionAsStatement(expression, items, want, implicitReturn: false);
        break;
    }
    return new(items);
  }

  private void LowerBareBlock(PerlBareBlock block, string? label, PerlWant want, bool implicitReturn, List<IrStmt> statements) {
    if (block.Continue is not null)
      throw this.Fail(PerlFrontend.ControlUnsupported, "continue block");
    var prev = (this._line, this._integer);
    var items = new List<IrStmt>();
    this._loops.Push(new(label, isDoWhile: false));
    this.LowerStatements(block.Body.Statements, items, null, want, allowImplicitReturn: false);
    this._loops.Pop();
    (this._line, this._integer) = prev;
    statements.Add(new IrBlock(items));
  }

  private void LowerWhile(PerlWhile loop, string? label, List<IrStmt> statements) {
    if (loop.Continue is not null)
      throw this.Fail(PerlFrontend.ControlUnsupported, "continue block");
    this.LowerWhileLoop(loop.Until, loop.Condition, loop.Body.Statements, label, statements);
  }

  private void LowerWhileLoop(bool until, PerlExpr? condition, IReadOnlyList<PerlStatement> body, string? label, List<IrStmt> statements) {
    IrExpr cond = condition is null ? new IrConst(IrType.Bool, 1) : this.Truth(this.LiftExpr(condition, PerlWant.Scalar));
    if (until)
      cond = new IrUnary(IrUnaryOp.LogicalNot, cond);
    this._loops.Push(new(label, isDoWhile: false));
    var items = new List<IrStmt>();
    var saved = (this._line, this._integer);
    this.LowerStatements(body, items, null, PerlWant.Void, allowImplicitReturn: false);
    (this._line, this._integer) = saved;
    this._loops.Pop();
    statements.Add(new IrWhile(cond, new(items)));
  }

  private void LowerCFor(PerlCFor loop, string? label, List<IrStmt> statements) {
    if (loop.Init is not null)
      this.LowerExpressionAsStatement(loop.Init, statements, PerlWant.Void, implicitReturn: false);
    IrExpr cond = loop.Condition is null ? new IrConst(IrType.Bool, 1) : this.Truth(this.LiftExpr(loop.Condition, PerlWant.Scalar));
    this._loops.Push(new(label, isDoWhile: false));
    var items = new List<IrStmt>();
    var saved = (this._line, this._integer);
    this.LowerStatements(loop.Body.Statements, items, null, PerlWant.Void, allowImplicitReturn: false);
    (this._line, this._integer) = saved;
    if (loop.Step is not null)
      this.LowerExpressionAsStatement(loop.Step, items, PerlWant.Void, implicitReturn: false);
    this._loops.Pop();
    statements.Add(new IrWhile(cond, new(items)));
  }

  private void LowerReturnStatement(PerlExpr? value, List<IrStmt> statements) {
    if (value is null || value is PerlList { Items.Count: 0 }) {
      statements.Add(new IrReturn(this._currentReturnType == IrType.Void ? null : DefaultExpr(this._currentReturnType)));
      return;
    }
    var first = value is PerlList { Parenthesized: false } list ? list.Items[0] : value;

    // Returning an anonymous sub escapes (#436 -> PLY1732).
    if (first is PerlAnonSub || first is PerlVariable { Sigil: '$', Lexical: { } lex } && this._closureLocals.ContainsKey(lex))
      throw this.Fail(PerlFrontend.EscapingClosure, first is PerlAnonSub ? "sub { ... }" : ((PerlVariable)first).Name, cause: "OBJ-CLOSURES");

    var expr = this.LiftExpr(first, PerlWant.Unknown);
    if (this._currentReturnType == IrType.Void)
      statements.Add(new IrReturn(null));
    else
      statements.Add(new IrReturn(this.ToType(expr, this._currentReturnType)));
  }

  // ---- foreach ----

  private void LowerForEachLoop(IReadOnlyList<PerlVariable> variables, PerlDeclarator? declarator, PerlExpr list, IReadOnlyList<PerlStatement> body, PerlBlock? next, string? label, List<IrStmt> statements) {
    if (next is not null)
      throw this.Fail(PerlFrontend.ControlUnsupported, "continue block");
    if (variables.Count > 1)
      throw this.Fail(PerlFrontend.ControlUnsupported, "foreach over several variables");
    var loopVariable = variables.Count == 1 ? variables[0] : null;
    if (loopVariable is not null && loopVariable.Lexical is null)
      throw this.Fail(PerlFrontend.ControlUnsupported, "foreach with a package loop variable");

    var reversed = false;
    var items = list is PerlList { Parenthesized: true } paren ? paren.Items : list is PerlList plain ? plain.Items : [list];
    if (items is [PerlCall { Builtin: true, Name: "reverse", Arguments: [var reversedItem] }]) {
      reversed = true;
      items = [reversedItem];
    }
    if (items is [PerlList { Parenthesized: true, Items: [var single] }])
      items = [single];

    // Case A: a range `A .. B`, or `reverse A .. B`.
    if (items is [PerlBinary { Operator: ".." } range]) {
      var startExpr = this.LiftExpr(range.Left, PerlWant.Scalar);
      var endExpr = this.LiftExpr(range.Right, PerlWant.Scalar);
      if (startExpr.Type == IrType.String || endExpr.Type == IrType.String)
        throw this.Fail(PerlFrontend.ControlUnsupported, "string range");

      var startBounds = this._bounds.BoundsOf(startExpr);
      var endBounds = this._bounds.BoundsOf(endExpr);
      var iterFitsI32 = PerlBounds.BoundsOf(IrType.I32).Covers(new(startBounds.Min, endBounds.Max));
      var boundType = this._bounds.FitsIn(startExpr, IrType.I32) && this._bounds.FitsIn(endExpr, IrType.I32)
        ? IrType.I32 : IrType.I64;
      var iterType = iterFitsI32 ? IrType.I32 : IrType.I64;
      var startVal = boundType == IrType.I32 ? this.ToType(startExpr, IrType.I32) : this.ToSigned64(startExpr);
      var endVal = boundType == IrType.I32 ? this.ToType(endExpr, IrType.I32) : this.ToSigned64(endExpr);

      var boundStart = this.TempLocal(boundType, "_start");
      var boundEnd = this.TempLocal(boundType, "_end");
      statements.Add(new IrDeclare(boundStart, startVal));
      statements.Add(new IrDeclare(boundEnd, endVal));

      var counter = this.TempLocal(boundType, "_iter");
      statements.Add(new IrDeclare(counter, new IrLocalRef(reversed ? boundEnd : boundStart)));

      var prevTopic = this._topicVar;
      IrLocal loopVar;
      var bodyStmts = new List<IrStmt>();
      if (loopVariable is not null) {
        loopVar = this.DeclareLocal(loopVariable.Name, iterType, loopVariable.Lexical);
      } else {
        loopVar = this.TempLocal(iterType, "_topic");
        this._topicVar = loopVar;
      }
      bodyStmts.Add(new IrDeclare(loopVar, this.ToType(new IrLocalRef(counter), iterType)));

      var outerLoop = new LoopContext(label, isDoWhile: false);
      this._loops.Push(outerLoop);
      var loweredBody = this.LowerLoopBody(body);
      this._loops.Pop();
      this._topicVar = prevTopic;

      bodyStmts.AddRange(loweredBody.Statements);
      if (outerLoop.BreakFlag is { } brk) {
        statements.Add(new IrDeclare(brk, new IrConst(IrType.Bool, 0)));
        bodyStmts.Add(new IrIf(new IrLocalRef(brk), new([new IrBreak()]), null));
      }
      if (outerLoop.ContinueFlag is { } cnt) {
        statements.Add(new IrDeclare(cnt, new IrConst(IrType.Bool, 0)));
        bodyStmts.Add(new IrIf(new IrLocalRef(cnt), new([new IrAssign(cnt, new IrConst(IrType.Bool, 0)), new IrContinue()]), null));
      }
      var cond = reversed
        ? new IrBinary(IrBinaryOp.GreaterOrEqual, new IrLocalRef(counter), new IrLocalRef(boundStart))
        : new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(counter), new IrLocalRef(boundEnd));
      var step = new IrBlock([new IrAssign(counter, new IrBinary(reversed ? IrBinaryOp.Subtract : IrBinaryOp.Add,
        new IrLocalRef(counter), new IrConst(boundType, 1)))]);
      statements.Add(new IrFor(cond, new(bodyStmts), step));
      return;
    }

    // Case B: `foreach my $e (@a)` / `for (@$a)` (#437)
    var arrayOp = items.FirstOrDefault(c => c is PerlVariable { Sigil: '@' or '$' } or PerlDeref { Sigil: '@' });
    if (arrayOp is not null) {
      var arrExpr = arrayOp is PerlDeref { Reference: PerlVariable { Sigil: '$' } inner }
        ? this.LiftExpr(inner, PerlWant.Scalar)
        : this.LiftExpr(arrayOp, PerlWant.List);
      if (!arrExpr.Type.IsArray && !arrExpr.Type.IsList)
        throw this.Fail(PerlFrontend.ControlUnsupported, "foreach over non-array");

      var elemType = arrExpr.Type.Element!;
      var arrTemp = this.TempLocal(arrExpr.Type, "_arr");
      statements.Add(new IrDeclare(arrTemp, arrExpr));
      var idx = this.TempLocal(IrType.I32, "_idx");
      statements.Add(new IrDeclare(idx, new IrConst(IrType.I32, 0)));

      var prevTopic = this._topicVar;
      IrLocal elemVar;
      if (loopVariable is not null) {
        elemVar = this.DeclareLocal(loopVariable.Name, elemType, loopVariable.Lexical);
      } else {
        elemVar = this.TempLocal(elemType, "_topic");
        this._topicVar = elemVar;
      }

      var bodyStmts = new List<IrStmt>();
      var readElem = arrExpr.Type.IsList
        ? (IrExpr)new IrCollection(IrCollectionOp.ListGet, elemType, [new IrLocalRef(arrTemp), new IrLocalRef(idx)])
        : new IrArrayLoad(new IrLocalRef(arrTemp), new IrLocalRef(idx));
      bodyStmts.Add(new IrDeclare(elemVar, readElem));

      this._loops.Push(new(label, isDoWhile: false));
      var loweredBody = this.LowerLoopBody(body);
      this._loops.Pop();
      this._topicVar = prevTopic;

      bodyStmts.AddRange(loweredBody.Statements);
      if (IsVarWritten(loweredBody, elemVar)) {
        if (arrExpr.Type.IsList)
          bodyStmts.Add(new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [new IrLocalRef(arrTemp), new IrLocalRef(idx), new IrLocalRef(elemVar)])));
        else
          bodyStmts.Add(new IrStore(new IrLocalRef(arrTemp), new IrLocalRef(idx), new IrLocalRef(elemVar)));
      }

      var lenExpr = arrExpr.Type.IsList
        ? (IrExpr)new IrCollection(IrCollectionOp.ListCount, IrType.I32, [new IrLocalRef(arrTemp)])
        : new IrArrayLength(new IrLocalRef(arrTemp));
      var cond = new IrBinary(IrBinaryOp.Less, new IrLocalRef(idx), lenExpr);
      var step = new IrBlock([new IrAssign(idx, new IrBinary(IrBinaryOp.Add, new IrLocalRef(idx), new IrConst(IrType.I32, 1)))]);
      statements.Add(new IrFor(cond, new(bodyStmts), step));
      return;
    }

    throw this.Fail(PerlFrontend.ControlUnsupported, "foreach over unsupported iterable");
  }

  private void LowerLoopJump(PerlJump node, List<IrStmt> statements) {
    var targetLabel = node.Label;
    if (node.Value is not null)
      throw this.Fail(PerlFrontend.DynamicLoopControl, node.Keyword + " EXPR");
    if (this._loops.Count == 0 || this._loops.Peek().IsDoWhile)
      throw this.Fail(PerlFrontend.DynamicLoopControl, node.Keyword + (targetLabel is not null ? $" {targetLabel}" : ""));

    if (targetLabel is null || this._loops.Peek().Label == targetLabel) {
      statements.Add(node.Keyword == "last" ? new IrBreak() : new IrContinue());
      return;
    }

    var target = this._loops.FirstOrDefault(l => l.Label == targetLabel)
      ?? throw this.Fail(PerlFrontend.DynamicLoopControl, $"{node.Keyword} {targetLabel}");

    if (node.Keyword == "last") {
      target.BreakFlag ??= this.TempLocal(IrType.Bool, "_brk");
      statements.Add(new IrAssign(target.BreakFlag, new IrConst(IrType.Bool, 1)));
      statements.Add(new IrBreak());
    } else {
      target.ContinueFlag ??= this.TempLocal(IrType.Bool, "_cnt");
      statements.Add(new IrAssign(target.ContinueFlag, new IrConst(IrType.Bool, 1)));
      statements.Add(new IrBreak());
    }
  }

  private IrBlock LowerLoopBody(IReadOnlyList<PerlStatement> body) {
    var items = new List<IrStmt>();
    var saved = (this._line, this._integer);
    this.LowerStatements(body, items, null, PerlWant.Void, allowImplicitReturn: false);
    (this._line, this._integer) = saved;
    return new(items);
  }

  private IrExpr LowerDoBlockExpr(PerlBlock block, PerlWant want) {
    var prev = (this._line, this._integer);
    var statements = block.Statements.Where(s => !IsOpless(s)).ToList();
    foreach (var s in block.Statements) {
      this._line = s.Line;
      this._integer = s.Integer;
    }
    if (statements.Count == 0 || statements[^1] is not PerlExpressionStatement { Modifier: null } last)
      throw this.Fail(PerlFrontend.ScalarUnsupported, "do BLOCK without a value");
    if (statements.Count > 1)
      throw this.Fail(PerlFrontend.ScalarUnsupported, "do BLOCK with statements");
    this._line = last.Line;
    this._integer = last.Integer;
    var result = this.LiftExpr(last.Expression, want);
    (this._line, this._integer) = prev;
    return result;
  }

  private static bool IsVarWritten(IrBlock block, IrLocal local) {
    foreach (var stmt in block.Statements) {
      if (stmt is IrAssign a && a.Local == local)
        return true;
      if (IrTree.Expressions(stmt).OfType<IrStep>().Any(s => s.Target is IrLocalRef r && r.Local == local))
        return true;
      foreach (var child in IrTree.Blocks(stmt))
        if (IsVarWritten(child, local))
          return true;
    }
    return false;
  }

}
