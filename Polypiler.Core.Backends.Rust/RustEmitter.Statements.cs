using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Statements one after the other into a list; a nested block's statements join the list, as every
    /// variable has a name of its own. What follows a statement that never ends - <c>return</c>,
    /// <c>break</c>, <c>continue</c>, a trap - is never written, which Rust would warn about.
    /// </summary>
    private void Statements(IReadOnlyList<IrStmt> statements, List<RsStatement> into) {
      for (var index = 0; index < statements.Count; ++index) {
        if (this.ValueLoop(statements, index) is { } valued) {
          into.Add(valued);
          return;
        }

        if (this.InclusiveLoop(statements, index) is { } inclusive) {
          into.Add(inclusive);
          index += 2;
          continue;
        }

        if (this.CountedLoop(statements, index) is { } counted) {
          into.Add(counted);
          ++index;
          continue;
        }

        if (this.ParallelLoop(statements, index, out var consumed) is { } parallel) {
          into.Add(parallel);
          index += consumed - 1;
          continue;
        }

        if (this.SplitViews(statements, index) is { } split) {
          into.Add(split);
          ++index;
          continue;
        }

        if (this.Statement(statements[index], into))
          return;
      }
    }

    private RsBlock Block(IReadOnlyList<IrStmt> statements) {
      var into = new List<RsStatement>();
      this.Statements(statements, into);
      return new RsBlock(into, null);
    }

    /// <summary>Writes one statement; <see langword="true"/> when control never goes on after it.</summary>
    private bool Statement(IrStmt statement, List<RsStatement> into) {
      var outer = this._statement;
      this._statement = statement;
      try {
        return this.StatementOf(statement, into);
      } finally {
        this._statement = outer;
      }
    }

    /// <summary>The statement of the IR being written, the innermost one, for the analyses that ask where in the function a read stands.</summary>
    private IrStmt? _statement;

    private bool StatementOf(IrStmt statement, List<RsStatement> into) {
      switch (statement) {
        case IrBlock block: {
          var before = into.Count;
          this.Statements(block.Statements, into);
          return into.Count > before && Diverges(into[^1]);
        }
        case IrExprStmt { Expression: IrCollection { Op: IrCollectionOp.ListSet } set }:
          this.ListSet(set, into);
          return false;
        case IrExprStmt { Expression: IrCollection operation } when this.Expression(operation) is RsBlockExpression { Block: { Tail: { } change } settled }:
          into.AddRange(settled.Statements);
          into.Add(new RsExpressionStatement(change));
          return false;
        case IrDeclare declare when this._nested.ContainsKey(declare.Local):
          return false;
        case IrDeclare { Local.Type.IsPointer: true } pointer:
          this.PointerDeclaration(pointer, into);
          return false;
        case IrAssign { Local.Type.IsPointer: true } pointer:
          into.Add(new RsExpressionStatement(new RsAssign(new RsPath(this._locals[pointer.Local]) { Type = IrType.I64 }, "=", this.PointerIndex(pointer.Value))));
          return false;
        case IrAssign assign when this._nested.ContainsKey(assign.Local):
          return false;
        case IrDeclare declare when this._shared.Contains(declare.Local):
          into.Add(this.SharedDeclaration(declare.Local, declare.Initializer is null ? null : this.Expression(declare.Initializer)));
          return false;
        case IrAssign assign when this._shared.Contains(assign.Local):
          into.Add(new RsExpressionStatement(this.SharedWrite(assign.Local, this.Expression(assign.Value))));
          return false;
        case IrExprStmt { Expression: IrStep { Target: IrLocalRef { Local: var stepped } } step } when this._shared.Contains(stepped):
          into.Add(new RsExpressionStatement(this.SharedStep(stepped, step, true)));
          return false;
        case IrReturn result when this._closureResults.Count > 0:
          into.Add(new RsExpressionStatement(new RsReturn(result.Value is null ? null : this.Argument(result.Value))));
          return true;
        case IrDeclare { Local.Type.IsLazySequence: true } declare:
          this.SequenceDeclaration(declare, into);
          return false;
        case IrDeclare declare:
          into.Add(new RsLet(this._locals[declare.Local], this._mutable.Contains(declare) || this.IsWrittenVector(declare.Local) || this._fieldWritten.Contains(declare.Local) || this.IsChangedCollection(declare.Local) && !this.IsBorrowedCollection(declare.Local)
            || this.IsFieldAlias(declare.Local) && this._arrayFacts.MutableLocals.Contains(declare.Local) && this._arrayFacts.FieldAliases[declare.Local].Target.Type is { IsObject: true } owner && !this.IsPlain(owner)
            || this.IsMutatedObject(declare.Local), this.LocalType(declare.Local),
            declare.Initializer switch {
              null => null,
              var value when declare.Local.Type.IsArray => this.ArrayInitial(declare.Local, value),
              var value when declare.Local.Type.IsString && this.IsNullableString(declare.Local) => this.NullableStringValue(value),
              var value when declare.Local.Type.IsString => this.StringValue(value),
              var value when IsError(declare.Local.Type) => this.ErrorValue(value),
              var value when declare.Local.Type.IsObject => this.ObjectValue(value, this.MayBeNull(declare.Local)),
              var value when declare.Local.Type.IsView => this.ViewSource(value, this.WritesView(declare.Local)),
              var value when declare.Local.Type.IsFunction => this.FunctionValue(value),
              var value when declare.Local.Type.IsCollection && this.IsBorrowedCollection(declare.Local) => this.BorrowedCollection(declare.Local, value),
              var value when declare.Local.Type.IsCollection => this.CollectionValue(value),
              var value => this.Expression(value)
            }));
          return false;
        case IrStore { Array: IrGlobalRef { Global: var locked } } store when IsLocked(locked):
          this.LockedStore(store, locked, into);
          return false;
        case IrStore store:
          this.Store(store, into);
          return false;
        case IrGlobalAssign assign when IsThreadLocalType(assign.Global.Type):
          this.ThreadLocalAssign(assign, into);
          return false;
        case IrGlobalAssign assign when IsLocked(assign.Global):
          this.LockedAssign(assign, into);
          return false;
        case IrExprStmt { Expression: IrStep { Target: IrArrayLoad { Array: IrGlobalRef { Global: var steppedGlobal } } load } step } when IsLocked(steppedGlobal):
          into.Add(new RsExpressionStatement(this.LockedStep(step, load, steppedGlobal, true, into)));
          return false;
        case IrFieldStore store when store.Target.Type.IsStruct:
          this.FieldStore(store, into);
          return false;
        case IrFieldStore store when store.Target.Type.IsObject:
          this.ObjectFieldStore(store, into);
          return false;
        case IrGlobalAssign assign:
          into.Add(new RsExpressionStatement(this.GlobalStore(assign)));
          return false;
        case IrExprStmt { Expression: IrStep { Target: IrArrayLoad load } step }:
          into.AddRange(((RsBlockExpression)this.ElementStep(load, step, true)).Block.Statements);
          return false;
        case IrExprStmt { Expression: IrIntrinsic { Op: IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig } store }:
          into.AddRange(((RsBlockExpression)this.EndianStore(store)).Block.Statements);
          return false;
        case IrAssign { Local.Type.IsString: true } assign when !this.IsNullableString(assign.Local) && this.Appended(assign.Local, assign.Value) is { } appended:
          into.AddRange(appended);
          return false;
        case IrAssign assign:
          into.Add(new RsExpressionStatement(this.Assignment(assign.Local, assign.Value)));
          return false;
        case IrExprStmt { Expression: IrStep { Target: IrLocalRef target } step }:
          into.Add(new RsExpressionStatement(this.StepAssignment(target, step)));
          return false;
        case IrExprStmt expression: {
          var written = this.Expression(expression.Expression);
          // A call that propagates inside a try body, its value unused: the error alone, if let Err(error) = f(x) { break 't0 error; }.
          if (written is RsMatch { Arms: [{ Patterns: ["Ok(value)"] }, { Patterns: ["Err(error)"] } failure] } propagated && expression.Expression.Type != IrType.Void)
            written = new RsIf(new RsLetCondition("Err(error)", propagated.Scrutinee), new RsBlock([new RsExpressionStatement(failure.Body)], null), null);
          into.Add(new RsExpressionStatement(written));
          return Diverges(into[^1]);
        }
        case IrIf choice:
          into.Add(new RsExpressionStatement(this.If(choice)));
          return false;
        case IrForEachCollection loop:
          this.ForEachCollection(loop, into);
          return false;
        case IrWhile loop when loop.Body.Statements.Count > 0 && loop.Body.Statements[^1] is IrReturn or IrThrow
          && !ContinuesLoop(loop.Body.Statements) && !BreaksLoop(loop.Body.Statements):
          // A loop whose first pass always leaves the function is an if (clippy's never_loop).
          into.Add(new RsExpressionStatement(this.If(new IrIf(loop.Condition, loop.Body, null))));
          return false;
        case IrWhile { Condition: IrConst { Value: not 0 } } loop when loop.Body.Statements.Count > 0 && loop.Body.Statements[^1] is IrBreak
          && !ContinuesLoop(loop.Body.Statements) && this.Has(RustCapabilities.LabeledBlock):
          // A loop whose first pass always leaves it is a block, left by its breaks (clippy's never_loop).
          into.Add(new RsExpressionStatement(this.OncePass(loop.Body.Statements.Take(loop.Body.Statements.Count - 1).ToList())));
          return false;
        case IrWhile loop:
          into.Add(new RsExpressionStatement(this.Loop(loop.Condition, loop.Body.Statements, null)));
          return false;
        case IrFor loop:
          into.Add(new RsExpressionStatement(this.Loop(loop.Condition, loop.Body.Statements, loop.Step.Statements)));
          return false;
        case IrDoWhile loop:
          into.Add(new RsExpressionStatement(this.DoWhile(loop)));
          return false;
        case IrSwitch choice:
          into.AddRange(this.Switch(choice));
          // A match every arm of which leaves - over every variant of a closed enum - is never left at its end.
          return into.Count > 0 && into[^1] is RsExpressionStatement { Expression: RsMatch match }
            && match.Arms.All(arm => arm.Body is RsBlockExpression { Block: { Tail: null, Statements: [.., var last] } } && Diverges(last));
        case IrBreak:
          into.Add(new RsExpressionStatement(this.Break()));
          return true;
        case IrContinue:
          into.AddRange(this.Continue());
          return true;
        case IrReturn { Value: { } thrower } result when this._tries.Count > 0 && IrTree.Nodes(thrower).Any(node => node is IrThrowExpression thrown && IsCatchable(thrown.Exception.Type)):
          // A value that may throw is bound before the return, which may not hold what leaves (clippy's diverging_sub_expression).
          var held = new IrLocal("$" + this._labels++ + ":returned", thrower.Type);
          this._locals[held] = this.Temporary("value");
          this.Statement(new IrDeclare(held, thrower), into);
          return this.Statement(result with { Value = new IrLocalRef(held) }, into);
        case IrReturn result:
          into.Add(new RsExpressionStatement(this.Leaving(result.Value switch {
            null => null,
            { Type.IsArray: true } value => this.ArrayResult(this._function, value),
            { Type.IsString: true } value when this._stringNulls.ReturnsNull(this._function.Name) => this.NullableStringValue(value),
            { Type.IsString: true } value => this.StringValue(value, true),
            IrLocalRef { Type.IsBigInt: true } value when !this._shared.Contains(value.Local) => new RsPath(this._locals[value.Local]) { Type = IrType.BigInt },
            { Type.IsObject: true } value => this.ObjectValue(value, this._nullness.ReturnsNull(this._function.Name)),
            { Type.IsFunction: true } value => this.FunctionValue(value),
            { Type.IsCollection: true } value => this.CollectionValue(value),
            var value => this.Expression(value)
          })));
          return true;
        case IrRethrow:
          into.Add(new RsExpressionStatement(this._caught.Count == 0 ? this.Refuse("a rethrow outside a catch clause") : this.Escape(this.Cloned(new RsReference(false, new RsPath(this._caught[^1]))))));
          return true;
        case IrThrow thrown: {
          var written = this.Throw(thrown.Exception);
          // The panic of an unhandled exception after the statements that make its message.
          if (written is RsBlockExpression { Block: { Tail: RsMacro panic } prepared }) {
            into.AddRange(prepared.Statements);
            written = panic;
          }

          into.Add(new RsExpressionStatement(written));
          return true;
        }
        case IrForEachSequence loop:
          this.ForEachSequence(loop, into);
          return false;
        case IrTry attempt:
          this.Try(attempt, into);
          return into.Count > 0 && Diverges(into[^1]);
        default:
          into.Add(new RsExpressionStatement(this.Refuse(statement.GetType().Name[2..])));
          return false;
      }
    }

    /// <summary>Whether a statement ends the path it is on: a <c>return</c>, <c>break</c>, <c>continue</c> or a panic.</summary>
    internal static bool Diverges(RsStatement statement)
      => statement is RsExpressionStatement { Expression: RsReturn or RsBreak or RsContinue or RsMacro { Name: "panic" or "unreachable" } or RsCall { Callee: RsPath { Name: "ply_unhandled" } } };

    /// <summary>A <c>return</c>: inside the body of a <c>finally</c> the break out of it, in a function that propagates <c>return Ok(v)</c>.</summary>
    private RsExpression Leaving(RsExpression? value) => this.FinallyReturn(value) ?? this.Returned(value);

    /// <summary>
    /// An assignment; where the value is an operator on the variable itself that Rust has a compound
    /// form for and that is not an integer's wrapping arithmetic - a bitwise operator, or arithmetic on
    /// floating-point numbers - the compound assignment <c>x ^= v</c>, as clippy's <c>assign_op_pattern</c> asks.
    /// </summary>
    private RsExpression Assignment(IrLocal local, IrExpr value) {
      var place = new RsPath(this._locals[local]) { Type = local.Type };
      if (local.Type.IsEnum || local.Type.IsBigInt)
        return new RsAssign(place, "=", this.Expression(value));
      if (local.Type.IsArray)
        return new RsAssign(place, "=", this.ArrayInitial(local, value));
      if (local.Type.IsString)
        return new RsAssign(place, "=", this.IsNullableString(local) ? this.NullableStringValue(value) : this.StringValue(value));
      if (local.Type.IsObject)
        return new RsAssign(place, "=", this.ObjectValue(value, this.MayBeNull(local)));
      if (local.Type.IsFunction)
        return new RsAssign(place, "=", this.FunctionValue(value));
      if (local.Type.IsCollection)
        return new RsAssign(place, "=", this.CollectionValue(value));
      if (value is IrBinary { Left: IrLocalRef self } binary && self.Local == local && Compound(binary) is { } op)
        return new RsAssign(place, op + "=", this.Operand(this.Expression(binary.Right), 2));
      if (value is IrBinary { Right: IrLocalRef other } swapped && other.Local == local && Compound(swapped) is { } bitwise && bitwise is "&" or "|" or "^")
        return new RsAssign(place, bitwise + "=", this.Operand(this.Expression(swapped.Left), 2));

      return new RsAssign(place, "=", this.Expression(value));
    }

    /// <summary>The operator of a compound assignment for a binary node, or <see langword="null"/>.</summary>
    private static string? Compound(IrBinary binary) => binary.Op switch {
      IrBinaryOp.And => "&",
      IrBinaryOp.Or => "|",
      IrBinaryOp.Xor => "^",
      IrBinaryOp.Add when binary.Type.IsFloat => "+",
      IrBinaryOp.Subtract when binary.Type.IsFloat => "-",
      IrBinaryOp.Multiply when binary.Type.IsFloat => "*",
      IrBinaryOp.Divide when binary.Type.IsFloat || binary.Right is IrConst { Value: not 0 and not -1 } => "/",
      IrBinaryOp.Remainder when binary.Type.IsFloat || binary.Right is IrConst { Value: not 0 and not -1 } => "%",
      _ => null
    };

    /// <summary>A step of a variable as a statement: <c>x = x.wrapping_add(1)</c>, <c>x += 1.0</c>.</summary>
    private RsExpression StepAssignment(IrLocalRef target, IrStep step) {
      var place = new RsPath(this._locals[target.Local]) { Type = target.Type };
      if (target.Type.IsFloat)
        return new RsAssign(place, step.Increment ? "+=" : "-=", Literal(new IrConst(target.Type, IrOperators.Bits(target.Type, 1.0))));

      return new RsAssign(place, "=", this.Stepped(place, step));
    }

    /// <summary>The value of a variable after a step: wrapping, or trapping with the overflow.</summary>
    private RsExpression Stepped(RsExpression place, IrStep step) {
      var one = new RsLiteral("1") { Type = place.Type };
      if (place.Type!.IsFloat)
        return new RsBinary(place, step.Increment ? "+" : "-", Literal(new IrConst(place.Type, IrOperators.Bits(place.Type, 1.0)))) { Type = place.Type };

      if (!step.Trap)
        return new RsMethodCall(place, step.Increment ? "wrapping_add" : "wrapping_sub", [one]) { Type = place.Type };

      return this.Checked(new RsMethodCall(place, step.Increment ? "checked_add" : "checked_sub", [one]) { Type = place.Type }, IrTrap.IntegerOverflow);
    }

    /// <summary><c>if</c>; an <c>else</c> that is one <c>if</c> becomes <c>else if</c>; an empty branch is turned around.</summary>
    private RsIf If(IrIf choice) {
      var then = this.Block(choice.Then.Statements);
      var otherwise = choice.Else is null ? null : this.Block(choice.Else.Statements);
      var condition = this.Expression(choice.Condition);
      if (then.Statements.Count == 0 && otherwise is { Statements.Count: > 0 })
        return Tidy.Collapsed(new RsIf(this.Not(choice.Condition), otherwise, null));

      RsExpression? elseNode = otherwise switch {
        null or { Statements.Count: 0 } => null,
        { Statements: [RsExpressionStatement { Expression: RsIf nested }], Tail: null } => nested,
        _ => new RsBlockExpression(otherwise)
      };
      return Tidy.Collapsed(new RsIf(condition, then, elseNode));
    }

    /// <summary>
    /// A loop that tests before every pass: <c>while c { … }</c>, or <c>loop { … }</c> when the
    /// condition is the constant <c>true</c>. The step of a <c>for</c> follows the body, and every
    /// <c>continue</c> of the loop runs it before it goes on.
    /// </summary>
    private RsExpression Loop(IrExpr condition, IReadOnlyList<IrStmt> body, IReadOnlyList<IrStmt>? step, IrExpr? value = null) {
      var label = this.NewLabel("l");
      var test = condition is IrConst { Value: not 0 } ? null : this.Expression(condition);
      var stepped = step is null ? null : this.Block(step).Statements;
      var loop = new Breakable(BreakableKind.Loop, label, stepped) { Value = value };
      this._breakables.Add(loop);
      var block = this.Block(body);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      if (stepped is not null && !(block.Statements.Count > 0 && Diverges(block.Statements[^1])))
        block = block with { Statements = [.. block.Statements, .. stepped] };

      var name = loop.Used ? label : null;
      return test is null ? new RsLoop(name, block) : new RsWhile(name, test, block);
    }

    /// <summary>
    /// A loop that tests after every pass: <c>loop { …; if !c { break; } }</c>. Where the body has a
    /// <c>continue</c>, which goes on to the test, the body is a labelled block the <c>continue</c> leaves.
    /// </summary>
    private RsExpression DoWhile(IrDoWhile loop) {
      var label = this.NewLabel("l");
      var outer = new Breakable(BreakableKind.Loop, label);
      var inner = new Breakable(BreakableKind.ContinueBlock, this.NewLabel("c"));
      this._breakables.Add(outer);
      this._breakables.Add(inner);
      var body = this.Block(loop.Body.Statements);
      this._breakables.RemoveRange(this._breakables.Count - 2, 2);
      var exit = new RsExpressionStatement(new RsIf(this.Not(loop.Condition), new RsBlock([new RsExpressionStatement(new RsBreak(null, null))], null), null));
      if (!inner.Used)
        return new RsLoop(outer.Used ? label : null, new RsBlock([.. body.Statements, exit], null));

      if (!this.Has(RustCapabilities.LabeledBlock))
        return this.Refuse("continue in a do-while loop (labelled blocks, Rust 1.65)");

      // Inside a labelled block every break and continue of the loop names it.
      var labelled = new RsExpressionStatement(new RsLabeledBlock(inner.Label, body));
      return new RsLoop(label, new RsBlock([labelled, exit with { Expression = new RsIf(this.Not(loop.Condition), new RsBlock([new RsExpressionStatement(new RsBreak(label, null))], null), null) }], null));
    }

    /// <summary>
    /// The body of a loop that runs once, its last <c>break</c> left out: <c>'b: { …; break 'b; … }</c> where a
    /// <c>break</c> leaves it early, a plain block where none does.
    /// </summary>
    private RsExpression OncePass(IReadOnlyList<IrStmt> body) {
      var block = new Breakable(BreakableKind.Switch, this.NewLabel("b"));
      this._breakables.Add(block);
      var written = this.Block(body);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      return block.Used ? new RsLabeledBlock(block.Label, written) : new RsBlockExpression(written);
    }

    /// <summary><c>break</c> of the innermost loop or switch, labelled where a labelled block stands between.</summary>
    private RsExpression Break() {
      var target = this._breakables.LastOrDefault(breakable => breakable.Kind != BreakableKind.ContinueBlock);
      if (target is null)
        return this.Refuse("break outside a loop");

      // A switch is left through its labelled block; a loop by its label where a switch or a labelled
      // block stands between, since Rust refuses an unlabelled break inside a labelled block (E0695).
      if (this._tries.Count > 0 && this._breakables.IndexOf(target) < this._tries[^1].Breakables)
        return this.Refuse("a break out of a try");

      var crossed = this._breakables.Count - 1 > this._breakables.IndexOf(target);
      var value = target.Value is null ? null : this.Expression(target.Value);
      if (target.Kind == BreakableKind.Loop && !crossed)
        return new RsBreak(null, value);

      target.Used = true;
      return new RsBreak(target.Label, value);
    }

    /// <summary>
    /// A loop without a condition whose exits are <c>break</c>s of it, followed by the <c>return</c>
    /// of a scalar, as the value of that return: <c>loop { …; break p; … }</c>. Nothing runs between a
    /// <c>break</c> and the return, so each <c>break</c> gives what the return would read. Not inside a
    /// <c>try</c> or a closure, nor in a function that propagates an exception, whose returns are
    /// written otherwise; <see langword="null"/> for any other pair of statements.
    /// </summary>
    private RsStatement? ValueLoop(IReadOnlyList<IrStmt> statements, int index) {
      if (index + 1 >= statements.Count || statements[index] is not IrWhile { Condition: IrConst { Value: not 0 } } loop
        || statements[index + 1] is not IrReturn { Value: { } value } || TypeName(value.Type) is null || !BreaksLoop(loop.Body.Statements)
        || this._tries.Count > 0 || this._closureResults.Count > 0 || this._throwing.Contains(this._function.Name))
        return null;

      return new RsExpressionStatement(new RsReturn(this.Loop(loop.Condition, loop.Body.Statements, null, value)));
    }

    /// <summary>
    /// A loop over an inclusive range as the Rust frontend imports it, written back as
    /// <c>for i in a..=b</c>: a flag that the range is done, true at once where the start is above the
    /// end; a counter at the start; and a loop while the flag is false whose step first sets the flag
    /// where the counter has reached the end, <c>(end as u64 - i as u64) &lt; 1</c>, then moves the
    /// counter on modulo its width - so that the counter never overflows at the end of its type, which
    /// Rust's <c>RangeInclusive</c> does not either. The bounds are constants or variables the body
    /// does not assign; the body assigns neither the flag nor the counter, has no <c>continue</c> where
    /// the loop is a <c>while</c>, and nothing after the loop names either. <see langword="null"/> for
    /// any other three statements.
    /// </summary>
    private RsStatement? InclusiveLoop(IReadOnlyList<IrStmt> statements, int index) {
      if (index + 2 >= statements.Count || statements[index] is not IrDeclare { Initializer: { } empty } flag || flag.Local.Type != IrType.Bool
        || statements[index + 1] is not IrDeclare { Initializer: { } start, Local: { Type.IsInteger: true } local })
        return null;

      List<IrStmt> rest;
      IReadOnlyList<IrStmt> step;
      switch (statements[index + 2]) {
        case IrWhile { Condition: IrUnary { Op: IrUnaryOp.LogicalNot, Operand: IrLocalRef tested }, Body.Statements: [.., _, _] } loop when tested.Local == flag.Local && !ContinuesLoop(loop.Body.Statements):
          (rest, step) = ([.. loop.Body.Statements.Take(loop.Body.Statements.Count - 2)], [.. loop.Body.Statements.Skip(loop.Body.Statements.Count - 2)]);
          break;
        case IrFor { Condition: IrUnary { Op: IrUnaryOp.LogicalNot, Operand: IrLocalRef tested }, Step.Statements: [_, _] } loop when tested.Local == flag.Local:
          (rest, step) = ([.. loop.Body.Statements], loop.Step.Statements);
          break;
        default:
          return null;
      }

      if (step[0] is not IrAssign { Value: IrBinary { Op: IrBinaryOp.Less, Left: IrBinary { Op: IrBinaryOp.Subtract, Left: var reached, Right: var current }, Right: IrConst { Value: 1 } } } last
        || last.Local != flag.Local || !IsCounter(current, local)
        || step[1] is not IrAssign advance || Moved(advance.Value) is not { } moved
        || advance.Local != local || !IsCounter(moved, local) || Bound(Extended(reached), local.Type) is not { } end || Bound(start, local.Type) is not { } first)
        return null;

      if (!EmptyWhen(empty, first, end) || this._shared.Contains(local) || this._shared.Contains(flag.Local) || Assigns(rest, local) || Assigns(rest, flag.Local)
        || new[] { first, end }.Any(bound => bound is IrLocalRef { Local: var variable } && (Assigns(rest, variable) || this._shared.Contains(variable)))
        || rest.Any(statement => Mentions(statement, flag.Local)) || statements.Skip(index + 3).Any(after => Mentions(after, local) || Mentions(after, flag.Local)))
        return null;

      // A first statement that only copies the counter into a variable of its own names the range's element.
      var pattern = rest.Any(statement => Mentions(statement, local)) ? this._locals[local] : "_";
      if (rest is [IrDeclare { Initializer: IrLocalRef { Local: var copied } } named, ..] && copied == local && named.Local.Type == local.Type
        && !Assigns(rest, named.Local) && !this._shared.Contains(named.Local) && !rest.Skip(1).Any(statement => Mentions(statement, local))) {
        pattern = this._locals[named.Local];
        rest = [.. rest.Skip(1)];
      }

      var label = this.NewLabel("l");
      var breakable = new Breakable(BreakableKind.Loop, label);
      this._breakables.Add(breakable);
      var block = this.Block(rest);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      var from = this.Expression(first);
      var to = this.Expression(end);
      // A range of two literals is typed by its end, 0..=255u8.
      if (from is RsLiteral && to is RsLiteral)
        to = Suffixed(to);

      return new RsExpressionStatement(new RsFor(breakable.Used ? label : null, pattern, new RsRange(this.Operand(from, 3), this.Operand(to, 3), true), block));
    }

    /// <summary>
    /// What a range loop's step adds one to modulo 2^64 and wraps back into the counter's type,
    /// <c>(i as u64 + 1) as u8</c>, or adds one to in a 64-bit counter's own type; <see langword="null"/> for any other value.
    /// </summary>
    private static IrExpr? Moved(IrExpr value) => value switch {
      IrConvert { Contract: IrConversion.Wrap, Operand: IrBinary { Op: IrBinaryOp.Add, Left: var moved, Right: IrConst { Value: 1 } } } => moved,
      IrBinary { Op: IrBinaryOp.Add, Left: var moved, Right: IrConst { Value: 1 } } => moved,
      _ => null
    };

    /// <summary>Whether an operand of a range loop's step is its counter, maybe extended to a wider type.</summary>
    private static bool IsCounter(IrExpr operand, IrLocal counter) => Extended(operand) is IrLocalRef { Local: var local } && local == counter;

    /// <summary>
    /// An operand without the conversions that extend it to a type at least as wide, which a step that
    /// subtracts or adds modulo 2^64 sees the same value through: <c>end as u64</c> as <c>end</c>.
    /// </summary>
    private static IrExpr Extended(IrExpr operand) {
      while (operand is IrConvert { Operand: { Type.IsInteger: true } inner } convert && convert.Type.IsInteger && convert.Type.Bits >= inner.Type.Bits)
        operand = inner;

      return operand;
    }

    /// <summary>A bound of a range of the counter's type: a variable of it, or a constant that is one of its values, as a constant of it.</summary>
    private static IrExpr? Bound(IrExpr bound, IrType type) => bound switch {
      IrLocalRef reference when reference.Type == type => reference,
      IrConst constant when constant.Type.IsInteger && IrOperators.Number(constant.Type, constant.Value) is var number
        && number >= IrOperators.Minimum(type) && number <= IrOperators.Maximum(type) => new IrConst(type, (long)number),
      _ => null
    };

    /// <summary>
    /// Whether the first value of a range's flag is that the range is empty, its start above its end:
    /// <c>start &gt; end</c> of the bounds or of their values in a type that holds both, <c>!(start &lt;= end)</c>,
    /// or the constant of two constant bounds.
    /// </summary>
    private static bool EmptyWhen(IrExpr flag, IrExpr start, IrExpr end) => flag switch {
      IrBinary { Op: IrBinaryOp.Greater } above => SameValue(above.Left, start) && SameValue(above.Right, end),
      IrUnary { Op: IrUnaryOp.LogicalNot, Operand: IrBinary { Op: IrBinaryOp.LessOrEqual } within } => SameValue(within.Left, start) && SameValue(within.Right, end),
      IrConst { Value: var value } when start is IrConst low && end is IrConst high => value != 0 == IrOperators.Number(low.Type, low.Value) > IrOperators.Number(high.Type, high.Value),
      _ => false
    };

    /// <summary>Whether an operand is a bound, maybe converted to a type that holds every value of it.</summary>
    private static bool SameValue(IrExpr operand, IrExpr bound) {
      while (operand is IrConvert { Operand: { Type.IsInteger: true } inner } convert && convert.Type.IsInteger && IrOperators.Contains(convert.Type, inner.Type))
        operand = inner;

      return (operand, bound) switch {
        (IrConst a, IrConst b) => IrOperators.Number(a.Type, a.Value) == IrOperators.Number(b.Type, b.Value),
        (IrLocalRef a, IrLocalRef b) => a.Local == b.Local,
        _ => false
      };
    }

    /// <summary>
    /// <c>continue</c> of the innermost loop: after the step of a <c>for</c>; in the body of a
    /// <c>do</c>-<c>while</c> the <c>break</c> out of its labelled body, which goes on to the test.
    /// </summary>
    private IEnumerable<RsStatement> Continue() {
      var index = this._breakables.FindLastIndex(breakable => breakable.Kind is BreakableKind.Loop or BreakableKind.ContinueBlock);
      if (index < 0)
        return [new RsExpressionStatement(this.Refuse("continue outside a loop"))];

      var target = this._breakables[index];
      if (this._tries.Count > 0 && index < this._tries[^1].Breakables)
        return [new RsExpressionStatement(this.Refuse("a continue out of a try"))];

      if (target.Kind == BreakableKind.ContinueBlock) {
        target.Used = true;
        return [new RsExpressionStatement(new RsBreak(target.Label, null))];
      }

      var crossed = this._breakables.Skip(index + 1).Any();
      if (crossed)
        target.Used = true;

      return [.. target.Step ?? [], new RsExpressionStatement(new RsContinue(crossed ? target.Label : null))];
    }

    /// <summary>
    /// A switch as a <c>match</c>: the labels of a section form an or-pattern, three or more
    /// consecutive ones a range, the default section the <c>_</c> arm, which is left out where the
    /// sections cover every value of an 8-bit selector. A section's last <c>break</c> is dropped,
    /// since an arm never falls through; a <c>break</c> elsewhere in it leaves a labelled block around
    /// the <c>match</c>. One section without a default is an <c>if</c>, only a default its body alone.
    /// </summary>
    private IEnumerable<RsStatement> Switch(IrSwitch choice) {
      // A switch on an enum is one on its integer, whose labels are the members' values; on a closed enum one on its variants.
      var closed = this.IsClosedEnum(choice.Value.Type) ? choice.Value.Type.EnumType : null;
      var value = closed is not null ? this.Expression(choice.Value) : choice.Value.Type.IsEnum ? this.Inner(choice.Value) : this.Expression(choice.Value);
      var type = choice.Value.Type.IsEnum ? choice.Value.Type.EnumType!.Underlying : choice.Value.Type;
      var label = this.NewLabel("s");
      var breakable = new Breakable(BreakableKind.Switch, label);
      this._breakables.Add(breakable);
      var arms = new List<RsMatchArm>();
      RsBlock? fallback = null;
      foreach (var section in choice.Sections) {
        var statements = section.Body.Statements;
        if (statements.Count > 0 && statements[^1] is IrBreak)
          statements = [.. statements.Take(statements.Count - 1)];

        var body = this.Block(statements);
        if (section.IsDefault)
          fallback = body;
        else
          arms.Add(new RsMatchArm(closed is not null ? this.VariantPatterns(closed, section.Labels) : Patterns(section.Labels, type), new RsBlockExpression(body)));
      }

      this._breakables.RemoveAt(this._breakables.Count - 1);
      var labelled = choice.Sections.Where(section => !section.IsDefault).Sum(section => section.Labels.Count);
      // A match over every variant of a closed enum has no _ arm; its default section is never reached.
      var exhaustive = closed is not null ? labelled == closed.Members.Count : type.Bits == 8 && labelled == 256;
      if (!exhaustive)
        arms.Add(new RsMatchArm(["_"], new RsBlockExpression(fallback ?? RsBlock.Empty)));

      RsExpression result;
      if (arms.Count == 1 && arms[0].Patterns is ["_"] && !breakable.Used) {
        var block = ((RsBlockExpression)arms[0].Body).Block;
        // The selector is evaluated for what it does, then the default section runs.
        return choice.Value is IrLocalRef or IrConst ? block.Statements : [new RsLet("_", false, null, value), .. block.Statements];
      }

      if (arms.Count == 2 && fallback is null && !breakable.Used) {
        var labels = choice.Sections.First(section => !section.IsDefault).Labels;
        var test = labels.Select(item => (RsExpression)this.Binary(value, "==", Literal(new IrConst(type, item)), IrType.Bool))
          .Aggregate((left, right) => this.Binary(left, "||", right, IrType.Bool));
        result = new RsIf(test, ((RsBlockExpression)arms[0].Body).Block, null);
      } else {
        result = new RsMatch(value, arms);
      }

      if (!breakable.Used)
        return [new RsExpressionStatement(result)];

      if (!this.Has(RustCapabilities.LabeledBlock))
        return [new RsExpressionStatement(this.Refuse("break out of a switch section (labelled blocks, Rust 1.65)"))];

      return [new RsExpressionStatement(new RsLabeledBlock(label, new RsBlock([new RsExpressionStatement(result)], null)))];
    }

    /// <summary>The patterns of a section's labels in their order: three or more consecutive values as one range.</summary>
    private static IReadOnlyList<string> Patterns(IReadOnlyList<long> labels, IrType type) {
      var values = labels.Select(label => IrOperators.Number(type, label)).ToList();
      var patterns = new List<string>();
      for (var index = 0; index < values.Count;) {
        var end = index;
        while (end + 1 < values.Count && values[end + 1] == values[end] + 1)
          ++end;

        if (end - index >= 2) {
          patterns.Add($"{Pattern(values[index], type)}..={Pattern(values[end], type)}");
          index = end + 1;
        } else {
          patterns.Add(Pattern(values[index], type));
          ++index;
        }
      }

      return patterns;
    }

    private static string Pattern(Int128 value, IrType type) => ((RsExpression)Literal(new IrConst(type, (long)value))) switch {
      RsLiteral literal => literal.Text,
      RsPath path => path.Name,
      _ => throw new InvalidOperationException("A label is an integer.")
    };

    /// <summary>
    /// A counted loop as <c>for i in a..b</c>: the declaration of an integer variable and a
    /// <c>while</c> that tests it against a bound with <c>&lt;</c> and whose body ends with the step by
    /// one, or a <c>for</c> whose step is that step alone. The bound is a constant, a variable the body
    /// does not assign or the length of an array the body does not assign, the body assigns the
    /// counter nowhere else and, where it is a <c>while</c>, has no <c>continue</c> of the loop, and
    /// nothing after the loop reads the counter, which the <c>for</c> keeps to itself. A loop from 0 to
    /// the length of an array that reads only its elements with the counter and writes none of them is
    /// an element loop (<see cref="ElementLoop"/>). <see langword="null"/> for any other pair of statements.
    /// </summary>
    private RsStatement? CountedLoop(IReadOnlyList<IrStmt> statements, int index) {
      if (index + 1 >= statements.Count || statements[index] is not IrDeclare { Initializer: { } start } declare || this.Counted(declare, statements[index + 1], true) is not ( { } bound, { } rest, var inclusive))
        return null;

      // i <= b is a range a..=b only where b is a constant below the largest value: at the largest one the source's loop never ends.
      if (inclusive && (bound is not IrConst { Value: var last } || IrOperators.Number(bound.Type, last) >= IrOperators.Maximum(bound.Type)))
        return null;

      var invariant = bound is IrConst || bound is IrConstRef || (bound is IrLocalRef limit && limit.Local != declare.Local && !Assigns(rest, limit.Local))
        || (bound is IrArrayLength { Array: IrConstRef or IrLocalRef } length && (length.Array is not IrLocalRef array || !Assigns(rest, array.Local)));
      if (!invariant || statements.Skip(index + 2).Any(after => Mentions(after, declare.Local)))
        return null;

      if (!inclusive && start is IrConst { Value: 0 } && bound is IrArrayLength { Array: var iterated } && this.ElementLoop(declare.Local, iterated, rest) is { } elements)
        return elements;

      var label = this.NewLabel("l");
      var breakable = new Breakable(BreakableKind.Loop, label);
      this._breakables.Add(breakable);
      var block = this.Block(rest);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      var from = this.Expression(start);
      var to = this.Expression(bound);
      // A range of two literals is typed by its start, 0u32..10, an inclusive one by its end, 0..=100i32.
      if (from is RsLiteral && to is RsLiteral && inclusive)
        to = Suffixed(to);
      else if (from is RsLiteral && to is RsLiteral)
        from = Suffixed(from);

      var pattern = rest.Any(statement => Mentions(statement, declare.Local)) ? this._locals[declare.Local] : "_";
      return new RsExpressionStatement(new RsFor(breakable.Used ? label : null, pattern, new RsRange(this.Operand(from, 3), this.Operand(to, 3), inclusive), block));
    }

    /// <summary>The element of an array an element loop reads in place of <c>a[i]</c>, by the loop's counter.</summary>
    private readonly Dictionary<IrLocal, string> _elements = [];

    /// <summary>
    /// An element loop, <c>for &amp;x in data { … }</c>: a counted loop from 0 to the length of an array
    /// whose body uses the counter only to read elements of that array and writes none of them. The
    /// element is the variable the body's first statement declares with it, unless the body assigns
    /// that variable; otherwise a binding of its own. <see langword="null"/> for any other loop.
    /// </summary>
    private RsStatement? ElementLoop(IrLocal counter, IrExpr array, List<IrStmt> body) {
      var nodes = body.SelectMany(IrTree.Nodes).ToList();
      var loads = nodes.Count(node => node is IrArrayLoad { Index: IrLocalRef index } load && index.Local == counter && SameArray(load.Array, array));
      var uses = nodes.Count(node => node is IrLocalRef reference && reference.Local == counter);
      if (loads == 0 || loads != uses || body.SelectMany(Walk).Any(statement => statement is IrStore store && SameArray(store.Array, array)
          || statement is IrFieldStore field && ElementArray(field.Target) is { } written && SameArray(written, array))
        || nodes.Any(node => node is IrStep { Target: IrArrayLoad stepped } && SameArray(stepped.Array, array)
          || node is IrIntrinsic { Op: IrIntrinsicOp.ArrayCopy } copy && SameArray(copy.Arguments[2], array)
          || node is IrIntrinsic { Op: IrIntrinsicOp.ArrayFill or IrIntrinsicOp.ArrayClear } fill && SameArray(fill.Arguments[0], array)
          || node is IrCall call && this._arrays.TryGetValue(call.Function, out var callee) && callee.MutableParameters.Any(position => SameArray(call.Arguments[position], array)))
        || array is IrLocalRef { Local: var variable } && Assigns(body, variable))
        return null;

      // Only an element that is a value is copied out of the array by the loop's pattern.
      if (array.Type.Element is not { } elementType || !IsValue(elementType))
        return null;

      string element;
      if (body.Count > 0 && body[0] is IrDeclare { Initializer: IrArrayLoad { Index: IrLocalRef first } } named && first.Local == counter && named.Local.Type == array.Type.Element && !Assigns(body, named.Local) && !this._shared.Contains(named.Local)) {
        element = this._locals[named.Local];
        body = body.Skip(1).ToList();
      } else {
        element = this.Temporary("element");
      }

      this._elements[counter] = element;
      var label = this.NewLabel("l");
      var breakable = new Breakable(BreakableKind.Loop, label);
      this._breakables.Add(breakable);
      var block = this.Block(body);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      this._elements.Remove(counter);
      return new RsExpressionStatement(new RsFor(breakable.Used ? label : null, "&" + element, this.Iterated(array), block));
    }

    /// <summary>Whether two expressions name the same array: one variable or one table.</summary>
    private static bool SameArray(IrExpr first, IrExpr second) => (first, second) switch {
      (IrLocalRef a, IrLocalRef b) => a.Local == b.Local,
      (IrConstRef a, IrConstRef b) => a.Constant == b.Constant,
      _ => false
    };

    /// <summary>Whether statements assign a variable, also by a step.</summary>
    private static bool Assigns(IEnumerable<IrStmt> statements, IrLocal local)
      => statements.SelectMany(Walk).Any(statement => statement is IrAssign assign && assign.Local == local)
        || statements.SelectMany(Walk).SelectMany(ExpressionsOf).SelectMany(Nodes).Any(node => node is IrStep { Target: IrLocalRef target } && target.Local == local);

    /// <summary>Whether statements have a <c>continue</c> of the loop they are the body of, not of a loop inside them.</summary>
    private static bool ContinuesLoop(IEnumerable<IrStmt> statements) => statements.Any(statement => statement switch {
      IrContinue => true,
      IrBlock block => ContinuesLoop(block.Statements),
      IrIf choice => ContinuesLoop(choice.Then.Statements) || (choice.Else is not null && ContinuesLoop(choice.Else.Statements)),
      IrSwitch choice => choice.Sections.Any(section => ContinuesLoop(section.Body.Statements)),
      _ => false
    });

    /// <summary>Whether statements have a <c>break</c> of the loop they are the body of, not of a loop or a switch inside them.</summary>
    private static bool BreaksLoop(IEnumerable<IrStmt> statements) => statements.Any(statement => statement switch {
      IrBreak => true,
      IrBlock block => BreaksLoop(block.Statements),
      IrIf choice => BreaksLoop(choice.Then.Statements) || (choice.Else is not null && BreaksLoop(choice.Else.Statements)),
      _ => false
    });

    /// <summary>Whether a statement names a variable anywhere.</summary>
    private static bool Mentions(IrStmt statement, IrLocal local)
      => Walk(statement).Any(item => item is IrDeclare declare && declare.Local == local || item is IrAssign assign && assign.Local == local)
        || Walk(statement).SelectMany(ExpressionsOf).SelectMany(Nodes).Any(node => node is IrLocalRef reference && reference.Local == local);

    /// <summary>A statement and every statement inside it.</summary>
    private static IEnumerable<IrStmt> Walk(IrStmt statement) {
      yield return statement;
      foreach (var block in IrTree.Blocks(statement).Where(block => !ReferenceEquals(block, statement)))
        foreach (var inner in block.Statements.SelectMany(Walk))
          yield return inner;

      if (statement is IrBlock own)
        foreach (var inner in own.Statements.SelectMany(Walk))
          yield return inner;
    }

    /// <summary>The expressions a statement holds directly.</summary>
    private static IEnumerable<IrExpr> ExpressionsOf(IrStmt statement) => IrTree.Expressions(statement);

    /// <summary>
    /// A throw: of an exception of the module, the exception leaving; of a new exception of the IR's
    /// library, the panic of its trap - under the trap policy <c>result</c> the trap's error, or, where
    /// the module has exceptions, the exception itself. A panic carries no object, so a throw of
    /// anything else cannot be written.
    /// </summary>
    private RsExpression Throw(IrExpr exception) {
      if (IsCatchable(exception.Type) || this.Results && this.PlyErrors && exception is IrNew && IsError(exception.Type))
        return this.Fail(this.ErrorValue(exception));
      if (exception is IrNew { Type.Declaration: IrClassType type } && TrapOf(type) is { } trap)
        return this.Trap(trap);
      if (exception is IrNew { Type.Declaration: IrClassType { IsLibrary: true } library } creation && IsError(creation.Type) && !this.Results)
        return this.Unhandled(library, creation.Arguments);

      return this.Refuse("the throw of an exception that is no new object");
    }

    /// <summary>
    /// A new exception of the library that no trap is, thrown where the crate panics (#1422): the panic
    /// with its message, <c>panic!("{}", String::from_utf16_lossy(&amp;message))</c>, or its class's name
    /// without one - what .NET prints of an unhandled exception, which nothing in the module catches.
    /// </summary>
    private RsExpression Unhandled(IrClassType library, IReadOnlyList<IrExpr> arguments) {
      if (arguments is not [{ Type.IsString: true } message])
        return new RsMacro("panic", [new RsString(library.Name[(library.Name.LastIndexOf('.') + 1)..])]);

      if (message is IrStrConst constant && !HasLoneSurrogate(constant.Value))
        return new RsMacro("panic", [new RsString("{}"), new RsString(constant.Value)]);

      // The message made first, then the panic that prints it.
      var name = this.Temporary("message");
      return new RsBlockExpression(new RsBlock([new RsLet(name, false, null, this.StringValue(message))],
        new RsMacro("panic", [new RsString("{}"), new RsCall(new RsPath("String::from_utf16_lossy"), [new RsReference(false, new RsPath(name))])])));
    }

    /// <summary>The trap whose class an exception class of the library is; the first one where several share it.</summary>
    private static IrTrap? TrapOf(IrClassType type)
      => Enum.GetValues<IrTrap>().Cast<IrTrap?>().FirstOrDefault(trap => IrExceptionClass.Of(trap!.Value) == type);

  }

}
