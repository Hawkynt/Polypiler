using System;
using System.Collections.Generic;
using System.Globalization;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// A Boolean expression over the input bits of a <see cref="BitBooleanTransform"/> (Knuth, TAOCP 4A
/// §7.1.1): <see cref="Input"/>, <see cref="Const"/>, <see cref="Not"/>, <see cref="And"/>,
/// <see cref="Or"/> and <see cref="Xor"/>. Equality is structural.
/// </summary>
public abstract record BoolExpr {

  private protected BoolExpr() { }

  /// <summary>Input bit <paramref name="Index"/>, 0 = least significant.</summary>
  /// <param name="Index">The input bit, not negative.</param>
  public sealed record Input(int Index) : BoolExpr {

    /// <summary>The input bit, not negative.</summary>
    public int Index { get; } = Index >= 0 ? Index : throw new ArgumentOutOfRangeException(nameof(Index), Index, "an input bit is not negative");

    /// <summary><c>Input 3</c>.</summary>
    public override string ToString() => "Input " + this.Index.ToString(CultureInfo.InvariantCulture);

  }

  /// <summary>The constant <paramref name="Value"/>.</summary>
  /// <param name="Value">The constant.</param>
  public sealed record Const(bool Value) : BoolExpr {

    /// <summary><c>Const 0</c> or <c>Const 1</c>.</summary>
    public override string ToString() => this.Value ? "Const 1" : "Const 0";

  }

  /// <summary>The complement of <paramref name="Operand"/>.</summary>
  /// <param name="Operand">The operand.</param>
  public sealed record Not(BoolExpr Operand) : BoolExpr {

    /// <summary>The operand.</summary>
    public BoolExpr Operand { get; } = Operand ?? throw new ArgumentNullException(nameof(Operand));

    /// <summary><c>Not(…)</c>.</summary>
    public override string ToString() => $"Not({this.Operand})";

  }

  /// <summary>The conjunction of <paramref name="Left"/> and <paramref name="Right"/>.</summary>
  /// <param name="Left">The left operand.</param>
  /// <param name="Right">The right operand.</param>
  public sealed record And(BoolExpr Left, BoolExpr Right) : BoolExpr {

    /// <summary>The left operand.</summary>
    public BoolExpr Left { get; } = Left ?? throw new ArgumentNullException(nameof(Left));

    /// <summary>The right operand.</summary>
    public BoolExpr Right { get; } = Right ?? throw new ArgumentNullException(nameof(Right));

    /// <summary><c>And(…, …)</c>.</summary>
    public override string ToString() => $"And({this.Left}, {this.Right})";

  }

  /// <summary>The disjunction of <paramref name="Left"/> and <paramref name="Right"/>.</summary>
  /// <param name="Left">The left operand.</param>
  /// <param name="Right">The right operand.</param>
  public sealed record Or(BoolExpr Left, BoolExpr Right) : BoolExpr {

    /// <summary>The left operand.</summary>
    public BoolExpr Left { get; } = Left ?? throw new ArgumentNullException(nameof(Left));

    /// <summary>The right operand.</summary>
    public BoolExpr Right { get; } = Right ?? throw new ArgumentNullException(nameof(Right));

    /// <summary><c>Or(…, …)</c>.</summary>
    public override string ToString() => $"Or({this.Left}, {this.Right})";

  }

  /// <summary>The exclusive or of <paramref name="Left"/> and <paramref name="Right"/>.</summary>
  /// <param name="Left">The left operand.</param>
  /// <param name="Right">The right operand.</param>
  public sealed record Xor(BoolExpr Left, BoolExpr Right) : BoolExpr {

    /// <summary>The left operand.</summary>
    public BoolExpr Left { get; } = Left ?? throw new ArgumentNullException(nameof(Left));

    /// <summary>The right operand.</summary>
    public BoolExpr Right { get; } = Right ?? throw new ArgumentNullException(nameof(Right));

    /// <summary><c>Xor(…, …)</c>.</summary>
    public override string ToString() => $"Xor({this.Left}, {this.Right})";

  }

  /// <summary>
  /// The expression folded bottom-up with exactly these rules and no others: constant propagation and
  /// absorption of constants (<c>And(e, 0) = 0</c>, <c>And(e, 1) = e</c>, <c>Or(e, 1) = 1</c>,
  /// <c>Or(e, 0) = e</c>, <c>Xor(e, 0) = e</c>), <c>Not(Not e) = e</c>, <c>And(e, e) = Or(e, e) = e</c>,
  /// <c>Xor(e, e) = 0</c> and <c>Xor(e, 1) = Not e</c>. Operands are compared structurally after they are
  /// folded. The folding is sound but not complete: <c>And(x, Not x)</c> stays as it is.
  /// </summary>
  /// <returns>The folded expression; the same object when nothing folds at the top.</returns>
  public BoolExpr Fold() => this switch {
    Input or Const => this,
    Not not => Negate(not.Operand.Fold()),
    And and => FoldAnd(and.Left.Fold(), and.Right.Fold()),
    Or or => FoldOr(or.Left.Fold(), or.Right.Fold()),
    _ => FoldXor(((Xor)this).Left.Fold(), ((Xor)this).Right.Fold())
  };

  private static BoolExpr Negate(BoolExpr operand) => operand switch {
    Const constant => new Const(!constant.Value),
    Not not => not.Operand,
    _ => new Not(operand)
  };

  private static BoolExpr FoldAnd(BoolExpr left, BoolExpr right) {
    if (left is Const l)
      return l.Value ? right : left;
    if (right is Const r)
      return r.Value ? left : right;

    return left == right ? left : new And(left, right);
  }

  private static BoolExpr FoldOr(BoolExpr left, BoolExpr right) {
    if (left is Const l)
      return l.Value ? left : right;
    if (right is Const r)
      return r.Value ? right : left;

    return left == right ? left : new Or(left, right);
  }

  private static BoolExpr FoldXor(BoolExpr left, BoolExpr right) {
    if (left is Const l)
      return l.Value ? Negate(right) : right;
    if (right is Const r)
      return r.Value ? Negate(left) : left;

    return left == right ? new Const(false) : new Xor(left, right);
  }

  /// <summary>The input bits the expression mentions, in ascending order.</summary>
  public SortedSet<int> Inputs() {
    var inputs = new SortedSet<int>();
    this.CollectInputs(inputs);
    return inputs;
  }

  private void CollectInputs(SortedSet<int> inputs) {
    switch (this) {
      case Input input:
        inputs.Add(input.Index);
        break;
      case Const:
        break;
      case Not not:
        not.Operand.CollectInputs(inputs);
        break;
      default:
        var (left, right) = this switch {
          And and => (and.Left, and.Right),
          Or or => (or.Left, or.Right),
          _ => (((Xor)this).Left, ((Xor)this).Right)
        };
        left.CollectInputs(inputs);
        right.CollectInputs(inputs);
        break;
    }
  }

}
