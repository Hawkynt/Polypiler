using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Net;

/// <summary>
/// A procedure with <c>GoTo</c>, labels, <c>On Error</c> or <c>Resume</c>, or with an <c>Exit</c> or
/// <c>Continue</c> of a statement around the innermost one, lowered as a state machine: its statements
/// flattened into sections between the labels, a loop that runs the section the state names and a
/// switch over the state. Each jump sets the state. Under <c>On Error</c> every statement is a section
/// of its own and the loop's body a try: an error sets <c>Err.Number</c> from the exception's class and
/// goes on with the next statement (<c>Resume Next</c>) or the handler's label, which <c>Resume</c>
/// leaves for the statement that failed, the one after it or a label.
/// </summary>
internal sealed partial class VbNetLowerer {

  /// <summary>A part of a flattened procedure.</summary>
  private abstract record Piece;

  /// <summary>The statements of one statement of Visual Basic; an error in them under <c>Resume Next</c> goes on at the label given, or with the next piece.</summary>
  private sealed record CodePiece(List<IrStmt> Statements, object? Resume = null) : Piece;

  /// <summary>A place a jump goes to: a label of the source, of a statement or one of the flattening.</summary>
  private sealed record MarkPiece(object Label) : Piece;

  /// <summary>A jump to a label, when the condition holds or always.</summary>
  private sealed record JumpPiece(object Target, IrExpr? When) : Piece;

  /// <summary><c>On Error</c>: <c>GoTo label</c>, <c>Resume Next</c>, <c>GoTo 0</c> or <c>GoTo -1</c>.</summary>
  private sealed record OnErrorPiece(SyntaxKind Kind, object? Handler) : Piece;

  /// <summary><c>Resume</c>, <c>Resume Next</c> or <c>Resume label</c>.</summary>
  private sealed record ResumePiece(SyntaxKind Kind, object? Target) : Piece;

  /// <summary>Labels of the source by symbol, those of the flattening by reference.</summary>
  private sealed class LabelComparer : IEqualityComparer<object> {
    public static LabelComparer Instance { get; } = new();
    public new bool Equals(object? x, object? y) => x is ISymbol a && y is ISymbol b ? SymbolEqualityComparer.Default.Equals(a, b) : ReferenceEquals(x, y);
    public int GetHashCode(object obj) => obj is ISymbol symbol ? SymbolEqualityComparer.Default.GetHashCode(symbol) : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
  }

  /// <summary>Whether the procedure is being lowered as a state machine.</summary>
  private bool _flat;

  /// <summary>Whether a structured lowering met a jump only a state machine writes.</summary>
  private bool _wantsJumps;

  /// <summary>Whether a procedure's source has a statement only a state machine lowers: <c>GoTo</c>, a label, <c>On Error</c>, <c>Resume</c>.</summary>
  private static bool NeedsJumps(SyntaxNode block)
    => block.DescendantNodes().Any(node => node is GoToStatementSyntax or LabelStatementSyntax or OnErrorGoToStatementSyntax or OnErrorResumeNextStatementSyntax or ResumeStatementSyntax);

  private static IrExpr Not(IrExpr condition) => condition is IrUnary { Op: IrUnaryOp.LogicalNot } negated ? negated.Operand : new IrUnary(IrUnaryOp.LogicalNot, condition);

  /// <summary>The statements of a procedure's body as a state machine.</summary>
  private List<IrStmt> Flat(IOperation body) {
    var pieces = new List<Piece>();
    this.Flatten(body, pieces);
    var trapping = pieces.Any(piece => piece is OnErrorPiece or ResumePiece);

    // The sections: a run of statements up to a jump or a label; under On Error each statement alone.
    var sections = new List<List<Piece>>();
    var labels = new Dictionary<object, int>(LabelComparer.Instance);
    var current = new List<Piece>();
    foreach (var piece in pieces) {
      if (piece is MarkPiece mark) {
        if (current.Count > 0) {
          sections.Add(current);
          current = [];
        }

        labels[mark.Label] = sections.Count;
        continue;
      }

      current.Add(piece);
      if (trapping || piece is not CodePiece) {
        sections.Add(current);
        current = [];
      }
    }

    if (current.Count > 0)
      sections.Add(current);

    var end = sections.Count;
    // Declared with their defaults, the state 0 and no handler on, at the start.
    var unused = new List<IrStmt>();
    var state = this.Temporary(IrType.I32, unused, new IrConst(IrType.I32, 0));
    IrLocal? mode = null, handler = null, resumeAt = null, handling = null;
    if (trapping) {
      mode = this.Temporary(IrType.I32, unused, new IrConst(IrType.I32, 0));
      handler = this.Temporary(IrType.I32, unused, new IrConst(IrType.I32, 0));
      resumeAt = this.Temporary(IrType.I32, unused, new IrConst(IrType.I32, 0));
      handling = this.Temporary(IrType.Bool, unused, new IrConst(IrType.Bool, 0));
    }

    IrStmt Go(int target) => new IrAssign(state, new IrConst(IrType.I32, target));
    IrExpr I32(int value) => new IrConst(IrType.I32, value);
    int Index(object label) => labels.TryGetValue(label, out var index) ? index : end;

    // Resume Next goes on with the next section, but after the value of a Select Case past its end.
    var resumes = sections.Select((section, index) => (index, section[^1] is CodePiece { Resume: { } label } ? Index(label) : index + 1)).Where(pair => pair.Item2 != pair.index + 1).ToList();
    IrExpr NextOf(IrLocal failed) => resumes.Aggregate((IrExpr)new IrBinary(IrBinaryOp.Add, new IrLocalRef(failed), I32(1)),
      (otherwise, pair) => new IrConditional(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(failed), I32(pair.index)), I32(pair.Item2), otherwise));

    var switched = new List<IrSwitchSection>();
    for (var index = 0; index < sections.Count; ++index) {
      var statements = new List<IrStmt>();
      var next = index + 1;
      var goes = true;
      foreach (var piece in sections[index]) {
        switch (piece) {
          case CodePiece code:
            statements.AddRange(code.Statements);
            break;
          case JumpPiece { When: null } jump:
            statements.Add(Go(Index(jump.Target)));
            goes = false;
            break;
          case JumpPiece jump:
            statements.Add(new IrIf(jump.When, new IrBlock([Go(Index(jump.Target))]), new IrBlock([Go(next)])));
            goes = false;
            break;
          case OnErrorPiece onError:
            statements.Add(this.SetErr(0));
            if (onError.Kind == SyntaxKind.OnErrorGoToMinusOneStatement) {
              statements.Add(new IrAssign(handling!, new IrConst(IrType.Bool, 0)));
              break;
            }

            statements.Add(new IrAssign(mode!, I32(onError.Kind switch {
              SyntaxKind.OnErrorResumeNextStatement => 1,
              SyntaxKind.OnErrorGoToLabelStatement => 2,
              _ => 0
            })));
            if (onError.Handler is { } label)
              statements.Add(new IrAssign(handler!, I32(Index(label))));
            break;
          case ResumePiece resume:
            // Resume outside a handler is Visual Basic's error 20, which no exception class of the IR stands for.
            statements.Add(new IrIf(new IrUnary(IrUnaryOp.LogicalNot, new IrLocalRef(handling!)), new IrBlock([Throw(IrExceptionClass.InvalidOperationException)]), null));
            statements.Add(new IrAssign(handling!, new IrConst(IrType.Bool, 0)));
            statements.Add(this.SetErr(0));
            statements.Add(resume.Kind switch {
              SyntaxKind.ResumeNextStatement => new IrAssign(state, NextOf(resumeAt!)),
              SyntaxKind.ResumeLabelStatement => Go(Index(resume.Target!)),
              _ => new IrAssign(state, new IrLocalRef(resumeAt!))
            });
            goes = false;
            break;
        }
      }

      if (Completes(new IrBlock(statements))) {
        if (goes)
          statements.Add(Go(next));
        statements.Add(new IrBreak());
      }

      switched.Add(new IrSwitchSection([index], false, new IrBlock(statements)));
    }

    IrStmt dispatch = new IrSwitch(new IrLocalRef(state), switched);
    if (trapping) {
      // An error with no handler on, or in the handler, goes to the caller; otherwise Err.Number is set
      // and the state is the next statement or the handler's.
      List<IrStmt> Trap(int number) => [
        new IrIf(new IrBinary(IrBinaryOp.LogicalOr, new IrBinary(IrBinaryOp.Equal, new IrLocalRef(mode!), I32(0)), new IrLocalRef(handling!)), new IrBlock([new IrRethrow()]), null),
        this.SetErr(number),
        new IrAssign(resumeAt!, new IrLocalRef(state)),
        new IrIf(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(mode!), I32(1)),
          new IrBlock([new IrAssign(state, NextOf(state))]),
          new IrBlock([new IrAssign(handling!, new IrConst(IrType.Bool, 1)), new IrAssign(state, new IrLocalRef(handler!))]))
      ];
      dispatch = new IrTry(new IrBlock([dispatch]), [.. _errors.Select(error => new IrCatch(error.Class, null, null, new IrBlock(Trap(error.Number))))], null);
    }

    var loop = new IrWhile(new IrBinary(IrBinaryOp.NotEqual, new IrLocalRef(state), I32(end)), new IrBlock([dispatch]));
    // Every way out of a function returns inside the loop; a target language that cannot see it is given a last return.
    return this._method is { ReturnsVoid: false } method ? [.. this._hoisted, loop, new IrReturn(Default(this._functions[method].Returns))] : [.. this._hoisted, loop];
  }

  /// <summary>A statement as the pieces of a flattened procedure.</summary>
  private void Flatten(IOperation operation, List<Piece> pieces) {
    switch (operation) {
      case IBlockOperation block:
        foreach (var inner in block.Operations)
          this.Flatten(inner, pieces);
        break;
      case ILabeledOperation label:
        pieces.Add(new MarkPiece(label.Label));
        if (label.Operation is { } labeled)
          this.Flatten(labeled, pieces);
        break;
      case IBranchOperation branch:
        pieces.Add(new JumpPiece(branch.Target, null));
        break;
      case IConditionalOperation branch:
        var otherwise = new object();
        pieces.Add(new JumpPiece(otherwise, Not(this.Condition(branch.Condition))));
        this.Flatten(branch.WhenTrue, pieces);
        if (branch.WhenFalse is { } whenFalse) {
          var after = new object();
          pieces.Add(new JumpPiece(after, null));
          pieces.Add(new MarkPiece(otherwise));
          this.Flatten(whenFalse, pieces);
          pieces.Add(new MarkPiece(after));
        } else {
          pieces.Add(new MarkPiece(otherwise));
        }

        break;
      case IWhileLoopOperation loop:
        var condition = loop.Condition is null ? null : this.Condition(loop.Condition);
        if (condition is not null && loop.ConditionIsUntil)
          condition = Not(condition);
        if (loop.ConditionIsTop || condition is null) {
          pieces.Add(new MarkPiece(loop.ContinueLabel));
          if (condition is not null)
            pieces.Add(new JumpPiece(loop.ExitLabel, Not(condition)));
          this.Flatten(loop.Body, pieces);
          pieces.Add(new JumpPiece(loop.ContinueLabel, null));
        } else {
          var top = new object();
          pieces.Add(new MarkPiece(top));
          this.Flatten(loop.Body, pieces);
          pieces.Add(new MarkPiece(loop.ContinueLabel));
          pieces.Add(new JumpPiece(top, condition));
        }

        pieces.Add(new MarkPiece(loop.ExitLabel));
        break;
      case IForToLoopOperation loop:
        var start = new List<IrStmt>();
        var (test, step) = this.ForToParts(loop, start);
        this.Loop(loop, start, test, [], step, pieces);
        break;
      case IForEachLoopOperation loop:
        var first = new List<IrStmt>();
        if (this.ForEachParts(loop, first) is var (more, head, advance))
          this.Loop(loop, first, more, head, advance, pieces);
        break;
      case ISwitchOperation choice:
        this.Cases(choice, pieces);
        break;
      case { Kind: OperationKind.None, Syntax: OnErrorGoToStatementSyntax onError }:
        pieces.Add(new OnErrorPiece(onError.Kind(), onError.IsKind(SyntaxKind.OnErrorGoToLabelStatement) ? this._model.GetSymbolInfo(onError.Label).Symbol : null));
        break;
      case { Kind: OperationKind.None, Syntax: OnErrorResumeNextStatementSyntax }:
        pieces.Add(new OnErrorPiece(SyntaxKind.OnErrorResumeNextStatement, null));
        break;
      case { Kind: OperationKind.None, Syntax: ResumeStatementSyntax resume }:
        pieces.Add(new ResumePiece(resume.Kind(), resume.IsKind(SyntaxKind.ResumeLabelStatement) ? this._model.GetSymbolInfo(resume.Label!).Symbol : null));
        break;
      case { Kind: OperationKind.None }:
        foreach (var inner in operation.ChildOperations)
          this.Flatten(inner, pieces);
        break;
      default:
        var statements = new List<IrStmt>();
        this.Statement(operation, statements);
        if (statements.Count > 0)
          pieces.Add(new CodePiece(statements));
        break;
    }
  }

  /// <summary>A counted loop: its start, the test before each pass, the statements that begin a pass, the body and the step.</summary>
  private void Loop(ILoopOperation loop, List<IrStmt> start, IrExpr test, List<IrStmt> head, List<IrStmt> step, List<Piece> pieces) {
    var top = new object();
    pieces.Add(new CodePiece(start));
    pieces.Add(new MarkPiece(top));
    pieces.Add(new JumpPiece(loop.ExitLabel, Not(test)));
    if (head.Count > 0)
      pieces.Add(new CodePiece(head));
    this.Flatten(loop.Body, pieces);
    pieces.Add(new MarkPiece(loop.ContinueLabel));
    pieces.Add(new CodePiece(step));
    pieces.Add(new JumpPiece(top, null));
    pieces.Add(new MarkPiece(loop.ExitLabel));
  }

  /// <summary><c>Select Case</c> flattened: the value evaluated once, a jump to the first case whose clauses hold, else to <c>Case Else</c> or past the end.</summary>
  private void Cases(ISwitchOperation choice, List<Piece> pieces) {
    var start = new List<IrStmt>();
    var value = this.Value(choice.Value);
    var held = new IrLocalRef(this.Temporary(value.Type, start, value));
    pieces.Add(new CodePiece(start, choice.ExitLabel));
    var labels = choice.Cases.Select(_ => new object()).ToList();
    object otherwise = choice.ExitLabel;
    for (var index = 0; index < choice.Cases.Length; ++index) {
      var clauses = choice.Cases[index].Clauses;
      if (clauses.Any(clause => clause is IDefaultCaseClauseOperation)) {
        otherwise = labels[index];
        continue;
      }

      var test = clauses.Select(clause => this.Clause(clause, held, choice.Value.Type!)).Aggregate((left, right) => new IrBinary(IrBinaryOp.LogicalOr, left, right));
      pieces.Add(new JumpPiece(labels[index], test));
    }

    pieces.Add(new JumpPiece(otherwise, null));
    for (var index = 0; index < choice.Cases.Length; ++index) {
      pieces.Add(new MarkPiece(labels[index]));
      foreach (var statement in choice.Cases[index].Body)
        this.Flatten(statement, pieces);
      pieces.Add(new JumpPiece(choice.ExitLabel, null));
    }

    pieces.Add(new MarkPiece(choice.ExitLabel));
  }

}
