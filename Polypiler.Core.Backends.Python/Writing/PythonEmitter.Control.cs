using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>The longest tail of a section that is copied into both branches of an <c>if</c> to take a <c>break</c> out of the section.</summary>
    private const int CopiedTail = 3;

    /// <summary>
    /// A switch. Python's <c>break</c> leaves the loop around a <c>match</c>, not the <c>match</c>,
    /// so a section's <c>break</c>s are taken out first: the one at its end is dropped, and one in a
    /// branch of an <c>if</c> moves the statements after the <c>if</c> into the other branch. Where
    /// that would copy more than a few statements, the switch is put into a loop of one pass, which
    /// its <c>break</c>s leave. From Python 3.10 the sections are a <c>match</c>: <c>case 1 | 2:</c>,
    /// a run of three or more consecutive labels as <c>case _ if a &lt;= x &lt;= b:</c> after the
    /// literal cases, and the default as <c>case _:</c>; before 3.10 they are an
    /// <c>if</c>/<c>elif</c> chain on the same tests.
    /// </summary>
    private void Switch(IrSwitch selection, List<PyStatement> written) {
      var type = selection.Value.Type;
      PyExpression subject;
      if (selection.Value is IrLocalRef or IrConst) {
        subject = this.Expression(selection.Value);
      } else {
        var temporary = this.Temporary();
        written.Add(new PyAssign(Name(temporary), this.Expression(selection.Value)));
        subject = Name(temporary);
      }

      var bodies = selection.Sections.Select(section => Eliminated(section.Body.Statements)).ToList();
      var wrapped = bodies.Any(body => body is null);
      var frame = new SwitchFrame(wrapped);
      var sections = selection.Sections.Select((section, index) => (
        Labels: section.Labels.Select(label => Big(IrOperators.Number(type, label))).Distinct().Order().ToList(),
        section.IsDefault,
        Body: this.Inside(frame, wrapped ? section.Body.Statements : bodies[index]!))).ToList();
      var cases = sections.Where(section => !section.IsDefault).ToList();
      var fallback = sections.FirstOrDefault(section => section.IsDefault).Body;

      // Without a case the default runs whatever the value; the value, if it is no variable, is computed above.
      List<PyStatement> choice;
      if (cases.Count == 0) {
        choice = fallback ?? [];
      } else if (this.Has(PythonCapabilities.Match)) {
        var literal = cases.Where(section => Runs(section.Labels).All(run => run.Count < 3))
          .Select(section => new PyMatchCase([.. section.Labels.Select(label => Integer(label))], null, section.Body));
        var guarded = cases.Where(section => Runs(section.Labels).Any(run => run.Count >= 3))
          .Select(section => new PyMatchCase([], Test(subject, section.Labels), section.Body));
        List<PyMatchCase> all = [.. literal, .. guarded];
        if (fallback is not null)
          all.Add(new PyMatchCase([], null, fallback));

        choice = [new PyMatchStatement(subject, all)];
      } else {
        IReadOnlyList<PyStatement> rest = fallback ?? [];
        for (var index = cases.Count - 1; index >= 0; --index)
          rest = [new PyIf(Test(subject, cases[index].Labels), cases[index].Body, rest)];

        choice = [.. rest];
      }

      if (wrapped)
        written.Add(new PyWhile(new PyBool(true), [.. choice, new PyBreak()]));
      else
        written.AddRange(choice);
    }

    /// <summary>The labels in runs of consecutive values, in ascending order.</summary>
    private static List<List<BigInteger>> Runs(IReadOnlyList<BigInteger> labels) {
      var runs = new List<List<BigInteger>>();
      foreach (var label in labels)
        if (runs.Count > 0 && runs[^1][^1] + 1 == label)
          runs[^1].Add(label);
        else
          runs.Add([label]);

      return runs;
    }

    /// <summary>
    /// Whether the subject is one of the labels: <c>x == 0</c>, <c>x in (1, 2)</c>, <c>3 &lt;= x &lt;= 9</c>
    /// for a run of three or more, joined by <c>or</c>.
    /// </summary>
    private static PyExpression Test(PyExpression subject, IReadOnlyList<BigInteger> labels) {
      var parts = new List<PyExpression>();
      var single = new List<BigInteger>();
      foreach (var run in Runs(labels)) {
        if (run.Count >= 3)
          parts.Add(new PyCompare([Integer(run[0]), subject, Integer(run[^1])], ["<=", "<="]));
        else
          single.AddRange(run);
      }

      if (single.Count == 1)
        parts.Insert(0, Compare(subject, "==", Integer(single[0])));
      else if (single.Count > 1)
        parts.Insert(0, new PyCompare([subject, new PyTuple([.. single.Select(label => Integer(label))])], ["in"]));

      return parts.Skip(1).Aggregate(parts[0], (left, right) => new PyBoolOp(left, "or", right));
    }

    /// <summary>
    /// The statements of a switch section with every <c>break</c> of the switch taken out: a path
    /// that reaches one skips what follows it. An <c>if</c> with a <c>break</c> in a branch takes the
    /// statements after it into both branches, where a branch that ends with a <c>break</c> drops
    /// them again; <see langword="null"/> where both branches could go on and more than
    /// <see cref="CopiedTail"/> statements would be copied.
    /// </summary>
    private static List<IrStmt>? Eliminated(IEnumerable<IrStmt> statements) {
      var flat = Flattened(statements);
      for (var index = 0; index < flat.Count; ++index) {
        switch (flat[index]) {
          case IrBreak:
            return flat[..index];
          case IrReturn or IrContinue:
            return flat[..(index + 1)];
          case IrIf conditional when Breaks(conditional):
            var tail = flat[(index + 1)..];
            var otherwise = conditional.Else?.Statements ?? [];
            if (tail.Count > CopiedTail && FallsThrough(conditional.Then.Statements) && FallsThrough(otherwise))
              return null;

            var then = Eliminated([.. conditional.Then.Statements, .. tail]);
            var @else = Eliminated([.. otherwise, .. tail]);
            if (then is null || @else is null)
              return null;

            return [.. flat.Take(index), new IrIf(conditional.Condition, new IrBlock(then), new IrBlock(@else))];
        }
      }

      return flat;
    }

    /// <summary>Whether a statement holds a <c>break</c> of the switch around it, one not inside a loop or switch of its own.</summary>
    private static bool Breaks(IrStmt statement) => statement switch {
      IrBreak => true,
      IrBlock block => block.Statements.Any(Breaks),
      IrIf conditional => Breaks(conditional.Then) || (conditional.Else is { } otherwise && Breaks(otherwise)),
      _ => false
    };

    /// <summary>Whether control can reach the end of the statements.</summary>
    private static bool FallsThrough(IEnumerable<IrStmt> statements) {
      var flat = Flattened(statements);
      return flat.Count == 0 || flat[^1] switch {
        IrBreak or IrReturn or IrContinue => false,
        IrIf { Else: { } otherwise } conditional => FallsThrough(conditional.Then.Statements) || FallsThrough(otherwise.Statements),
        _ => true
      };
    }

    /// <summary>
    /// A counted loop as <c>for i in range(…)</c>: the counter is declared just before the loop, the
    /// loop compares it with a bound that does not change in it, steps it by a constant, and no
    /// statement of the body writes it. The counter's value is not read after the loop, where Python
    /// would leave the last value of the range in it rather than the first that failed the test. The
    /// step after the last pass stays inside the counter's type for every value the bound can have
    /// (<see cref="Values"/>): an inclusive bound that may be the end of the type, or a step of more than
    /// one up to a bound that may be near it, would wrap in the IR and run on. The bound and
    /// the start are evaluated once, as Python evaluates the arguments of <c>range</c>.
    /// <see langword="null"/> where the loop is no such loop.
    /// </summary>
    private PyStatement? RangeLoop(IrDeclare declaration, IrExpr condition, IReadOnlyList<IrStmt> statements, IrStmt step, IReadOnlyList<IrStmt> after) {
      var counter = declaration.Local;
      var type = counter.Type;
      if (!type.IsInteger || type.IsEnum || StepOf(step, counter) is not { } amount || amount == 0)
        return null;

      if (condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual } test)
        return null;

      IrExpr bound;
      IrBinaryOp op;
      if (test.Left is IrLocalRef { Local: var left } && left == counter) {
        (bound, op) = (test.Right, test.Op);
      } else if (test.Right is IrLocalRef { Local: var right } && right == counter) {
        (bound, op) = (test.Left, test.Op switch {
          IrBinaryOp.Less => IrBinaryOp.Greater,
          IrBinaryOp.LessOrEqual => IrBinaryOp.GreaterOrEqual,
          IrBinaryOp.Greater => IrBinaryOp.Less,
          _ => IrBinaryOp.LessOrEqual
        });
      } else {
        return null;
      }

      var writes = new HashSet<IrLocal>();
      foreach (var statement in statements.Append(step))
        Written(statement, writes);

      writes.Remove(counter);
      // A counter a closure captures is read after the loop through the closure, which a range leaves at its last value.
      if (Written(new IrBlock(statements), counter) || !Invariant(bound, writes, counter) || ReadAfter(after, counter) || this._captured.Contains(counter))
        return null;

      var upward = op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual;
      if (upward != amount > 0)
        return null;

      // A counter that wraps runs on in the IR; a range stops. Only a loop whose step after the last
      // pass stays inside the type for every value the bound can have is taken: an inclusive bound
      // that may be the end of the type, or a step of more than one up to a bound that may be near it,
      // stays a while with the IR's own step.
      var inclusive = op is IrBinaryOp.LessOrEqual or IrBinaryOp.GreaterOrEqual;
      var (low, high) = Values(bound);
      var last = upward ? (inclusive ? high : high - 1) + amount : (inclusive ? low : low + 1) + amount;
      if (last > Big(IrOperators.Maximum(type)) || last < Big(IrOperators.Minimum(type)))
        return null;

      var start = this.Expression(declaration.Initializer!);
      var limitExpression = this.Expression(bound);
      PyExpression stop = !inclusive ? limitExpression
        : limitExpression is PyInt literal ? Integer(literal.Value + (upward ? 1 : -1))
        : Binary(limitExpression, upward ? "+" : "-", Integer(1));
      List<PyExpression> arguments = amount == 1 && start is PyInt { Value.IsZero: true } ? [stop]
        : amount == 1 ? [start, stop]
        : [start, stop, Integer(amount)];

      var name = this.LocalName(counter);
      var nonNegative = upward ? this.NonNegative(declaration.Initializer!) : bound is IrConst floor && Big(IrOperators.Number(floor.Type, floor.Value)) >= (inclusive ? 0 : -1);
      var known = this._nonNegative.Contains(counter);
      if (nonNegative)
        this._nonNegative.Add(counter);

      var body = this.Inside(new LoopFrame(null, null), statements);
      if (!known)
        this._nonNegative.Remove(counter);

      return new PyFor(name, new PyCall(Name("range"), arguments), body);
    }

    /// <summary>
    /// The smallest and the largest value a bound of a counted loop can have: a constant its own, a
    /// widening conversion of an integer the range of the type converted from, anything else every
    /// value of its type.
    /// </summary>
    private static (BigInteger Low, BigInteger High) Values(IrExpr bound) => bound switch {
      IrConst constant => (Big(IrOperators.Number(constant.Type, constant.Value)), Big(IrOperators.Number(constant.Type, constant.Value))),
      IrConvert { Operand.Type: { IsInteger: true } from } conversion when IrOperators.Minimum(from) >= IrOperators.Minimum(conversion.Type) && IrOperators.Maximum(from) <= IrOperators.Maximum(conversion.Type)
        => (Big(IrOperators.Minimum(from)), Big(IrOperators.Maximum(from))),
      _ => (Big(IrOperators.Minimum(bound.Type)), Big(IrOperators.Maximum(bound.Type)))
    };

    /// <summary>
    /// A loop that may be a counted one, as its test, the statements of its body and its step: a
    /// <c>for</c> with one statement of step, or a <c>while</c> whose body ends with what may be the
    /// step and holds no <c>continue</c> of its own, which in the <c>while</c> skips the step but in
    /// a range goes on to the next value. <see langword="null"/> for every other statement.
    /// </summary>
    private static (IrExpr Condition, IReadOnlyList<IrStmt> Body, IrStmt Step)? Counted(IrStmt statement) {
      if (statement is IrFor { Step.Statements: [var step] } loop)
        return (loop.Condition, loop.Body.Statements, step);

      if (statement is not IrWhile @while)
        return null;

      var body = Flattened(@while.Body.Statements);
      return body.Count > 0 && !body.Any(Continues) ? (@while.Condition, body[..^1], body[^1]) : null;
    }

    /// <summary>Whether a statement holds a <c>continue</c> of the loop around it, one not inside a loop of its own.</summary>
    private static bool Continues(IrStmt statement) => statement switch {
      IrContinue => true,
      IrBlock block => block.Statements.Any(Continues),
      IrIf conditional => Continues(conditional.Then) || (conditional.Else is { } otherwise && Continues(otherwise)),
      IrSwitch selection => selection.Sections.Any(section => Continues(section.Body)),
      _ => false
    };

    /// <summary>The constant a step statement adds to the counter, or <see langword="null"/>.</summary>
    private static BigInteger? StepOf(IrStmt step, IrLocal counter) => step switch {
      IrExprStmt { Expression: IrStep { Target: IrLocalRef { Local: var target } } stepped } when target == counter => stepped.Increment ? 1 : -1,
      IrAssign { Value: IrBinary { Op: IrBinaryOp.Add, Left: IrLocalRef { Local: var target }, Right: IrConst amount } } assignment when assignment.Local == counter && target == counter
        => Big(IrOperators.Number(amount.Type, amount.Value)),
      IrAssign { Value: IrBinary { Op: IrBinaryOp.Add, Left: IrConst amount, Right: IrLocalRef { Local: var target } } } assignment when assignment.Local == counter && target == counter
        => Big(IrOperators.Number(amount.Type, amount.Value)),
      IrAssign { Value: IrBinary { Op: IrBinaryOp.Subtract, Left: IrLocalRef { Local: var target }, Right: IrConst amount } } assignment when assignment.Local == counter && target == counter
        => -Big(IrOperators.Number(amount.Type, amount.Value)),
      _ => null
    };

    /// <summary>Whether a bound reads the same value in every pass: a constant, a variable or the length of an array variable the loop does not write, or a conversion of one.</summary>
    private static bool Invariant(IrExpr bound, HashSet<IrLocal> writes, IrLocal counter) => bound switch {
      IrConst => true,
      IrConstRef { Constant.Type.IsArray: false } => true,
      IrLocalRef { Local: var local } => local != counter && !writes.Contains(local),
      IrArrayLength { Array: IrConstRef } => true,
      IrArrayLength { Array: IrLocalRef { Local: var array } } => !writes.Contains(array),
      // A conversion of an invariant value: the first test evaluates it, so one that traps does so before the first pass either way.
      IrConvert { Operand: var operand } => Invariant(operand, writes, counter),
      _ => false
    };

    /// <summary>Collects every local a statement declares, assigns or steps.</summary>
    private static void Written(IrStmt statement, HashSet<IrLocal> writes) {
      switch (statement) {
        case IrDeclare declaration:
          writes.Add(declaration.Local);
          break;
        case IrAssign assignment:
          writes.Add(assignment.Local);
          break;
        case IrBlock block:
          foreach (var inner in block.Statements)
            Written(inner, writes);

          break;
        case IrIf conditional:
          Written(conditional.Then, writes);
          if (conditional.Else is { } otherwise)
            Written(otherwise, writes);

          break;
        case IrWhile loop:
          Written(loop.Body, writes);
          break;
        case IrDoWhile loop:
          Written(loop.Body, writes);
          break;
        case IrFor loop:
          Written(loop.Body, writes);
          Written(loop.Step, writes);
          break;
        case IrSwitch selection:
          foreach (var section in selection.Sections)
            Written(section.Body, writes);

          break;
      }

      foreach (var step in Expressions(statement).SelectMany(Descendants).OfType<IrStep>())
        if (step.Target is IrLocalRef { Local: var local })
          writes.Add(local);
    }

    private static bool Written(IrStmt statement, IrLocal local) {
      var writes = new HashSet<IrLocal>();
      Written(statement, writes);
      return writes.Contains(local);
    }

    /// <summary>
    /// Whether a statement after the loop may read the counter before it is written again: a read
    /// anywhere in a statement that does not start by assigning it.
    /// </summary>
    private static bool ReadAfter(IReadOnlyList<IrStmt> after, IrLocal counter) {
      foreach (var statement in after) {
        // A declaration is a new variable, whose value cannot be the old one's.
        if (statement is IrDeclare { Local: var declared } && declared == counter)
          return false;

        if (statement is IrAssign { Local: var assigned } assignment && assigned == counter)
          return Reads(assignment.Value, counter);

        if (Reads(statement, counter))
          return true;
      }

      return false;
    }

    private static bool Reads(IrExpr expression, IrLocal local) => Descendants(expression).Any(node => node is IrLocalRef { Local: var read } && read == local);

    private static bool Reads(IrStmt statement, IrLocal local) => statement switch {
      IrBlock block => block.Statements.Any(inner => Reads(inner, local)),
      IrIf conditional => Reads(conditional.Condition, local) || Reads(conditional.Then, local) || (conditional.Else is { } otherwise && Reads(otherwise, local)),
      IrWhile loop => Reads(loop.Condition, local) || Reads(loop.Body, local),
      IrDoWhile loop => Reads(loop.Condition, local) || Reads(loop.Body, local),
      IrFor loop => Reads(loop.Condition, local) || Reads(loop.Body, local) || Reads(loop.Step, local),
      IrSwitch selection => Reads(selection.Value, local) || selection.Sections.Any(section => Reads(section.Body, local)),
      _ => Expressions(statement).Any(expression => Reads(expression, local))
    };

    /// <summary>The expressions a simple statement evaluates.</summary>
    private static IEnumerable<IrExpr> Expressions(IrStmt statement) => statement switch {
      IrDeclare { Initializer: { } value } => [value],
      IrAssign assignment => [assignment.Value],
      IrGlobalAssign assignment => [assignment.Value],
      IrStore store => [store.Array, store.Index, store.Value],
      IrExprStmt expression => [expression.Expression],
      IrReturn { Value: { } value } => [value],
      IrIf conditional => [conditional.Condition],
      IrWhile loop => [loop.Condition],
      IrDoWhile loop => [loop.Condition],
      IrFor loop => [loop.Condition],
      IrSwitch selection => [selection.Value],
      _ => []
    };

    /// <summary>An expression and every expression below it.</summary>
    private static IEnumerable<IrExpr> Descendants(IrExpr expression) {
      yield return expression;
      IEnumerable<IrExpr> children = expression switch {
        IrUnary unary => [unary.Operand],
        IrBinary binary => [binary.Left, binary.Right],
        IrConvert conversion => [conversion.Operand],
        IrConditional choice => [choice.Condition, choice.WhenTrue, choice.WhenFalse],
        IrArrayLoad load => [load.Array, load.Index],
        IrArrayLength length => [length.Array],
        IrNewArray creation => [creation.Length],
        IrCall call => call.Arguments,
        IrStep step => [step.Target],
        IrIntrinsic intrinsic => intrinsic.Arguments,
        _ => []
      };
      foreach (var child in children)
        foreach (var descendant in Descendants(child))
          yield return descendant;
    }

  }

}
