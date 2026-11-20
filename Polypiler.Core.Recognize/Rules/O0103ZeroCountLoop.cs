using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0103: a loop that counts zero bits until it meets a set bit, or counts the bits of a value by
/// shifting it out, becomes <see cref="IrIntrinsicOp.TrailingZeroCount"/>,
/// <see cref="IrIntrinsicOp.LeadingZeroCount"/> or <c>w - LeadingZeroCount</c>.
/// <list type="bullet">
/// <item><em>guarded</em>: <c>n = 0; while (n &lt; w &amp;&amp; bit == 0) { [x &gt;&gt;= 1 | x &lt;&lt;= 1;] n++; }</c>,
/// the test reading bit <c>n</c> (or <c>w - 1 - n</c>) of one value in every pass;</item>
/// <item><em>unguarded</em>: the same loop without the bound, which does not end for zero, rewritten
/// only where the value is known to be non-zero - inside <c>if (x != 0)</c> or after
/// <c>if (x == 0) return …;</c> with nothing writing <c>x</c> in between - and otherwise refused as
/// <c>input may be zero</c>;</item>
/// <item><em>bit-length</em>: <c>n = 0; while (v != 0) { v &gt;&gt;&gt;= 1; n++; }</c>, which ends for every
/// value with a logical shift and never ends for a negative one with an arithmetic shift.</item>
/// </list>
/// <para>
/// The loop is unrolled symbolically: for each pass the condition and the values of the variables the
/// body writes are expressions of the values before the loop, and the final value of each variable is
/// the chain <c>c0 ? (c1 ? … : v1) : v0</c> of the passes' exit values. The host proves the final
/// counter equal to the count, and the final shifted variable equal to the value shifted by the count,
/// under the non-zero fact where the form needs it. The body may hold only assignments to the counter,
/// which steps by one from zero, and to at most one variable it shifts by one.
/// </para>
/// </summary>
public sealed class O0103ZeroCountLoop : IrRule {

  /// <summary>Creates the rule.</summary>
  public O0103ZeroCountLoop() : base("O0103", "zero count from a bit-scanning loop") { }

  private enum Form { Guarded, Unguarded, BitLength }

  /// <inheritdoc/>
  public override bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    rewrite = null;
    if (index + 1 >= statements.Count || statements[index + 1] is not IrWhile loop)
      return false;

    var (counter, start, declared) = statements[index] switch {
      IrDeclare { Initializer: IrConst { Type.IsInteger: true } constant } declare => (declare.Local, constant.Value, true),
      IrAssign { Value: IrConst { Type.IsInteger: true } constant } assign => (assign.Local, constant.Value, false),
      _ => (null, 0L, false)
    };
    if (counter is null || !counter.Type.IsInteger || Classify(loop.Condition, counter) is not var (form, bound, test))
      return false;

    if (Recognize(statements, index, loop, counter, start, form, bound, test, context, out var reason) is { } replacement) {
      var (assignments, proofs) = replacement;
      foreach (var (before, after) in proofs)
        if (context.Confirm(this.Id, before, after, assignments.Description) is null)
          return false;

      rewrite = new(2, [declared ? new IrDeclare(counter, assignments.Count) : new IrAssign(counter, assignments.Count), .. assignments.Rest]);
      return true;
    }

    if (reason is not null)
      context.Note?.Invoke($"O0103 not applied: {reason}");
    return false;
  }

  /// <summary>What the rule writes in place of the loop, and the proofs it needs first.</summary>
  private sealed record Assignments(IrExpr Count, IReadOnlyList<IrStmt> Rest, string Description);

  /// <summary>
  /// The form of a loop condition: <c>n &lt; k &amp;&amp; e == 0</c> (either order), <c>e == 0</c> alone, or
  /// <c>v != 0</c>; <see langword="null"/> for every other condition, which the rule ignores silently.
  /// </summary>
  private static (Form Form, int Bound, IrExpr Test)? Classify(IrExpr condition, IrLocal counter) {
    if (condition is IrBinary { Op: IrBinaryOp.LogicalAnd } both) {
      foreach (var (bounded, test) in new[] { (both.Left, both.Right), (both.Right, both.Left) })
        if (bounded is IrBinary { Op: IrBinaryOp.Less, Left: IrLocalRef reference, Right: IrConst limit } && reference.Local == counter && IsBitTest(test))
          return (Form.Guarded, (int)System.Math.Clamp(limit.Value, 0, 128), test);

      return null;
    }

    if (IsBitTest(condition))
      return (Form.Unguarded, 0, condition);

    return Tested(condition) is not null ? (Form.BitLength, 0, condition) : null;
  }

  /// <summary>The operand of <c>v != 0</c> (either order), through conversions; <see langword="null"/> for another condition.</summary>
  private static IrExpr? Tested(IrExpr condition)
    => condition is not IrBinary { Op: IrBinaryOp.NotEqual } unequal ? null
      : Images.IsConstant(unequal.Right, out var right) && right == 0 && Strip(unequal.Left) is IrLocalRef ? unequal.Left
      : Images.IsConstant(unequal.Left, out var left) && left == 0 && Strip(unequal.Right) is IrLocalRef ? unequal.Right
      : null;

  /// <summary>Whether a condition is <c>(… &amp; …) == 0</c>, the shape of a test of one bit.</summary>
  private static bool IsBitTest(IrExpr test)
    => test is IrBinary { Op: IrBinaryOp.Equal } equal && (Images.IsConstant(equal.Right, out var right) && right == 0 && Images.Peel(equal.Left) is IrBinary { Op: IrBinaryOp.And }
      || Images.IsConstant(equal.Left, out var left) && left == 0 && Images.Peel(equal.Right) is IrBinary { Op: IrBinaryOp.And });

  private static (Assignments Assignments, IReadOnlyList<(IrExpr Before, IrExpr After)> Proofs)? Recognize(IReadOnlyList<IrStmt> statements, int index, IrWhile loop,
    IrLocal counter, long start, Form form, int bound, IrExpr test, RuleContext context, out string? reason) {
    reason = "unrecognized loop form";
    var body = loop.Body.Statements.Select(CounterLoop.Assignment).ToList();
    if (body.Any(assignment => assignment is null)) {
      reason = "loop body has effects";
      return null;
    }

    var assignments = body.Select(assignment => assignment!).ToList();
    if (start != 0 || assignments.Count(assignment => assignment.Local == counter) != 1 || !assignments.Any(assignment => CounterLoop.Steps(assignment, counter)))
      return null;

    var others = assignments.Where(assignment => assignment.Local != counter).ToList();
    if (others.Count > 1 || others.Count == 1 && Shift(others[0]) is null)
      return null;

    var shifted = others.Count == 0 ? null : others[0];
    var shift = shifted is null ? null : Shift(shifted);
    if (form == Form.BitLength) {
      if (shifted is null || shift is null || Strip(Tested(test)!) is not IrLocalRef tested || tested.Local != shifted.Local || shift.Op == IrBinaryOp.ShiftLeft)
        return null;
      // A narrow unsigned value is shifted after a widening that keeps it non-negative: that shift is logical.
      if (shift.Op == IrBinaryOp.ShiftRight && shift.Type.IsSigned && Images.Unwiden(shift.Left).Type.IsSigned) {
        reason = "arithmetic right shift";
        return null;
      }

      var width = shifted.Local.Type.Bits;
      var exits = Unroll(assignments, counter, test, width, Tested(test)!, out var terminal);
      // A logical shift has shifted every bit out after w passes: the loop has ended by then.
      if (WeightEvaluator.Evaluate(terminal, context) is not { Image.IsConstant: true } ended || ended.Image.Constant != 0)
        return null;

      var value = new IrLocalRef(shifted.Local);
      var length = Images.Fit(new IrBinary(IrBinaryOp.Subtract, Images.Constant(IrType.I32, width), Images.Call(IrIntrinsicOp.LeadingZeroCount, IrType.I32, value)), counter.Type);
      var zero = Images.Constant(shifted.Local.Type, 0);
      reason = null;
      return (new(length, [new IrAssign(shifted.Local, zero)], $"LeadingZeroCount.{shifted.Local.Type} from bit-length loop"),
        [(exits[shifted.Local], zero), (exits[counter], length)]);
    }

    var passes = form == Form.Guarded ? bound : 64;
    if (Scan(test, passes, counter, assignments, context) is not var (atom, leading, scanned))
      return null;
    if (form == Form.Guarded && bound != scanned) {
      reason = "bound differs from width";
      return null;
    }
    if (shifted is not null && !(atom is IrLocalRef origin && origin.Local == shifted.Local))
      return null;

    IrExpr? fact = null;
    if (form == Form.Unguarded && (atom is not IrLocalRef guarded || (fact = NonZero(statements, index, guarded.Local, context)) is null)) {
      reason = "input may be zero";
      return null;
    }

    var finals = Unroll(assignments, counter, test, scanned, test, out _);
    var count = Images.Fit(Images.Call(leading ? IrIntrinsicOp.LeadingZeroCount : IrIntrinsicOp.TrailingZeroCount, IrType.I32, atom), counter.Type);
    var name = $"{(leading ? "LeadingZeroCount" : "TrailingZeroCount")}.{atom.Type} from {(form == Form.Guarded ? "guarded" : "unguarded")} loop";
    IrExpr Under(IrExpr chain, IrExpr after) => fact is null ? chain : new IrConditional(fact, chain, after);

    var proofs = new List<(IrExpr, IrExpr)>();
    var rest = new List<IrStmt>();
    if (shifted is not null && shift is not null) {
      var moved = ShiftBy(shifted, shift, Images.Fit(count, IrType.I32));
      proofs.Add((Under(finals[shifted.Local], moved), moved));
      rest.Add(new IrAssign(shifted.Local, ShiftBy(shifted, shift, Images.Fit(new IrLocalRef(counter), IrType.I32))));
    }

    proofs.Add((Under(finals[counter], count), count));
    reason = null;
    return (new(count, rest, name), proofs);
  }

  /// <summary>The shift by one of a variable's assignment <c>v = v op 1</c>, through conversions; <see langword="null"/> otherwise.</summary>
  private static IrBinary? Shift(IrAssign assignment)
    => Strip(assignment.Value) is IrBinary { Op: IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Right: IrConst { Value: 1 } } shift
      && Strip(shift.Left) is IrLocalRef self && self.Local == assignment.Local ? shift : null;

  private static IrExpr Strip(IrExpr expression) {
    while (expression is IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true, Type.IsInteger: true } conversion)
      expression = conversion.Operand;

    return expression;
  }

  /// <summary>The assignment's value with its shift by one replaced by a shift by <paramref name="count"/>.</summary>
  private static IrExpr ShiftBy(IrAssign assignment, IrBinary shift, IrExpr count)
    => IrShape.Map(assignment.Value, node => ReferenceEquals(node, shift) ? shift with { Right = count } : null);

  /// <summary>
  /// <c>x != 0</c> as an unsigned comparison, when the loop at a position runs only for a non-zero
  /// <c>x</c>: inside <c>if (x != 0)</c>, or after <c>if (x == 0) return …;</c> in the same block,
  /// and nothing between that test and the loop mentions <c>x</c>.
  /// </summary>
  private static IrExpr? NonZero(IReadOnlyList<IrStmt> statements, int index, IrLocal local, RuleContext context) {
    // Widening and same-width conversions keep whether a value is zero; a narrowing one does not and is not looked through.
    static IrExpr Kept(IrExpr expression) {
      for (var previous = (IrExpr?)null; !ReferenceEquals(previous, expression);)
        (previous, expression) = (expression, Images.Unwiden(Images.Peel(expression)));

      return expression;
    }

    bool Tests(IrExpr condition, IrBinaryOp op) => condition is IrBinary binary && binary.Op == op
      && (Images.IsConstant(binary.Right, out var right) && right == 0 && Kept(binary.Left) is IrLocalRef first && first.Local == local
        || Images.IsConstant(binary.Left, out var left) && left == 0 && Kept(binary.Right) is IrLocalRef second && second.Local == local);

    var unsigned = Images.Unsigned(local.Type.Bits);
    IrExpr fact = new IrBinary(IrBinaryOp.Greater, Images.Fit(new IrLocalRef(local), unsigned), Images.Constant(unsigned, 0));
    for (var position = index - 1; position >= 0; --position) {
      if (statements[position] is IrIf { Else: null, Then.Statements: [.., IrReturn] } exit && Tests(exit.Condition, IrBinaryOp.Equal))
        return fact;
      if (IrStatements.Mentions(statements[position], local))
        return null;
    }

    // The frames hold copies of the blocks; an if without an else holds one block, which must be the one the loop is in.
    return context.Frames.Count > 0 && context.Frames[^1].Owner is IrIf { Else: null } enclosing && Tests(enclosing.Condition, IrBinaryOp.NotEqual) ? fact : null;
  }

  /// <summary>
  /// The final values of the counter and the variables the body writes after a loop of at most
  /// <paramref name="passes"/> passes, as chains over the passes' conditions; the condition after the
  /// last pass is returned for the caller to show that the loop has ended by then: <paramref name="watched"/>
  /// as it stands after the last pass.
  /// </summary>
  private static Dictionary<IrLocal, IrExpr> Unroll(IReadOnlyList<IrAssign> body, IrLocal counter, IrExpr condition, int passes, IrExpr watched, out IrExpr terminal) {
    var locals = body.Select(assignment => assignment.Local).Append(counter).Distinct().ToList();
    var values = locals.ToDictionary(local => local, local => (IrExpr)new IrLocalRef(local));
    IrExpr Substitute(IrExpr expression, Dictionary<IrLocal, IrExpr> current)
      => IrShape.Map(expression, node => node is IrLocalRef reference && current.TryGetValue(reference.Local, out var value) ? value : null);

    var snapshots = new List<Dictionary<IrLocal, IrExpr>>();
    var tests = new List<IrExpr>();
    for (var pass = 0; pass < passes; ++pass) {
      values[counter] = Images.Constant(counter.Type, pass);
      snapshots.Add(new(values));
      tests.Add(Substitute(condition, values));
      foreach (var assignment in body)
        values[assignment.Local] = Substitute(assignment.Value, new(values));
    }

    values[counter] = Images.Constant(counter.Type, passes);
    terminal = Substitute(watched, values);
    var finals = new Dictionary<IrLocal, IrExpr>();
    foreach (var local in locals) {
      var final = values[local];
      for (var pass = passes - 1; pass >= 0; --pass)
        final = new IrConditional(tests[pass], final, snapshots[pass][local]);

      finals[local] = final;
    }

    return finals;
  }

  /// <summary>
  /// The value whose bits the test reads, whether it reads them from the top, and its width, when for
  /// every pass <c>r</c> below the width the test is <c>e == 0</c> with <c>e</c> exactly one bit of that
  /// value: bit <c>r</c>, or bit <c>w - 1 - r</c>. At most <paramref name="passes"/> passes are read.
  /// </summary>
  private static (IrExpr Atom, bool Leading, int Width)? Scan(IrExpr test, int passes, IrLocal counter, IReadOnlyList<IrAssign> body, RuleContext context) {
    var values = body.Select(assignment => assignment.Local).Append(counter).Distinct().ToDictionary(local => local, local => (IrExpr)new IrLocalRef(local));
    IrExpr? atom = null;
    bool? leading = null;
    var width = passes;
    for (var pass = 0; pass < width; ++pass) {
      values[counter] = Images.Constant(counter.Type, pass);
      var current = values;
      var tested = IrShape.Map(test, node => node is IrLocalRef reference && current.TryGetValue(reference.Local, out var value) ? value : null);
      if (tested is not IrBinary { Op: IrBinaryOp.Equal } equal || (Images.IsConstant(equal.Right, out var right) && right == 0 ? equal.Left : equal.Right) is not { } bit
        || WeightEvaluator.Evaluate(bit, context) is not { Image: { Constant: 0, Terms.Count: 1 } } value || value.Single is not { } source
        || (atom is not null && !IrShape.Same(atom, source)) || !source.Type.IsInteger)
        return null;

      atom = source;
      width = System.Math.Min(passes, source.Type.Bits);
      var position = value.Image.Terms.Keys.Single() & 63;
      leading ??= position != 0;
      if (position != (leading.Value ? source.Type.Bits - 1 - pass : pass))
        return null;

      foreach (var assignment in body) {
        var before = new Dictionary<IrLocal, IrExpr>(values);
        values[assignment.Local] = IrShape.Map(assignment.Value, node => node is IrLocalRef reference && before.TryGetValue(reference.Local, out var known) ? known : null);
      }
    }

    return atom is not null && IrShape.IsTrapFree(atom) ? (atom, leading!.Value, atom.Type.Bits) : null;
  }

}
