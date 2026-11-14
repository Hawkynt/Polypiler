using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0101: a loop that counts set bits becomes <see cref="IrIntrinsicOp.PopCount"/>.
/// <list type="bullet">
/// <item>Kernighan's loop <c>while (x != 0) { c++; x &amp;= x - 1; }</c>, the two statements in
/// either order, becomes <c>c = c + PopCount(x); x = 0;</c>. Each pass clears the lowest set bit, so
/// the loop ends for every value, after as many passes as it has set bits. The counter wraps; a
/// counter that traps is left alone.</item>
/// <item>A counted loop over the width - <c>for (i = 0; i &lt; w; i++) c += (x &gt;&gt; i) &amp; 1;</c>,
/// or <c>if (((x &gt;&gt; i) &amp; 1) != 0) c++;</c>, or shifting <c>x</c> instead - is unrolled
/// symbolically (<see cref="CounterLoop"/>) and replaced when the counter ends as its old value plus
/// every bit of one value from before the loop, once each, and every other variable as a constant.</item>
/// </list>
/// </summary>
public sealed class O0101PopCountLoop : IrRule {

  /// <summary>Creates the rule.</summary>
  public O0101PopCountLoop() : base("O0101", "population count from a counting loop") { }

  /// <inheritdoc/>
  public override bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    if (Kernighan(statements[index], context) is { } replacement) {
      rewrite = new(1, replacement);
      return true;
    }

    var done = CounterLoops.TryReplace(statements, index, context, (local, value) => Counted(local, value, context), out rewrite);
    return done;
  }

  /// <summary>
  /// The final value of a counter that adds every bit of one value to where it started: its old value,
  /// or the constant it is known to start from. <see langword="null"/> otherwise.
  /// </summary>
  private static IrExpr? Counted(IrLocal counter, IrExpr value, RuleContext context) {
    if (!counter.Type.IsWideInteger || WeightEvaluator.Evaluate(value, context) is not { } evaluated)
      return null;

    var own = evaluated.Atoms.ToList().FindIndex(atom => atom is IrLocalRef reference && reference.Local == counter);
    var used = evaluated.Image.Terms.Keys.Select(key => key >> 6).Where(atom => atom != own).Distinct().ToList();
    if (used is not [var other] || !evaluated.Atoms.Where((atom, position) => position != own).All(IrShape.IsTrapFree))
      return null;

    var source = evaluated.Atoms[other];
    var bits = evaluated.Image.Terms.Keys.Count(key => key >> 6 == other);
    var kept = own < 0 ? 0 : evaluated.Image.Terms.Keys.Count(key => key >> 6 == own);
    foreach (var (key, weight) in evaluated.Image.Terms)
      if (weight != (key >> 6 == own ? 1UL << (key & 63) : 1UL))
        return null;

    // Every bit of the value once, and the counter either whole or not at all.
    if (bits != source.Type.Bits || (own >= 0 && (kept != counter.Type.Bits || evaluated.Image.Constant != 0)))
      return null;

    var count = Images.Fit(Images.Call(IrIntrinsicOp.PopCount, IrType.I32, source), counter.Type);
    IrExpr start = own >= 0 ? new IrLocalRef(counter) : Images.Constant(counter.Type, (long)evaluated.Image.Constant);
    return start is IrConst { Value: 0 } ? count : new IrBinary(IrBinaryOp.Add, start, count);
  }

  /// <summary>Kernighan's loop as the assignments of its final values, or <see langword="null"/>.</summary>
  private static IReadOnlyList<IrStmt>? Kernighan(IrStmt statement, RuleContext context) {
    if (statement is not IrWhile { Body.Statements: [var first, var second] } loop)
      return null;

    var steps = new[] { first, second }.Select(CounterLoop.Assignment).ToList();
    if (steps.Any(step => step is null))
      return null;

    foreach (var (count, clear) in new[] { (steps[0]!, steps[1]!), (steps[1]!, steps[0]!) }) {
      var x = clear.Local;
      var atom = new IrLocalRef(x);
      if (count.Local == x || !CounterLoop.Steps(count, count.Local) || !x.Type.IsWideInteger || !Tests(loop.Condition, atom, context)
        || Images.Peel(clear.Value) is not IrBinary { Op: IrBinaryOp.And } and)
        continue;

      bool Is(IrExpr operand, bool decremented) => Images.Computes(operand, atom, bits => decremented ? bits.Add(Weights.Of(bits.Width, bits.Mask)) : bits, context);
      if (Is(and.Left, false) && Is(and.Right, true) || Is(and.Left, true) && Is(and.Right, false))
        return [
          new IrAssign(count.Local, new IrBinary(IrBinaryOp.Add, new IrLocalRef(count.Local), Images.Fit(Images.Call(IrIntrinsicOp.PopCount, IrType.I32, atom), count.Local.Type))),
          new IrAssign(x, Images.Constant(x.Type, 0))
        ];
    }

    return null;
  }

  /// <summary>Whether a condition is <c>x != 0</c>, or <c>x &gt; 0</c> for an unsigned <c>x</c>.</summary>
  private static bool Tests(IrExpr condition, IrExpr atom, RuleContext context) => condition switch {
    IrBinary { Op: IrBinaryOp.NotEqual, Right: IrConst { Value: 0 } } test => Images.Computes(test.Left, atom, bits => bits, context),
    IrBinary { Op: IrBinaryOp.Greater, Right: IrConst { Value: 0 } } test => !test.Left.Type.IsSigned && Images.Computes(test.Left, atom, bits => bits, context),
    _ => false
  };

}
