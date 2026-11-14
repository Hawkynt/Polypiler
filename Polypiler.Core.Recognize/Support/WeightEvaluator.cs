using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>A variable shift count whose value modulo 64 is fixed for one evaluation.</summary>
/// <param name="Variable">The count expression that is not a constant.</param>
/// <param name="Residue">Its value modulo 64.</param>
internal sealed record CountBinding(IrExpr Variable, int Residue);

/// <summary>An expression as weighted bits of atoms, and the atoms by their index.</summary>
/// <param name="Image">The value.</param>
/// <param name="Atoms">The expressions whose bits the value is made of.</param>
internal sealed record WeightedValue(Weights Image, IReadOnlyList<IrExpr> Atoms) {

  /// <summary>
  /// The single atom the value is made of, or <see langword="null"/> when it has none or several. An
  /// atom whose bits cancelled out was still evaluated; it may be dropped only when it cannot trap.
  /// </summary>
  public IrExpr? Single {
    get {
      var used = this.Image.Terms.Keys.Select(key => key >> 6).Distinct().ToList();
      return used.Count == 1 && this.Atoms.Where((atom, index) => index != used[0]).All(IrShape.IsTrapFree) ? this.Atoms[used[0]] : null;
    }
  }

}

/// <summary>
/// Computes the <see cref="Weights"/> of an integer expression. A node the form cannot follow - a
/// product of two variables, an and of a sum, a load - is an atom: its own bits stand for it. An
/// operand whose bits a parent needs but whose form does not show them becomes an atom as well, and
/// the evaluation starts over, so that every occurrence of the same tree is the same atom. An
/// expression with an effect - a call, a step - cannot be an atom, and the evaluation then fails.
/// </summary>
internal sealed class WeightEvaluator {

  private readonly RuleContext _context;
  private readonly CountBinding? _binding;
  private readonly HashSet<IrExpr> _forced = new(IrShape.Comparer);
  private readonly List<IrExpr> _atoms = [];
  private readonly Dictionary<IrExpr, int> _index = new(IrShape.Comparer);
  private IrExpr? _pending;
  private bool _refused;

  private WeightEvaluator(RuleContext context, CountBinding? binding) {
    this._context = context;
    this._binding = binding;
  }

  /// <summary>The weighted bits of an integer expression, or <see langword="null"/> when it has an effect.</summary>
  /// <param name="root">The expression.</param>
  /// <param name="context">What is known of the variables: a stable one is its definition.</param>
  /// <param name="binding">The residue of a variable shift count, or <see langword="null"/>.</param>
  public static WeightedValue? Evaluate(IrExpr root, RuleContext context, CountBinding? binding = null) {
    if (!root.Type.IsInteger)
      return null;

    var evaluator = new WeightEvaluator(context, binding);
    while (true) {
      evaluator._atoms.Clear();
      evaluator._index.Clear();
      evaluator._pending = null;
      if (evaluator.Eval(root) is { } image)
        return new(image, [.. evaluator._atoms]);

      if (evaluator._refused || evaluator._pending is not { } pending || !evaluator._forced.Add(pending))
        return null;
    }
  }

  private Weights? Fail(IrExpr atom) {
    this._pending ??= atom;
    return null;
  }

  private Weights? Atom(IrExpr expression) {
    if (!IrShape.IsSideEffectFree(expression)) {
      this._refused = true;
      return null;
    }

    if (!this._index.TryGetValue(expression, out var index)) {
      index = this._atoms.Count;
      this._atoms.Add(expression);
      this._index.Add(expression, index);
    }

    return Weights.Atom(index, expression.Type.Bits);
  }

  private Weights? Eval(IrExpr expression) {
    if (this._forced.Contains(expression))
      return this.Atom(expression);

    var width = expression.Type.Bits;
    switch (expression) {
      case IrConst constant:
        return Weights.Of(width, (ulong)constant.Value);
      case IrLocalRef reference when this._context.Definition(reference.Local) is { } definition:
        return this.Eval(definition);
      case IrConvert { Contract: IrConversion.Wrap } conversion when conversion.Operand.Type.IsInteger:
        return this.Convert(conversion);
      case IrUnary { Op: IrUnaryOp.Negate } unary:
        return this.Eval(unary.Operand)?.Negate();
      case IrUnary { Op: IrUnaryOp.BitNot } unary:
        return this.Eval(unary.Operand)?.Complement();
      case IrBinary binary when binary.Left.Type.IsInteger:
        return this.Binary(binary);
      case IrIntrinsic intrinsic:
        return this.Intrinsic(intrinsic);
      default:
        return this.Atom(expression);
    }
  }

  private Weights? Convert(IrConvert conversion) {
    var from = conversion.Operand.Type;
    var to = conversion.Type.Bits;
    if (this.Eval(conversion.Operand) is not { } inner)
      return null;

    if (to <= from.Bits)
      return inner.Truncate(to);

    return (from.IsSigned ? inner.SignExtend(to) : inner.ZeroExtend(to)) ?? this.Fail(conversion.Operand);
  }

  private Weights? Binary(IrBinary binary) {
    if (binary.Op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical)
      return this.Shift(binary);

    if (binary.Op is not (IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor))
      return this.Atom(binary);

    if (this.Eval(binary.Left) is not { } left || this.Eval(binary.Right) is not { } right)
      return null;

    switch (binary.Op) {
      case IrBinaryOp.Add:
        return left.Add(right);
      case IrBinaryOp.Subtract:
        return left.Subtract(right);
      case IrBinaryOp.Multiply:
        return right.IsConstant ? left.Scale(right.Constant) : left.IsConstant ? right.Scale(left.Constant) : this.Fail(binary);
      case IrBinaryOp.And when right.IsConstant || left.IsConstant:
        var (value, mask, operand) = right.IsConstant ? (left, right.Constant, binary.Left) : (right, left.Constant, binary.Right);
        return value.And(mask) ?? this.Fail(operand);
      case IrBinaryOp.And:
        return this.Fail(binary);
      default:
        if (left.Combine(right, binary.Op == IrBinaryOp.Xor) is { } combined)
          return combined;

        return left.Decompose() is null ? this.Fail(binary.Left) : right.Decompose() is null ? this.Fail(binary.Right) : this.Fail(binary);
    }
  }

  private Weights? Shift(IrBinary binary) {
    var width = binary.Type.Bits;
    if (this.CountOf(binary.Right) is not { } raw)
      return this.Fail(binary);

    var count = raw & (width - 1);
    if (this.Eval(binary.Left) is not { } value)
      return null;

    var shifted = binary.Op switch {
      IrBinaryOp.ShiftLeft => value.ShiftLeft(count),
      IrBinaryOp.ShiftRight when binary.Type.IsSigned => value.ShiftRightArithmetic(count),
      _ => value.ShiftRightLogical(count)
    };
    return shifted ?? this.Fail(binary.Left);
  }

  private Weights? Intrinsic(IrIntrinsic intrinsic) {
    switch (intrinsic.Op) {
      case IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight when intrinsic.Arguments[0].Type.IsWideInteger: {
        var width = intrinsic.Type.Bits;
        if (this.CountOf(intrinsic.Arguments[1]) is not { } raw)
          return this.Atom(intrinsic);

        if (this.Eval(intrinsic.Arguments[0]) is not { } value)
          return null;

        var count = (intrinsic.Op == IrIntrinsicOp.RotateLeft ? raw : width - raw) & (width - 1);
        if (count == 0)
          return value;

        return value.ShiftRightLogical(width - count) is { } low && value.ShiftLeft(count).Combine(low, false) is { } rotated
          ? rotated
          : this.Fail(intrinsic.Arguments[0]);
      }
      case IrIntrinsicOp.ByteSwap or IrIntrinsicOp.BitReverse when intrinsic.Arguments[0].Type.IsInteger: {
        var width = intrinsic.Type.Bits;
        if (this.Eval(intrinsic.Arguments[0]) is not { } value)
          return null;

        var permuted = intrinsic.Op == IrIntrinsicOp.ByteSwap
          ? value.Permute(position => (width / 8 - 1 - position / 8) * 8 + position % 8)
          : value.Permute(position => width - 1 - position);
        return permuted ?? this.Fail(intrinsic.Arguments[0]);
      }
      case IrIntrinsicOp.ToInt32 or IrIntrinsicOp.ToUint32
        when intrinsic.Arguments[0] is IrConvert { Operand.Type: { IsInteger: true, Bits: <= 32 } } exact:
        return this.Eval(new IrConvert(intrinsic.Type, exact.Operand));
      case IrIntrinsicOp.ToUint32:
        // The same 32 bits as ToInt32 of the same number, which is the atom both stand for.
        return this.Eval(new IrConvert(IrType.U32, Images.Call(IrIntrinsicOp.ToInt32, IrType.I32, intrinsic.Arguments[0])));
      default:
        return this.Atom(intrinsic);
    }
  }

  /// <summary>A shift or rotate count: a constant, or the bound variable's residue through arithmetic that keeps residues modulo 64.</summary>
  private int? CountOf(IrExpr count) => count switch {
    IrConst constant => (int)(constant.Value & 63),
    IrIntrinsic { Op: IrIntrinsicOp.ToInt32, Arguments: [IrConst { Type.Kind: IrTypeKind.F64 } number] }
      => (int)(IrOperators.ToWord(IrType.I32, IrOperators.Float(IrType.F64, number.Value)) & 63),
    _ when this._binding is { } binding => Residue(count, binding),
    _ => null
  };

  /// <summary>The value of a count expression modulo 64, or <see langword="null"/> when it depends on more than the residue of the variable.</summary>
  internal static int? Residue(IrExpr count, CountBinding binding) {
    if (IrShape.Same(count, binding.Variable))
      return binding.Residue;

    int? Of(IrExpr operand) => Residue(operand, binding);
    var result = count switch {
      IrConst constant => (int)(constant.Value & 63),
      IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsWord: true } conversion when conversion.Type.IsWord => Of(conversion.Operand),
      IrUnary { Op: IrUnaryOp.Negate } unary => -Of(unary.Operand),
      IrUnary { Op: IrUnaryOp.BitNot } unary => ~Of(unary.Operand),
      IrBinary { Op: IrBinaryOp.Add } binary => Of(binary.Left) + Of(binary.Right),
      IrBinary { Op: IrBinaryOp.Subtract } binary => Of(binary.Left) - Of(binary.Right),
      IrBinary { Op: IrBinaryOp.Multiply } binary => Of(binary.Left) * Of(binary.Right),
      IrBinary { Op: IrBinaryOp.And } binary => Of(binary.Left) & Of(binary.Right),
      IrBinary { Op: IrBinaryOp.Or } binary => Of(binary.Left) | Of(binary.Right),
      IrBinary { Op: IrBinaryOp.Xor } binary => Of(binary.Left) ^ Of(binary.Right),
      _ => null
    };
    return result & 63;
  }

}
