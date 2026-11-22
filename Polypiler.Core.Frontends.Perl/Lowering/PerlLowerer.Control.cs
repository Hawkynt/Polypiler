using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  private void LowerStatements(PerlOp[] nodes, List<IrStmt> statements, HashSet<PerlOp>? consumed, bool allowImplicitReturn) {
    var last = nodes.LastOrDefault(n => n.Name != "nextstate" && (consumed is null || !consumed.Contains(n)));
    string? pendingLabel = null;
    foreach (var node in nodes) {
      if (node.Name == "nextstate") {
        this._line = node.Line;
        this._integer = node.Integer != 0;
        pendingLabel = node.Label;
        continue;
      }
      if (consumed is not null && consumed.Contains(node)) {
        pendingLabel = null;
        continue;
      }

      var label = pendingLabel;
      pendingLabel = null;

      if (this.TryLowerBlessStatement(node, statements))
        continue;

      if (this.TryLowerMooseSetterStmt(node, statements))
        continue;

      if (this.TryLowerControlStatement(node, label, statements))
        continue;

      if (node is { Name: "return", Children: var retKids }) {
        this.LowerReturnStatement(retKids, statements);
        continue;
      }

      if (node.Name == "die") {
        this.LowerDieStatement(node, statements);
        continue;
      }

      if (node.Name is "leave" && this.TryLowerTryCatchFinally(node, statements))
        continue;

      if (node.Name == "leavetrycatch") {
        this.LowerTryCatch(node, null, statements);
        continue;
      }

      if (node.Name == "leavetry") {
        this.LowerEvalStatement(node, statements);
        continue;
      }

      if (node.Name is "lineseq" or "scope" or "list" or "leave") {
        var innerNodes = StripMarks(node.Children).Where(c => c.Name != "enter").ToArray();
        this.LowerStatements(innerNodes, statements, null, allowImplicitReturn: allowImplicitReturn && ReferenceEquals(node, last));
        continue;
      }

      if (this.TryLowerAssignmentStatement(node, statements))
        continue;

      if (allowImplicitReturn && ReferenceEquals(node, last) && this._currentReturnType != IrType.Void) {
        var val = this.LiftExpr(node);
        statements.Add(new IrReturn(this.ToType(val, this._currentReturnType)));
        continue;
      }

      if (node.Name is "unstack" or "enter" || (node.Name == "null" && node.Children.Length == 0))
        continue;

      var expr = this.LiftExpr(node);
      statements.Add(new IrExprStmt(expr));
    }
  }

  private void LowerReturnStatement(PerlOp[] retKids, List<IrStmt> statements) {
    var vals = retKids.Where(k => k.Name != "pushmark").ToArray();
    if (vals.Length == 0) {
      statements.Add(new IrReturn(this._currentReturnType == IrType.Void ? null : DefaultExpr(this._currentReturnType)));
      return;
    }

    // Returning an anonymous sub escapes (#436 -> PLY1732).
    if (vals[0].Name == "anoncode" || (vals[0].Name == "padsv" && this._closureLocals.ContainsKey(vals[0].Target)))
      throw this.Fail(PerlFrontend.EscapingClosure, vals[0].Name == "anoncode" ? "sub { ... }" : this.LexicalName(vals[0].Target), cause: "OBJ-CLOSURES");

    var expr = this.LiftExpr(vals[0]);
    if (this._currentReturnType == IrType.Void)
      statements.Add(new IrReturn(null));
    else
      statements.Add(new IrReturn(this.ToType(expr, this._currentReturnType)));
  }

  private bool TryLowerControlStatement(PerlOp node, string? label, List<IrStmt> statements) {
    // Reject unsupported control ops (#435)
    if (node.Name is "goto" or "redo" or "given" or "when")
      throw this.Fail(PerlFrontend.ControlUnsupported, node.Name);

    // Unwrap `null` wrapping a single void-context condition (`and`, `or`, `cond_expr`).
    if (node is { Name: "null", Children: [var wrapped] } && wrapped.Name is "and" or "or" or "cond_expr")
      return this.TryLowerControlStatement(wrapped, label, statements);

    // `if (C) { ... }` / `STMT if C;` compiled as `and` in void or statement/tail context
    if (node is { Name: "and", Children: [var condOp, var thenOp] } && (node.Want == "void" || IsStatementOp(thenOp))) {
      var cond = this.Truth(this.LiftExpr(condOp));
      var thenBlock = this.LowerSingleBody(thenOp);
      statements.Add(new IrIf(cond, thenBlock, null));
      return true;
    }

    // `unless (C) { ... }` / `STMT unless C;` compiled as `or` in void or statement/tail context
    if (node is { Name: "or", Children: [var condOp2, var elseOp] } && (node.Want == "void" || IsStatementOp(elseOp))) {
      var cond = new IrUnary(IrUnaryOp.LogicalNot, this.Truth(this.LiftExpr(condOp2)));
      var thenBlock = this.LowerSingleBody(elseOp);
      statements.Add(new IrIf(cond, thenBlock, null));
      return true;
    }

    // `if (C) { ... } elsif ... else { ... }` compiled as `cond_expr` in void or statement/tail context
    if (node is { Name: "cond_expr", Children: [var testOp, var yesOp, var noOp] } &&
        (node.Want == "void" || IsStatementOp(yesOp) || IsStatementOp(noOp))) {
      var cond = this.Truth(this.LiftExpr(testOp));
      var yesBlock = this.LowerSingleBody(yesOp);
      var noBlock = this.LowerSingleBody(noOp);
      statements.Add(new IrIf(cond, yesBlock, noBlock.Statements.Count > 0 ? noBlock : null));
      return true;
    }

    // Loop constructs: `leaveloop`
    if (node is { Name: "leaveloop", Children: [var enterOp, var bodyOp] }) {
      this.LowerLoop(enterOp, bodyOp, label, statements);
      return true;
    }

    // `last` / `next` (plain or labelled)
    if (node.Name is "last" or "next") {
      this.LowerLoopJump(node, statements);
      return true;
    }

    return false;
  }

  private static readonly HashSet<string> _statementOps = new(StringComparer.Ordinal) {
    "scope", "leave", "lineseq", "return", "die", "last", "next",
    "leaveloop", "cond_expr", "and", "or", "padsv_store", "sassign", "aassign"
  };

  private static bool IsStatementOp(PerlOp op)
    => _statementOps.Contains(UnwrapSingleNull(op).Name);

  private static PerlOp UnwrapSingleNull(PerlOp op) {
    while (op.Name == "null" && op.Children.Length == 1)
      op = op.Children[0];
    return op;
  }

  private void LowerLoop(PerlOp enterOp, PerlOp bodyOp, string? label, List<IrStmt> statements) {
    // Unwrap `null` around loop body
    var inner = bodyOp is { Name: "null", Children: [var single] } ? single : bodyOp;

    // 1. Bare block `{ ... }` (no loop condition, body is `lineseq`)
    if (enterOp.Name == "enterloop" && inner.Name == "lineseq") {
      var prev = (this._line, this._integer);
      var items = new List<IrStmt>();
      this._loops.Push(new(label, isDoWhile: false));
      this.LowerStatements(inner.Children, items, null, allowImplicitReturn: false);
      this._loops.Pop();
      (this._line, this._integer) = prev;
      statements.Add(new IrBlock(items));
      return;
    }

    // 2. `for` / `foreach` (`enteriter` + `and(iter, body)`)
    if (enterOp.Name == "enteriter") {
      this.LowerForEachLoop(enterOp, inner, label, statements);
      return;
    }

    // 3. `while (C) { ... }` / `until (C) { ... }` / C-style `for`
    if (enterOp.Name == "enterloop") {
      var cond = this.Truth(this.LiftExpr(inner.Children[0]));
      if (inner.Name == "or")
        cond = new IrUnary(IrUnaryOp.LogicalNot, cond);
      this._loops.Push(new(label, isDoWhile: false));
      var body = this.LowerLoopBody(inner.Children[1]);
      this._loops.Pop();
      statements.Add(new IrWhile(cond, body));
      return;
    }

    throw this.Fail(PerlFrontend.ControlUnsupported, $"loop shape {enterOp.Name}/{inner.Name}");
  }

  private void LowerForEachLoop(PerlOp enterIter, PerlOp inner, string? label, List<IrStmt> statements) {
    var iterBody = inner.Children[1];

    // Check if reversed (`reverse A .. B`)
    var reversed = this._tree.IterReversed != 0 && (enterIter.Private & this._tree.IterReversed) != 0;

    // Inspect the iterable list in `enterIter.Children`
    var iterKids = enterIter.Children.Where(c => c.Name != "null" || c.Children.Length > 0).ToArray();
    var listNode = iterKids.FirstOrDefault(c => c.Name == "null") ?? iterKids.FirstOrDefault();

    // Case A: Range `A .. B` (direct `[pushmark, startOp, endOp]` or `reverse A .. B` with `flop -> flip -> range(startOp, endOp)`)
    PerlOp? rangeStart = null;
    PerlOp? rangeEnd = null;
    if (listNode is { Children: [{ Name: "pushmark" }, var s1, var e1] }) {
      rangeStart = s1;
      rangeEnd = e1;
    } else if (Descendants(enterIter).FirstOrDefault(o => o.Name == "range") is { Children: [var s2, var e2] }) {
      rangeStart = s2;
      rangeEnd = e2;
    }

    if (rangeStart is not null && rangeEnd is not null) {
      var startExpr = this.LiftExpr(rangeStart);
      var endExpr = this.LiftExpr(rangeEnd);
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

      // Evaluate bounds once before the loop (#435).
      var boundStart = this.TempLocal(boundType, "_start");
      var boundEnd = this.TempLocal(boundType, "_end");
      statements.Add(new IrDeclare(boundStart, startVal));
      statements.Add(new IrDeclare(boundEnd, endVal));

      var counter = this.TempLocal(boundType, "_iter");
      statements.Add(new IrDeclare(counter, new IrLocalRef(reversed ? boundEnd : boundStart)));

      // Loop variable: either `my $i` (`enterIter.Target > 0`) or implicit `$_` (`enterIter.Target == 0`).
      var prevTopic = this._topicVar;
      IrLocal loopVar;
      var bodyStmts = new List<IrStmt>();
      if (enterIter.Target > 0) {
        var vname = this.LexicalName(enterIter.Target);
        loopVar = this.DeclareLocal(vname, iterType, enterIter.Target);
      } else {
        loopVar = this.TempLocal(iterType, "_topic");
        this._topicVar = loopVar;
      }

      // Per-iteration copy so writing to the loop variable does not alter the trip count (#435).
      bodyStmts.Add(new IrDeclare(loopVar, this.ToType(new IrLocalRef(counter), iterType)));

      var outerLoop = new LoopContext(label, isDoWhile: false);
      this._loops.Push(outerLoop);
      var loweredBody = this.LowerLoopBody(iterBody);
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
    var arrayOp = listNode?.Children.FirstOrDefault(c => c.Name is "padav" or "rv2av" or "padsv");
    if (arrayOp is not null) {
      var arrExpr = arrayOp is { Name: "rv2av", Children: [{ Name: "padsv" } innerSv] }
        ? this.LiftExpr(innerSv)
        : this.LiftExpr(arrayOp);
      if (!arrExpr.Type.IsArray && !arrExpr.Type.IsList)
        throw this.Fail(PerlFrontend.ControlUnsupported, "foreach over non-array");

      var elemType = arrExpr.Type.Element!;
      var arrTemp = this.TempLocal(arrExpr.Type, "_arr");
      statements.Add(new IrDeclare(arrTemp, arrExpr));
      var idx = this.TempLocal(IrType.I32, "_idx");
      statements.Add(new IrDeclare(idx, new IrConst(IrType.I32, 0)));

      var prevTopic = this._topicVar;
      IrLocal elemVar;
      if (enterIter.Target > 0) {
        var vname = this.LexicalName(enterIter.Target);
        elemVar = this.DeclareLocal(vname, elemType, enterIter.Target);
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
      var loweredBody = this.LowerLoopBody(iterBody);
      this._loops.Pop();
      this._topicVar = prevTopic;

      bodyStmts.AddRange(loweredBody.Statements);
      // Aliasing (#437): write back modified loop variable to array element!
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

  private void LowerLoopJump(PerlOp node, List<IrStmt> statements) {
    var targetLabel = node.Pv;
    if (this._loops.Count == 0 || this._loops.Peek().IsDoWhile)
      throw this.Fail(PerlFrontend.DynamicLoopControl, node.Name + (targetLabel is not null ? $" {targetLabel}" : ""));

    if (targetLabel is null || this._loops.Peek().Label == targetLabel) {
      statements.Add(node.Name == "last" ? new IrBreak() : new IrContinue());
      return;
    }

    var target = this._loops.FirstOrDefault(l => l.Label == targetLabel)
      ?? throw this.Fail(PerlFrontend.DynamicLoopControl, $"{node.Name} {targetLabel}");

    if (node.Name == "last") {
      target.BreakFlag ??= this.TempLocal(IrType.Bool, "_brk");
      statements.Add(new IrAssign(target.BreakFlag, new IrConst(IrType.Bool, 1)));
      statements.Add(new IrBreak());
    } else {
      target.ContinueFlag ??= this.TempLocal(IrType.Bool, "_cnt");
      statements.Add(new IrAssign(target.ContinueFlag, new IrConst(IrType.Bool, 1)));
      statements.Add(new IrBreak());
    }
  }

  private IrBlock LowerSingleBody(PerlOp node) {
    var items = new List<IrStmt>();
    if (node is { Name: "leave" or "scope" or "lineseq", Children: var kids }) {
      var filtered = kids.Where(k => k.Name != "enter").ToArray();
      this.LowerStatements(filtered, items, null, allowImplicitReturn: false);
    } else if (node is { Name: "null", Children: [var inner] }) {
      return this.LowerSingleBody(inner);
    } else {
      this.LowerStatements([node], items, null, allowImplicitReturn: false);
    }
    return new(items);
  }

  private IrBlock LowerLoopBody(PerlOp node) {
    var items = new List<IrStmt>();
    node = UnwrapSingleNull(node);
    var kids = node.Name == "lineseq" ? node.Children : [node];
    var filtered = kids.Where(k => k.Name is not ("enter" or "unstack")).ToArray();
    this.LowerStatements(filtered, items, null, allowImplicitReturn: false);
    return new(items);
  }

  private IrExpr LowerDoBlockExpr(PerlOp leaveOp) {
    var prev = (this._line, this._integer);
    PerlOp? last = null;
    foreach (var k in leaveOp.Children) {
      if (k.Name == "nextstate") {
        this._line = k.Line;
        this._integer = k.Integer != 0;
      } else if (k.Name != "enter") {
        last = k;
      }
    }
    var result = this.LiftExpr(last!);
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
