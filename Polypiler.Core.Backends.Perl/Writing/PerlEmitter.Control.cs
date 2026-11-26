using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Analysis;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Perl;

public static partial class PerlEmitter {

  private sealed partial class Writer {

    private void EmitBlockStatements(IrBlock block, StringBuilder text, int indent, int startIndex = 0) {
      for (var index = startIndex; index < block.Statements.Count; ++index) {
        this._point = new(block, index);
        var statement = block.Statements[index];

        // Recognise `int i = START; for (; i < BOUND; i += 1)` as idiomatic Perl range / element loops (#430, #432).
        if (statement is IrDeclare { Initializer: not null } decl &&
            index + 1 < block.Statements.Count &&
            block.Statements[index + 1] is IrFor nextFor &&
            this.TryEmitCountedLoop(decl, nextFor, text, indent)) {
          ++index;
          continue;
        }

        this.EmitStatement(statement, text, indent);
      }
    }

    private void EmitStatement(IrStmt statement, StringBuilder text, int indent) {
      var pad = Indent(indent);
      switch (statement) {
        case IrBlock inner:
          text.Append(pad).Append("{\n");
          this.EmitBlockStatements(inner, text, indent + 1);
          text.Append(pad).Append("}\n");
          break;

        case IrDeclare declare: {
          var name = this.LocalName(declare.Local);
          if (declare.Initializer is { } init)
            text.Append(pad).Append("my ").Append(name).Append(" = ").Append(this.CopyIfStruct(declare.Local.Type, this.EmitExpr(init))).Append(";\n");
          else
            text.Append(pad).Append("my ").Append(name).Append(" = ").Append(this.DefaultValue(declare.Local.Type)).Append(";\n");
          break;
        }

        case IrAssign assign:
          text.Append(pad).Append(this.LocalName(assign.Local)).Append(" = ")
            .Append(this.CopyIfStruct(assign.Local.Type, this.EmitExpr(assign.Value))).Append(";\n");
          break;

        case IrGlobalAssign globalAssign:
          text.Append(pad).Append('$').Append(this._globalNames[globalAssign.Global]).Append(" = ")
            .Append(this.CopyIfStruct(globalAssign.Global.Type, this.EmitExpr(globalAssign.Value))).Append(";\n");
          break;

        case IrStore store:
          this.EmitStoreStatement(store, text, indent);
          break;

        case IrFieldStore fieldStore:
          this.EmitFieldStoreStatement(fieldStore, text, indent);
          break;

        case IrExprStmt exprStmt:
          text.Append(pad).Append(this.EmitExpr(exprStmt.Expression)).Append(";\n");
          break;

        case IrWriteLine:
          this.UnsupportedOp(nameof(IrWriteLine));
          break;

        case IrIf @if:
          this.EmitIf(@if, text, indent);
          break;

        case IrWhile @while:
          this.EmitWhile(@while, text, indent);
          break;

        case IrDoWhile doWhile:
          this.EmitDoWhile(doWhile, text, indent);
          break;

        case IrFor @for:
          this.EmitGeneralFor(@for, text, indent);
          break;

        case IrSwitch @switch:
          this.EmitSwitch(@switch, text, indent);
          break;

        case IrBreak:
          this.EmitBreak(text, indent);
          break;

        case IrContinue:
          this.EmitContinue(text, indent);
          break;

        case IrReturn ret:
          this.EmitReturn(ret, text, indent);
          break;

        case IrThrow @throw:
          text.Append(pad).Append("die ").Append(this.EmitExceptionValue(@throw.Exception)).Append(";\n");
          break;

        case IrRethrow:
          text.Append(pad).Append("die $_e;\n");
          break;

        case IrTry @try:
          this.EmitTry(@try, text, indent);
          break;

        case IrForEachCollection each:
          this.EmitForEachCollection(each, text, indent);
          break;

        case IrForEachSequence eachSeq:
          this.EmitForEachSequence(eachSeq, text, indent);
          break;

        case IrYield yieldStmt:
          this.EmitYield(yieldStmt, text, indent);
          break;

        case IrYieldBreak:
          this.EmitOuterReturn(null, text, indent);
          break;

        case IrYieldFrom yieldFrom:
          this.EmitYieldFrom(yieldFrom, text, indent);
          break;

        case IrUsing @using:
          this.EmitUsing(@using, text, indent);
          break;
      }
    }

    private void EmitIf(IrIf @if, StringBuilder text, int indent) {
      var pad = Indent(indent);
      // Statement modifier form (#430): single break/continue/return with no else.
      if (@if.Else is null && this._tryFrames.Count == 0 && @if.Then.Statements.Count == 1) {
        var single = @if.Then.Statements[0];
        if (single is IrContinue && this._loops.Count > 0 && !this._loops.Peek().IsSequence && this._loops.Peek().ContinueLabel is null) {
          text.Append(pad).Append("next if ").Append(this.EmitCondition(@if.Condition)).Append(";\n");
          return;
        }
        if (single is IrBreak && this._loops.Count > 0 && !this._loops.Peek().IsSequence) {
          text.Append(pad).Append("last if ").Append(this.EmitCondition(@if.Condition)).Append(";\n");
          return;
        }
        if (single is IrReturn ret && this._seqReturnFrame is null && this._asyncResultVar is null && !this._isIterator) {
          var retText = ret.Value is { } val ? "return " + this.CopyIfStruct(val.Type, this.EmitExpr(val)) : "return";
          text.Append(pad).Append(retText).Append(" if ").Append(this.EmitCondition(@if.Condition)).Append(";\n");
          return;
        }
      }

      text.Append(pad).Append("if (").Append(this.EmitCondition(@if.Condition)).Append(") {\n");
      this.EmitBlockStatements(@if.Then, text, indent + 1);
      var currentElse = @if.Else;
      while (currentElse is { Statements: [IrIf nested] }) {
        text.Append(pad).Append("} elsif (").Append(this.EmitCondition(nested.Condition)).Append(") {\n");
        this.EmitBlockStatements(nested.Then, text, indent + 1);
        currentElse = nested.Else;
      }
      if (currentElse is not null) {
        text.Append(pad).Append("} else {\n");
        this.EmitBlockStatements(currentElse, text, indent + 1);
      }
      text.Append(pad).Append("}\n");
    }

    private void EmitWhile(IrWhile @while, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var label = this.NextLabel();
      text.Append(pad).Append(label).Append(": while (").Append(this.EmitCondition(@while.Condition)).Append(") {\n");
      this._loops.Push(new(label, null, this._tryFrames.Count));
      this.EmitBlockStatements(@while.Body, text, indent + 1);
      this._loops.Pop();
      text.Append(pad).Append("}\n");
    }

    private void EmitDoWhile(IrDoWhile doWhile, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var hasBreak = ContainsDirectLoopJump(doWhile.Body, wantBreak: true);
      var hasContinue = ContainsDirectLoopJump(doWhile.Body, wantBreak: false);

      // Native do { ... } while (C) only when there is no break or continue inside (#430).
      if (!hasBreak && !hasContinue) {
        text.Append(pad).Append("do {\n");
        this.EmitBlockStatements(doWhile.Body, text, indent + 1);
        text.Append(pad).Append("} while (").Append(this.EmitCondition(doWhile.Condition)).Append(");\n");
        return;
      }

      var label = this.NextLabel();
      if (!hasContinue) {
        text.Append(pad).Append(label).Append(": while (1) {\n");
        this._loops.Push(new(label, null, this._tryFrames.Count));
        this.EmitBlockStatements(doWhile.Body, text, indent + 1);
        this._loops.Pop();
        text.Append(Indent(indent + 1)).Append("last unless ").Append(this.EmitCondition(doWhile.Condition)).Append(";\n");
        text.Append(pad).Append("}\n");
        return;
      }

      // With continue: wrap body in labelled bare block so `continue` reaches the condition (#430).
      var bodyLabel = this.NextLabel("BODY");
      text.Append(pad).Append(label).Append(": while (1) {\n");
      text.Append(Indent(indent + 1)).Append(bodyLabel).Append(": {\n");
      this._loops.Push(new(label, bodyLabel, this._tryFrames.Count));
      this.EmitBlockStatements(doWhile.Body, text, indent + 2);
      this._loops.Pop();
      text.Append(Indent(indent + 1)).Append("}\n");
      text.Append(Indent(indent + 1)).Append("last unless ").Append(this.EmitCondition(doWhile.Condition)).Append(";\n");
      text.Append(pad).Append("}\n");
    }

    private bool TryEmitCountedLoop(IrDeclare initDecl, IrFor @for, StringBuilder text, int indent) {
      var iv = initDecl.Local;
      if (!iv.Type.IsInteger || iv.Type.IsLong)
        return false;
      if (@for.Condition is not IrBinary { Left: IrLocalRef { Local: var condLocal } } cmp || condLocal != iv)
        return false;
      if (cmp.Op is not (IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual))
        return false;

      // Determine constant step (+1 or -1).
      int? stepDelta = @for.Step.Statements switch {
        [IrExprStmt { Expression: IrStep { Target: IrLocalRef { Local: var s }, Increment: var inc } }] when s == iv => inc ? 1 : -1,
        [IrAssign { Local: var s, Value: IrBinary { Op: IrBinaryOp.Add, Left: IrLocalRef { Local: var l }, Right: IrConst { Value: 1 } } }] when s == iv && l == iv => 1,
        [IrAssign { Local: var s, Value: IrBinary { Op: IrBinaryOp.Subtract, Left: IrLocalRef { Local: var l }, Right: IrConst { Value: 1 } } }] when s == iv && l == iv => -1,
        _ => null
      };
      if (stepDelta is null)
        return false;
      if (IsLocalModified(@for.Body, iv))
        return false;

      var pad = Indent(indent);
      var ivName = this.LocalName(iv);
      var label = this.NextLabel();

      // Forward +1 loop: 0 <= i < $a->length read-only element loop (#432) or `for my $i (START .. BOUND)` (#430).
      if (stepDelta == 1 && cmp.Op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual) {
        if (cmp.Op == IrBinaryOp.Less && initDecl.Initializer is IrConst { Value: 0 } &&
            cmp.Right is IrArrayLength { Array: IrLocalRef { Local: var arrLocal } } &&
            !(arrLocal.Type.Element == IrType.U8 && this._profile.ByteStorage == PerlByteStorage.String) &&
            !IsArrayWritten(@for.Body, arrLocal) &&
            TryMatchReadOnlyElementLoop(@for.Body, iv, arrLocal, out var elemLocal)) {
          var elemName = this.LocalName(elemLocal);
          var arrName = this.LocalName(arrLocal);
          text.Append(pad).Append(label).Append(": for my ").Append(elemName).Append(" (@").Append(arrName).Append(") {\n");
          this._loops.Push(new(label, null, this._tryFrames.Count));
          this.EmitBlockStatements(@for.Body, text, indent + 1, startIndex: 1);
          this._loops.Pop();
          text.Append(pad).Append("}\n");
          return true;
        }

        var start = this.EmitExpr(initDecl.Initializer!);
        var bound = cmp.Op == IrBinaryOp.Less
          ? (cmp.Right is IrArrayLength { Array: IrLocalRef { Local: var arr } } &&
             !(arr.Type.Element == IrType.U8 && this._profile.ByteStorage == PerlByteStorage.String)
              ? "$#" + this.LocalName(arr)
              : this.EmitExpr(cmp.Right) + " - 1")
          : this.EmitExpr(cmp.Right);
        text.Append(pad).Append(label).Append(": for my ").Append(ivName).Append(" (").Append(start).Append(" .. ").Append(bound).Append(") {\n");
        this._loops.Push(new(label, null, this._tryFrames.Count));
        this.EmitBlockStatements(@for.Body, text, indent + 1);
        this._loops.Pop();
        text.Append(pad).Append("}\n");
        return true;
      }

      // Reverse -1 loop: `for my $i (reverse BOUND .. START)` (#430).
      if (stepDelta == -1 && cmp.Op is IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual) {
        var start = this.EmitExpr(initDecl.Initializer!);
        var bound = cmp.Op == IrBinaryOp.Greater ? this.EmitExpr(cmp.Right) + " + 1" : this.EmitExpr(cmp.Right);
        text.Append(pad).Append(label).Append(": for my ").Append(ivName).Append(" (reverse ").Append(bound).Append(" .. ").Append(start).Append(") {\n");
        this._loops.Push(new(label, null, this._tryFrames.Count));
        this.EmitBlockStatements(@for.Body, text, indent + 1);
        this._loops.Pop();
        text.Append(pad).Append("}\n");
        return true;
      }

      return false;
    }

    private static bool TryMatchReadOnlyElementLoop(IrBlock body, IrLocal iv, IrLocal arrLocal, out IrLocal elemLocal) {
      elemLocal = null!;
      if (body.Statements.Count == 0 ||
          body.Statements[0] is not IrDeclare { Initializer: IrArrayLoad { Array: IrLocalRef { Local: var a }, Index: IrLocalRef { Local: var idx } } } first ||
          a != arrLocal || idx != iv)
        return false;

      for (var i = 1; i < body.Statements.Count; ++i)
        if (IrTree.Nodes(body.Statements[i]).OfType<IrLocalRef>().Any(r => r.Local == iv))
          return false;
      elemLocal = first.Local;
      return true;
    }

    private void EmitGeneralFor(IrFor @for, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var label = this.NextLabel();
      var hasContinue = @for.Step.Statements.Count > 0 && ContainsDirectLoopJump(@for.Body, wantBreak: false);

      text.Append(pad).Append(label).Append(": while (").Append(this.EmitCondition(@for.Condition)).Append(") {\n");
      if (hasContinue) {
        var bodyLabel = this.NextLabel("BODY");
        text.Append(Indent(indent + 1)).Append(bodyLabel).Append(": {\n");
        this._loops.Push(new(label, bodyLabel, this._tryFrames.Count));
        this.EmitBlockStatements(@for.Body, text, indent + 2);
        this._loops.Pop();
        text.Append(Indent(indent + 1)).Append("}\n");
      } else {
        this._loops.Push(new(label, null, this._tryFrames.Count));
        this.EmitBlockStatements(@for.Body, text, indent + 1);
        this._loops.Pop();
      }
      if (@for.Step.Statements.Count > 0)
        this.EmitBlockStatements(@for.Step, text, indent + 1);
      text.Append(pad).Append("}\n");
    }

    private void EmitSwitch(IrSwitch @switch, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var sel = this.Temp("_sw");
      var selType = @switch.Value.Type;
      text.Append(pad).Append("my ").Append(sel).Append(" = ").Append(this.EmitExpr(@switch.Value)).Append(";\n");

      var cases = @switch.Sections.Where(s => !s.IsDefault).ToList();
      var defaultSection = @switch.Sections.FirstOrDefault(s => s.IsDefault);
      var hasBreak = @switch.Sections.Any(s => ContainsDirectLoopJump(s.Body, wantBreak: true));
      var swLabel = hasBreak ? this.NextLabel("SW") : null;
      if (swLabel is not null) {
        text.Append(pad).Append(swLabel).Append(": {\n");
        this._loops.Push(new(swLabel, null, this._tryFrames.Count));
        ++indent;
        pad = Indent(indent);
      }

      var first = true;
      foreach (var section in cases) {
        var cond = this.FormatSwitchCondition(sel, selType, section.Labels);
        text.Append(pad).Append(first ? "if (" : "} elsif (").Append(cond).Append(") {\n");
        this.EmitBlockStatements(section.Body, text, indent + 1);
        first = false;
      }

      if (defaultSection is not null) {
        if (first) {
          this.EmitBlockStatements(defaultSection.Body, text, indent);
        } else {
          text.Append(pad).Append("} else {\n");
          this.EmitBlockStatements(defaultSection.Body, text, indent + 1);
          text.Append(pad).Append("}\n");
        }
      } else if (!first) {
        text.Append(pad).Append("}\n");
      }

      if (swLabel is not null) {
        this._loops.Pop();
        text.Append(Indent(indent - 1)).Append("}\n");
      }
    }

    private string FormatSwitchCondition(string sel, IrType selType, IReadOnlyList<long> labels) {
      if (selType.EnumType is { } enumType && this._enumMembers.TryGetValue(enumType, out var map)) {
        return string.Join(" || ", labels.Select(v =>
          enumType.MemberOf(v) is { } m && map.TryGetValue(m, out var name)
            ? $"{sel} == {name}"
            : $"{sel} == {FormatIntConstant(selType.Integral, v)}"));
      }

      // Compress consecutive runs of >= 3 integers into `$x >= A && $x <= B` (#430).
      var sorted = labels.Order().ToList();
      var parts = new List<string>();
      var i = 0;
      while (i < sorted.Count) {
        var start = sorted[i];
        var end = start;
        var j = i + 1;
        while (j < sorted.Count && sorted[j] == end + 1 && selType != IrType.U64) {
          end = sorted[j];
          ++j;
        }
        if (j - i >= 3) {
          parts.Add($"({sel} >= {FormatIntConstant(selType.Integral, start)} && {sel} <= {FormatIntConstant(selType.Integral, end)})");
          i = j;
        } else {
          parts.Add($"{sel} == {FormatIntConstant(selType.Integral, start)}");
          ++i;
        }
      }
      return string.Join(" || ", parts);
    }

    private void EmitBreak(StringBuilder text, int indent) {
      var pad = Indent(indent);
      var loop = this._loops.Peek();
      if (this._tryFrames.Count > loop.TryDepth) {
        var frame = this._tryFrames.Peek();
        text.Append(pad).Append(frame.BreakFlag).Append(" = 1; return 1;\n");
        return;
      }
      if (loop.IsSequence) {
        text.Append(pad).Append("return 0;\n");
        return;
      }
      text.Append(pad).Append("last ").Append(loop.Label).Append(";\n");
    }

    private void EmitContinue(StringBuilder text, int indent) {
      var pad = Indent(indent);
      var loop = this._loops.Peek();
      if (this._tryFrames.Count > loop.TryDepth) {
        var frame = this._tryFrames.Peek();
        text.Append(pad).Append(frame.ContinueFlag).Append(" = 1; return 1;\n");
        return;
      }
      if (loop.IsSequence) {
        text.Append(pad).Append("return 1;\n");
        return;
      }
      if (loop.ContinueLabel is not null)
        text.Append(pad).Append("last ").Append(loop.ContinueLabel).Append(";\n");
      else
        text.Append(pad).Append("next ").Append(loop.Label).Append(";\n");
    }

    private void EmitReturn(IrReturn ret, StringBuilder text, int indent) {
      var value = ret.Value is { } expr ? this.CopyIfStruct(expr.Type, this.EmitExpr(expr)) : null;
      this.EmitOuterReturn(value, text, indent);
    }

    private void EmitOuterReturn(string? value, StringBuilder text, int indent) {
      var pad = Indent(indent);
      if (this._tryFrames.Count > 0) {
        var frame = this._tryFrames.Peek();
        if (value is not null && frame.ReturnValue is not null)
          text.Append(pad).Append(frame.ReturnValue).Append(" = ").Append(value).Append(";\n");
        text.Append(pad).Append(frame.ReturnFlag).Append(" = 1; return 1;\n");
        return;
      }
      if (this._seqReturnFrame is { } seqRet) {
        if (value is not null)
          text.Append(pad).Append(seqRet.ReturnValue).Append(" = ").Append(value).Append(";\n");
        text.Append(pad).Append(seqRet.ReturnFlag).Append(" = 1; return 0;\n");
        return;
      }
      if (this._asyncResultVar is { } asyncVal) {
        if (value is not null)
          text.Append(pad).Append(asyncVal).Append(" = ").Append(value).Append(";\n");
        text.Append(pad).Append("return 1;\n");
        return;
      }
      if (this._isIterator || value is null) {
        text.Append(pad).Append("return;\n");
        return;
      }
      text.Append(pad).Append("return ").Append(value).Append(";\n");
    }

    private void EmitYield(IrYield yieldStmt, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var value = this.CopyIfStruct(yieldStmt.Value.Type, this.EmitExpr(yieldStmt.Value));
      text.Append(pad).Append("if (!$_yield->(").Append(value).Append(")) {\n");
      this.EmitOuterReturn(null, text, indent + 1);
      text.Append(pad).Append("}\n");
    }

    private void EmitYieldFrom(IrYieldFrom yieldFrom, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var innerPad = Indent(indent + 1);
      var seq = this.EmitExpr(yieldFrom.Sequence);
      var stop = this.Temp("_stop");
      text.Append(pad).Append("my ").Append(stop).Append(" = 0;\n");
      text.Append(pad).Append('(').Append(seq).Append(")->(sub {\n");
      text.Append(innerPad).Append("if (!$_yield->($_[0])) { ").Append(stop).Append(" = 1; return 0; }\n");
      text.Append(innerPad).Append("return 1;\n");
      text.Append(pad).Append("});\n");
      text.Append(pad).Append("if (").Append(stop).Append(") {\n");
      this.EmitOuterReturn(null, text, indent + 1);
      text.Append(pad).Append("}\n");
    }

    private void EmitForEachSequence(IrForEachSequence each, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var innerPad = Indent(indent + 1);
      var seqVar = this.Temp("_seq");
      var elemVar = this.LocalName(each.Element);
      var hasExit = ContainsReturnOrYieldExit(each.Body);
      var retFlag = hasExit ? this.Temp("_sret") : null;
      var retVal = hasExit ? this.Temp("_srval") : null;

      text.Append(pad).Append("my ").Append(seqVar).Append(" = ").Append(this.EmitExpr(each.Sequence)).Append(";\n");
      if (hasExit)
        text.Append(pad).Append("my (").Append(retFlag).Append(", ").Append(retVal).Append(") = (0, undef);\n");

      var savedTries = new Stack<TryFrame>(this._tryFrames.Reverse());
      var savedSeqRet = this._seqReturnFrame;
      this._tryFrames.Clear();
      this._seqReturnFrame = hasExit ? new SeqReturnFrame(retFlag!, retVal!) : null;
      this._loops.Push(new(this.NextLabel("SEQ"), null, 0, IsSequence: true));

      text.Append(pad).Append(seqVar).Append("->(sub {\n");
      text.Append(innerPad).Append("my (").Append(elemVar).Append(") = @_;\n");
      this.EmitBlockStatements(each.Body, text, indent + 1);
      text.Append(innerPad).Append("return 1;\n");
      text.Append(pad).Append("});\n");

      this._loops.Pop();
      this._seqReturnFrame = savedSeqRet;
      foreach (var frame in savedTries)
        this._tryFrames.Push(frame);

      if (hasExit) {
        var resultType = this._currentFunction?.ReturnType is { IsAsync: true } asyncType ? asyncType.Element! : this._currentFunction?.ReturnType;
        var replayVal = this._isIterator || resultType == IrType.Void ? null : retVal;
        text.Append(pad).Append("if (").Append(retFlag).Append(") {\n");
        this.EmitOuterReturn(replayVal, text, indent + 1);
        text.Append(pad).Append("}\n");
      }
    }

    private string EmitCondition(IrExpr condition) {
      if (condition is IrBinary cmp && TryComparisonOperator(cmp.Op, out var op))
        return $"{this.EmitExpr(cmp.Left)} {op} {this.EmitExpr(cmp.Right)}";
      return this.EmitExpr(condition);
    }

    private static bool ContainsDirectLoopJump(IrBlock block, bool wantBreak) {
      foreach (var stmt in block.Statements) {
        if (wantBreak && stmt is IrBreak) return true;
        if (!wantBreak && stmt is IrContinue) return true;
        if (stmt is IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence)
          continue;
        if (wantBreak && stmt is IrSwitch)
          continue;
        foreach (var child in IrTree.Blocks(stmt))
          if (ContainsDirectLoopJump(child, wantBreak))
            return true;
      }
      return false;
    }

    private static bool IsLocalModified(IrBlock block, IrLocal local) {
      foreach (var stmt in AllStatements(block)) {
        if (stmt is IrAssign a && a.Local == local)
          return true;
        if (IrTree.Expressions(stmt).OfType<IrStep>().Any(s => s.Target is IrLocalRef r && r.Local == local))
          return true;
        if (IrTree.Expressions(stmt).OfType<IrBind>().Any(b => b.Local == local))
          return true;
      }
      return false;
    }

    private static IEnumerable<IrStmt> AllStatements(IrBlock block) {
      foreach (var stmt in block.Statements) {
        yield return stmt;
        foreach (var child in IrTree.Blocks(stmt))
          foreach (var nested in AllStatements(child))
            yield return nested;
      }
    }

  }

}
