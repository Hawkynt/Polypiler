using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// <c>match</c>: the scrutinee is evaluated once; the arms are tried top to bottom, a guard only
/// after its pattern matched, and the first arm whose pattern and guard hold runs - a chain of
/// <c>if</c>s, or of conditional expressions when every arm is a plain value. The checker has made
/// sure the arms cover every value, so the last arm without a guard is the final <c>else</c> and the
/// arms after it are never reached; no default arm is invented.
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary>One arm, lowered: its test (<see langword="null"/> for always), its guard and its body.</summary>
  private sealed record LoweredArm(IrExpr? Test, (List<IrStmt> Statements, IrExpr? Value, bool Diverged) Guard, (List<IrStmt> Statements, IrExpr? Value, bool Diverged) Body);

  private IrExpr? Match(MatchExpr match) {
    var type = this._typed.TypeOf(match);
    var wanted = type.Kind is not (RKind.Unit or RKind.Never);
    var scrutinees = match.Scrutinee is TupleExpr tuple ? this.Operands([.. tuple.Elements]) : [this.Value(match.Scrutinee)];
    // A binding aliases the scrutinee, so the scrutinee must be a value no arm can change: a local of its own.
    var binds = match.Arms.Any(arm => this.Binds(arm.Pattern));
    for (var index = 0; index < scrutinees.Length; ++index)
      scrutinees[index] = binds && scrutinees[index] is not IrConst ? this.Temporary(scrutinees[index], "scrutinee") : this.Stable(scrutinees[index], "scrutinee");

    var last = match.Arms.ToList().FindLastIndex(arm => arm.Guard is null);
    var arms = new List<LoweredArm>();
    for (var index = 0; index <= last; ++index) {
      var arm = match.Arms[index];
      var test = index == last ? null : this.Test(arm.Pattern, scrutinees);
      var bindings = this.Bindings(arm.Pattern, scrutinees).ToList();
      foreach (var (pattern, value) in bindings)
        this._values[this._typed.Bindings[pattern]] = value;

      var guard = this.Capture(() => arm.Guard is null ? null : this.Value(arm.Guard));
      var body = this.Capture(() => {
        foreach (var (pattern, value) in bindings.Where(binding => binding.Pattern.Mutable))
          this.Bind(pattern, value);

        return this.Lower(arm.Body);
      });
      arms.Add(new LoweredArm(test, guard, body));
    }

    if (arms.Any(arm => arm.Guard.Statements.Count > 0 || arm.Guard.Diverged))
      return this.Flagged(arms, wanted ? type.Ir : null);

    if (wanted && arms.All(arm => arm.Body.Statements.Count == 0 && !arm.Body.Diverged)) {
      var value = arms[^1].Body.Value!;
      for (var index = arms.Count - 2; index >= 0; --index)
        value = new IrConditional(Condition(arms[index])!, arms[index].Body.Value!, value);

      return value;
    }

    var slot = wanted ? this.Slot(type.Ir, "result") : null;
    IrBlock? otherwise = null;
    for (var index = arms.Count - 1; index >= 0; --index) {
      var block = Assigned(arms[index].Body, slot);
      var condition = Condition(arms[index]);
      otherwise = condition is null ? block : new IrBlock([new IrIf(condition, block, otherwise)]);
    }

    this.Splice([.. otherwise!.Statements], arms.All(arm => arm.Body.Diverged));
    return slot is null ? null : new IrLocalRef(slot);
  }

  /// <summary>Whether an arm is taken: its pattern and then its guard.</summary>
  private static IrExpr? Condition(LoweredArm arm) => arm.Guard.Value is null ? arm.Test : arm.Test is null ? arm.Guard.Value : And(arm.Test, arm.Guard.Value);

  /// <summary>The statements of an arm, ending with the assignment of its value.</summary>
  private static IrBlock Assigned((List<IrStmt> Statements, IrExpr? Value, bool Diverged) body, IrLocal? slot) {
    var statements = new List<IrStmt>(body.Statements);
    if (slot is not null && !body.Diverged)
      statements.Add(new IrAssign(slot, body.Value!));

    return new IrBlock(statements);
  }

  /// <summary>A match with a guard that needs statements: each arm runs when no earlier one did and its pattern holds, then tests its guard.</summary>
  private IrExpr? Flagged(List<LoweredArm> arms, IrType? type) {
    var slot = type is null ? null : this.Slot(type, "result");
    var matched = this.Slot(IrType.Bool, "matched");
    foreach (var arm in arms) {
      var taken = new List<IrStmt>(arm.Guard.Statements);
      var body = Assigned(arm.Body, slot).Statements.Prepend(new IrAssign(matched, Truth(true))).ToList();
      if (!arm.Guard.Diverged)
        taken.Add(arm.Guard.Value is null ? new IrBlock(body) : new IrIf(arm.Guard.Value, new IrBlock(body), null));

      var test = arm.Test is null ? Not(new IrLocalRef(matched)) : And(Not(new IrLocalRef(matched)), arm.Test);
      this.Emit(new IrIf(test, new IrBlock(taken), null));
    }

    return slot is null ? null : new IrLocalRef(slot);
  }

  /// <summary>The bindings of an arm's pattern, each with the scrutinee value it names.</summary>
  private IEnumerable<(IdentifierPattern Pattern, IrExpr Value)> Bindings(Pattern pattern, IrExpr[] scrutinees)
    => scrutinees.Length == 1 ? this.Bound(pattern, scrutinees[0])
      : pattern is TuplePattern tuple ? tuple.Elements.SelectMany((element, index) => this.Bound(element, scrutinees[index]))
      : [];

  private IEnumerable<(IdentifierPattern Pattern, IrExpr Value)> Bound(Pattern pattern, IrExpr value)
    => this.IsBinding(pattern) ? [((IdentifierPattern)pattern, value)] : [];

  private bool IsBinding(Pattern pattern) => pattern is IdentifierPattern identifier && this._typed.Bindings[identifier].Constant is null;

  /// <summary>Whether a pattern binds a name.</summary>
  private bool Binds(Pattern pattern) => this.IsBinding(pattern) || (pattern is TuplePattern tuple && tuple.Elements.Any(this.IsBinding));

  /// <summary>The test of a pattern over the scrutinee values; <see langword="null"/> when it matches every value.</summary>
  private IrExpr? Test(Pattern pattern, IrExpr[] scrutinees) {
    if (scrutinees.Length == 1)
      return this.Test(pattern, scrutinees[0]);

    IrExpr? Tuple(Pattern alternative) {
      if (alternative is not TuplePattern tuple)
        return null;

      IrExpr? all = null;
      for (var index = 0; index < scrutinees.Length; ++index)
        if (this.Test(tuple.Elements[index], scrutinees[index]) is { } test)
          all = all is null ? test : And(all, test);

      return all;
    }

    return Any(pattern is OrPattern or ? or.Alternatives.Select(Tuple) : [Tuple(pattern)]);
  }

  private IrExpr? Test(Pattern pattern, IrExpr value) {
    switch (pattern) {
      case IdentifierPattern identifier when this._typed.Bindings[identifier].Constant is { } constant:
        return Equal(value, this.Value(constant.Value));
      case LiteralPattern literal:
        return Equal(value, this.Value(literal.Value));
      case RangePattern range: {
        var low = range.Low is null ? null : Compare(IrBinaryOp.LessOrEqual, this.Value(range.Low), value);
        var high = range.High is null ? null : Compare(range.Inclusive ? IrBinaryOp.LessOrEqual : IrBinaryOp.Less, value, this.Value(range.High));
        return low is null ? high : high is null ? low : And(low, high);
      }
      case OrPattern alternatives:
        return Any(alternatives.Alternatives.Select(alternative => this.Test(alternative, value)));
      default:
        return null;
    }
  }

  /// <summary>The disjunction of tests, <see langword="null"/> (always) when one of them always holds.</summary>
  private static IrExpr? Any(IEnumerable<IrExpr?> tests) {
    IrExpr? any = null;
    foreach (var test in tests) {
      if (test is null)
        return null;

      any = any is null ? test : Or(any, test);
    }

    return any;
  }

}
