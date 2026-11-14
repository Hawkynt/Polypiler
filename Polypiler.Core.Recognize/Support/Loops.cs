using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>
/// A loop over a counter that starts at a constant, written as the frontends lower it: the
/// initialization - a declaration or an assignment of a constant - followed by a <c>while</c> whose
/// condition compares the counter and whose body ends by adding one to it.
/// </summary>
/// <param name="Counter">The counter.</param>
/// <param name="Start">Its first value.</param>
/// <param name="Condition">The condition of the loop.</param>
/// <param name="Body">The statements of the body without the step of the counter, each an assignment.</param>
/// <param name="Declared">Whether the initialization declares the counter.</param>
/// <param name="Known">The constants the variables of the body are known to hold before the loop.</param>
internal sealed record CounterLoop(IrLocal Counter, long Start, IrExpr Condition, IReadOnlyList<IrAssign> Body, bool Declared, IReadOnlyDictionary<IrLocal, IrConst> Known) {

  /// <summary>The longest loop that is unrolled.</summary>
  public const int Longest = 64;

  /// <summary>
  /// Reads a loop at a position: the initialization there, the loop right after it. The body may hold
  /// assignments and steps of variables other than the counter and <c>if (c) v = v + 1;</c>, all
  /// without effect beyond those writes and without a trap.
  /// </summary>
  public static CounterLoop? At(IReadOnlyList<IrStmt> statements, int index, RuleContext context) {
    if (index + 1 >= statements.Count)
      return null;

    var (counter, start, declared) = statements[index] switch {
      IrDeclare { Initializer: IrConst constant } declare when Number(constant) is { } value => (declare.Local, value, true),
      IrAssign { Value: IrConst constant } assign when Number(constant) is { } value => (assign.Local, value, false),
      _ => (null, 0L, false)
    };
    if (counter is null || statements[index + 1] is not IrWhile loop || loop.Body.Statements.Count == 0 || !Steps(loop.Body.Statements[^1], counter))
      return null;

    var assignments = new List<IrAssign>();
    foreach (var statement in loop.Body.Statements.Take(loop.Body.Statements.Count - 1)) {
      if (Assignment(statement) is not { } assignment || assignment.Local == counter)
        return null;

      assignments.Add(assignment);
    }

    if (!IrShape.IsTrapFree(loop.Condition))
      return null;

    var known = new Dictionary<IrLocal, IrConst>();
    foreach (var local in assignments.Select(assignment => assignment.Local).Distinct())
      if (Before(statements, index, local, context) is { } constant)
        known[local] = constant;

    return new(counter, start, loop.Condition, assignments, declared, known);
  }

  /// <summary>
  /// The constant a variable holds at a position of a block: the last statement before it that writes
  /// the variable declares or assigns that constant. Every statement of a block that runs before the
  /// position has run to its end, so nothing else can have intervened; a plain block is entered
  /// straight from the statements before it, so the search goes on there.
  /// </summary>
  private static IrConst? Before(IReadOnlyList<IrStmt> statements, int index, IrLocal local, RuleContext context) {
    for (var level = context.Frames.Count; ; --level) {
      for (var position = index - 1; position >= 0; --position) {
        var statement = statements[position];
        if (statement is IrDeclare { Initializer: IrConst initial } declare && declare.Local == local)
          return initial;

        if (statement is IrAssign { Value: IrConst value } assign && assign.Local == local)
          return value;

        if (IrStatements.Writes(statement).Contains(local))
          return null;
      }

      if (level == 0 || context.Frames[level - 1].Owner is not IrBlock)
        return null;

      statements = context.Frames[level - 1].Statements;
      index = context.Frames[level - 1].Index;
    }
  }

  /// <summary>
  /// The integer a counter constant holds: an <c>i32</c>, or a double without a fraction - JavaScript
  /// counts in doubles - between -2^31 and 2^31; <see langword="null"/> otherwise.
  /// </summary>
  public static long? Number(IrConst constant) => constant.Type.Kind switch {
    IrTypeKind.I32 => constant.Value,
    IrTypeKind.F64 when IrOperators.Float(IrType.F64, constant.Value) is var number && number == System.Math.Floor(number) && System.Math.Abs(number) < 2147483648.0
      => (long)number,
    _ => null
  };

  /// <summary>A counter constant of a counter's type.</summary>
  public static IrConst ValueOf(IrLocal counter, long value)
    => counter.Type == IrType.F64 ? new(IrType.F64, IrOperators.Bits(IrType.F64, value)) : Images.Constant(counter.Type, value);

  /// <summary>Whether a statement adds one to a variable, an integer or a double that counts.</summary>
  public static bool Steps(IrStmt statement, IrLocal local) => Assignment(statement) is { } assignment && assignment.Local == local
    && assignment.Value is IrBinary { Op: IrBinaryOp.Add, Left: IrLocalRef reference, Right: IrConst one } && reference.Local == local && Number(one) == 1;

  /// <summary>
  /// A statement of a body as an assignment: an assignment without trap or effect, a step of a variable
  /// that wraps, or <c>if (c) v = v + 1;</c> as <c>v = c ? v + 1 : v</c>.
  /// </summary>
  public static IrAssign? Assignment(IrStmt statement) {
    switch (statement) {
      case IrAssign assign when IrShape.IsTrapFree(assign.Value):
        return assign;
      case IrExprStmt { Expression: IrStep { Target: IrLocalRef reference, Trap: false } step } when reference.Type.IsWideInteger:
        return new(reference.Local, new IrBinary(step.Increment ? IrBinaryOp.Add : IrBinaryOp.Subtract, reference, Images.Constant(reference.Type, 1)));
      case IrIf { Else: null, Then.Statements: [var only] } conditional when IrShape.IsTrapFree(conditional.Condition) && Assignment(only) is { } inner:
        return new(inner.Local, new IrConditional(conditional.Condition, inner.Value, new IrLocalRef(inner.Local)));
      default:
        return null;
    }
  }

  /// <summary>
  /// The number of passes when the condition is <c>counter &lt; N</c>, <c>counter &lt;= N</c> or
  /// <c>counter != N</c> with a constant <c>N</c> the counter reaches, at most <see cref="Longest"/>.
  /// </summary>
  public int? Passes() {
    var bound = this.Condition switch {
      IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.NotEqual, Left: IrLocalRef reference, Right: IrConst limit } when reference.Local == this.Counter => Number(limit),
      IrBinary { Op: IrBinaryOp.LessOrEqual, Left: IrLocalRef reference, Right: IrConst limit } when reference.Local == this.Counter => Number(limit) + 1,
      _ => (long?)null
    };
    return bound - this.Start is { } passes && passes is > 0 and <= Longest ? (int)passes : null;
  }

  /// <summary>
  /// The values every variable the body writes holds after a number of passes, as expressions of the
  /// values before the loop - a constant where it is <see cref="Known"/>; the counter holds its
  /// constant in each pass.
  /// </summary>
  public Dictionary<IrLocal, IrExpr> Unroll(int passes, RuleContext context) {
    var values = this.Body.Select(assignment => assignment.Local).Distinct()
      .ToDictionary(local => local, local => this.Known.TryGetValue(local, out var constant) ? constant : (IrExpr)new IrLocalRef(local));
    for (var pass = 0; pass < passes; ++pass) {
      var counter = ValueOf(this.Counter, this.Start + pass);
      foreach (var assignment in this.Body) {
        var current = new Dictionary<IrLocal, IrExpr>(values) { [this.Counter] = counter };
        var value = IrShape.Map(assignment.Value, node
          => node is IrLocalRef reference && current.TryGetValue(reference.Local, out var known) ? known : null);
        values[assignment.Local] = Counted(value, context) ?? value;
      }
    }

    return values;
  }

  /// <summary>
  /// <c>e != 0 ? v + 1 : v</c> as <c>v + ((e &gt;&gt; p) &amp; 1)</c> when <c>e</c> is one bit at
  /// position <c>p</c>, and <c>e == 0</c> with that bit inverted; <see langword="null"/> otherwise.
  /// A count unrolled this way stays a sum instead of growing a choice per pass.
  /// </summary>
  private static IrExpr? Counted(IrExpr value, RuleContext context) {
    if (value is not IrConditional { Condition: IrBinary { Op: IrBinaryOp.NotEqual or IrBinaryOp.Equal, Right: IrConst { Value: 0 } } test } choice
      || choice.WhenTrue is not IrBinary { Op: IrBinaryOp.Add, Right: IrConst { Value: 1 } } increment || !IrShape.Same(increment.Left, choice.WhenFalse)
      || !test.Left.Type.IsWideInteger || WeightEvaluator.Evaluate(test.Left, context) is not { Image: { Constant: 0, Terms.Count: 1 } image })
      return null;

    var type = test.Left.Type;
    var weight = image.Terms.Values.Single();
    var position = System.Numerics.BitOperations.TrailingZeroCount(weight);
    IrExpr bit = new IrBinary(IrBinaryOp.And, new IrBinary(IrBinaryOp.ShiftRightLogical, test.Left, Images.Constant(IrType.I32, position)), Images.Constant(type, 1));
    if (test.Op == IrBinaryOp.Equal)
      bit = new IrBinary(IrBinaryOp.Xor, bit, Images.Constant(type, 1));

    return new IrBinary(IrBinaryOp.Add, increment.Left, Images.Fit(bit, increment.Left.Type));
  }

  /// <summary>
  /// The assignments that give each variable its final value, ordered so that each reads the values
  /// from before the loop: a variable is assigned after every final value that reads it. <see langword="null"/> when two read each other.
  /// </summary>
  public static IReadOnlyList<IrStmt>? Ordered(IReadOnlyDictionary<IrLocal, IrExpr> finals) {
    var pending = finals.ToDictionary();
    var ordered = new List<IrStmt>();
    while (pending.Count > 0) {
      var next = pending.Keys.FirstOrDefault(local => !pending.Any(other => other.Key != local && IrShape.Reads(other.Value, local)));
      if (next is null)
        return null;

      ordered.Add(new IrAssign(next, pending[next]));
      pending.Remove(next);
    }

    return ordered;
  }

}
