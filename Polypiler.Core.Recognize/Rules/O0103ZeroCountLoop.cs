using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0103: a loop that counts zero bits from the bottom or from the top until it meets a set bit
/// becomes <see cref="IrIntrinsicOp.TrailingZeroCount"/> or <see cref="IrIntrinsicOp.LeadingZeroCount"/>.
/// <para>
/// The loop starts its counter <c>n</c> at 0, its condition is <c>n &lt; w</c> joined by <c>&amp;&amp;</c>
/// with a test that one bit is zero, and its body adds one to <c>n</c> and may shift the tested
/// variable by one. The pass is simulated for every <c>n</c> below the width <c>w</c>: the test must
/// read bit <c>n</c> (or bit <c>w - 1 - n</c>) of one value from before the loop, which makes the
/// final <c>n</c> the number of zeros below (or above) the first set bit, and <c>w</c> for zero. A
/// shifted variable ends shifted by that count - also for zero, which shifts to zero either way. A
/// test without the bound <c>n &lt; w</c> would never end for zero and is left alone.
/// </para>
/// </summary>
public sealed class O0103ZeroCountLoop : IrRule {

  /// <summary>Creates the rule.</summary>
  public O0103ZeroCountLoop() : base("O0103", "zero count from a bit-scanning loop") { }

  /// <inheritdoc/>
  public override bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    rewrite = null;
    if (index + 1 >= statements.Count || statements[index + 1] is not IrWhile { Condition: IrBinary { Op: IrBinaryOp.LogicalAnd } both } loop)
      return false;

    var (counter, declared) = statements[index] switch {
      IrDeclare { Initializer: IrConst { Value: 0 } } declare when declare.Local.Type == IrType.I32 => (declare.Local, true),
      IrAssign { Value: IrConst { Value: 0 } } assign when assign.Local.Type == IrType.I32 => (assign.Local, false),
      _ => (null, false)
    };
    if (counter is null)
      return false;

    var body = loop.Body.Statements.Select(CounterLoop.Assignment).ToList();
    if (body.Any(assignment => assignment is null) || body.Count(assignment => assignment!.Local == counter) != 1
      || !body.Any(assignment => CounterLoop.Steps(assignment!, counter)))
      return false;

    var test = Bounded(both.Left, counter) is { } bound ? (Bound: bound, Test: both.Right) : Bounded(both.Right, counter) is { } other ? (Bound: other, Test: both.Left) : default;
    if (test.Test is null || !IrShape.IsTrapFree(test.Test))
      return false;

    var shifted = body.Where(assignment => assignment!.Local != counter).ToList();
    if (shifted.Count > 1)
      return false;

    var shift = shifted.Count == 0 ? null : shifted[0];
    if (shift is not null && (shift.Value is not IrBinary { Op: IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Left: IrLocalRef self, Right: IrConst { Value: 1 } }
      || self.Local != shift.Local))
      return false;

    if (Scan(test.Test, test.Bound, counter, body!, context) is not var (atom, leading)
      || shift is not null && !(atom is IrLocalRef origin && origin.Local == shift.Local))
      return false;

    var count = Images.Fit(Images.Call(leading ? IrIntrinsicOp.LeadingZeroCount : IrIntrinsicOp.TrailingZeroCount, IrType.I32, atom), counter.Type);
    var replacement = new List<IrStmt> { declared ? new IrDeclare(counter, count) : new IrAssign(counter, count) };
    if (shift is not null)
      replacement.Add(new IrAssign(shift.Local, new IrBinary(((IrBinary)shift.Value).Op, new IrLocalRef(shift.Local), new IrLocalRef(counter))));

    rewrite = new(2, replacement);
    return true;
  }

  /// <summary>The bound <c>w</c> of a condition <c>n &lt; w</c>, or <see langword="null"/>.</summary>
  private static int? Bounded(IrExpr condition, IrLocal counter)
    => condition is IrBinary { Op: IrBinaryOp.Less, Left: IrLocalRef reference, Right: IrConst { Value: >= 8 and <= 64 } limit } && reference.Local == counter
      ? (int)limit.Value : null;

  /// <summary>
  /// The value whose bits the test reads and whether it reads them from the top, when for every pass
  /// <c>r</c> below the bound the test is <c>e == 0</c> with <c>e</c> exactly one bit of that value:
  /// bit <c>r</c>, or bit <c>w - 1 - r</c>.
  /// </summary>
  private static (IrExpr Atom, bool Leading)? Scan(IrExpr test, int bound, IrLocal counter, IReadOnlyList<IrAssign> body, RuleContext context) {
    var values = body.Select(assignment => assignment.Local).Distinct().ToDictionary(local => local, local => (IrExpr)new IrLocalRef(local));
    IrExpr? atom = null;
    bool? leading = null;
    for (var pass = 0; pass < bound; ++pass) {
      values[counter] = Images.Constant(IrType.I32, pass);
      var current = values;
      var tested = IrShape.Map(test, node => node is IrLocalRef reference && current.TryGetValue(reference.Local, out var value) ? value : null);
      if (tested is not IrBinary { Op: IrBinaryOp.Equal } equal || (equal.Right is IrConst { Value: 0 } ? equal.Left : equal.Left is IrConst { Value: 0 } ? equal.Right : null) is not { } bit
        || WeightEvaluator.Evaluate(bit, context) is not { Image: { Constant: 0, Terms.Count: 1 } } value || value.Single is not { } source
        || source.Type.Bits != bound || (atom is not null && !IrShape.Same(atom, source)))
        return null;

      atom = source;
      var position = value.Image.Terms.Keys.Single() & 63;
      leading ??= position != 0;
      if (position != (leading.Value ? bound - 1 - pass : pass))
        return null;

      foreach (var assignment in body) {
        var before = new Dictionary<IrLocal, IrExpr>(values);
        values[assignment.Local] = IrShape.Map(assignment.Value, node => node is IrLocalRef reference && before.TryGetValue(reference.Local, out var value) ? value : null);
      }
    }

    return IrShape.IsTrapFree(atom!) ? (atom!, leading!.Value) : null;
  }

}
