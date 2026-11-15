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
        if (this.CountedLoop(statements, index) is { } counted) {
          into.Add(counted);
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
      switch (statement) {
        case IrBlock block: {
          var before = into.Count;
          this.Statements(block.Statements, into);
          return into.Count > before && Diverges(into[^1]);
        }
        case IrDeclare declare:
          into.Add(new RsLet(this._locals[declare.Local], this._mutable.Contains(declare), this.Type(declare.Local.Type),
            declare.Initializer is null ? null : this.Expression(declare.Initializer)));
          return false;
        case IrAssign assign:
          into.Add(new RsExpressionStatement(this.Assignment(assign.Local, assign.Value)));
          return false;
        case IrExprStmt { Expression: IrStep { Target: IrLocalRef target } step }:
          into.Add(new RsExpressionStatement(this.StepAssignment(target, step)));
          return false;
        case IrExprStmt expression: {
          var written = this.Expression(expression.Expression);
          into.Add(new RsExpressionStatement(written));
          return Diverges(into[^1]);
        }
        case IrIf choice:
          into.Add(new RsExpressionStatement(this.If(choice)));
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
          return false;
        case IrBreak:
          into.Add(new RsExpressionStatement(this.Break()));
          return true;
        case IrContinue:
          into.AddRange(this.Continue());
          return true;
        case IrReturn result:
          into.Add(new RsExpressionStatement(new RsReturn(result.Value is null ? null : this.Expression(result.Value))));
          return true;
        case IrThrow thrown:
          into.Add(new RsExpressionStatement(this.Throw(thrown.Exception)));
          return true;
        case IrTry:
          if (this._refused.Add("exception handling"))
            this._problems.Add(ExceptionRegionDescriptor.At($"exception region in {this._construct} cannot be emitted under the trap policy 'panic': 'exception handling' is not supported by the Rust backend"));
          return false;
        default:
          into.Add(new RsExpressionStatement(this.Refuse(statement.GetType().Name[2..])));
          return false;
      }
    }

    /// <summary>Whether a statement ends the path it is on: a <c>return</c>, <c>break</c>, <c>continue</c> or a panic.</summary>
    internal static bool Diverges(RsStatement statement)
      => statement is RsExpressionStatement { Expression: RsReturn or RsBreak or RsContinue or RsMacro { Name: "panic" or "unreachable" } };

    /// <summary>
    /// An assignment; where the value is an operator on the variable itself that Rust has a compound
    /// form for and that is not an integer's wrapping arithmetic - a bitwise operator, or arithmetic on
    /// floating-point numbers - the compound assignment <c>x ^= v</c>, as clippy's <c>assign_op_pattern</c> asks.
    /// </summary>
    private RsExpression Assignment(IrLocal local, IrExpr value) {
      var place = new RsPath(this._locals[local]) { Type = local.Type };
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

      return Expect(new RsMethodCall(place, step.Increment ? "checked_add" : "checked_sub", [one]) { Type = place.Type }, IrTrap.IntegerOverflow);
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
    private RsExpression Loop(IrExpr condition, IReadOnlyList<IrStmt> body, IReadOnlyList<IrStmt>? step) {
      var label = this.NewLabel("l");
      var test = condition is IrConst { Value: not 0 } ? null : this.Expression(condition);
      var stepped = step is null ? null : this.Block(step).Statements;
      var loop = new Breakable(BreakableKind.Loop, label, stepped);
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

    /// <summary><c>break</c> of the innermost loop or switch, labelled where a labelled block stands between.</summary>
    private RsExpression Break() {
      var target = this._breakables.LastOrDefault(breakable => breakable.Kind != BreakableKind.ContinueBlock);
      if (target is null)
        return this.Refuse("break outside a loop");

      // A switch is left through its labelled block; a loop by its label where a switch or a labelled
      // block stands between, since Rust refuses an unlabelled break inside a labelled block (E0695).
      var crossed = this._breakables.Count - 1 > this._breakables.IndexOf(target);
      if (target.Kind == BreakableKind.Loop && !crossed)
        return new RsBreak(null, null);

      target.Used = true;
      return new RsBreak(target.Label, null);
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
      var value = this.Expression(choice.Value);
      var type = choice.Value.Type;
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
          arms.Add(new RsMatchArm(Patterns(section.Labels, type), new RsBlockExpression(body)));
      }

      this._breakables.RemoveAt(this._breakables.Count - 1);
      var exhaustive = type.Bits == 8 && choice.Sections.Where(section => !section.IsDefault).Sum(section => section.Labels.Count) == 256;
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
    /// <c>while</c> that tests it against a bound with <c>&lt;</c> and whose body ends with the step by one.
    /// The bound is a constant or a variable the body does not assign, the body assigns the counter
    /// nowhere else and has no <c>continue</c> of the loop, and nothing after the loop reads the counter,
    /// which the <c>for</c> keeps to itself. <see langword="null"/> for any other pair of statements.
    /// </summary>
    private RsStatement? CountedLoop(IReadOnlyList<IrStmt> statements, int index) {
      if (index + 1 >= statements.Count || statements[index] is not IrDeclare { Initializer: { } start } declare
        || statements[index + 1] is not IrWhile { Condition: IrBinary { Op: IrBinaryOp.Less, Left: IrLocalRef counter, Right: var bound } } loop
        || counter.Local != declare.Local || !counter.Type.IsInteger || loop.Body.Statements.Count == 0)
        return null;

      var body = loop.Body.Statements;
      var stepsByOne = body[^1] switch {
        IrAssign { Value: IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.AddChecked, Left: IrLocalRef self, Right: IrConst { Value: 1 } } } assign => assign.Local == declare.Local && self.Local == declare.Local,
        IrExprStmt { Expression: IrStep { Target: IrLocalRef self, Increment: true } } => self.Local == declare.Local,
        _ => false
      };
      if (!stepsByOne)
        return null;

      var rest = body.Take(body.Count - 1).ToList();
      var invariant = bound is IrConst || bound is IrConstRef || (bound is IrLocalRef limit && limit.Local != declare.Local && !Assigns(rest, limit.Local));
      if (!invariant || Assigns(rest, declare.Local) || ContinuesLoop(rest) || statements.Skip(index + 2).Any(after => Mentions(after, declare.Local)))
        return null;

      var label = this.NewLabel("l");
      var breakable = new Breakable(BreakableKind.Loop, label);
      this._breakables.Add(breakable);
      var block = this.Block(rest);
      this._breakables.RemoveAt(this._breakables.Count - 1);
      var from = this.Expression(start);
      var to = this.Expression(bound);
      if (from is RsLiteral && to is RsLiteral)
        from = Suffixed(from);

      var pattern = rest.Any(statement => Mentions(statement, declare.Local)) ? this._locals[declare.Local] : "_";
      return new RsExpressionStatement(new RsFor(breakable.Used ? label : null, pattern, new RsRange(this.Operand(from, 3), this.Operand(to, 3), false), block));
    }

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

    /// <summary>Whether a statement names a variable anywhere.</summary>
    private static bool Mentions(IrStmt statement, IrLocal local)
      => Walk(statement).Any(item => item is IrDeclare declare && declare.Local == local || item is IrAssign assign && assign.Local == local)
        || Walk(statement).SelectMany(ExpressionsOf).SelectMany(Nodes).Any(node => node is IrLocalRef reference && reference.Local == local);

    /// <summary>A statement and every statement inside it.</summary>
    private static IEnumerable<IrStmt> Walk(IrStmt statement) {
      yield return statement;
      IEnumerable<IrStmt> children = statement switch {
        IrBlock block => block.Statements,
        IrIf choice => [choice.Then, .. choice.Else is null ? [] : new IrStmt[] { choice.Else }],
        IrWhile loop => [loop.Body],
        IrDoWhile loop => [loop.Body],
        IrFor loop => [loop.Body, loop.Step],
        IrSwitch choice => choice.Sections.Select(section => (IrStmt)section.Body),
        IrTry attempt => [attempt.Body, .. attempt.Catches.Select(clause => (IrStmt)clause.Body), .. attempt.Finally is null ? [] : new IrStmt[] { attempt.Finally }],
        _ => []
      };
      foreach (var child in children)
        foreach (var item in Walk(child))
          yield return item;
    }

    /// <summary>The expressions a statement holds directly.</summary>
    private static IEnumerable<IrExpr> ExpressionsOf(IrStmt statement) => statement switch {
      IrDeclare { Initializer: { } initializer } => [initializer],
      IrAssign assign => [assign.Value],
      IrStore store => [store.Array, store.Index, store.Value],
      IrExprStmt expression => [expression.Expression],
      IrIf choice => [choice.Condition],
      IrWhile loop => [loop.Condition],
      IrDoWhile loop => [loop.Condition],
      IrFor loop => [loop.Condition],
      IrSwitch choice => [choice.Value],
      IrReturn { Value: { } value } => [value],
      IrThrow thrown => [thrown.Exception],
      _ => []
    };

    /// <summary>
    /// A throw of a new exception of the IR's library as the panic of its trap; a panic carries no
    /// object, so a throw of anything else, and a handler, cannot be written.
    /// </summary>
    private RsExpression Throw(IrExpr exception) {
      if (exception is IrNew { Type.Declaration: IrClassType type } && TrapOf(type) is { } trap)
        return Panic(trap);

      return this.Refuse("exception handling");
    }

    /// <summary>The trap whose class an exception class of the library is; the first one where several share it.</summary>
    private static IrTrap? TrapOf(IrClassType type)
      => Enum.GetValues<IrTrap>().Cast<IrTrap?>().FirstOrDefault(trap => IrExceptionClass.Of(trap!.Value) == type);

  }

}
